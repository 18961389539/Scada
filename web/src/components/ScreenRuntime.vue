<template>
  <div v-loading="loading" class="runtime" data-testid="screen-runtime">
    <div class="runtime-toolbar" :class="{ compact }">
      <strong>{{ previewTargetId ? detail?.name : previewName ?? detail?.name ?? $t('screenRuntime.k60e3e7') }}</strong>
      <el-tag v-if="isPreview" type="warning">{{ $t('screenRuntime.kf0ab76') }}</el-tag>
      <el-tag v-else type="success" size="small">{{ $t('screenRuntime.k975944', [detail?.publishedRevision]) }}</el-tag>
      <el-tag v-if="mobileReadOnly" type="info" size="small" data-test="mobile-readonly">{{ $t('screenRuntime.k18dc2e') }}</el-tag>
      <el-tag :type="connected ? 'success' : 'warning'" size="small">{{ connected ? $t('screenRuntime.k2e937e') : $t('screenRuntime.k76dd0d') }}</el-tag>
      <span class="spacer" />
      <el-button v-if="previewTargetId && previewDocument" @click="previewHome">{{ $t('screenRuntime.k37daea') }}</el-button>
      <slot name="toolbar" />
      <el-select v-model="zoomChoice" :aria-label="$t('screenRuntime.k3701ee')" style="width: 100px" data-test="runtime-zoom"><el-option :label="$t('screenRuntime.ka4fe59')" value="fit" /><el-option v-for="z in zoomOptions" :key="z" :label="`${Math.round(z * 100)}%`" :value="z" /></el-select>
      <el-button @click="refresh">{{ $t('screenRuntime.k38108e') }}</el-button>
    </div>
    <el-alert v-if="error" type="error" :title="error" :closable="false" show-icon />
    <el-alert v-if="valueError" type="error" :title="valueError" :closable="false" show-icon data-test="runtime-value-error" />
    <el-alert v-if="library.error && usesLibrary" type="error" :title="library.error" :closable="false" show-icon data-test="runtime-library-error" />
    <el-alert v-if="deviceError && needsDeviceCatalog" type="error" :title="deviceError" :closable="false" show-icon data-test="runtime-device-error" />
    <el-alert v-if="commandError" type="error" :title="commandError" :closable="false" show-icon data-test="runtime-command-error" />
    <CommandReceipt v-if="receiptId" :id="receiptId" />
    <ControlConfirmDialog ref="confirmDialog" />
    <div v-if="document" ref="stage" class="runtime-stage" :class="{ fill: fill, touch: isMobile }" :data-zoom="zoom" data-test="runtime-stage">
      <ScreenCanvas :document="document" :values="values" :zoom="zoom" :library="library.items" :library-failed="!!library.error" @activate="activate" />
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
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import ScreenCanvas from './ScreenCanvas.vue'
import { screenApi } from '../api/screens'
import { deviceApi, realtimeApi, tagApi } from '../api'
import CommandReceipt from './CommandReceipt.vue'
import { createCommandId } from '../api/commandTypes'
import { errorMessage } from '../api/http'
import type { ScreenDetail, ScreenDocument, ScreenNode } from '../screens/model'
import { expandNodes } from '../screens/model'
import { useLibraryStore } from '../stores/library'
import type { Device, Tag, TagValue } from '../types'
import { useAuthStore } from '../stores/auth'
import { canActOnDevice, hasAny, hasGlobal, hasInArea } from '../utils'
import { hubState as realtimeHubState, onHubReconnected, subscribeTags, type TagSubscription } from '../realtime/hub'
import { usePolling } from '../composables/usePolling'
import { tr } from '../i18n'
const props = withDefaults(defineProps<{ screenId?: number; previewDocument?: ScreenDocument; previewName?: string; preview?: boolean; depth?: number
  // emit：导航与弹窗交给外层（运行布局的区域历史与浮动面板窗口）处理。
  navigateMode?: 'router' | 'emit'; compact?: boolean; fill?: boolean; initialZoom?: number | 'fit' }>(), { depth: 0, preview: false, navigateMode: 'router', compact: false, fill: false, initialZoom: .75 })
const emit = defineEmits<{ navigate: [screenId: number]; popup: [screenId: number] }>()
const router = useRouter(), auth = useAuthStore()
const devices = ref<Device[]>([])
const deviceError = ref('')
const commandError = ref('')
const tagsById = ref(new Map<number, Tag>())
const tagCatalog = ref<'pending' | 'ready' | 'failed'>('pending')
function canControlBoundTag(tagId?: number | null): boolean {
  if (!hasAny(auth.user, 'Control')) return false
  if (tagId == null) return false
  const tag = tagsById.value.get(tagId)
  if (!tag) return tagCatalog.value === 'failed' ? false : tagCatalog.value === 'pending' || hasGlobal(auth.user, 'Control')
  if (tag.areaId != null) return hasInArea(auth.user, 'Control', tag.areaId)
  if (tag.deviceId != null) return canActOnDevice(auth.user, 'Control', devices.value, tag.deviceId)
  return hasGlobal(auth.user, 'Control')
}
const detail = ref<ScreenDetail | null>(null), loading = ref(false), error = ref(''), valueError = ref('')
// 缩放：固定比例或“适应”（按区域大小等比缩放，窗口/区域尺寸变化时自动重算）。
const zoomChoice = ref<number | 'fit'>(props.initialZoom), stage = ref<HTMLDivElement>(), stageSize = reactive({ w: 0, h: 0 })
const zoom = computed(() => {
  if (zoomChoice.value !== 'fit') return zoomChoice.value
  const doc = document.value; if (!doc || !stageSize.w) return .75
  return Math.max(.1, Math.min((stageSize.w - 4) / doc.width, (stageSize.h > 40 ? (stageSize.h - 4) / doc.height : Infinity), 3))
})
let resizeObserver: ResizeObserver | undefined, detachPinch: (() => void) | undefined
const zoomOptions = computed(() => { const base = [.5, .75, 1, 1.25, 1.5]; const z = zoomChoice.value; return typeof z === 'number' && !base.includes(z) ? [...base, z].sort((a, b) => a - b) : base })
const mobileReadOnly = computed(() => isMobile.value && !mobileControl.value && !isPreview.value && hasAny(auth.user, 'Control'))
if (isMobile.value && props.initialZoom !== 'fit' && !props.depth) zoomChoice.value = 'fit'
watch(stage, el => { resizeObserver?.disconnect(); detachPinch?.(); detachPinch = undefined; if (!el) return
  detachPinch = attachPinch(el, () => zoom.value, z => { zoomChoice.value = z })
  resizeObserver = new ResizeObserver(() => { stageSize.w = el.clientWidth; stageSize.h = el.clientHeight }); resizeObserver.observe(el) })
const values = reactive<Record<number, TagValue>>({}), popupId = ref<number>(), popupVisible = ref(false), busy = ref(false)
// 连接状态来自共享连接模块；画面可有多个实例（主画面 + 最多 3 层弹窗）共用一条连接
const hubState = realtimeHubState()
const connected = computed(() => hubState.value === 'connected')
const receiptId = ref<string | null>(null)
const previewTargetId = ref<number>()
const isPreview = computed(() => props.preview || !!props.previewDocument)
const document = computed(() => previewTargetId.value ? detail.value?.document : props.previewDocument ?? detail.value?.document)
let generation = 0, polling = false, disposed = false
let lastSnapshot = 0
let subscription: TagSubscription | undefined
let disposeReconnected: (() => void) | undefined
const library = useLibraryStore()
const usesLibrary = computed(() => (document.value?.nodes ?? []).some(n => n.type === 'template' || !!n.symbol?.startsWith('user:')))
const needsDeviceCatalog = computed(() => (document.value?.nodes ?? []).some(n => {
  if (n.action.type !== 'command' || n.action.tagId == null) return false
  const tag = tagsById.value.get(n.action.tagId)
  return !!tag && tag.areaId == null && tag.deviceId != null
}))
const boundIds = computed(() => [...new Set(expandNodes(document.value?.nodes ?? [], library.items).flatMap(n => [n.tagId, n.alarmTagId].filter((x): x is number => !!x)))])
watch(boundIds, () => { void resubscribe(); void poll() })

function applyValue(value: TagValue): void {
  if (!disposed) values[value.tagId] = value
}

// 共享连接按 tagId 只回传本画面订阅的测点；订阅走引用计数，
// 多个画面（主画面 + 弹窗）订阅同一测点时互不干扰，离开时只释放自己那份。
async function resubscribe(): Promise<void> {
  if (disposed) return
  const next = await subscribeTags(boundIds.value, applyValue)
  // 先订阅新的再释放旧的：画面切换时重叠测点不会出现取消/重订抖动
  subscription?.dispose()
  subscription = next
}
async function poll(): Promise<void> {
  if (polling || !document.value || disposed) return
  const ids = boundIds.value
  if (!ids.length) return
  const currentGeneration = generation
  polling = true
  try {
    const incoming = await realtimeApi.get(ids)
    if (generation === currentGeneration && !disposed) {
      for (const value of incoming) values[value.tagId] = value
      lastSnapshot = Date.now()
      valueError.value = ''
    }
  } catch (e) {
    if (generation === currentGeneration && !disposed) valueError.value = errorMessage(e, tr('screenRuntime.valueReadFailed'))
  } finally { polling = false }
}
function refresh(): void { void load(true) }
async function loadDevices(): Promise<void> {
  try { devices.value = await deviceApi.list(); deviceError.value = '' }
  catch (e) { deviceError.value = errorMessage(e, tr('screenRuntime.devicesLoadFailed')) }
}
function deviceCatalogBlocks(tag: Tag): boolean {
  if (!deviceError.value || tag.areaId != null || tag.deviceId == null || hasGlobal(auth.user, 'Control')) return false
  return devices.value.find(d => d.id === tag.deviceId)?.areaId == null
}
async function load(retryCatalogs = false): Promise<void> {
  if (retryCatalogs) { await library.ensure(); await loadDevices() }
  const currentGeneration = ++generation
  error.value = ''; detail.value = null
  for (const key of Object.keys(values)) delete values[Number(key)]
  if (props.previewDocument && !previewTargetId.value) { await poll(); await resubscribe(); return }
  const screenId = previewTargetId.value ?? props.screenId
  if (!screenId) return
  loading.value = true
  try { const incoming = await screenApi.get(screenId, isPreview.value); if (generation === currentGeneration && !disposed) { detail.value = incoming; await poll(); await resubscribe() } }
  catch (e) { if (generation === currentGeneration) error.value = errorMessage(e, tr('screenRuntime.kfb670f')) }
  finally { if (generation === currentGeneration) loading.value = false }
}
async function activate(node: ScreenNode): Promise<void> {
  if (isPreview.value && node.action.type === 'command') { ElMessage.info(tr('screenRuntime.k1e2e1d')); return }
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
  if (node.action.type !== 'command' || busy.value || !detail.value) return
  if (controlBlockedOnMobile()) { commandError.value = tr('screenRuntime.keffbad'); return }
  const tag = node.action.tagId == null ? undefined : tagsById.value.get(node.action.tagId)
  if (tag && deviceCatalogBlocks(tag)) { commandError.value = deviceError.value; return }
  if (!canControlBoundTag(node.action.tagId)) { commandError.value = tr('screenRuntime.kdba1d0'); return }
  busy.value = true
  commandError.value = ''
  let requestId: string | undefined
  try {
    // 与实时监视同一控制闭环：服务端预检查 → 确认 → （需要时）双人复核 → 下发回读与反馈。
    if (!await confirmDialog.value?.open(node.action.tagId!, node.action.value, `${node.text || node.id}`)) return
    requestId = createCommandId()
    const result = await screenApi.command(detail.value.id, node.id, detail.value.publishedRevision!, requestId, true)
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
onMounted(async () => {
  if (isMobile.value) void loadClientPolicy()
  const [, tags] = await Promise.all([
    library.ensure(),
    tagApi.list().then(list => ({ ok: true as const, list })).catch(() => ({ ok: false as const, list: [] as Tag[] })),
    loadDevices()
  ])
  if (disposed) return
  tagCatalog.value = tags.ok ? 'ready' : 'failed'
  tagsById.value = new Map(tags.list.map(tag => [tag.id, tag]))
  await load(); if (disposed) return
  await resubscribe()
  if (disposed) return
  // 重连后共享模块已恢复订阅，这里只补快照，避免断线期间的值缺口
  disposeReconnected = onHubReconnected(() => { if (!disposed) void poll() })
})
onBeforeUnmount(() => { resizeObserver?.disconnect(); detachPinch?.(); disposed = true; generation++; subscription?.dispose(); disposeReconnected?.() })
</script>

<style scoped>
.runtime-toolbar { display: flex; align-items: center; gap: 12px; padding: 12px 0; }
.spacer { flex: 1; }
.runtime-stage { overflow: auto; max-height: 75vh; border-radius: 8px; border: 1px solid #294260; }
.runtime-stage.fill { max-height: none; flex: 1; min-height: 0; }
.runtime { display: flex; flex-direction: column; min-height: 0; height: 100%; }
.runtime-stage.touch { max-height: none; -webkit-overflow-scrolling: touch; }
.runtime-stage.touch :deep(.screen-canvas) { touch-action: pan-x pan-y; }
.runtime-toolbar.compact { padding: 4px 0; gap: 6px; font-size: 12px; }
</style>
