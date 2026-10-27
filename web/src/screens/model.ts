import { tr } from '../i18n'
export type NodeType = 'text' | 'value' | 'pump' | 'valve' | 'tank' | 'pipe' | 'rectangle' | 'ellipse' | 'button' | 'gauge' | 'motor' | 'fan' | 'symbol' | 'template' | 'led' | 'progress' | 'kpi' | 'clock' | 'image' | 'marquee' | 'dial' | 'chart-bar' | 'chart-pie' | 'chart-line' | 'alarm-banner' | 'alarm-stats' | 'alarm-list' | 'table' | 'history-table' | 'oee-kpi' | 'switch' | 'slider' | 'spinner'
export interface ScreenAction { type: 'none' | 'navigate' | 'popup' | 'command'; screenId: number | null; tagId: number | null; value: number; bind?: string | null }
export interface ScreenNode {
  id: string; type: NodeType; x: number; y: number; width: number; height: number; rotation: number
  text: string; color: string; activeColor: string; unit: string; decimals: number; min: number; max: number
  activeThreshold: number; tagId: number | null; action: ScreenAction
  // 可选扩展（为空不保存）：图库符号、报警状态测点、模板实例及绑定、模板内占位符、多语言文字、多测点（图表/表格）、统计窗口秒数、OEE 产线。
  symbol?: string | null; alarmTagId?: number | null; templateKey?: string | null; bindings?: ScreenBinding[] | null
  bind?: string | null; alarmBind?: string | null; texts?: Record<string, string> | null
  tags?: number[] | null; windowSec?: number | null; lineId?: number | null
  // 仅渲染用：展开后的模板内部图元指向其实例。
  ownerId?: string
}
export interface ScreenBinding { key: string; tagId: number | null }
export interface LibraryItem {
  id: number; key: string; name: string; category: string; kind: 'symbol' | 'template'; svg: string | null; width: number; height: number
  revision: number; updatedTs: number; updatedBy: string; document: ScreenDocument | null; placeholders: string[]
}
export const SEPARATOR = '--'
// 图表类图元（在画面内以 ECharts SVG 输出）。
export const chartTypes: NodeType[] = ['chart-bar', 'chart-pie', 'chart-line', 'alarm-stats']
// 写交互控件（通过控制闭环下发运行期取值）。
export const controlTypes: NodeType[] = ['switch', 'slider', 'spinner']
// 需要绑定多个测点（tags）的图元类型。
const tagTypes: NodeType[] = ['chart-bar', 'chart-pie', 'chart-line', 'table', 'history-table']
// 需要统计窗口（windowSec）的图元类型。
const windowTypes: NodeType[] = ['chart-line', 'history-table', 'oee-kpi']
// 与服务端 ScreenExpander 相同：模板实例展开为内部图元（编号“实例--内部”），按实例绑定解析占位符；实例自身保留为一个透明框（编辑选取用）。
export function expandNodes(nodes: ScreenNode[], library: LibraryItem[]): ScreenNode[] {
  const out: ScreenNode[] = []
  for (const node of nodes) {
    if (node.type !== 'template') { out.push(node); continue }
    const template = library.find(x => x.kind === 'template' && x.key === node.templateKey)
    if (template?.document) {
      const sx = node.width / template.width, sy = node.height / template.height
      const bound = new Map((node.bindings ?? []).map(b => [b.key, b.tagId]))
      const resolve = (key?: string | null) => (key ? bound.get(key) ?? null : null)
      for (const inner of template.document.nodes) out.push({
        ...inner, id: `${node.id}${SEPARATOR}${inner.id}`, ownerId: node.id, x: node.x + inner.x * sx, y: node.y + inner.y * sy, width: inner.width * sx, height: inner.height * sy,
        tagId: inner.bind ? resolve(inner.bind) : null, alarmTagId: inner.alarmBind ? resolve(inner.alarmBind) : null, bind: null, alarmBind: null,
        action: { ...inner.action, tagId: inner.action.bind ? resolve(inner.action.bind) : null, bind: null }
      })
    }
    out.push(node)
  }
  return out
}
// 画面保存前去掉空的扩展字段，保持与旧格式一致。
export function cleanNode(node: ScreenNode): ScreenNode {
  const copy: ScreenNode = { ...node, action: { ...node.action } }
  for (const k of ['symbol', 'alarmTagId', 'templateKey', 'bindings', 'bind', 'alarmBind', 'texts', 'tags', 'windowSec', 'lineId', 'ownerId'] as const) if (copy[k] == null || (k === 'texts' && !Object.keys(copy.texts ?? {}).length) || (k === 'tags' && !copy.tags?.length)) delete copy[k]
  if (copy.action.bind == null) delete copy.action.bind
  return copy
}
export interface ScreenDocument { schemaVersion: number; width: number; height: number; background: string; nodes: ScreenNode[] }
export interface ScreenRect { x: number; y: number; width: number; height: number }
export function nodesInRect(nodes: ScreenNode[], box: ScreenRect): ScreenNode[] {
  return nodes.filter(node => {
    const radians = node.rotation * Math.PI / 180
    const width = Math.abs(node.width * Math.cos(radians)) + Math.abs(node.height * Math.sin(radians))
    const height = Math.abs(node.width * Math.sin(radians)) + Math.abs(node.height * Math.cos(radians))
    const left = node.x + (node.width - width) / 2, top = node.y + (node.height - height) / 2
    return left < box.x + box.width && left + width > box.x && top < box.y + box.height && top + height > box.y
  })
}
export interface ScreenSummary { id: number; name: string; revision: number; publishedRevision: number | null; updatedTs: number; updatedBy: string; hasDraftChanges: boolean }
export interface ScreenDetail extends ScreenSummary { document: ScreenDocument }
export interface ScreenIssue { nodeId: string; message: string }
export const palette: { type: NodeType; label: string; icon: string }[] = [
  { type: 'text', label: tr('model.kf4d3da'), icon: 'T' }, { type: 'value', label: tr('model.k521523'), icon: '123' },
  { type: 'pump', label: tr('model.kb23932'), icon: '◉' }, { type: 'valve', label: tr('model.k257555'), icon: '⋈' },
  { type: 'tank', label: tr('model.kca4d3a'), icon: '▤' }, { type: 'pipe', label: tr('model.kad30d9'), icon: '━' },
  { type: 'motor', label: tr('model.k758b20'), icon: 'M' }, { type: 'fan', label: tr('model.k41eef0'), icon: '✣' },
  { type: 'gauge', label: tr('model.kaad5df'), icon: '◴' }, { type: 'dial', label: tr('model.k776360'), icon: '◔' },
  { type: 'button', label: tr('model.ka6a6ea'), icon: '▣' },
  { type: 'switch', label: tr('model.kd7716c'), icon: '⏻' }, { type: 'slider', label: tr('model.k6c9e05'), icon: '⇔' }, { type: 'spinner', label: tr('model.k71cbc3'), icon: '±' },
  { type: 'led', label: tr('model.kb5e5d1'), icon: '●' }, { type: 'progress', label: tr('model.k6eb113'), icon: '▰' },
  { type: 'kpi', label: tr('model.kd6a373'), icon: '▢' }, { type: 'clock', label: tr('model.kcb6d97'), icon: '◷' },
  { type: 'image', label: tr('model.kbe8da6'), icon: '▨' }, { type: 'marquee', label: tr('model.k310602'), icon: '⇄' },
  { type: 'chart-bar', label: tr('model.k6b30b4'), icon: '▮' }, { type: 'chart-pie', label: tr('model.kbdf13a'), icon: '◕' },
  { type: 'chart-line', label: tr('model.kfcf8ed'), icon: '⌇' },
  { type: 'alarm-banner', label: tr('model.ka0af41'), icon: '⚠' }, { type: 'alarm-stats', label: tr('model.kaaca95'), icon: '▦' },
  { type: 'alarm-list', label: tr('model.k459887'), icon: '☰' }, { type: 'table', label: tr('model.k09c6cd'), icon: '▤' }, { type: 'history-table', label: tr('model.kc3abd8'), icon: '▦' },
  { type: 'oee-kpi', label: tr('model.ke310c3'), icon: '◎' },
  { type: 'rectangle', label: tr('model.kb5d5ad'), icon: '▭' }, { type: 'ellipse', label: tr('model.ke00623'), icon: '◯' }
]
export function emptyDocument(): ScreenDocument { return { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [] } }
const nodeSize: Partial<Record<NodeType, [number, number]>> = { led: [72, 72], progress: [220, 40], kpi: [200, 112], clock: [240, 96], image: [200, 150], marquee: [320, 48], 'chart-bar': [280, 180], 'chart-pie': [240, 240], 'chart-line': [420, 200], 'alarm-banner': [420, 48], 'alarm-stats': [280, 180], 'alarm-list': [380, 200], table: [320, 180], 'history-table': [440, 180], 'oee-kpi': [300, 200], switch: [120, 56], slider: [240, 56], spinner: [160, 56] }
export function createNode(type: NodeType, x = 80, y = 80): ScreenNode {
  if (type === 'symbol') return createSymbolNode('isa:pump', 100, 100, tr('model.ka0dcad'), x, y)
  const box = nodeSize[type]
  const defaultText = palette.find(p => p.type === type)?.label ?? type
  return { id: crypto.randomUUID(), type, x, y, width: box ? box[0] : type === 'pipe' ? 200 : 140,
    height: box ? box[1] : ['text', 'value', 'button', 'pipe'].includes(type) ? 48 : 112,
    rotation: 0, text: type === 'image' ? '' : defaultText, color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId: null,
    tags: tagTypes.includes(type) ? [] : null,
    windowSec: windowTypes.includes(type) ? 300 : null,
    lineId: null,
    action: { type: 'none', screenId: null, tagId: null, value: 1 } }
}
export function createSymbolNode(symbol: string, width: number, height: number, text: string, x = 80, y = 80): ScreenNode {
  return { ...createNode('rectangle', x, y), type: 'symbol', symbol, width, height, text, color: '#6b7580', activeColor: '#e9edf1' }
}
export function createTemplateNode(item: LibraryItem, x = 80, y = 80): ScreenNode {
  return { ...createNode('rectangle', x, y), type: 'template', templateKey: item.key, width: item.width, height: item.height, text: item.name,
    bindings: item.placeholders.map(key => ({ key, tagId: null })) }
}
export function clampNode(node: ScreenNode, doc: ScreenDocument): ScreenNode {
  const width = Math.min(doc.width, Math.max(16, node.width)), height = Math.min(doc.height, Math.max(16, node.height))
  return { ...node, width, height, x: Math.max(0, Math.min(doc.width - width, node.x)), y: Math.max(0, Math.min(doc.height - height, node.y)) }
}
export function translateNodes(nodes: ScreenNode[], doc: ScreenDocument, dx: number, dy: number): ScreenNode[] {
  if (!nodes.length) return []
  const offsetX = Math.max(-Math.min(...nodes.map(node => node.x)), Math.min(doc.width - Math.max(...nodes.map(node => node.x + node.width)), dx))
  const offsetY = Math.max(-Math.min(...nodes.map(node => node.y)), Math.min(doc.height - Math.max(...nodes.map(node => node.y + node.height)), dy))
  return nodes.map(node => ({ ...node, x: node.x + offsetX, y: node.y + offsetY }))
}
export type ArrangeOperation = 'left' | 'center-x' | 'right' | 'top' | 'center-y' | 'bottom' | 'space-x' | 'space-y' | 'width' | 'height' | 'size'
export function arrangeNodes(nodes: ScreenNode[], doc: ScreenDocument, operation: ArrangeOperation): ScreenNode[] | null {
  if (nodes.length < 2) return nodes
  const anchor = nodes[0]
  if (operation === 'width' || operation === 'height' || operation === 'size')
    return nodes.map(node => clampNode({ ...node, width: operation === 'height' ? node.width : anchor.width,
      height: operation === 'width' ? node.height : anchor.height }, doc))
  const horizontal = ['left', 'center-x', 'right', 'space-x'].includes(operation)
  const position = horizontal ? 'x' : 'y', size = horizontal ? 'width' : 'height'
  if (operation === 'space-x' || operation === 'space-y') {
    if (nodes.length < 3) return nodes
    const ordered = [...nodes].sort((first, second) => first[position] - second[position])
    const start = ordered[0][position], end = Math.max(...nodes.map(node => node[position] + node[size]))
    const gap = (end - start - nodes.reduce((sum, node) => sum + node[size], 0)) / (nodes.length - 1)
    if (gap < 0) return null
    let cursor = start
    const positions = new Map(ordered.map(node => { const at = cursor; cursor += node[size] + gap; return [node.id, at] }))
    return nodes.map(node => ({ ...node, [position]: positions.get(node.id)! }))
  }
  const fraction = operation.startsWith('center') ? .5 : operation === 'right' || operation === 'bottom' ? 1 : 0
  const largest = Math.max(...nodes.map(node => node[size]))
  const target = Math.max(largest * fraction, Math.min(doc[size] - largest * (1 - fraction), anchor[position] + anchor[size] * fraction))
  return nodes.map(node => ({ ...node, [position]: target - node[size] * fraction }))
}
// Snapshots include the title: undo/redo must restore all editable state, not just node positions.
export class EditHistory {
  private past: string[] = []; private future: string[] = []
  get canUndo(): boolean { return this.past.length > 0 }
  get canRedo(): boolean { return this.future.length > 0 }
  push(snapshot: string): void { if (this.past.at(-1) !== snapshot) this.past.push(snapshot); if (this.past.length > 100) this.past.shift(); this.future = [] }
  undo(current: string): string | undefined { const previous = this.past.pop(); if (previous !== undefined) this.future.push(current); return previous }
  redo(current: string): string | undefined { const next = this.future.pop(); if (next !== undefined) this.past.push(current); return next }
  clear(): void { this.past = []; this.future = [] }
}