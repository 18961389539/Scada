export type NodeType = 'text' | 'value' | 'pump' | 'valve' | 'tank' | 'pipe' | 'rectangle' | 'ellipse' | 'button' | 'gauge' | 'motor' | 'fan' | 'symbol' | 'template'
export interface ScreenAction { type: 'none' | 'navigate' | 'popup' | 'command'; screenId: number | null; tagId: number | null; value: number; bind?: string | null }
export interface ScreenNode {
  id: string; type: NodeType; x: number; y: number; width: number; height: number; rotation: number
  text: string; color: string; activeColor: string; unit: string; decimals: number; min: number; max: number
  activeThreshold: number; tagId: number | null; action: ScreenAction
  // 可选扩展（为空不保存）：图库符号、报警状态测点、模板实例及绑定、模板内占位符、多语言文字。
  symbol?: string | null; alarmTagId?: number | null; templateKey?: string | null; bindings?: ScreenBinding[] | null
  bind?: string | null; alarmBind?: string | null; texts?: Record<string, string> | null
  // 仅渲染用：展开后的模板内部图元指向其实例。
  ownerId?: string
}
export interface ScreenBinding { key: string; tagId: number | null }
export interface LibraryItem {
  id: number; key: string; name: string; category: string; kind: 'symbol' | 'template'; svg: string | null; width: number; height: number
  revision: number; updatedTs: number; updatedBy: string; document: ScreenDocument | null; placeholders: string[]
}
export const SEPARATOR = '--'
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
  for (const k of ['symbol', 'alarmTagId', 'templateKey', 'bindings', 'bind', 'alarmBind', 'texts', 'ownerId'] as const) if (copy[k] == null || (k === 'texts' && !Object.keys(copy.texts ?? {}).length)) delete copy[k]
  if (copy.action.bind == null) delete copy.action.bind
  return copy
}
export interface ScreenDocument { schemaVersion: number; width: number; height: number; background: string; nodes: ScreenNode[] }
export interface ScreenSummary { id: number; name: string; revision: number; publishedRevision: number | null; updatedTs: number; updatedBy: string; hasDraftChanges: boolean }
export interface ScreenDetail extends ScreenSummary { document: ScreenDocument }
export interface ScreenIssue { nodeId: string; message: string }
export const palette: { type: NodeType; label: string; icon: string }[] = [
  { type: 'text', label: '文字', icon: 'T' }, { type: 'value', label: '数值', icon: '123' },
  { type: 'pump', label: '水泵', icon: '◉' }, { type: 'valve', label: '阀门', icon: '⋈' },
  { type: 'tank', label: '储罐', icon: '▤' }, { type: 'pipe', label: '管道', icon: '━' },
  { type: 'motor', label: '电机', icon: 'M' }, { type: 'fan', label: '风机', icon: '✣' },
  { type: 'gauge', label: '仪表', icon: '◴' }, { type: 'button', label: '按钮', icon: '▣' },
  { type: 'rectangle', label: '矩形', icon: '▭' }, { type: 'ellipse', label: '圆形', icon: '◯' }
]
export function emptyDocument(): ScreenDocument { return { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [] } }
export function createNode(type: NodeType, x = 80, y = 80): ScreenNode {
  if (type === 'symbol') return createSymbolNode('isa:pump', 100, 100, '离心泵', x, y)
  return { id: crypto.randomUUID(), type, x, y, width: type === 'pipe' ? 200 : 140, height: ['text', 'value', 'button', 'pipe'].includes(type) ? 48 : 112,
    rotation: 0, text: palette.find(p => p.type === type)?.label ?? type, color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId: null,
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
