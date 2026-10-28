import type { EChartsOption, GridComponentOption, LineSeriesOption, XAXisComponentOption, YAXisComponentOption } from 'echarts'
import type { HistorySeries } from '../types'
import type { ChartPalette } from '../theme'

export interface TrendTrace extends HistorySeries {
  name: string
  unit: string
  isBoolean: boolean
  color: string
}

export interface TrendChartLabels {
  unitless: string
  boolean: string
  aggregateBoolean: string
  zero: string
  one: string
  formatNumber: (value: number) => string
}

export function formatTrendValue(value: number, trace: Pick<TrendTrace, 'unit' | 'isBoolean'>, aggregated: boolean, labels: TrendChartLabels): string {
  if (trace.isBoolean && !aggregated) {
    if (value === 0) return labels.zero
    if (value === 1) return labels.one
  }
  return [labels.formatNumber(value), trace.unit].filter(Boolean).join(' ')
}

export function buildTrendChart(traces: TrendTrace[], options: {
  from: number
  to: number
  resolution: string
  palette: ChartPalette
  labels: TrendChartLabels
}) {
  const { from, to, palette, labels } = options
  const aggregated = options.resolution !== 'Raw'
  const tracks: { key: string; label: string; isBoolean: boolean; traces: TrendTrace[] }[] = []
  for (const trace of traces.filter(trace => trace.points.length)) {
    const unit = trace.unit.trim()
    // Missing units do not establish that two points have compatible dimensions.
    const key = trace.isBoolean ? `boolean:${trace.tagId}` : unit ? `unit:${unit}` : `tag:${trace.tagId}`
    let track = tracks.find(track => track.key === key)
    if (!track) {
      track = {
        key, isBoolean: trace.isBoolean, traces: [],
        label: trace.isBoolean ? `${trace.name} - ${aggregated ? labels.aggregateBoolean : labels.boolean}` : unit || `${trace.name} - ${labels.unitless}`
      }
      tracks.push(track)
    }
    track.traces.push(trace)
  }

  const grid: GridComponentOption[] = []
  const xAxis: XAXisComponentOption[] = []
  const yAxis: YAXisComponentOption[] = []
  const series: LineSeriesOption[] = []
  let top = 56
  tracks.forEach((track, index) => {
    const height = tracks.length === 1 ? 240 : track.isBoolean ? 76 : 160
    grid.push({ left: 64, right: 24, top, height })
    xAxis.push({
      type: 'time', gridIndex: index, min: from, max: to,
      axisLabel: { show: index === tracks.length - 1, hideOverlap: true, color: palette.muted },
      axisTick: { show: index === tracks.length - 1 }
    })
    yAxis.push({
      type: 'value', gridIndex: index, scale: !track.isBoolean,
      name: track.label, nameLocation: 'end', nameRotate: 0, nameGap: 12,
      nameTruncate: { maxWidth: 240, ellipsis: '...' },
      nameTextStyle: { align: 'left', color: palette.text },
      min: track.isBoolean ? 0 : undefined,
      max: track.isBoolean ? 1 : undefined,
      interval: track.isBoolean ? aggregated ? 0.5 : 1 : undefined,
      axisLabel: { color: palette.muted },
      splitLine: { lineStyle: { color: palette.split } }
    })
    for (const trace of track.traces) {
      series.push({
        id: String(trace.tagId),
        name: trace.unit && !trace.isBoolean ? `${trace.name} [${trace.unit}]` : trace.name,
        type: 'line', xAxisIndex: index, yAxisIndex: index,
        step: trace.isBoolean && !aggregated ? 'end' : false,
        showSymbol: trace.points.length === 1, symbolSize: 6,
        sampling: trace.isBoolean ? undefined : 'lttb',
        lineStyle: { width: 1.6 }, itemStyle: { color: trace.color },
        tooltip: { valueFormatter: value => formatTrendValue(Number(Array.isArray(value) ? value[1] : value), trace, aggregated, labels) },
        data: trace.points.map(point => [point.ts, point.value])
      })
    }
    top += height + 44
  })
  const height = Math.max(300, top + 20)
  const axisIndices = tracks.map((_, index) => index)
  const option: EChartsOption = {
    textStyle: { color: palette.text },
    tooltip: { trigger: 'axis', confine: true, axisPointer: { type: 'cross' } },
    axisPointer: { link: [{ xAxisIndex: 'all' }] },
    legend: { type: 'scroll', top: 4, textStyle: { color: palette.text }, tooltip: { show: true } },
    grid, xAxis, yAxis, series, animation: false,
    dataZoom: [
      { type: 'inside', xAxisIndex: axisIndices, filterMode: 'none', start: 0, end: 100 },
      { type: 'slider', xAxisIndex: axisIndices, filterMode: 'none', height: 18, bottom: 8, showDetail: false, ...palette.zoom, handleSize: '80%', moveHandleSize: 3 }
    ]
  }
  return { tracks, height, option }
}
