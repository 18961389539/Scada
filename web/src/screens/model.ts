export type NodeType = 'text' | 'value' | 'pump' | 'valve' | 'tank' | 'pipe' | 'rectangle' | 'ellipse' | 'button' | 'gauge' | 'motor' | 'fan'
export interface ScreenAction { type: 'none' | 'navigate' | 'popup' | 'command'; screenId: number | null; tagId: number | null; value: number }
export interface ScreenNode {
  id: string; type: NodeType; x: number; y: number; width: number; height: number; rotation: number
  text: string; color: string; activeColor: string; unit: string; decimals: number; min: number; max: number
  activeThreshold: number; tagId: number | null; action: ScreenAction
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
  return { id: crypto.randomUUID(), type, x, y, width: type === 'pipe' ? 200 : 140, height: ['text', 'value', 'button', 'pipe'].includes(type) ? 48 : 112,
    rotation: 0, text: palette.find(p => p.type === type)?.label ?? type, color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId: null,
    action: { type: 'none', screenId: null, tagId: null, value: 1 } }
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
