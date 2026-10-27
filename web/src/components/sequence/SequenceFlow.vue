<template>
  <div ref="root" class="flow" :class="{ readonly: !editable }" data-test="sequence-flow" @keydown.delete="deleteSelectedEdge">
    <div class="flow-tools">
      <el-button-group size="small">
        <el-tooltip :content="t('flow.zoomIn')"><el-button :icon="ZoomIn" :aria-label="t('flow.zoomIn')" data-test="flow-zoom-in" @click="zoomIn()" /></el-tooltip>
        <el-tooltip :content="t('flow.zoomOut')"><el-button :icon="ZoomOut" :aria-label="t('flow.zoomOut')" data-test="flow-zoom-out" @click="zoomOut()" /></el-tooltip>
        <el-button :icon="FullScreen" data-test="flow-fit" @click="fit()">{{ t('flow.fit') }}</el-button>
        <el-button v-if="editable" :icon="Rank" data-test="flow-layout" @click="relayout()">{{ t('flow.autoLayout') }}</el-button>
      </el-button-group>
      <el-button v-if="editable && selectedEdge" size="small" type="danger" :icon="Delete" data-test="flow-edge-delete" @click="removeEdge()">{{ t('flow.deleteEdge') }}</el-button>
    </div>
    <VueFlow :id="flowId" :nodes="nodes" :edges="edges" class="flow-canvas" :style="{ height: `${height}px` }"
      :nodes-draggable="editable" :nodes-connectable="editable" :edges-updatable="false" :auto-connect="false"
      :delete-key-code="null" :selection-key-code="null" :multi-selection-key-code="null" :connect-on-click="true"
      :zoom-on-double-click="false" :min-zoom="0.25" :max-zoom="3" :is-valid-connection="validConnection"
      @node-click="selectNode" @node-drag-start="selectNode" @node-drag="moveNode" @node-drag-stop="finishNodeMove"
      @edge-click="selectEdge" @pane-click="selectedEdge = null" @connect="addConnection" @connect-start="startConnection" @connect-end="finishConnection"
      @nodes-initialized="fit()">
      <template #node-step="{ id, data }">
        <div class="step-node" :class="[`t-${typeOf(data.step)}`, statusOf(id), { selected: modelValue === id, error: errorIds.includes(id) }]">
          <Handle id="input" type="target" :position="Position.Top" :connectable="editable" class="input-port" />
          <div class="node-body" />
          <strong class="node-title" :title="`${id} · ${data.step.name}`">{{ id }} · {{ data.step.name }}</strong>
          <span class="node-description" :title="describe(data.step)">{{ describe(data.step) }}</span>
          <span v-if="executions(id) > 1" class="node-executions">×{{ executions(id) }}</span>
          <Handle v-for="(port, index) in portsOf(data.step)" :id="port" :key="port" type="source" :position="Position.Bottom"
            :connectable="editable" class="output-port" :style="{ left: `${portX(data.step, index)}px`, background: edgeColor(port) }"
            :title="t(`flow.port.${port}`)" :aria-label="t(`flow.port.${port}`)" :data-test="`flow-port-${id}-${port}`" />
        </div>
      </template>
    </VueFlow>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { Delete, FullScreen, Rank, ZoomIn, ZoomOut } from '@element-plus/icons-vue'
import { Handle, MarkerType, Position, VueFlow, useVueFlow, type Connection, type Edge, type EdgeMouseEvent, type Node, type NodeDragEvent, type NodeMouseEvent, type OnConnectStartParams } from '@vue-flow/core'
import '@vue-flow/core/dist/style.css'
import '@vue-flow/core/dist/theme-default.css'
import { useI18n } from 'vue-i18n'
import type { ControlStep, ControlStepResult } from '../../api/control'
import { autoLayout, connect, disconnect, edgesOf, NODE_H, NODE_W, portsOf, typeOf, type EdgeKind, type FlowEdge } from './graph'

const props = withDefaults(defineProps<{ steps: ControlStep[]; modelValue?: string | null; editable?: boolean; errorIds?: string[]; results?: ControlStepResult[] | null; describe?: (s: ControlStep) => string; height?: number }>(),
  { modelValue: null, editable: false, errorIds: () => [], results: null, describe: () => '', height: 460 })
const emit = defineEmits<{ 'update:modelValue': [id: string | null]; changed: [] }>()
const { t } = useI18n()
const root = ref<HTMLDivElement>()
const flowId = `sequence-${crypto.randomUUID()}`
const { fitView, zoomIn, zoomOut } = useVueFlow({ id: flowId })
const selectedEdge = ref<string | null>(null)
let pendingConnection: { source: string; port: EdgeKind } | null = null
let movedNode = false
const describe = (s: ControlStep) => props.describe(s)

const layout = computed(() => autoLayout(props.steps))
const nodes = computed<Node<{ step: ControlStep }>[]>(() => props.steps.filter(step => step.id).map(step => {
  const position = layout.value.get(step.id!) ?? { x: 20, y: 20 }
  return { id: step.id!, type: 'step', position: { x: step.x ?? position.x, y: step.y ?? position.y }, data: { step },
    width: NODE_W, height: NODE_H, draggable: props.editable, connectable: props.editable, deletable: false,
    domAttributes: { tabindex: 0, 'data-test': `flow-node-${step.id}`, 'data-status': statusOf(step.id!) || undefined, 'data-error': props.errorIds.includes(step.id!) || undefined } }
}))
const graphEdges = computed(() => edgesOf(props.steps))

const colors: Record<EdgeKind, string> = { next: '#606266', then: '#2f9e44', else: '#e8590c', branch: '#1971c2', join: '#868e96', target: '#7048e8', fail: '#e03131' }
const edgeColor = (k: EdgeKind) => colors[k]
function edgeLabel(e: FlowEdge): string { return e.label?.startsWith('flow') ? t(`flow.${e.label.slice(4).toLowerCase()}`) : e.label ?? '' }
function portX(s: ControlStep, n: number): number { const c = portsOf(s).length; return (NODE_W * (n + 1)) / (c + 1) }

const edges = computed<Edge[]>(() => graphEdges.value.map(edge => ({
  id: edge.key, source: edge.from, target: edge.to, sourceHandle: edge.kind, targetHandle: 'input', label: edgeLabel(edge),
  type: 'default', selectable: true, deletable: false, updatable: false,
  markerEnd: { type: MarkerType.ArrowClosed, color: edgeColor(edge.kind) },
  style: { stroke: edgeColor(edge.kind), strokeWidth: selectedEdge.value === edge.key ? 3 : 1.6,
    strokeDasharray: edge.implicit || edge.kind === 'fail' || edge.kind === 'join' ? '5 4' : undefined },
  labelStyle: { fill: edgeColor(edge.kind), fontSize: 10 },
  domAttributes: { tabindex: 0, 'data-test': `flow-edge-${edge.from}-${edge.kind}-${edge.to}` }
})))

const resultMap = computed(() => new Map((props.results ?? []).map(r => [r.stepId ?? String(r.index), r])))
function statusOf(id: string): string { const r = resultMap.value.get(id); return r ? `st-${r.status}` : '' }
const executions = (id: string) => resultMap.value.get(id)?.executions ?? 0

function selectNode({ node }: NodeMouseEvent): void { selectedEdge.value = null; emit('update:modelValue', node.id) }
function moveNode({ node }: NodeDragEvent): void {
  if (!props.editable) return
  const step = props.steps.find(item => item.id === node.id)
  if (!step) return
  const x = Math.round(node.position.x), y = Math.round(node.position.y)
  if (step.x !== x || step.y !== y) { step.x = x; step.y = y; movedNode = true }
}
function finishNodeMove(event: NodeDragEvent): void {
  moveNode(event)
  if (movedNode) { movedNode = false; emit('changed') }
}
function validConnection(connection: Connection): boolean {
  const source = props.steps.find(step => step.id === connection.source)
  return props.editable && !!source && connection.source !== connection.target
    && props.steps.some(step => step.id === connection.target) && portsOf(source).includes(connection.sourceHandle as EdgeKind)
}
function addConnection(connection: Connection): void {
  if (!validConnection(connection)) return
  connect(props.steps.find(step => step.id === connection.source)!, connection.sourceHandle as EdgeKind, connection.target)
  pendingConnection = null; emit('changed')
}
function startConnection(event: OnConnectStartParams): void {
  pendingConnection = props.editable && event.handleType === 'source' && event.nodeId && event.handleId
    ? { source: event.nodeId, port: event.handleId as EdgeKind } : null
}
function finishConnection(event?: MouseEvent | TouchEvent): void {
  const pending = pendingConnection; pendingConnection = null
  if (!pending || !event || !props.editable) return
  const point = 'changedTouches' in event ? event.changedTouches[0] : event
  if (!point) return
  const target = window.document.elementFromPoint(point.clientX, point.clientY)?.closest<HTMLElement>('.vue-flow__node')
  if (target?.dataset.id && root.value?.contains(target)) addConnection({ source: pending.source, sourceHandle: pending.port, target: target.dataset.id })
}
function selectEdge({ edge }: EdgeMouseEvent): void { selectedEdge.value = edge.id }
function removeEdge(): void {
  const e = graphEdges.value.find(x => x.key === selectedEdge.value); if (!e || !props.editable) return
  const step = props.steps.find(s => s.id === e.from); if (step) { disconnect(step, e); emit('changed') }
  selectedEdge.value = null
}
function deleteSelectedEdge(event: KeyboardEvent): void {
  if (event.isComposing || (event.target as Element).closest('input, textarea, select, [contenteditable="true"]')) return
  if (props.editable && selectedEdge.value) { event.preventDefault(); removeEdge() }
}
function fit(): void { void fitView({ padding: .2, maxZoom: 1.5 }) }
function relayout(): void {
  if (!props.editable) return
  const positions = autoLayout(props.steps)
  props.steps.forEach(step => { const position = step.id ? positions.get(step.id) : null; if (position) { step.x = position.x; step.y = position.y } })
  emit('changed'); void nextTick(fit)
}
watch(() => props.steps.length, () => nextTick(fit))
defineExpose({ fit, relayout })
</script>

<style scoped>
.flow { min-width: 0; overflow: hidden; border: 1px solid var(--el-border-color); border-radius: 4px; background: var(--scada-panel, #fafbfc); }
.flow-tools { display: flex; gap: 8px; align-items: center; padding: 6px; border-bottom: 1px solid var(--el-border-color-lighter); flex-wrap: wrap; }
.flow-canvas { width: 100%; touch-action: none; }
.step-node { position: relative; width: 170px; height: 58px; cursor: pointer; color: #212529; }
.node-body { position: absolute; inset: 0; border: 1.4px solid #909399; border-radius: 8px; background: #fff; pointer-events: none; }
.t-end .node-body { border-radius: 8px; background: #f1f3f5; }
.t-condition .node-body { border-radius: 0; background: #fffbe6; clip-path: polygon(50% 0, 100% 50%, 50% 100%, 0 50%); }
.t-condition::before { content: ''; position: absolute; inset: -2px; background: #909399; clip-path: polygon(50% 0, 100% 50%, 50% 100%, 0 50%); }
.t-condition.selected::before { background: #409eff; }
.t-condition.error::before, .t-condition.st-failed::before { background: #e03131; }
.t-condition.st-running::before { background: #f08c00; }
.t-condition.st-done::before { background: #2f9e44; }
.t-join .node-body, .t-parallel .node-body { border-radius: 2px; border-top: 6px solid #495057; }
.selected .node-body { border-color: #409eff; border-width: 2.6px; }
.error .node-body { border-color: #e03131; border-width: 2.6px; background: #fff5f5; }
.st-running .node-body { background: #fff3bf; border-color: #f08c00; border-width: 2.6px; }
.st-done .node-body { background: #ebfbee; border-color: #2f9e44; }
.st-failed .node-body { background: #ffe3e3; border-color: #e03131; border-width: 2.6px; }
.st-skipped { opacity: .45; }
.node-title, .node-description { position: absolute; left: 8px; right: 8px; display: block; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; text-align: center; pointer-events: none; }
.node-title { top: 10px; font-size: 12px; font-weight: 600; }
.node-description { top: 31px; font-size: 10px; color: #6c757d; }
.t-condition .node-title { top: 14px; left: 34px; right: 34px; font-size: 10px; }
.t-condition .node-description { top: 31px; left: 40px; right: 40px; font-size: 8px; line-height: 10px; }
.node-executions { position: absolute; right: 5px; top: 1px; font-size: 9px; pointer-events: none; }
.output-port { width: 12px; height: 12px; border: 1.5px solid #fff; cursor: crosshair; }
.input-port { width: 9px; height: 9px; background: #909399; border: 1px solid #fff; }
.readonly .input-port, .readonly .output-port { opacity: 0; pointer-events: none; }
.flow :deep(.vue-flow__edge-path) { cursor: pointer; }
</style>
