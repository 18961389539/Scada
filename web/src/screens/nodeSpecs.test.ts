import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, it } from 'vitest'
import {
  EMPTY_COLLECTION_FIELDS, EMPTY_OBJECT_FIELDS, FALSEY_NODE_FLAGS, FONT_SIZE_ORDER, hasFeature, NODE_SPECS, NODE_TYPES,
  NULLABLE_NODE_FIELDS, PALETTE_ORDER, typesWith, type NodeType
} from './nodeSpecs'
import { chartTypes, cleanNode, controlTypes, createNode, defaultFontSizes, fontSizeTypes, palette, type ScreenNode } from './model'
import { tr } from '../i18n'

// 重构前的硬编码清单：派生结果必须逐项一致，避免注册表顺手改了界面行为。
const oldChartTypes: NodeType[] = ['chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal', 'chart-bar-stack', 'chart-area', 'chart-radar', 'chart-funnel', 'chart-gauge', 'alarm-stats']
const oldControlTypes: NodeType[] = ['switch', 'slider', 'spinner', 'input', 'jog', 'pulse', 'selector', 'knob']
const oldFontSizeTypes: NodeType[] = [
  'text', 'value', 'button', 'rectangle', 'ellipse', 'marquee', 'kpi', 'progress', 'clock',
  'gauge', 'dial', 'switch', 'slider', 'spinner', 'input', 'equipment-panel',
  'jog', 'pulse', 'selector', 'knob', 'reset', 'silence', 'recipe',
  'symbol', 'pump', 'valve', 'motor', 'fan', 'tank', 'alarm-banner', 'oee-kpi'
]
const oldWindowTypes: NodeType[] = ['chart-line', 'chart-area', 'history-table', 'oee-kpi']
const oldMultiTagTypes: NodeType[] = ['chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal', 'chart-bar-stack', 'chart-area', 'chart-radar', 'chart-funnel', 'table', 'history-table']
const oldPaletteTypes: NodeType[] = [
  'polyline', 'equipment-panel', 'input', 'text', 'value', 'pump', 'valve', 'tank', 'pipe', 'motor', 'fan',
  'gauge', 'dial', 'button', 'switch', 'slider', 'spinner', 'jog', 'pulse', 'selector', 'knob', 'reset', 'silence', 'recipe',
  'led', 'progress', 'kpi', 'clock', 'image', 'marquee', 'chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal',
  'chart-bar-stack', 'chart-area', 'chart-radar', 'chart-funnel', 'chart-gauge', 'alarm-banner', 'alarm-stats', 'alarm-list',
  'table', 'history-table', 'oee-kpi', 'rectangle', 'ellipse'
]
const oldDataBindingTypes: NodeType[] = ['value', 'symbol', 'pump', 'valve', 'tank', 'gauge', 'dial', 'motor', 'fan', 'led', 'progress', 'kpi', 'equipment-panel', 'pipe', 'polyline']
const oldTypeSettingTypes: NodeType[] = ['polyline', 'pipe', 'symbol', 'pump', 'valve', 'motor', 'fan', 'tank', 'gauge', 'dial', 'equipment-panel', 'image', 'chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal', 'chart-bar-stack', 'chart-area', 'chart-radar', 'chart-funnel', 'chart-gauge', 'table', 'history-table', 'oee-kpi', 'selector', 'knob', 'reset', 'silence', 'recipe']
const oldLabelTypes: NodeType[] = ['text', 'button', 'rectangle', 'ellipse', 'marquee', 'symbol', 'pump', 'valve', 'motor', 'fan', 'tank', 'equipment-panel', 'input', 'kpi']
const oldTextStyleTypes: NodeType[] = ['text', 'value', 'button', 'rectangle', 'ellipse']
const oldShapeStyleTypes: NodeType[] = ['rectangle', 'button', 'ellipse']
const oldControlRangeTypes: NodeType[] = ['slider', 'spinner', 'input']
const oldStatesTypes: NodeType[] = ['symbol', 'pump', 'valve', 'motor', 'fan', 'tank', 'equipment-panel', 'selector', 'knob']
const oldAnimationTypes: NodeType[] = ['symbol', 'pipe', 'polyline', 'pump', 'tank', 'valve']
const oldChartOptionTypes: NodeType[] = ['chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal', 'chart-bar-stack', 'chart-area', 'chart-radar', 'chart-funnel', 'chart-gauge']
const oldChartTargetTypes: NodeType[] = ['chart-line', 'chart-area', 'chart-bar', 'chart-bar-horizontal', 'chart-bar-stack']
const sorted = (types: NodeType[]): NodeType[] => [...types].sort()

const baseNode = (): ScreenNode => ({
  id: 'n1', type: 'text', x: 0, y: 0, width: 100, height: 50, rotation: 0, text: '标题', color: '#7394b8', activeColor: '#34d399', // i18n-ignore
  unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId: null,
  action: { type: 'none', screenId: null, tagId: null, value: 1 }
})

describe('node registry', () => {
  it('covers every node type exactly once with a label key', () => {
    expect(NODE_TYPES).toHaveLength(49)
    expect(new Set(NODE_TYPES).size).toBe(49)
    for (const type of NODE_TYPES) expect(NODE_SPECS[type].labelKey).toBeTruthy()
    expect(PALETTE_ORDER).toHaveLength(47)
    expect(palette.map(item => item.type)).toEqual(oldPaletteTypes)
    expect(new Set([...PALETTE_ORDER, 'symbol', 'template'])).toEqual(new Set(NODE_TYPES))
  })

  it('keeps derived lists identical to the old hardcoded ones', () => {
    expect(chartTypes).toEqual(oldChartTypes)
    expect(controlTypes).toEqual(oldControlTypes)
    expect(fontSizeTypes).toEqual(oldFontSizeTypes)
    expect(Object.keys(defaultFontSizes)).toEqual(oldFontSizeTypes)
    expect(FONT_SIZE_ORDER).toEqual(oldFontSizeTypes)
    expect(typesWith('window')).toEqual(oldWindowTypes)
    expect(typesWith('multiTag')).toEqual(oldMultiTagTypes)
    expect(sorted(typesWith('dataBinding'))).toEqual(sorted(oldDataBindingTypes))
    expect(sorted(typesWith('typeSettings'))).toEqual(sorted(oldTypeSettingTypes))
    expect(sorted(typesWith('labelText'))).toEqual(sorted(oldLabelTypes))
    expect(sorted(typesWith('textStyle'))).toEqual(sorted(oldTextStyleTypes))
    expect(sorted(typesWith('shapeStyle'))).toEqual(sorted(oldShapeStyleTypes))
    expect(sorted(typesWith('controlRange'))).toEqual(sorted(oldControlRangeTypes))
    expect(sorted(typesWith('states'))).toEqual(sorted(oldStatesTypes))
    expect(sorted(typesWith('animation'))).toEqual(sorted(oldAnimationTypes))
    expect(typesWith('chartOptions')).toEqual(oldChartOptionTypes)
    expect(sorted(typesWith('chartTarget'))).toEqual(sorted(oldChartTargetTypes))
  })

  it('answers feature queries only for known types', () => {
    expect(hasFeature('gauge', 'gauge')).toBe(true)
    expect(hasFeature('text', 'gauge')).toBe(false)
    expect(hasFeature('selector', 'positional')).toBe(true)
    expect(hasFeature('knob', 'positional')).toBe(true)
    expect(hasFeature(null, 'states')).toBe(false)
    expect(hasFeature(undefined, 'states')).toBe(false)
    expect(hasFeature('unknown' as NodeType, 'states')).toBe(false)
  })

  it('matches the shared cross-language type fixture', () => {
    const fixture = JSON.parse(readFileSync(resolve(process.cwd(), '../tests/fixtures/screen-node-types.json'), 'utf8')) as string[]

    expect(fixture).toHaveLength(49)
    expect([...NODE_TYPES].sort()).toEqual(fixture)
  })
})

describe('cleanNode field groups', () => {
  it('keeps the previous deletion rules', () => {
    const node: ScreenNode = {
      ...baseNode(), symbol: null, bindings: [], tags: [], tagBinds: null, texts: {}, gaugeBands: [], tableColumns: [],
      chartOptions: {}, points: [], states: [], commands: [], opacity: 0, locked: false, wrap: false
    }

    expect(JSON.stringify(cleanNode(node))).toBe(JSON.stringify({ ...baseNode(), points: [], states: [], commands: [], opacity: 0 }))
  })

  it('keeps every non-empty extension untouched', () => {
    const node: ScreenNode = {
      ...baseNode(),
      symbol: 'isa:pump', alarmTagId: 2, templateKey: 'tpl', bindings: [{ key: 'level', tagId: 3 }], bind: 'level',
      alarmBind: 'alarm', texts: { 'en-US': 'Title' }, fontSize: 20, align: 'center', wrap: true, autoFit: true,
      bold: true, opacity: 0.5, borderWidth: 2, radius: 4, groupId: 'g1', locked: true, hidden: true, tags: [1, 2],
      tagBinds: ['a'], animationBind: 'a', windowSec: 300, lineId: 1,
      animation: { type: 'rotate', source: 'value', tagId: 1, from: 0, to: 100, outFrom: 0, outTo: 360 },
      points: [{ x: 0, y: 0 }],
      states: [{ value: 0, label: '停', color: '#111111' }], source: { nodeId: 'a', port: 'right' }, // i18n-ignore
      target: { nodeId: 'b', port: 'left' }, commands: [{ key: 'start', label: '启动', tagId: 1, value: 1 }], // i18n-ignore
      releaseValue: 0, pulseMs: 500, maxHoldMs: 5000, resetMode: 'tag', silenceMinutes: 5, recipeTemplateId: 1,
      gaugeBands: [{ from: 0, to: 50, color: '#00ff00' }], setpoint: 60, tableColumns: ['name'],
      chartOptions: { legend: true, targetLines: [{ value: 60, color: '#ff0000', label: '上限' }] }, // i18n-ignore
      action: { type: 'command', screenId: null, tagId: 1, value: 1, bind: 'cmd', sequenceId: 2, recipeId: 3, jobId: 4 }
    }

    expect(JSON.stringify(cleanNode(node))).toBe(JSON.stringify(node))
  })

  it('deletes nulls, empty collections/objects and false flags', () => {
    const node: ScreenNode = {
      ...baseNode(), symbol: null, bindings: null, tags: null, tagBinds: null, texts: null, gaugeBands: null,
      tableColumns: null, chartOptions: null, bind: null, alarmBind: null, templateKey: null,
      wrap: false, autoFit: false, bold: false, locked: false, hidden: false
    }
    const cleaned = cleanNode(node)

    for (const key of [...NULLABLE_NODE_FIELDS, ...FALSEY_NODE_FLAGS]) expect(cleaned).not.toHaveProperty(key)
    for (const key of [...EMPTY_COLLECTION_FIELDS, ...EMPTY_OBJECT_FIELDS]) expect(cleaned).not.toHaveProperty(key)
  })
})

describe('createNode defaults', () => {
  // 与重构前 nodeSize / createNode 的默认尺寸逐项一致。
  const oldSizes: Record<NodeType, [number, number]> = {
    text: [140, 48], value: [140, 48], pump: [140, 112], valve: [140, 112], tank: [140, 112], pipe: [200, 48],
    rectangle: [140, 112], ellipse: [140, 112], button: [140, 48], gauge: [140, 112], motor: [140, 112], fan: [140, 112],
    symbol: [100, 100], template: [140, 112], led: [72, 72], progress: [220, 40], kpi: [200, 112], clock: [240, 96],
    image: [200, 150], marquee: [320, 48], dial: [140, 112], 'chart-bar': [280, 180], 'chart-pie': [240, 240],
    'chart-line': [420, 200], 'chart-bar-horizontal': [280, 200], 'chart-bar-stack': [280, 180], 'chart-area': [420, 200],
    'chart-radar': [240, 240], 'chart-funnel': [240, 240], 'chart-gauge': [220, 180], 'alarm-banner': [420, 48],
    'alarm-stats': [280, 180], 'alarm-list': [380, 200], table: [320, 180], 'history-table': [440, 180],
    'oee-kpi': [300, 200], switch: [120, 56], slider: [240, 56], spinner: [160, 56], input: [220, 84],
    polyline: [240, 120], 'equipment-panel': [320, 300], reset: [120, 56], jog: [120, 56], pulse: [120, 56],
    silence: [120, 56], selector: [220, 72], knob: [140, 140], recipe: [300, 150]
  }

  it('keeps the old default size for every type', () => {
    for (const type of NODE_TYPES) expect([createNode(type).width, createNode(type).height], type).toEqual(oldSizes[type])
  })

  it('keeps default text, tags, window and control flags', () => {
    for (const type of NODE_TYPES) {
      const node = createNode(type)
      const label = palette.find(item => item.type === type)?.label
      expect(node.type, type).toBe(type)
      expect(node.text, type).toBe(type === 'image' ? '' : type === 'symbol' ? tr('model.centrifugalPump') : label ?? type)
      expect(node.tags, type).toEqual(oldMultiTagTypes.includes(type) ? [] : null)
      expect(node.windowSec, type).toBe(oldWindowTypes.includes(type) ? 300 : null)
      expect(node.points?.length ?? null, type).toBe(type === 'polyline' ? 4 : null)
      expect(node.states?.length ?? null, type).toBe(type === 'selector' || type === 'knob' ? 2 : null)
      expect(node.commands ?? null, type).toEqual(type === 'equipment-panel' ? [] : null)
      expect(node.releaseValue, type).toBe(type === 'jog' || type === 'pulse' ? 0 : null)
      expect(node.pulseMs, type).toBe(type === 'pulse' ? 500 : null)
      expect(node.maxHoldMs, type).toBe(type === 'jog' ? 5000 : null)
      expect(node.resetMode, type).toBe(type === 'reset' ? 'alarms' : null)
      expect(node.silenceMinutes, type).toBe(type === 'silence' ? 5 : null)
      expect(node.action.type, type).toBe(oldControlTypes.includes(type) ? 'command' : 'none')
    }
  })

  it('resolves every palette label', () => {
    for (const type of PALETTE_ORDER) {
      const label = palette.find(item => item.type === type)!.label
      expect(label, type).not.toBe(NODE_SPECS[type].labelKey)
      expect(label.length, type).toBeGreaterThan(0)
    }
  })
})
