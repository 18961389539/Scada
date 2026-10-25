import type { ControlStep } from '../../api/control'

// 顺控步骤图（与后端 SequenceGraph.Successors 语义一致）：边由步骤字段推导，连线/删线即修改对应字段，因此与顺控 JSON 无损往返。
export type EdgeKind = 'next' | 'then' | 'else' | 'branch' | 'join' | 'target' | 'fail'
export interface FlowEdge { key: string; from: string; to: string; kind: EdgeKind; implicit?: boolean; label?: string }
export const NODE_W = 170, NODE_H = 58, GAP_X = 40, GAP_Y = 54

export function typeOf(s: ControlStep): string { return s.type ?? 'write' }

export function edgesOf(steps: ControlStep[]): FlowEdge[] {
  const ids = new Set(steps.map(s => s.id).filter(Boolean) as string[])
  const out: FlowEdge[] = []
  const add = (from: string, to: string | null | undefined, kind: EdgeKind, implicit = false, label?: string, n = 0) => {
    if (to && ids.has(to)) out.push({ key: `${from}:${kind}:${n}:${to}`, from, to, kind, implicit, label })
  }
  steps.forEach((s, i) => {
    const id = s.id; if (!id) return
    const fall = () => (s.next ? { to: s.next, implicit: false } : { to: steps[i + 1]?.id ?? null, implicit: true })
    const t = typeOf(s)
    if (t === 'write' || t === 'wait' || t === 'join') { const f = fall(); add(id, f.to, 'next', f.implicit) }
    else if (t === 'condition') { add(id, s.then, 'then', false, 'flowThen'); const e = s.else ? { to: s.else, implicit: false } : { to: steps[i + 1]?.id ?? null, implicit: true }; add(id, e.to, 'else', e.implicit, 'flowElse') }
    else if (t === 'parallel') { (s.branches ?? []).forEach((b, n) => add(id, b, 'branch', false, undefined, n)); add(id, s.join, 'join') }
    else if (t === 'jump') add(id, s.target, 'target', false, `≤${s.maxIterations ?? '?'}`)
    if (t !== 'end' && t !== 'join' && s.onFailure === 'goto') add(id, s.onFailureGoto, 'fail', false, 'flowFail')
  })
  return out
}

// 输出端口：每种步骤可拖出的连线类型。
export function portsOf(s: ControlStep): EdgeKind[] {
  switch (typeOf(s)) {
    case 'write': case 'wait': return ['next', 'fail']
    case 'join': return ['next']
    case 'condition': return ['then', 'else', 'fail']
    case 'parallel': return ['branch', 'join', 'fail']
    case 'jump': return ['target']
    default: return []
  }
}

export function connect(s: ControlStep, kind: EdgeKind, to: string): void {
  switch (kind) {
    case 'next': s.next = to; break
    case 'then': s.then = to; break
    case 'else': s.else = to; break
    case 'branch': { const b = [...(s.branches ?? [])]; if (!b.includes(to)) b.push(to); s.branches = b; break }
    case 'join': s.join = to; break
    case 'target': s.target = to; if (!s.maxIterations) s.maxIterations = 3; break
    case 'fail': s.onFailure = 'goto'; s.onFailureGoto = to; break
  }
}

export function disconnect(s: ControlStep, e: FlowEdge): void {
  switch (e.kind) {
    case 'next': s.next = null; break
    case 'then': s.then = null; break
    case 'else': s.else = null; break
    case 'branch': s.branches = (s.branches ?? []).filter(x => x !== e.to); break
    case 'join': s.join = null; break
    case 'target': s.target = null; break
    case 'fail': s.onFailure = null; s.onFailureGoto = null; break
  }
}

// 自动布局：从第一步按后继做广度优先分层（失败转向不参与分层），同层从左到右排开；不可达步骤放在最后一层。
export function autoLayout(steps: ControlStep[]): Map<string, { x: number; y: number }> {
  const edges = edgesOf(steps).filter(e => e.kind !== 'fail' && e.kind !== 'join')
  const layer = new Map<string, number>()
  const first = steps[0]?.id
  if (first) {
    layer.set(first, 0); const queue = [first]
    while (queue.length) {
      const id = queue.shift()!
      for (const e of edges) if (e.from === id && !layer.has(e.to)) { layer.set(e.to, layer.get(id)! + 1); queue.push(e.to) }
    }
  }
  const max = Math.max(-1, ...layer.values())
  steps.forEach(s => { if (s.id && !layer.has(s.id)) layer.set(s.id, max + 1) })
  const rows = new Map<number, string[]>()
  steps.forEach(s => { if (!s.id) return; const l = layer.get(s.id)!; rows.set(l, [...(rows.get(l) ?? []), s.id]) })
  const widest = Math.max(1, ...[...rows.values()].map(r => r.length))
  const pos = new Map<string, { x: number; y: number }>()
  for (const [l, row] of rows) {
    const offset = ((widest - row.length) * (NODE_W + GAP_X)) / 2
    row.forEach((id, n) => pos.set(id, { x: 20 + offset + n * (NODE_W + GAP_X), y: 20 + l * (NODE_H + GAP_Y) }))
  }
  return pos
}
