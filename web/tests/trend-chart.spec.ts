import { test, expect } from './fixtures'
import { buildTrendChart, formatTrendValue, type TrendChartLabels, type TrendTrace } from '../src/components/trendChart'
import type { ChartPalette } from '../src/theme'

const palette: ChartPalette = {
  text: '#333', muted: '#777', split: '#eee', annotationBg: '#fff', annotationText: '#333',
  zoom: {
    borderColor: '#eee', backgroundColor: '#fff', fillerColor: '#ccc',
    dataBackground: { lineStyle: { color: '#ccc' }, areaStyle: { color: '#eee' } },
    selectedDataBackground: { lineStyle: { color: '#333' }, areaStyle: { color: '#ccc' } },
    handleStyle: { color: '#fff', borderColor: '#333' }, moveHandleStyle: { color: '#333' }
  }
}
const labels: TrendChartLabels = {
  unitless: 'No unit', boolean: 'Boolean (0/1)', aggregateBoolean: 'Boolean sample mean (0-1)',
  zero: 'Clear (0)', one: 'Set (1)', formatNumber: value => value.toFixed(3)
}
function trace(tagId: number, unit: string, isBoolean = false, values = [0, 1, 0]): TrendTrace {
  return {
    tagId, name: `Device / Point ${tagId}`, unit, isBoolean, color: '#1668dc',
    points: values.map((value, index) => ({ ts: 1000 + index * 1000, value, quality: 'Good' }))
  }
}

test('trend axes group compatible units and give each Boolean signal its own discrete lane', () => {
  const model = buildTrendChart([trace(1, 'C', false, [100, 110]), trace(2, 'C', false, [90, 95]), trace(3, 'MPa', false, [0.1, 0.2]), trace(4, '', true), trace(5, '', true)], {
    from: 0, to: 5000, resolution: 'Raw', palette, labels
  })
  expect(model.tracks.map(track => track.traces.map(item => item.tagId))).toEqual([[1, 2], [3], [4], [5]])
  const { series, yAxis, xAxis, dataZoom } = model.option
  if (!Array.isArray(series) || !Array.isArray(yAxis) || !Array.isArray(xAxis) || !Array.isArray(dataZoom)) throw new Error('Expected aligned chart component arrays')
  expect(xAxis).toHaveLength(4)
  expect(xAxis.every(axis => axis.min === 0 && axis.max === 5000)).toBe(true)
  expect(dataZoom.every(zoom => JSON.stringify(zoom.xAxisIndex) === '[0,1,2,3]')).toBe(true)
  for (const id of ['4', '5']) {
    const item = series.find(series => series.id === id)
    if (item?.type !== 'line') throw new Error('Expected a line series')
    expect(item.step).toBe('end')
    expect(item.sampling).toBeUndefined()
    expect(item.data).toEqual([[1000, 0], [2000, 1], [3000, 0]])
    expect(yAxis[Number(item.yAxisIndex)]).toMatchObject({ min: 0, max: 1, interval: 1 })
  }
  expect(model.height).toBeGreaterThan(500)
})

test('aggregate Boolean sample means remain fractional and are not presented as physical states', () => {
  const item = trace(7, '', true, [0.25, 0.75])
  const model = buildTrendChart([item], { from: 0, to: 5000, resolution: 'FiveMinute', palette, labels })
  const series = model.option.series
  if (!Array.isArray(series) || series[0]?.type !== 'line') throw new Error('Expected a line series')
  expect(model.tracks[0].label).toContain('sample mean')
  expect(series[0].step).toBe(false)
  expect(series[0].data).toEqual([[1000, 0.25], [2000, 0.75]])
  expect(formatTrendValue(0.25, item, true, labels)).toBe('0.250')
  expect(formatTrendValue(1, item, true, labels)).toBe('1.000')
  expect(formatTrendValue(1, item, false, labels)).toBe('Set (1)')
  expect(formatTrendValue(0, item, false, labels)).toBe('Clear (0)')
  expect(formatTrendValue(0.25, item, false, labels)).toBe('0.250')
  expect(formatTrendValue(1.5, trace(8, 'MPa'), false, labels)).toBe('1.500 MPa')
})

test('unconfigured units are not assumed compatible and empty series do not create misleading tracks', () => {
  const empty = trace(3, 'C'); empty.points = []
  const model = buildTrendChart([trace(1, ''), trace(2, ''), empty], { from: 0, to: 5000, resolution: 'Raw', palette, labels })
  expect(model.tracks).toHaveLength(2)
  expect(model.tracks.every(track => track.label.includes('No unit'))).toBe(true)
  expect(buildTrendChart([], { from: 0, to: 5000, resolution: 'Raw', palette, labels }).height).toBe(300)
})
