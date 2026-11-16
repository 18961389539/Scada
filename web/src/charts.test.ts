// @vitest-environment node
import { describe, expect, it } from 'vitest'
import { init } from './charts'

describe('charts', () => {
  it('renders a server-side SVG with the registered renderers and series', () => {
    const chart = init(null, null, { renderer: 'svg', ssr: true, width: 240, height: 160 })
    try {
      chart.setOption({
        xAxis: { type: 'category', data: ['a', 'b', 'c'] },
        yAxis: { type: 'value' },
        series: [{ type: 'line', data: [1, 2, 3] }]
      })
      const svg = chart.renderToSVGString()
      expect(svg).toContain('<svg')
      expect(svg).toContain('</svg>')
      expect(svg.length).toBeGreaterThan(500)
    } finally {
      chart.dispose()
    }
  })

  it('keeps the gauge and pie renderers usable after tree-shaken registration', () => {
    const chart = init(null, null, { renderer: 'svg', ssr: true, width: 200, height: 200 })
    try {
      chart.setOption({ series: [{ type: 'gauge', data: [{ value: 42 }] }] })
      expect(chart.renderToSVGString()).toContain('<svg')
      chart.setOption({ series: [{ type: 'pie', data: [{ name: 'x', value: 1 }] }] })
      expect(chart.renderToSVGString()).toContain('<svg')
    } finally {
      chart.dispose()
    }
  })
})