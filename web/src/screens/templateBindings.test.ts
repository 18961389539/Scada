import { describe, expect, it } from 'vitest'
import { emptyDocument, type ScreenDocument, type ScreenNode } from './model'
import {
  TemplateBindingError, templateFromEditor, templatePlaceholderInUse, templateToEditor,
  type TemplatePlaceholder
} from './templateBindings'

const placeholders: TemplatePlaceholder[] = [{ key: 'level', tagId: -1 }, { key: 'temperature', tagId: -2 }]

const node = (overrides: Partial<ScreenNode>): ScreenNode => ({
  id: 'node-1', type: 'value', x: 0, y: 0, width: 100, height: 40, rotation: 0,
  text: '', color: '#fff', activeColor: '#0f0', unit: '', decimals: 1, min: 0, max: 100,
  activeThreshold: 0.5, tagId: null, action: { type: 'none', screenId: null, tagId: null, value: 1 },
  ...overrides
})

const document = (nodes: ScreenNode[]): ScreenDocument => ({ ...emptyDocument(), nodes })

describe('templateToEditor', () => {
  it('turns placeholder keys into pseudo tags on every binding surface', () => {
    const source = document([node({
      bind: 'level', alarmBind: 'temperature', tagBinds: ['level', 'temperature'],
      action: { type: 'command', screenId: null, tagId: null, value: 1, bind: 'level' },
      commands: [{ key: 'start', label: 'Start', tagId: null, bind: 'temperature', value: 1 }],
      animation: { type: 'level', source: 'value', tagId: null, from: 0, to: 100, outFrom: 0, outTo: 100 },
      animationBind: 'temperature'
    })])

    const edited = templateToEditor(source, placeholders).nodes[0]

    expect(edited).toMatchObject({
      tagId: -1, alarmTagId: -2, tags: [-1, -2], bind: null, alarmBind: null,
      action: { tagId: -1, bind: null },
      commands: [{ tagId: -2, bind: null }],
      animation: { tagId: -2 }
    })
    expect(edited.tagBinds).toBeUndefined()
    expect(edited.animationBind).toBeUndefined()
    expect(source.nodes[0].bind).toBe('level')
  })

  it('rejects unknown placeholder keys with the node and binding name', () => {
    let error: unknown
    try {
      templateToEditor(document([node({ bind: 'missing' })]), placeholders)
    } catch (caught) {
      error = caught
    }

    expect(error).toBeInstanceOf(TemplateBindingError)
    expect(error).toMatchObject({ nodeId: 'node-1', binding: 'missing' })
  })
})

describe('templateFromEditor', () => {
  it('round-trips editor pseudo tags back into named bindings', () => {
    const source = document([node({
      bind: 'level', alarmBind: 'temperature', tagBinds: ['level', 'temperature'],
      action: { type: 'command', screenId: null, tagId: null, value: 1, bind: 'level' },
      commands: [{ key: 'start', label: 'Start', tagId: null, bind: 'temperature', value: 1 }],
      animation: { type: 'level', source: 'value', tagId: null, from: 0, to: 100, outFrom: 0, outTo: 100 },
      animationBind: 'temperature'
    })])

    const restored = templateFromEditor(templateToEditor(source, placeholders), placeholders).nodes[0]

    expect(restored).toMatchObject({
      tagId: null, alarmTagId: null, bind: 'level', alarmBind: 'temperature', tagBinds: ['level', 'temperature'],
      action: { tagId: null, bind: 'level' },
      commands: [{ tagId: null, bind: 'temperature' }],
      animation: { tagId: null }, animationBind: 'temperature'
    })
    expect(restored.tags).toBeUndefined()
  })

  it('rejects pseudo tags that are not in the placeholder list', () => {
    expect(() => templateFromEditor(document([node({ tagId: -99 })]), placeholders))
      .toThrowError(TemplateBindingError)
  })
})

describe('templatePlaceholderInUse', () => {
  it('detects a placeholder used by key or by pseudo tag', () => {
    expect(templatePlaceholderInUse(document([node({ action: { type: 'navigate', screenId: 1, tagId: null, value: 1, bind: 'level' } })]), placeholders[0])).toBe(true)
    expect(templatePlaceholderInUse(document([node({ animation: { type: 'rotate', source: 'value', tagId: -2, from: 0, to: 100, outFrom: 0, outTo: 100 } })]), placeholders[1])).toBe(true)
    expect(templatePlaceholderInUse(document([node({ tagId: 7 })]), placeholders[0])).toBe(false)
  })
})
