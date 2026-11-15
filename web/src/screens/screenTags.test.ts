import { describe, expect, it } from 'vitest'
import type { ScreenNode } from './model'
import { collectScreenTags } from './screenTags'

const node: ScreenNode = {
  id: 'node-1', type: 'equipment-panel', x: 0, y: 0, width: 100, height: 100, rotation: 0,
  text: '', color: '#fff', activeColor: '#0f0', unit: '', decimals: 1, min: 0, max: 100,
  activeThreshold: 0.5, tagId: 1, alarmTagId: 2, tags: [3, 0],
  bindings: [{ key: 'level', tagId: 4 }],
  commands: [{ key: 'start', label: 'Start', tagId: 5, value: 1 }],
  animation: { type: 'rotate', source: 'value', tagId: 6, from: 0, to: 100, outFrom: 0, outTo: 100 },
  action: { type: 'command', screenId: null, tagId: 7, value: 1 }
}

describe('collectScreenTags', () => {
  it('collects every tag source and ignores zero or empty ids', () => {
    const tags = new Set<number>()
    collectScreenTags(node, tags)

    expect([...tags].sort((left, right) => left - right)).toEqual([1, 2, 3, 4, 5, 6, 7])
  })

  it('keeps existing ids when merging nodes', () => {
    const tags = new Set<number>([3])
    collectScreenTags(node, tags)

    expect(tags.size).toBe(7)
  })
})
