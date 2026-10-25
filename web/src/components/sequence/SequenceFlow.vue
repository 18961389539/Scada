<template>
  <div class="flow" data-test="sequence-flow">
    <div class="flow-tools">
      <el-button-group size="small">
        <el-button data-test="flow-zoom-in" @click="zoomBy(1.2)">＋</el-button>
        <el-button data-test="flow-zoom-out" @click="zoomBy(1 / 1.2)">－</el-button>
        <el-button data-test="flow-fit" @click="fit()">{{ t('flow.fit') }}</el-button>
        <el-button v-if="editable" data-test="flow-layout" @click="relayout()">{{ t('flow.autoLayout') }}</el-button>
      </el-button-group>
      <el-button v-if="editable && selectedEdge" size="small" type="danger" data-test="flow-edge-delete" @click="removeEdge()">{{ t('flow.deleteEdge') }}</el-button>
      <span class="tiny muted">{{ editable ? t('flow.hintEdit') : t('flow.hintRun') }}</span>
    </div>
    <svg ref="svg" class="flow-svg" :style="{ height: `${height}px` }" @pointerdown="onBackgroundDown" @pointermove="onMove" @pointerup="onUp" @pointerleave="onUp" @wheel.prevent="onWheel" @keydown.delete="removeEdge()" tabindex="0">
      <defs>
        <marker v-for="m in markerColors" :id="`arrow-${m.kind}-${uid}`" :key="m.kind" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" :fill="m.color" /></marker>
      </defs>
      <g :transform="`translate(${view.x},${view.y}) scale(${view.k})`">
        <g v-for="e in edges" :key="e.key" class="edge" :class="{ selected: selectedEdge === e.key }" :data-test="`flow-edge-${e.from}-${e.kind}-${e.to}`" @pointerdown.stop="selectEdge(e.key)">
          <path :d="edgePath(e)" fill="none" stroke="transparent" stroke-width="12" />
          <path :d="edgePath(e)" fill="none" :stroke="edgeColor(e.kind)" :stroke-width="selectedEdge === e.key ? 3 : 1.6" :stroke-dasharray="e.implicit || e.kind === 'fail' || e.kind === 'join' ? '5 4' : undefined" :marker-end="`url(#arrow-${e.kind}-${uid})`" />
          <text v-if="e.label" :x="labelPos(e).x" :y="labelPos(e).y" class="edge-label" :fill="edgeColor(e.kind)">{{ edgeLabel(e) }}</text>
        </g>
        <path v-if="drag?.kind === 'connect'" :d="`M${drag.sx},${drag.sy} L${drag.cx},${drag.cy}`" stroke="#409eff" stroke-dasharray="4 3" fill="none" />
        <g v-for="s in nodes" :key="s.id" class="node" :class="[`t-${s.type}`, statusOf(s.id), { selected: modelValue === s.id, error: errorIds.includes(s.id) }]" :transform="`translate(${s.x},${s.y})`" :data-test="`flow-node-${s.id}`" :data-status="statusOf(s.id) || undefined" :data-error="errorIds.includes(s.id) || undefined" @pointerdown.stop="onNodeDown($event, s.id)" @pointerup.stop="onNodeUp(s.id)">
          <rect v-if="s.type !== 'condition'" :width="NODE_W" :height="NODE_H" :rx="s.type === 'end' ? 29 : s.type === 'join' || s.type === 'parallel' ? 2 : 8" class="body" />
          <polygon v-else :points="`${NODE_W / 2},0 ${NODE_W},${NODE_H / 2} ${NODE_W / 2},${NODE_H} 0,${NODE_H / 2}`" class="body" />
          <rect v-if="s.type === 'parallel' || s.type === 'join'" :width="NODE_W" height="6" class="bar" />
          <text :x="NODE_W / 2" y="22" class="title">{{ s.id }} · {{ s.name }}</text>
          <text :x="NODE_W / 2" y="40" class="sub">{{ describe(s.step) }}</text>
          <text v-if="executions(s.id) > 1" :x="NODE_W - 6" y="12" class="sub" text-anchor="end">×{{ executions(s.id) }}</text>
          <template v-if="editable">
            <g v-for="(p, n) in portsOf(s.step)" :key="p" class="port" :transform="`translate(${portX(s.step, n)},${NODE_H})`" :data-test="`flow-port-${s.id}-${p}`" @pointerdown.stop="startConnect($event, s.id, p)">
              <circle r="6" :fill="edgeColor(p)" /><title>{{ t(`flow.port.${p}`) }}</title>
            </g>
          </template>
        </g>
      </g>
    </svg>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { ControlStep, ControlStepResult } from '../../api/control'
import { autoLayout, connect, disconnect, edgesOf, NODE_H, NODE_W, portsOf, typeOf, type EdgeKind, type FlowEdge } from './graph'

const props = withDefaults(defineProps<{ steps: ControlStep[]; modelValue?: string | null; editable?: boolean; errorIds?: string[]; results?: ControlStepResult[] | null; describe?: (s: ControlStep) => string; height?: number }>(),
  { modelValue: null, editable: false, errorIds: () => [], results: null, describe: () => '', height: 460 })
const emit = defineEmits<{ 'update:modelValue': [id: string | null]; changed: [] }>()
const { t } = useI18n()
const uid = Math.random().toString(36).slice(2, 8)
const svg = ref<SVGSVGElement>()
const view = reactive({ x: 0, y: 0, k: 1 })
const selectedEdge = ref<string | null>(null)
const describe = (s: ControlStep) => props.describe(s)

const layout = computed(() => autoLayout(props.steps))
const nodes = computed(() => props.steps.filter(s => s.id).map(s => {
  const auto = layout.value.get(s.id!) ?? { x: 20, y: 20 }
  return { id: s.id!, name: s.name, type: typeOf(s), step: s, x: s.x ?? auto.x, y: s.y ?? auto.y }
}))
const nodeMap = computed(() => new Map(nodes.value.map(n => [n.id, n])))
const edges = computed(() => edgesOf(props.steps))

const colors: Record<EdgeKind, string> = { next: '#606266', then: '#2f9e44', else: '#e8590c', branch: '#1971c2', join: '#868e96', target: '#7048e8', fail: '#e03131' }
const markerColors = Object.entries(colors).map(([kind, color]) => ({ kind, color }))
const edgeColor = (k: EdgeKind) => colors[k]
function edgeLabel(e: FlowEdge): string { return e.label?.startsWith('flow') ? t(`flow.${e.label.slice(4).toLowerCase()}`) : e.label ?? '' }
function portX(s: ControlStep, n: number): number { const c = portsOf(s).length; return (NODE_W * (n + 1)) / (c + 1) }

function anchors(e: FlowEdge) {
  const a = nodeMap.value.get(e.from)!, b = nodeMap.value.get(e.to)!
  const ports = portsOf(a.step), pi = Math.max(0, ports.indexOf(e.kind))
  const sx = a.x + portX(a.step, pi), sy = a.y + NODE_H
  const back = b.y <= a.y
  const tx = b.x + NODE_W / 2, ty = back ? b.y + NODE_H / 2 : b.y
  return { sx, sy, tx: back ? b.x + NODE_W : tx, ty, back }
}
function edgePath(e: FlowEdge): string {
  const { sx, sy, tx, ty, back } = anchors(e)
  if (back) { const off = 40 + (e.kind === 'fail' ? 30 : 0); const rx = Math.max(sx, tx) + off; return `M${sx},${sy} C${sx},${sy + 40} ${rx},${sy + 40} ${rx},${(sy + ty) / 2} S${tx + 30},${ty} ${tx},${ty}` }
  const my = (sy + ty) / 2
  return `M${sx},${sy} C${sx},${my} ${tx},${my} ${tx},${ty}`
}
function labelPos(e: FlowEdge) { const { sx, sy, tx, ty, back } = anchors(e); return back ? { x: Math.max(sx, tx) + 44, y: (sy + ty) / 2 } : { x: (sx + tx) / 2 + 4, y: (sy + ty) / 2 } }

const resultMap = computed(() => new Map((props.results ?? []).map(r => [r.stepId ?? String(r.index), r])))
function statusOf(id: string): string { const r = resultMap.value.get(id); return r ? `st-${r.status}` : '' }
const executions = (id: string) => resultMap.value.get(id)?.executions ?? 0

// 交互：拖动节点、拖出端口连线、拖动背景平移、滚轮缩放。
type Drag = { kind: 'node'; id: string; ox: number; oy: number; moved: boolean } | { kind: 'pan'; px: number; py: number; vx: number; vy: number } | { kind: 'connect'; from: string; port: EdgeKind; sx: number; sy: number; cx: number; cy: number }
const drag = ref<Drag | null>(null)
function toWorld(ev: PointerEvent | WheelEvent) {
  const r = svg.value!.getBoundingClientRect(); return { x: (ev.clientX - r.left - view.x) / view.k, y: (ev.clientY - r.top - view.y) / view.k }
}
function onBackgroundDown(ev: PointerEvent): void { selectedEdge.value = null; drag.value = { kind: 'pan', px: ev.clientX, py: ev.clientY, vx: view.x, vy: view.y } }
function onNodeDown(ev: PointerEvent, id: string): void {
  emit('update:modelValue', id); selectedEdge.value = null
  if (!props.editable) return
  const p = toWorld(ev), n = nodeMap.value.get(id)!
  drag.value = { kind: 'node', id, ox: p.x - n.x, oy: p.y - n.y, moved: false }
}
function startConnect(ev: PointerEvent, from: string, port: EdgeKind): void {
  const p = toWorld(ev); drag.value = { kind: 'connect', from, port, sx: p.x, sy: p.y, cx: p.x, cy: p.y }
}
function onMove(ev: PointerEvent): void {
  const d = drag.value; if (!d) return
  if (d.kind === 'pan') { view.x = d.vx + ev.clientX - d.px; view.y = d.vy + ev.clientY - d.py; return }
  const p = toWorld(ev)
  if (d.kind === 'connect') { d.cx = p.x; d.cy = p.y; return }
  const step = props.steps.find(s => s.id === d.id); if (!step) return
  step.x = Math.round(p.x - d.ox); step.y = Math.round(p.y - d.oy); d.moved = true
}
function onNodeUp(id: string): void {
  const d = drag.value
  if (d?.kind === 'connect' && d.from !== id) {
    const step = props.steps.find(s => s.id === d.from)
    if (step) { connect(step, d.port, id); emit('changed') }
  } else if (d?.kind === 'node' && d.moved) emit('changed')
  drag.value = null
}
function onUp(): void { if (drag.value?.kind === 'node' && drag.value.moved) emit('changed'); drag.value = null }
function onWheel(ev: WheelEvent): void {
  const r = svg.value!.getBoundingClientRect(), mx = ev.clientX - r.left, my = ev.clientY - r.top
  const k = Math.min(3, Math.max(0.25, view.k * (ev.deltaY < 0 ? 1.1 : 1 / 1.1)))
  view.x = mx - ((mx - view.x) * k) / view.k; view.y = my - ((my - view.y) * k) / view.k; view.k = k
}
function zoomBy(f: number): void { const r = svg.value?.getBoundingClientRect(); const mx = (r?.width ?? 600) / 2, my = (r?.height ?? 400) / 2; const k = Math.min(3, Math.max(0.25, view.k * f)); view.x = mx - ((mx - view.x) * k) / view.k; view.y = my - ((my - view.y) * k) / view.k; view.k = k }
function fit(): void {
  const r = svg.value?.getBoundingClientRect(); if (!r || !nodes.value.length) return
  const minX = Math.min(...nodes.value.map(n => n.x)), minY = Math.min(...nodes.value.map(n => n.y))
  const maxX = Math.max(...nodes.value.map(n => n.x + NODE_W)) + 80, maxY = Math.max(...nodes.value.map(n => n.y + NODE_H))
  const k = Math.min(1.5, Math.max(0.25, Math.min((r.width - 40) / (maxX - minX), (r.height - 40) / (maxY - minY))))
  view.k = k; view.x = 20 - minX * k + (r.width - 40 - (maxX - minX) * k) / 2; view.y = 20 - minY * k
}
function relayout(): void { const pos = autoLayout(props.steps); props.steps.forEach(s => { const p = s.id ? pos.get(s.id) : null; if (p) { s.x = p.x; s.y = p.y } }); emit('changed'); nextTick(fit) }
function selectEdge(key: string): void { selectedEdge.value = key }
function removeEdge(): void {
  const e = edges.value.find(x => x.key === selectedEdge.value); if (!e || !props.editable) return
  const step = props.steps.find(s => s.id === e.from); if (step) { disconnect(step, e); emit('changed') }
  selectedEdge.value = null
}
onMounted(() => nextTick(fit))
watch(() => props.steps.length, () => nextTick(fit))
defineExpose({ fit, relayout })
</script>

<style scoped>
.flow { border: 1px solid var(--el-border-color); border-radius: 4px; background: #fafbfc; }
.flow-tools { display: flex; gap: 8px; align-items: center; padding: 6px; border-bottom: 1px solid var(--el-border-color-lighter); flex-wrap: wrap; }
.flow-svg { width: 100%; display: block; cursor: grab; touch-action: none; outline: none; }
.node { cursor: pointer; }
.node .body { fill: #fff; stroke: #909399; stroke-width: 1.4; }
.node .bar { fill: #495057; }
.node.t-end .body { fill: #f1f3f5; }
.node.t-condition .body { fill: #fffbe6; }
.node.selected .body { stroke: #409eff; stroke-width: 2.6; }
.node.error .body { stroke: #e03131; stroke-width: 2.6; fill: #fff5f5; }
.node.st-running .body { fill: #fff3bf; stroke: #f08c00; stroke-width: 2.6; }
.node.st-done .body { fill: #ebfbee; stroke: #2f9e44; }
.node.st-failed .body { fill: #ffe3e3; stroke: #e03131; stroke-width: 2.6; }
.node.st-skipped { opacity: 0.45; }
.title { font-size: 12px; font-weight: 600; text-anchor: middle; fill: #212529; pointer-events: none; }
.sub { font-size: 10px; text-anchor: middle; fill: #6c757d; pointer-events: none; }
.edge { cursor: pointer; }
.edge-label { font-size: 10px; pointer-events: none; }
.port { cursor: crosshair; }
.port circle { stroke: #fff; stroke-width: 1.5; }
</style>
