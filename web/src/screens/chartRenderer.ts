import * as echarts from '../charts'
import { tr } from '../i18n'
import type { HistoryPoint, TagValue } from '../types'
import { seriesColor } from './chartColors'
import type { ScreenNode } from './model'

// 图表统一用 ECharts SSR 输出静态 SVG，画面内不持有 canvas 实例。
// 渲染、缓存与十字线命中共用同一组量程/边距数值，不读取 ECharts 私有模型。
export interface ChartSource {
  values: Record<number, TagValue>
  history: Record<number, HistoryPoint[]>
  editing: boolean
  historyUnavailable: boolean
  alarmUnavailable: boolean
  alarmCounts: Record<string, number>
  tagName(id: number): string
  tagUnit(id: number): string
  tagGood(id: number): boolean
  alarmLevelColor(level: string): string
}

export interface ChartPlot { x: number; y: number; width: number; height: number; xMin: number; xMax: number; yMin: number; yMax: number }
interface TrendExtent { xMin: number; xMax: number; yMin: number; yMax: number }
interface ChartCacheEntry { svg: string; plot?: ChartPlot }
const chartCacheLimit = 300
export const trendGrid = { left: 60, right: 30, top: 28, bottom: 26 }

export function isTrendChart(node: ScreenNode): boolean { return (node.type === 'chart-line' || node.type === 'chart-area') && !!node.tags?.length }

function niceRange(min: number, max: number): [number, number] {
  const span = max - min
  if (span === 0) { const pad = Math.abs(min) * 0.1 || 1; return [min - pad, max + pad] }
  const raw = span / 4
  const power = 10 ** Math.floor(Math.log10(raw))
  const fraction = raw / power
  const step = (fraction < 1.5 ? 1 : fraction < 3 ? 2 : fraction < 7 ? 5 : 10) * power
  return [Number((Math.floor(min / step) * step).toPrecision(12)), Number((Math.ceil(max / step) * step).toPrecision(12))]
}

export function createChartRenderer(source: () => ChartSource) {
  const chartCache = new Map<string, ChartCacheEntry>()
  const chartPlots = new Map<string, ChartPlot>()

  function value(id: number): number { return Number(source().values[id]?.value ?? 0) }

  function trendExtent(node: ScreenNode): TrendExtent | undefined {
    const { history } = source()
    let xMin = Infinity, xMax = -Infinity, yMin = Infinity, yMax = -Infinity
    const track = (ts: number, sample: number) => {
      if (!Number.isFinite(ts) || !Number.isFinite(sample)) return
      xMin = Math.min(xMin, ts); xMax = Math.max(xMax, ts)
      yMin = Math.min(yMin, sample); yMax = Math.max(yMax, sample)
    }
    if (node.type === 'chart-area') {
      // 面积图按时间戳堆叠，量程取各时刻累计值。
      const totals = new Map<number, number>()
      for (const id of node.tags ?? []) for (const point of history[id] ?? []) totals.set(point.ts, (totals.get(point.ts) ?? 0) + point.value)
      for (const [ts, sample] of totals) track(ts, sample)
    } else {
      for (const id of node.tags ?? []) for (const point of history[id] ?? []) track(point.ts, point.value)
    }
    if (!Number.isFinite(xMin) || !Number.isFinite(yMin)) return undefined
    if (xMin === xMax) { xMin -= 1000; xMax += 1000 }
    const [low, high] = niceRange(yMin, yMax)
    return { xMin, xMax, yMin: low, yMax: high }
  }

  function trendPlot(node: ScreenNode): ChartPlot | undefined {
    if (node.type !== 'chart-line' && node.type !== 'chart-area') return undefined
    const extent = trendExtent(node)
    if (!extent) return undefined
    const width = Math.max(40, Math.round(node.width)) - trendGrid.left - trendGrid.right
    const height = Math.max(40, Math.round(node.height)) - trendGrid.top - trendGrid.bottom
    if (width <= 0 || height <= 0) return undefined
    return { x: trendGrid.left, y: trendGrid.top, width, height, ...extent }
  }

  function nearestPoint(id: number, ts: number): HistoryPoint | undefined {
    const points = source().history[id]
    if (!points?.length) return undefined
    let best = points[0]!, distance = Math.abs(best.ts - ts)
    for (const point of points) { const next = Math.abs(point.ts - ts); if (next < distance) { best = point; distance = next } }
    return best
  }

  function chartSigOf(id: number): string {
    const src = source(), points = src.history[id], sample = src.values[id], lastPoint = points?.[points.length - 1]
    return `${points?.length}:${lastPoint?.ts}:${lastPoint?.value}|${sample?.value}:${sample?.quality}:${sample?.ts}:${src.tagGood(id)}`
  }

  function chartSig(node: ScreenNode): string {
    const options = JSON.stringify(node.chartOptions ?? null)
    if (node.type === 'alarm-stats') return `${options}|${Object.entries(source().alarmCounts).filter(([, count]) => count > 0).map(([level, count]) => `${level}:${count}`).join(',')}`
    if (node.type === 'chart-gauge') return `${options}|${node.tagId == null ? '' : chartSigOf(node.tagId)}`
    return `${options}|${(node.tags ?? []).map(id => chartSigOf(id)).join(',')}`
  }

  function chartUnavailable(node: ScreenNode): boolean {
    const src = source()
    if (src.editing) return false
    if (node.type === 'alarm-stats') return src.alarmUnavailable
    if (node.type === 'chart-line' || node.type === 'chart-area') return src.historyUnavailable
    const ids = node.type === 'chart-gauge' ? node.tagId == null ? [] : [node.tagId] : node.tags ?? []
    return !ids.length || ids.some(id => !src.tagGood(id))
  }

  function showLegend(node: ScreenNode, fallback = true): boolean { return node.chartOptions?.legend ?? fallback }
  function showGrid(node: ScreenNode): boolean { return node.chartOptions?.grid ?? true }
  function dataLabels(node: ScreenNode, fallback = false): boolean { return node.chartOptions?.dataLabels ?? fallback }
  function axisName(node: ScreenNode): string | undefined { const unit = node.chartOptions?.yUnit?.trim(); return unit || undefined }
  function targetMarkLine(node: ScreenNode, axis: 'x' | 'y'): Record<string, unknown> | undefined {
    const lines = node.chartOptions?.targetLines ?? []
    if (!lines.length) return undefined
    return {
      silent: true, symbol: 'none', animation: false,
      label: { position: 'end', color: '#cbd5e1', fontSize: 9 },
      data: lines.map(line => ({
        [axis === 'x' ? 'xAxis' : 'yAxis']: line.value,
        name: line.label || String(line.value),
        lineStyle: { color: line.color, type: 'dashed', width: 1 },
        label: { formatter: line.label || String(line.value), color: line.color }
      }))
    }
  }

  function chartOption(node: ScreenNode, ids: number[]): echarts.EChartsOption {
    const src = source(), names = ids.map(id => `#${id}`), colors = ids.map((_, i) => seriesColor(i))
    if (node.type === 'alarm-stats') {
      const levels = ['Info', 'Warning', 'Critical']
      return { backgroundColor: 'transparent', animation: false,
        grid: { left: 38, right: 10, top: 14, bottom: 26 },
        xAxis: { type: 'category', data: levels, axisLabel: { color: '#9fb3c8', fontSize: 10 } },
        yAxis: { type: 'value', minInterval: 1, axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { lineStyle: { color: '#243449' } } },
        series: [{ type: 'bar', data: levels.map(level => ({ value: src.alarmCounts[level] ?? 0, itemStyle: { color: src.alarmLevelColor(level) } })), barMaxWidth: 40 }] }
    }
    if (node.type === 'chart-line') {
      const extent = trendExtent(node)
      return { backgroundColor: 'transparent', animation: false,
        legend: { show: showLegend(node), top: 0, selectedMode: false, textStyle: { color: '#9fb3c8', fontSize: 10 } },
        grid: { ...trendGrid },
        xAxis: { type: 'time', min: extent?.xMin, max: extent?.xMax, axisLabel: { color: '#9fb3c8', fontSize: 10 }, axisLine: { lineStyle: { color: '#243449' } }, splitLine: { show: false } },
        yAxis: { type: 'value', min: extent?.yMin, max: extent?.yMax, name: axisName(node), nameTextStyle: { color: '#9fb3c8', fontSize: 9 }, axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { show: showGrid(node), lineStyle: { color: '#243449' } } },
        series: ids.map((id, i) => ({ type: 'line', name: src.tagName(id), showSymbol: false, sampling: 'lttb', data: (src.history[id] ?? []).map(p => [p.ts, p.value]),
          label: { show: dataLabels(node), color: '#e5effa', fontSize: 9 }, markLine: i === 0 ? targetMarkLine(node, 'y') : undefined,
          lineStyle: { color: colors[i] }, itemStyle: { color: colors[i] } })) }
    }
    if (node.type === 'chart-pie') {
      return { backgroundColor: 'transparent', animation: false,
        legend: { show: showLegend(node, false), top: 0, textStyle: { color: '#9fb3c8', fontSize: 9 } },
        series: [{ type: 'pie', radius: ['34%', '70%'], center: ['50%', '50%'], data: ids.map((id, i) => ({ name: names[i], value: value(id), itemStyle: { color: colors[i] } })), label: { show: dataLabels(node, true), color: '#e5effa', fontSize: 11 } }] }
    }
    if (node.type === 'chart-bar-horizontal') {
      return { backgroundColor: 'transparent', animation: false,
        grid: { left: 56, right: 14, top: 12, bottom: 24 },
        xAxis: { type: 'value', name: axisName(node), nameTextStyle: { color: '#9fb3c8', fontSize: 9 }, axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { show: showGrid(node), lineStyle: { color: '#243449' } } },
        yAxis: { type: 'category', data: names, axisLabel: { color: '#9fb3c8', fontSize: 10 } },
        series: [{ type: 'bar', data: ids.map((id, i) => ({ value: value(id), itemStyle: { color: colors[i] } })), barMaxWidth: 18, label: { show: dataLabels(node), color: '#e5effa', fontSize: 9, position: 'right' }, markLine: targetMarkLine(node, 'x') }] }
    }
    if (node.type === 'chart-bar-stack') {
      return { backgroundColor: 'transparent', animation: false,
        legend: { show: showLegend(node), top: 0, textStyle: { color: '#9fb3c8', fontSize: 9 } },
        grid: { left: 36, right: 10, top: 26, bottom: 22 },
        xAxis: { type: 'category', data: ['Total'], axisLabel: { color: '#9fb3c8', fontSize: 10 } },
        yAxis: { type: 'value', name: axisName(node), nameTextStyle: { color: '#9fb3c8', fontSize: 9 }, axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { show: showGrid(node), lineStyle: { color: '#243449' } } },
        series: ids.map((id, i) => ({ type: 'bar', stack: 'total', name: names[i], data: [value(id)], itemStyle: { color: colors[i] }, barMaxWidth: 70, label: { show: dataLabels(node), color: '#e5effa', fontSize: 9 }, markLine: i === 0 ? targetMarkLine(node, 'y') : undefined })) }
    }
    if (node.type === 'chart-area') {
      const extent = trendExtent(node)
      return { backgroundColor: 'transparent', animation: false,
        legend: { show: showLegend(node), top: 0, textStyle: { color: '#9fb3c8', fontSize: 9 } },
        grid: { ...trendGrid },
        xAxis: { type: 'time', min: extent?.xMin, max: extent?.xMax, axisLabel: { color: '#9fb3c8', fontSize: 10 }, axisLine: { lineStyle: { color: '#243449' } }, splitLine: { show: false } },
        yAxis: { type: 'value', min: extent?.yMin, max: extent?.yMax, name: axisName(node), nameTextStyle: { color: '#9fb3c8', fontSize: 9 }, axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { show: showGrid(node), lineStyle: { color: '#243449' } } },
        series: ids.map((id, i) => ({ type: 'line', name: names[i], showSymbol: false, sampling: 'lttb', stack: 'area',
          label: { show: dataLabels(node), color: '#e5effa', fontSize: 9 }, markLine: i === 0 ? targetMarkLine(node, 'y') : undefined,
          data: (src.history[id] ?? []).map(p => [p.ts, p.value]), lineStyle: { color: colors[i] }, itemStyle: { color: colors[i] }, areaStyle: { color: colors[i], opacity: 0.35 } })) }
    }
    if (node.type === 'chart-radar') {
      return { backgroundColor: 'transparent', animation: false,
        radar: { indicator: names.map(name => ({ name, max: 100 })), radius: '62%', center: ['50%', '54%'],
          axisName: { color: '#9fb3c8', fontSize: 9 }, splitLine: { lineStyle: { color: '#243449' } }, axisLine: { lineStyle: { color: '#243449' } } },
        series: [{ type: 'radar', data: [{ value: ids.map(id => Math.max(0, Math.min(100, value(id)))), itemStyle: { color: colors[0] }, areaStyle: { color: colors[0], opacity: 0.35 } }] }] }
    }
    if (node.type === 'chart-funnel') {
      const data = ids.map((id, i) => ({ name: names[i], value: value(id), itemStyle: { color: colors[i] } })).sort((a, b) => b.value - a.value)
      return { backgroundColor: 'transparent', animation: false,
        legend: { show: showLegend(node, false), top: 0, textStyle: { color: '#9fb3c8', fontSize: 9 } },
        series: [{ type: 'funnel', left: '12%', right: '12%', top: 8, bottom: 8, minSize: '20%', maxSize: '90%',
          label: { show: dataLabels(node, true), color: '#e5effa', fontSize: 10, position: 'inside' }, data }] }
    }
    if (node.type === 'chart-gauge') {
      const current = node.tagId == null ? node.min : value(node.tagId)
      return { backgroundColor: 'transparent', animation: false,
        series: [{ type: 'gauge', min: node.min, max: node.max, startAngle: 210, endAngle: -30, radius: '82%', center: ['50%', '58%'],
          progress: { show: true, width: 12, itemStyle: { color: colors[0] } },
          axisLine: { lineStyle: { width: 12, color: [[1, '#243449']] } },
          axisTick: { show: false }, splitLine: { length: 8, lineStyle: { color: '#3d4650' } },
          axisLabel: { color: '#9fb3c8', fontSize: 9, distance: 12 },
          pointer: { width: 4, itemStyle: { color: '#e5effa' } }, anchor: { show: false },
          detail: { fontSize: 16, color: '#e5effa', offsetCenter: [0, '40%'], formatter: (v: number) => `${Number(v).toFixed(node.decimals)}${node.unit ? ` ${node.unit}` : ''}` },
          title: { offsetCenter: [0, '72%'], color: '#9fb3c8', fontSize: 10 },
          data: [{ value: current, name: node.text }] }] }
    }
    return { backgroundColor: 'transparent', animation: false,
      grid: { left: 38, right: 10, top: 14, bottom: 26, containLabel: true },
      xAxis: { type: 'category', data: ids.map(src.tagName), axisLabel: { color: '#9fb3c8', fontSize: 10, interval: 0, hideOverlap: true } },
      yAxis: { type: 'value', name: axisName(node), nameTextStyle: { color: '#9fb3c8', fontSize: 9 }, axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { show: showGrid(node), lineStyle: { color: '#243449' } } },
      series: [{ type: 'bar', data: ids.map((id, i) => ({ value: value(id), itemStyle: { color: colors[i] } })), barMaxWidth: 34, label: { show: dataLabels(node), color: '#e5effa', fontSize: 9, position: 'top' }, markLine: targetMarkLine(node, 'y') }] }
  }

  function chartSvg(node: ScreenNode): string {
    const src = source()
    if (chartUnavailable(node)) { chartPlots.delete(node.id); return `<text x="${node.width / 2}" y="${node.height / 2}" text-anchor="middle" fill="#fbbf24" font-size="14">${tr('screenMonitor.unavailable')}</text>` }
    const sig = `${node.type}|${Math.round(node.width)}x${Math.round(node.height)}|${chartSig(node)}|${src.editing ? 1 : 0}|${JSON.stringify((node.tags ?? []).map(src.tagName))}`
    const cached = chartCache.get(sig)
    if (cached) {
      // 命中后移到队尾，容量上限内按最近使用淘汰。
      chartCache.delete(sig); chartCache.set(sig, cached)
      if (cached.plot) chartPlots.set(node.id, cached.plot); else chartPlots.delete(node.id)
      return cached.svg
    }
    const width = Math.max(40, Math.round(node.width)), height = Math.max(40, Math.round(node.height))
    const chart = echarts.init(null, null, { renderer: 'svg', ssr: true, width, height })
    chart.setOption(chartOption(node, node.tags ?? []))
    const plot = trendPlot(node)
    const svg = chart.renderToSVGString()
    chart.dispose()
    if (chartCache.size >= chartCacheLimit) {
      const oldest = chartCache.keys().next()
      if (!oldest.done) chartCache.delete(oldest.value)
    }
    chartCache.set(sig, { svg, plot })
    if (plot) chartPlots.set(node.id, plot); else chartPlots.delete(node.id)
    return svg
  }

  return { isTrendChart, chartUnavailable, chartSvg, nearestPoint, plot: (nodeId: string) => chartPlots.get(nodeId) }
}
