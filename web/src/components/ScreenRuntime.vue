<template>
  <div v-loading="loading" class="runtime" data-testid="screen-runtime">
    <div class="runtime-toolbar" :class="{ compact }">
      <strong>{{ previewTargetId ? detail?.name : previewName ?? detail?.name ?? $t('screenRuntime.k60e3e7') }}</strong>
      <el-tag v-if="isPreview" type="warning">{{ $t('screenRuntime.kf0ab76') }}</el-tag>
      <el-tag v-else-if="detail?.publishedRevision != null" :disable-transitions="true" type="success" size="small" data-test="runtime-version">{{ $t('screenRuntime.k975944', [detail.publishedRevision]) }}</el-tag>
      <el-tag v-if="mobileReadOnly" type="info" size="small" data-test="mobile-readonly">{{ $t('screenRuntime.k18dc2e') }}</el-tag>
      <el-tag :type="connected ? 'success' : 'warning'" size="small">{{ connected ? $t('screenRuntime.k2e937e') : $t('screenRuntime.k76dd0d') }}</el-tag>
      <el-tag v-if="usesAlarms" :disable-transitions="true" type="info" size="small" data-test="runtime-alarm-scope">{{ $t('screenMonitor.systemAlarms') }}</el-tag>
      <span class="spacer" />
      <el-button v-if="previewTargetId && previewDocument" @click="previewHome">{{ $t('screenRuntime.k37daea') }}</el-button>
      <slot name="toolbar" />
      <el-button v-if="editLocation" data-test="runtime-edit" @click="router.push(editLocation)">{{ $t('screenRuntime.editCurrentScreen') }}</el-button>
      <el-select v-model="zoomChoice" :aria-label="$t('screenRuntime.k3701ee')" style="width: 100px" data-test="runtime-zoom"><el-option :label="$t('screenRuntime.ka4fe59')" value="fit" /><el-option v-for="z in zoomOptions" :key="z" :label="`${Math.round(z * 100)}%`" :value="z" /></el-select>
      <el-button :loading="loading" :disabled="busy" @click="refresh">{{ $t('screenRuntime.k38108e') }}</el-button>
    </div>
    <el-alert v-if="error" type="error" :title="error" :closable="false" show-icon data-test="runtime-screen-error" />
    <el-alert v-if="valueError" type="error" :title="valueError" :closable="false" show-icon data-test="runtime-value-error" />
    <el-alert v-if="alarmError" type="error" :title="alarmError" :closable="false" show-icon data-test="runtime-alarm-error" />
    <el-alert v-if="alarmLimited" type="warning" :title="$t('screenMonitor.alarmsLimited')" :closable="false" show-icon data-test="runtime-alarm-limited" />
    <el-alert v-if="historyError" type="error" :title="historyError" :closable="false" show-icon data-test="runtime-history-error" />
    <el-alert v-if="oeeError" type="error" :title="oeeError" :closable="false" show-icon data-test="runtime-oee-error" />
    <el-alert v-if="library.error && usesLibrary" type="error" :title="library.error" :closable="false" show-icon data-test="runtime-library-error" />
    <el-alert v-if="deviceError && needsDeviceCatalog" type="error" :title="deviceError" :closable="false" show-icon data-test="runtime-device-error" />
    <el-alert v-if="tagError" type="error" :title="tagError" :closable="false" show-icon data-test="runtime-tag-error" />
    <el-alert v-if="commandError" type="error" :title="commandError" :closable="false" show-icon data-test="runtime-command-error" />
    <CommandReceipt v-if="receiptId" :id="receiptId" />
    <ControlConfirmDialog ref="confirmDialog" />
    <div v-if="document" ref="stage" class="runtime-stage" :class="{ fill: fill, touch: isMobile }" :data-zoom="zoom" data-test="runtime-stage">
      <ScreenCanvas :document="document" :values="values" :sample-max-age="sampleMaxAge" :history="history" :alarm-events="alarmEvents" :alarm-unavailable="alarmUnavailable" :alarm-limited="alarmLimited" :history-unavailable="historyUnavailable" :oee-unavailable="oeeUnavailable" :tag-meta="tagMeta" :oee="oeeByLine" :zoom="zoom" :library="library.items" :library-failed="!!library.error" :can-command="canControlBoundTag" :control-busy="busy || isPreview || mobileReadOnly" @activate="activate" @command="activateCommand" />
    </div>
    <el-empty v-else-if="!loading && !error" :description="$t('screenRuntime.k094788')" />
    <el-dialog v-model="popupVisible" :title="$t('screenRuntime.k3643d4')" width="90%" append-to-body destroy-on-close>
      <ScreenRuntime v-if="popupVisible && popupId" :key="popupId" :screen-id="popupId" :preview="isPreview" :depth="depth + 1" />
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import ControlConfirmDialog from './ControlConfirmDialog.vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { attachPinch, controlBlockedOnMobile, isMobile, loadClientPolicy, mobileControl } from '../mobile'
const confirmDialog = ref<InstanceType<typeof ControlConfirmDialog> | null>(null)
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import ScreenCanvas from './ScreenCanvas.vue'
import { screenApi } from '../api/screens'
import { deviceApi, historyApi, realtimeApi, tagApi, alarmApi } from '../api'
import { oeeApi, type OeeKpi } from '../api/oee'

import CommandReceipt from './CommandReceipt.vue'
import { createCommandId } from '../api/commandTypes'
import { errorMessage } from '../api/http'
import type { ScreenDetail, ScreenDocument, ScreenNode } from '../screens/model'
import { expandNodes } from '../screens/model'
import { runtimeReturnPath } from '../screens/navigation'
import { useLibraryStore } from '../stores/library'
import type { AlarmEvent, Device, HistoryPoint, Tag, TagValue } from '../types'
import { useAuthStore } from '../stores/auth'
import { canActOnDevice } from '../utils'
import { can, canGlobal } from '../access/policies'
import { hubState as realtimeHubState, onHubReconnected, subscribeTags, type TagSubscription } from '../realtime/hub'
import { usePolling } from '../composables/usePolling'
import { tr } from '../i18n'
const props = withDefaults(defineProps<{ screenId?: number; previewDocument?: ScreenDocument; previewName?: string; preview?: boolean; depth?: number
  // emit：导航与弹窗交给外层（运行布局的区域历史与浮动面板窗口）处理。
  navigateMode?: 'router' | 'emit'; compact?: boolean; fill?: boolean; initialZoom?: number | 'fit'; editReturnTo?: string }>(), { depth: 0, preview: false, navigateMode: 'router', compact: false, fill: false, initialZoom: .75 })
const emit = defineEmits<{ navigate: [screenId: number]; popup: [screenId: number] }>()
const route = useRoute(), router = useRouter(), auth = useAuthStore()
const devices = ref<Device[]>([])
const deviceError = ref('')
const commandError = ref('')
const tagsById = ref(new Map<number, Tag>())
const tagCatalog = ref<'pending' | 'ready' | 'failed'>('pending')
const tagError = ref('')
function canControlBoundTag(tagId?: number | null): boolean {
  if (!can(auth.user, 'Control')) return false
  if (tagId == null) return false
  const tag = tagsById.value.get(tagId)
  if (!tag) return tagCatalog.value === 'failed' ? false : tagCatalog.value === 'pending' || canGlobal(auth.user, 'Control')
  if (tag.areaId != null) return can(auth.user, 'Control', tag.areaId)
  if (tag.deviceId != null) return canActOnDevice(auth.user, 'Control', devices.value, tag.deviceId)
  return canGlobal(auth.user, 'Control')
}
const detail = ref<ScreenDetail | null>(null), loading = ref(false), error = ref(''), valueError = ref('')
const alarmError = ref(''), historyError = ref(''), oeeError = ref('')
const alarmLimited = ref(false)
const sourceNow = ref(Date.now()), alarmVerifiedAt = ref(0), historyVerifiedAt = ref(0), oeeVerifiedAt = ref(0)
const alarmUnavailable = computed(() => !!alarmError.value || !alarmVerifiedAt.value || sourceNow.value - alarmVerifiedAt.value > 30000)
const historyUnavailable = computed(() => !!historyError.value || !historyVerifiedAt.value || sourceNow.value - historyVerifiedAt.value > 30000)
const oeeUnavailable = computed(() => !!oeeError.value || !oeeVerifiedAt.value || sourceNow.value - oeeVerifiedAt.value > 90000)
// 缩放：固定比例或“适应”（按区域大小等比缩放，窗口/区域尺寸变化时自动重算）。
const zoomChoice = ref<number | 'fit'>(props.initialZoom), stage = ref<HTMLDivElement>(), stageSize = reactive({ w: 0, h: 0 })
const zoom = computed(() => {
  if (zoomChoice.value !== 'fit') return zoomChoice.value
  const doc = document.value; if (!doc || !stageSize.w) return .75
  return Math.max(.1, Math.min((stageSize.w - 4) / doc.width, (stageSize.h > 40 ? (stageSize.h - 4) / doc.height : Infinity), 3))
})
let resizeObserver: ResizeObserver | undefined, detachPinch: (() => void) | undefined
const zoomOptions = computed(() => { const base = [.5, .75, 1, 1.25, 1.5]; const z = zoomChoice.value; return typeof z === 'number' && !base.includes(z) ? [...base, z].sort((a, b) => a - b) : base })
const mobileReadOnly = computed(() => isMobile.value && !mobileControl.value && !isPreview.value && can(auth.user, 'Control'))
if (isMobile.value && props.initialZoom !== 'fit' && !props.depth) zoomChoice.value = 'fit'
watch(stage, el => { resizeObserver?.disconnect(); detachPinch?.(); detachPinch = undefined; if (!el) return
  detachPinch = attachPinch(el, () => zoom.value, z => { zoomChoice.value = z })
  resizeObserver = new ResizeObserver(() => { stageSize.w = el.clientWidth; stageSize.h = el.clientHeight }); resizeObserver.observe(el) })
const values = reactive<Record<number, TagValue>>({}), history = reactive<Record<number, HistoryPoint[]>>({}), popupId = ref<number>(), popupVisible = ref(false), busy = ref(false), alarmEvents = ref<AlarmEvent[]>([]), oeeByLine = reactive<Record<number, OeeKpi>>({})
// 连接状态来自共享连接模块；画面可有多个实例（主画面 + 最多 3 层弹窗）共用一条连接
const hubState = realtimeHubState()
const connected = computed(() => hubState.value === 'connected')
const receiptId = ref<string | null>(null)
const previewTargetId = ref<number>()
const isPreview = computed(() => props.preview || !!props.previewDocument)
const editLocation = computed(() => {
  const screenId = detail.value?.id
  if (isPreview.value || screenId == null || !canGlobal(auth.user, 'Configure')) return null
  const source = props.editReturnTo ?? (route.path.startsWith('/console') ? `/screens/${screenId}` : route.fullPath)
  return { path: `/config/screens/${screenId}`, query: { returnTo: runtimeReturnPath(source, screenId) ?? `/screens/${screenId}` } }
})
const document = computed(() => previewTargetId.value ? detail.value?.document : props.previewDocument ?? detail.value?.document)
let generation = 0, disposed = false
let pollingGeneration: number | undefined
let lastSnapshot = 0
let subscription: TagSubscription | undefined
let subscriptionSeq = 0
let disposeReconnected: (() => void) | undefined
let historyTimer: ReturnType<typeof setInterval> | undefined
let alarmTimer: ReturnType<typeof setInterval> | undefined
let oeeTimer: ReturnType<typeof setInterval> | undefined

const library = useLibraryStore()
const usesLibrary = computed(() => (document.value?.nodes ?? []).some(n => n.type === 'template' || !!n.symbol?.startsWith('user:')))
const needsDeviceCatalog = computed(() => expandNodes(document.value?.nodes ?? [], library.items).some(node => [node.action.type === 'command' ? node.action.tagId : null, ...(node.commands ?? []).map(command => command.tagId)].some(tagId => {
  const tag = tagId == null ? undefined : tagsById.value.get(tagId)
  return !!tag && tag.areaId == null && tag.deviceId != null
})))
// 画面内表格/告警列表需要测点名称与单位（目录在 onMounted 拉取）。
const tagMeta = computed(() => { const meta: Record<number, { name: string; unit: string }> = {}; for (const tag of tagsById.value.values()) meta[tag.id] = { name: tag.name, unit: tag.unit ?? '' }; return meta })
const sampleMaxAge = computed(() => {
  const intervals = new Map(devices.value.map(device => [device.id, device.pollIntervalMs]))
  const ages: Record<number, number> = {}
  for (const tag of tagsById.value.values()) ages[tag.id] = Math.max(15000, (tag.kind === 'Calculated' ? tag.calculationIntervalMs ?? 0 : intervals.get(tag.deviceId ?? 0) ?? 0) * 3)
  return ages
})
const usesAlarms = computed(() => expandNodes(document.value?.nodes ?? [], library.items).some(node => ['alarm-banner', 'alarm-list', 'alarm-stats'].includes(node.type)))
const boundIds = computed(() => [...new Set(expandNodes(document.value?.nodes ?? [], library.items).flatMap(n => [n.tagId, n.alarmTagId, n.animation?.tagId, n.action.type === 'command' ? n.action.tagId : null, ...(n.commands ?? []).map(command => command.tagId), ...(n.tags ?? [])].filter((x): x is number => !!x)))])
// 趋势图（chart-line）需要历史序列：按绑定测点拉取最近窗口，定时刷新（历史不可用不影响实时显示）。
const lineTagIds = computed(() => [...new Set(expandNodes(document.value?.nodes ?? [], library.items).filter(n => ['chart-line', 'chart-area', 'history-table'].includes(n.type)).flatMap(n => n.tags ?? []))])
const lineWindowSec = computed(() => Math.max(60, ...expandNodes(document.value?.nodes ?? [], library.items).filter(n => ['chart-line', 'chart-area', 'history-table'].includes(n.type)).map(n => n.windowSec ?? 300)))
watch(boundIds, () => { void resubscribe(); void poll() })
watch(lineTagIds, () => { void loadHistory() })

function applyValue(value: TagValue): void {
  if (!disposed && (!values[value.tagId] || value.ts >= values[value.tagId]!.ts)) values[value.tagId] = value
}

// 共享连接按 tagId 只回传本画面订阅的测点；订阅走引用计数，
// 多个画面（主画面 + 弹窗）订阅同一测点时互不干扰，离开时只释放自己那份。
async function resubscribe(): Promise<void> {
  if (disposed) return
  const seq = ++subscriptionSeq, currentGeneration = generation
  const next = await subscribeTags(boundIds.value, value => {
    if (currentGeneration === generation) applyValue(value)
  })
  if (disposed || seq !== subscriptionSeq || currentGeneration !== generation) { next.dispose(); return }
  // 先订阅新的再释放旧的：画面切换时重叠测点不会出现取消/重订抖动
  subscription?.dispose()
  subscription = next
}
async function loadHistory(): Promise<void> {
  const ids = lineTagIds.value
  if (disposed || !ids.length) return
  const currentGeneration = generation
  const to = Date.now(), from = to - lineWindowSec.value * 1000
  try {
    const result = await historyApi.query(ids, from, to)
    if (disposed || currentGeneration !== generation) return
    for (const series of result.series) history[series.tagId] = series.points
    historyVerifiedAt.value = Date.now(); historyError.value = ''
  } catch (e) { if (!disposed && currentGeneration === generation) historyError.value = errorMessage(e, tr('screenMonitor.historyFailed')) }
}
// 报警图元（alarm-banner / alarm-stats）：拉取活动报警并按等级汇总，定时刷新。
async function loadAlarms(): Promise<void> {
  if (disposed || !usesAlarms.value) return
  const currentGeneration = generation
  try {
    const list = await alarmApi.query({ activeOnly: true, live: true, priorityFirst: true, limit: 2000 })
    if (disposed || currentGeneration !== generation) return
    alarmEvents.value = list
    alarmLimited.value = list.length >= 2000
    alarmVerifiedAt.value = Date.now(); alarmError.value = ''
  } catch (e) { if (!disposed && currentGeneration === generation) alarmError.value = errorMessage(e, tr('screenMonitor.alarmsFailed')) }
}
// OEE 看板：按产线拉取统计窗口内的 KPI。
const oeeLineIds = computed(() => [...new Set(expandNodes(document.value?.nodes ?? [], library.items).filter(n => n.type === 'oee-kpi' && n.lineId != null).map(n => n.lineId!))])
const oeeWindowSec = computed(() => Math.max(3600, ...expandNodes(document.value?.nodes ?? [], library.items).filter(n => n.type === 'oee-kpi').map(n => n.windowSec ?? 86400)))
async function loadOee(): Promise<void> {
  const ids = oeeLineIds.value
  if (disposed || !ids.length) return
  const currentGeneration = generation, failures: string[] = []
  const to = Date.now(), from = to - oeeWindowSec.value * 1000
  await Promise.all(ids.map(async lineId => {
    try { const report = await oeeApi.analytics({ from, to, lineId }); if (!disposed && currentGeneration === generation) oeeByLine[lineId] = report.kpi }
    catch (e) { failures.push(errorMessage(e, tr('screenMonitor.oeeFailed'))) }
  }))
  if (!disposed && currentGeneration === generation) {
    oeeError.value = failures.join('; ')
    if (!failures.length) oeeVerifiedAt.value = Date.now()
  }
}
async function poll(): Promise<void> {
  if (pollingGeneration === generation || !document.value || disposed) return
  const ids = boundIds.value
  if (!ids.length) return
  const currentGeneration = generation
  pollingGeneration = currentGeneration
  try {
    const incoming = await realtimeApi.get(ids)
    if (generation === currentGeneration && !disposed) {
      for (const value of incoming) applyValue(value)
      lastSnapshot = Date.now()
      valueError.value = ''
    }
  } catch (e) {
    if (generation === currentGeneration && !disposed) valueError.value = errorMessage(e, tr('screenRuntime.valueReadFailed'))
  } finally { if (pollingGeneration === currentGeneration) pollingGeneration = undefined }
}
function refresh(): void { void load(true) }
async function loadTags(): Promise<void> {
  try {
    const tags = await tagApi.list()
    if (disposed) return
    tagsById.value = new Map(tags.map(tag => [tag.id, tag]))
    tagCatalog.value = 'ready'; tagError.value = ''
  } catch (e) {
    if (disposed) return
    tagsById.value = new Map()
    tagCatalog.value = 'failed'; tagError.value = errorMessage(e, tr('screenMonitor.tagsFailed'))
  }
}
async function loadDevices(): Promise<void> {
  try { devices.value = await deviceApi.list(); deviceError.value = '' }
  catch (e) { deviceError.value = errorMessage(e, tr('screenRuntime.devicesLoadFailed')) }
}
function deviceCatalogBlocks(tag: Tag): boolean {
  if (!deviceError.value || tag.areaId != null || tag.deviceId == null || canGlobal(auth.user, 'Control')) return false
  return devices.value.find(d => d.id === tag.deviceId)?.areaId == null
}
async function load(retryCatalogs = false): Promise<void> {
  const currentGeneration = ++generation
  const screenId = previewTargetId.value ?? props.screenId
  loading.value = true
  error.value = ''; detail.value = null
  valueError.value = ''; lastSnapshot = 0
  alarmError.value = ''; historyError.value = ''; oeeError.value = ''
  alarmLimited.value = false
  alarmVerifiedAt.value = 0; historyVerifiedAt.value = 0; oeeVerifiedAt.value = 0
  alarmEvents.value = []
  for (const key of Object.keys(history)) delete history[Number(key)]
  for (const key of Object.keys(oeeByLine)) delete oeeByLine[Number(key)]
  for (const key of Object.keys(values)) delete values[Number(key)]
  try {
    if (retryCatalogs) await Promise.all([library.ensure(), loadDevices(), loadTags()])
    if (disposed || generation !== currentGeneration) return
    if (props.previewDocument && !previewTargetId.value) { await Promise.all([poll(), resubscribe(), loadHistory(), loadAlarms(), loadOee()]); return }
    if (!screenId) return
    const incoming = await screenApi.get(screenId, isPreview.value)
    if (generation === currentGeneration && !disposed) { detail.value = incoming; await Promise.all([poll(), resubscribe(), loadHistory(), loadAlarms(), loadOee()]) }
  }
  catch (e) { if (!disposed && generation === currentGeneration) error.value = errorMessage(e, tr('screenRuntime.kfb670f')) }
  finally { if (generation === currentGeneration) loading.value = false }
}
async function activate(node: ScreenNode): Promise<void> {
  if (isPreview.value && node.action.type === 'command') { commandError.value = tr('screenRuntime.k1e2e1d'); return }
  if (props.navigateMode === 'emit' && !isPreview.value && (node.action.type === 'navigate' || node.action.type === 'popup') && node.action.screenId) {
    if (node.action.type === 'navigate') emit('navigate', node.action.screenId); else emit('popup', node.action.screenId)
    return
  }
  if (node.action.type === 'navigate' && node.action.screenId) {
    if (isPreview.value) { previewTargetId.value = node.action.screenId; await load() }
    else await router.push(`/screens/${node.action.screenId}`)
    popupVisible.value = false
  }
  if (node.action.type === 'popup' && node.action.screenId) {
    if (props.depth >= 3) { commandError.value = tr('screenRuntime.kc697a4'); return }
    popupId.value = node.action.screenId; popupVisible.value = true
  }
  if (node.action.type !== 'command') return
  await activateCommand(node, node.action.value)
}
// 写交互控件（开关/滑块/步进）下发运行期取值；与固定值按钮共用同一控制闭环。
async function activateCommand(node: ScreenNode, value: number, operation?: string): Promise<void> {
  if (isPreview.value) { commandError.value = tr('screenRuntime.k1e2e1d'); return }
  const command = operation ? node.commands?.find(item => item.key === operation) : undefined
  const tagId = operation ? command?.tagId : node.action.tagId
  if (busy.value || !detail.value || tagId == null || !Number.isFinite(value)) return
  if (controlBlockedOnMobile()) { commandError.value = tr('screenRuntime.keffbad'); return }
  const tag = tagsById.value.get(tagId)
  if (tag && deviceCatalogBlocks(tag)) { commandError.value = deviceError.value; return }
  if (!canControlBoundTag(tagId)) { commandError.value = tr('screenRuntime.kdba1d0'); return }
  busy.value = true
  commandError.value = ''
  let requestId: string | undefined
  try {
    // 与实时监视同一控制闭环：服务端预检查 → 确认 → （需要时）双人复核 → 下发回读与反馈。
    if (!await confirmDialog.value?.open(tagId, value, `${node.text || node.id}${command ? ` / ${command.label}` : ''}`)) return
    requestId = createCommandId()
    const result = await screenApi.command(detail.value.id, node.id, detail.value.publishedRevision!, requestId, true, value, operation)
    receiptId.value = result.commandId ?? null
    if (result.pending) ElMessage.warning(tr('screenRuntime.kf1787c'))
    else if (result.success) ElMessage.success(tr('screenRuntime.kc89364', [result.readBack]))
    else commandError.value = result.message || tr('screenRuntime.k343341')
    await poll()
  } catch (e) { if (e !== 'cancel' && e !== 'close') { receiptId.value = requestId ?? null; commandError.value = errorMessage(e, tr('screenRuntime.k343341')) } }
  finally { busy.value = false }
}
async function previewHome(): Promise<void> { previewTargetId.value = undefined; await load() }
watch(() => props.screenId, () => { popupVisible.value = false; void load() })
// 推送可用时只走订阅；断线或快照超过 5s 未更新才回落到 HTTP 补一次，画面切到后台自动暂停。
usePolling(() => { if (!connected.value || Date.now() - lastSnapshot >= 5000) return poll() }, 1000)
usePolling(() => { sourceNow.value = Date.now() }, 1000)
onMounted(async () => {
  if (isMobile.value) void loadClientPolicy()
  await Promise.all([library.ensure(), loadTags(), loadDevices()])
  if (disposed) return
  await load(); if (disposed) return
  await resubscribe()
  if (disposed) return
  historyTimer = setInterval(() => void loadHistory(), 10000)
  alarmTimer = setInterval(() => void loadAlarms(), 10000)
  oeeTimer = setInterval(() => void loadOee(), 30000)
  // 重连后共享模块已恢复订阅，这里只补快照，避免断线期间的值缺口
  disposeReconnected = onHubReconnected(() => { if (!disposed) void poll() })
})
onBeforeUnmount(() => { resizeObserver?.disconnect(); detachPinch?.(); disposed = true; generation++; subscription?.dispose(); disposeReconnected?.(); clearInterval(historyTimer); clearInterval(alarmTimer); clearInterval(oeeTimer) })
</script>

<style scoped>
.runtime-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; padding: 12px 0; }
.runtime-toolbar > .el-tag { flex-shrink: 0; }
.runtime-toolbar > strong { min-width: 0; overflow-wrap: anywhere; }
@media (max-width: 600px) { .runtime-toolbar > strong { flex-basis: 100%; } .runtime-toolbar .spacer { display: none; } }
.spacer { flex: 1; }
.runtime-stage { overflow: auto; max-height: 75vh; border-radius: 8px; border: 1px solid #294260; }
.runtime-stage.fill { max-height: none; flex: 1; min-height: 0; }
.runtime { display: flex; flex-direction: column; min-height: 0; height: 100%; }
.runtime-stage.touch { max-height: none; -webkit-overflow-scrolling: touch; }
.runtime-stage.touch :deep(.screen-canvas) { touch-action: pan-x pan-y; }
.runtime-toolbar.compact { padding: 4px 0; gap: 6px; font-size: 12px; }
</style>
