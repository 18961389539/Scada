<template>
  <div class="runtime" data-testid="screen-runtime">
    <div class="runtime-toolbar" :class="{ compact, embedded: toolbarMode === 'embedded' }">
      <slot name="toolbar-start" />
      <strong v-if="toolbarMode !== 'embedded'">{{ previewTargetId ? detail?.name : previewName ?? detail?.name ?? $t('screenRuntime.loadingScreen') }}</strong>
      <el-tag v-if="isPreview" type="warning">{{ $t('screenRuntime.draftPreviewControlActionsDisabled') }}</el-tag>
      <el-tag v-if="monitorMode" type="info" size="small" data-test="runtime-monitor-mode">{{ $t('runtimeImprovements.monitorReadOnly') }}</el-tag>
      <el-tag v-if="mobileReadOnly" type="info" size="small" data-test="mobile-readonly">{{ $t('screenRuntime.mobileReadOnly') }}</el-tag>
      <el-tag v-if="replayMode" type="warning" size="small" data-test="runtime-replay-mode">{{ $t('screenReplay.active', [formatTime(replayAt ?? 0)]) }}</el-tag>
      <span v-else-if="connected" class="runtime-live">{{ $t('screenRuntime.live') }}</span>
      <el-tag v-else type="warning" size="small">{{ $t('screenRuntime.reconnectingSnapshotRefresh') }}</el-tag>
      <span class="spacer" />
      <el-button v-if="previewTargetId && previewDocument" @click="previewHome">{{ $t('screenRuntime.backToPreviewHome') }}</el-button>
      <slot name="toolbar" />
      <el-popover placement="bottom-end" :width="280" trigger="click" :title="$t('runtimeImprovements.screenOptions')">
        <template #reference><el-button :size="toolbarMode === 'embedded' ? 'small' : 'default'" data-test="runtime-more" :aria-label="$t('runtimeImprovements.screenOptions')">{{ $t('runtimeImprovements.more') }}</el-button></template>
        <div class="runtime-options">
          <el-tag v-if="!isPreview && detail?.publishedRevision != null" class="tag-quiet" :type="undefined" :disable-transitions="true" size="small" data-test="runtime-version">{{ $t('screenRuntime.runtimeVersion', [detail.publishedRevision]) }}</el-tag>
          <el-tag v-if="usesAlarms" class="tag-quiet" :type="undefined" :disable-transitions="true" size="small" data-test="runtime-alarm-scope">{{ $t('screenMonitor.systemAlarms') }}</el-tag>
          <label class="runtime-zoom-option"><span>{{ $t('screenRuntime.screenZoom') }}</span><el-select v-model="zoomChoice" :aria-label="$t('screenRuntime.screenZoom')" data-test="runtime-zoom"><el-option :label="$t('screenRuntime.fit')" value="fit" /><el-option v-for="z in zoomOptions" :key="z" :label="`${Math.round(z * 100)}%`" :value="z" /></el-select></label>
          <el-button v-if="toolbarMode === 'embedded'" :loading="loading" :disabled="anyBusy" data-test="runtime-local-refresh" @click="refresh">{{ $t('runtimeImprovements.refreshScreen') }}</el-button>
          <el-button v-else :loading="loading" :disabled="anyBusy" data-test="runtime-refresh" @click="refresh">{{ $t('screenRuntime.refresh') }}</el-button>
          <el-button v-if="editLocation && !monitorMode" data-test="runtime-edit" @click="router.push(editLocation)">{{ $t('screenRuntime.editCurrentScreen') }}</el-button>
          <slot name="options" />
        </div>
      </el-popover>
    </div>
    <div v-if="hasStatus" class="runtime-status" data-test="runtime-status">
      <p v-if="error" class="runtime-status-item danger" data-test="runtime-screen-error">{{ error }}</p>
      <p v-if="replayMode" class="runtime-status-item warn" data-test="runtime-replay-hint">{{ $t('screenReplay.alarmHint') }}</p>
      <p v-if="valueError" class="runtime-status-item danger" data-test="runtime-value-error">{{ valueError }}</p>
      <p v-if="alarmError" class="runtime-status-item danger" data-test="runtime-alarm-error">{{ alarmError }}</p>
      <p v-if="alarmLimited" class="runtime-status-item warn" data-test="runtime-alarm-limited">{{ $t('screenMonitor.alarmsLimited') }}</p>
      <p v-if="historyError" class="runtime-status-item danger" data-test="runtime-history-error">{{ historyError }}</p>
      <p v-if="oeeError" class="runtime-status-item danger" data-test="runtime-oee-error">{{ oeeError }}</p>
      <p v-if="library.error && usesLibrary" class="runtime-status-item danger" data-test="runtime-library-error">{{ library.error }}</p>
      <p v-if="deviceError && needsDeviceCatalog" class="runtime-status-item danger" data-test="runtime-device-error">{{ deviceError }}</p>
      <p v-if="tagError" class="runtime-status-item danger" data-test="runtime-tag-error">{{ tagError }}</p>
      <p v-if="unverifiedCount" class="runtime-status-item warn" data-test="runtime-quality">{{ $t('screenUx.unverifiedTags', [unverifiedCount]) }}</p>
      <p v-if="sequenceError" class="runtime-status-item danger" data-test="runtime-sequence-error">{{ sequenceError }}</p>
      <p v-if="recipeError" class="runtime-status-item danger" data-test="runtime-recipe-error">{{ recipeError }}</p>
      <p v-if="commandError" class="runtime-status-item danger" data-test="runtime-command-error">{{ commandError }}</p>
      <p v-if="newerRevision" class="runtime-status-item info" data-test="runtime-new-version">
        <span>{{ $t('screenRuntime.newVersion', [newerRevision]) }}</span>
        <el-button size="small" data-test="runtime-reload-version" @click="load()">{{ $t('screenRuntime.reloadVersion') }}</el-button>
      </p>
    </div>
    <CommandReceipt v-if="receiptId" :id="receiptId" />
    <ControlConfirmDialog ref="confirmDialog" />
    <ScreenTrendDialog v-model="trendVisible" :title="trendTitle" :series="trendSeries" :unavailable="historyUnavailable" />
    <RecipeDownloadDialog v-model="recipeDialogVisible" :recipe="recipeTarget" @completed="onRecipeDownloaded" />
    <div v-if="document" ref="stage" v-loading="loading" class="runtime-stage" :class="{ fill: fill, touch: isMobile }" :data-zoom="zoom" data-test="runtime-stage">
      <ScreenCanvas :document="document" :values="values" :sample-max-age="sampleMaxAge" :history="history" :alarm-events="alarmEvents" :alarm-unavailable="alarmUnavailable" :alarm-limited="alarmLimited" :history-unavailable="historyUnavailable" :oee-unavailable="oeeUnavailable" :tag-meta="tagMeta" :oee="oeeByLine" :zoom="zoom" :viewport="viewport" :library="runtimeLibrary" :library-failed="usesLibrary && !!library.error" :can-command="canCommandTag" :control-busy="readOnly" :replay-at="replayMode ? replayAt : null" :busy-node-ids="busyNodeIds" :command-feedback="commandFeedback" :recipe-templates="recipeTemplates" :recipes="recipes" :can-recipe="canControlRecipeTemplate" @activate="activate" @command="activateCommand" @momentary="activateMomentary" @trend="openTrend" @trend-tag="openTagTrend" @recipe="openRecipeDownload" />
    </div>
    <el-empty v-else-if="!loading && !error" :description="$t('screenRuntime.noScreenToRun')" />
    <Teleport to="body">
    <div v-if="popupVisible && popupId" class="runtime-popup" role="dialog" aria-modal="false" :aria-labelledby="popupLabelId" data-test="runtime-popup" :style="{ left: `${popupPos.x}px`, top: `${popupPos.y}px` }">
      <div class="runtime-popup-head" @pointerdown="startPopupMove">
        <strong :id="popupLabelId" data-test="runtime-popup-title">{{ popupTitle || $t('screenRuntime.loadingScreen') }}</strong>
        <button type="button" class="runtime-popup-close" :aria-label="$t('screenUx.closeDialog')" @click="popupVisible = false">×</button>
      </div>
      <div class="runtime-popup-body">
        <ScreenRuntime v-if="popupId" :key="popupId" :screen-id="popupId" :preview="isPreview" :published-preview="publishedPreview" :depth="depth + 1" :monitor-mode="monitorMode" :replay-at="replayMode ? replayAt : null" toolbar-mode="embedded" @screen-loaded="onPopupLoaded" />
      </div>
    </div>
    </Teleport>
  </div>
</template>

<script setup lang="ts">
import ControlConfirmDialog from './ControlConfirmDialog.vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { attachPinch, controlBlockedOnMobile, isMobile, loadClientPolicy, mobileControl } from '../mobile'
const confirmDialog = ref<InstanceType<typeof ControlConfirmDialog> | null>(null)
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import ScreenCanvas from './ScreenCanvas.vue'
import ScreenTrendDialog from './ScreenTrendDialog.vue'
import RecipeDownloadDialog from './RecipeDownloadDialog.vue'
import { screenApi } from '../api/screens'
import { annunciatorApi } from '../api/annunciators'
import { realtimeApi, historyApi } from '../api'
import { recipeApi, type Recipe, type RecipeDownload } from '../api/recipes'

import CommandReceipt from './CommandReceipt.vue'
import { createCommandId } from '../api/commandTypes'
import { commandErrorMessage, errorMessage } from '../api/http'
import { controlApi, type ControlCheck } from '../api/control'
import { calcJobApi } from '../api/calcJobs'
import type { ScreenDetail, ScreenDocument, ScreenNode } from '../screens/model'
import { expandNodes } from '../screens/model'
import { seriesColor } from '../screens/chartColors'
import { runtimeReturnPath, screenRunLocation, trailIds } from '../screens/navigation'
import { useLibraryStore } from '../stores/library'
import type { HistoryPoint, TagValue } from '../types'
import { useAuthStore } from '../stores/auth'
import { can, canGlobal } from '../access/policies'
import { hubState as realtimeHubState, onHubReconnected, subscribeTags, type TagSubscription } from '../realtime/hub'
import { usePolling } from '../composables/usePolling'
import { useNowTicker } from '../composables/useNowTicker'
import { useScreenCatalogs } from '../screens/useScreenCatalogs'
import { useScreenAuxData } from '../screens/useScreenAuxData'
import { tr } from '../i18n'
import { formatTime } from '../utils'

// 多个运行区域/弹窗共享发布版本查询，避免每个区域各自轮询画面目录。
let publishedCache: { at: number; items: { id: number; publishedRevision: number | null }[] } | null = null
let publishedInflight: Promise<{ id: number; publishedRevision: number | null }[]> | null = null
async function publishedRevisions(): Promise<{ id: number; publishedRevision: number | null }[]> {
  if (publishedCache && Date.now() - publishedCache.at < 5000) return publishedCache.items
  if (!publishedInflight) {
    publishedInflight = screenApi.list(false)
      .then(list => {
        const items = list.map(item => ({ id: item.id, publishedRevision: item.publishedRevision }))
        publishedCache = { at: Date.now(), items }
        return items
      })
      .finally(() => { publishedInflight = null })
  }
  return publishedInflight
}
const props = withDefaults(defineProps<{ screenId?: number; previewDocument?: ScreenDocument; previewName?: string; preview?: boolean; publishedPreview?: boolean; depth?: number
  // emit：导航与弹窗交给外层（运行布局的区域历史与浮动面板窗口）处理。
  navigateMode?: 'router' | 'emit'; compact?: boolean; fill?: boolean; initialZoom?: number | 'fit'; editReturnTo?: string
  toolbarMode?: 'default' | 'embedded'; monitorMode?: boolean; replayAt?: number | null }>(), { depth: 0, preview: false, publishedPreview: false, navigateMode: 'router', compact: false, fill: false, initialZoom: .75, toolbarMode: 'default', monitorMode: false, replayAt: null })
const emit = defineEmits<{ navigate: [screenId: number]; popup: [screenId: number]; 'screen-loaded': [screen: { id: number; name: string } | null] }>()
const route = useRoute(), router = useRouter(), auth = useAuthStore()
const commandError = ref('')
const detail = ref<ScreenDetail | null>(null), loading = ref(false), error = ref(''), valueError = ref('')
const newerRevision = ref<number | null>(null)
let revisionTimer: number | undefined
watch(detail, screen => emit('screen-loaded', screen ? { id: screen.id, name: screen.name } : null))
// 缩放：固定比例或“适应”（按区域大小等比缩放，窗口/区域尺寸变化时自动重算）。
const zoomChoice = ref<number | 'fit'>(props.initialZoom), stage = ref<HTMLDivElement>(), stageSize = reactive({ w: 0, h: 0 })
const zoom = computed(() => {
  if (zoomChoice.value !== 'fit') return zoomChoice.value
  const doc = document.value; if (!doc || !stageSize.w) return .75
  return Math.max(.1, Math.min((stageSize.w - 4) / doc.width, (stageSize.h > 40 ? (stageSize.h - 4) / doc.height : Infinity), 3))
})
let resizeObserver: ResizeObserver | undefined, detachPinch: (() => void) | undefined, detachScroll: (() => void) | undefined
const zoomOptions = computed(() => { const base = [.5, .75, 1, 1.25, 1.5]; const z = zoomChoice.value; return typeof z === 'number' && !base.includes(z) ? [...base, z].sort((a, b) => a - b) : base })
const mobileReadOnly = computed(() => isMobile.value && !mobileControl.value && !isPreview.value && can(auth.user, 'Control'))
if (isMobile.value && props.initialZoom !== 'fit' && !props.depth) zoomChoice.value = 'fit'
const viewport = reactive({ x: 0, y: 0, width: 0, height: 0 })
function updateViewport(el: HTMLDivElement): void {
  const z = zoom.value || 1
  viewport.x = el.scrollLeft / z
  viewport.y = el.scrollTop / z
  viewport.width = el.clientWidth / z
  viewport.height = el.clientHeight / z
}
watch(stage, el => {
  resizeObserver?.disconnect(); detachPinch?.(); detachScroll?.(); detachPinch = undefined; detachScroll = undefined
  if (!el) return
  detachPinch = attachPinch(el, () => zoom.value, z => { zoomChoice.value = z })
  const onScroll = () => updateViewport(el)
  el.addEventListener('scroll', onScroll, { passive: true })
  detachScroll = () => el.removeEventListener('scroll', onScroll)
  resizeObserver = new ResizeObserver(() => { stageSize.w = el.clientWidth; stageSize.h = el.clientHeight; updateViewport(el) }); resizeObserver.observe(el)
  updateViewport(el)
})
const values = reactive<Record<number, TagValue>>({}), popupId = ref<number>(), popupVisible = ref(false)
const popupTitle = ref(''), popupPos = reactive({ x: 48, y: 64 }), samplesReady = ref(false)
const { now } = useNowTicker()
const popupLabelId = `runtime-popup-title-${props.depth}`
const busyNodeIds = ref<string[]>([])
const commandFeedback = ref<Record<string, { state: 'pending' | 'rejected' | 'ok'; text: string }>>({})
const anyBusy = computed(() => busyNodeIds.value.length > 0)
function nodeIsBusy(id: string): boolean { return busyNodeIds.value.includes(id) }
function setNodeBusy(id: string, on: boolean): void {
  busyNodeIds.value = on ? [...new Set([...busyNodeIds.value, id])] : busyNodeIds.value.filter(item => item !== id)
}
function setFeedback(id: string, state: 'pending' | 'rejected' | 'ok', text: string): void {
  commandFeedback.value = { ...commandFeedback.value, [id]: { state, text } }
}
function clearFeedback(id: string): void {
  if (!commandFeedback.value[id]) return
  const next = { ...commandFeedback.value }
  delete next[id]
  commandFeedback.value = next
}
// 连接状态来自共享连接模块；画面可有多个实例（主画面 + 最多 3 层弹窗）共用一条连接
const hubState = realtimeHubState()
const connected = computed(() => hubState.value === 'connected')
const receiptId = ref<string | null>(null)
const previewTargetId = ref<number>()
const isPreview = computed(() => props.preview || props.publishedPreview || !!props.previewDocument)
const replayMode = computed(() => props.replayAt != null && Number.isFinite(props.replayAt))
const readOnly = computed(() => isPreview.value || props.monitorMode || replayMode.value || mobileReadOnly.value)
const referenceNow = computed(() => replayMode.value ? props.replayAt! : now.value)
const draftPreview = computed(() => isPreview.value && !props.publishedPreview)
const editLocation = computed(() => {
  const screenId = detail.value?.id ?? props.screenId
  if (isPreview.value || screenId == null || !canGlobal(auth.user, 'Configure')) return null
  const source = props.editReturnTo ?? (route.path.startsWith('/console') ? `/screens/${screenId}` : route.fullPath)
  return { path: `/config/screens/${screenId}`, query: { returnTo: runtimeReturnPath(source, screenId) ?? `/screens/${screenId}` } }
})
const document = computed(() => previewTargetId.value ? detail.value?.document : props.previewDocument ?? detail.value?.document)
watch(zoom, () => { if (stage.value) updateViewport(stage.value) })
let generation = 0, disposed = false
let pollingGeneration: number | undefined
let lastSnapshot = 0
let subscription: TagSubscription | undefined
let subscriptionSeq = 0
let disposeReconnected: (() => void) | undefined

const library = useLibraryStore()
const runtimeLibrary = computed(() => draftPreview.value ? library.items : [])
const runtimeNodes = computed(() => expandNodes(document.value?.nodes ?? [], runtimeLibrary.value))
const usesLibrary = computed(() => draftPreview.value && (document.value?.nodes ?? []).some(n => n.type === 'template' || !!n.symbol?.startsWith('user:')))
const catalogs = useScreenCatalogs({ isDisposed: () => disposed, monitorMode: () => props.monitorMode, runtimeNodes })
const { devices, deviceError, tagsById, tagError, canControlBoundTag, needsDeviceCatalog, tagMeta, sampleMaxAge, loadTags, loadDevices, deviceCatalogBlocks } = catalogs
const usesAlarms = computed(() => runtimeNodes.value.some(node => ['alarm-banner', 'alarm-list', 'alarm-stats'].includes(node.type)))
const boundIds = computed(() => [...new Set(runtimeNodes.value.flatMap(n => [n.tagId, n.alarmTagId, n.animation?.tagId, n.action.type === 'command' ? n.action.tagId : null, ...(n.commands ?? []).map(command => command.tagId), ...(n.tags ?? [])].filter((x): x is number => !!x)))])
const unverifiedCount = computed(() => {
  const ids = boundIds.value
  if (!ids.length) return 0
  let count = 0
  for (const id of ids) {
    const sample = values[id]
    if (!sample) { if (samplesReady.value) count++; continue }
    const maxAge = sampleMaxAge.value[id] ?? 15000
    if (sample.quality !== 'Good' || !Number.isFinite(sample.value) || referenceNow.value - sample.ts >= maxAge) count++
  }
  return count
})
// 历史、报警、OEE、配方与顺控按画面引用按需拉取；失败只影响对应图元/控件。
const aux = useScreenAuxData({ isDisposed: () => disposed, generation: () => generation, runtimeNodes, replayAt: () => replayMode.value ? props.replayAt! : null })
const { history, alarmEvents, alarmLimited, oeeByLine, recipeTemplates, recipes, sequences, scriptJobs,
  alarmError, historyError, oeeError, recipeError, sequenceError, scriptError,
  alarmUnavailable, historyUnavailable, oeeUnavailable,
  lineTagIds, usesSequences, reset: resetAux,
  loadHistory, loadAlarms, loadOee, loadAll: loadAux, startTimers: startAuxTimers, stopTimers: stopAuxTimers } = aux
const hasStatus = computed(() => !!(replayMode.value || error.value || valueError.value || alarmError.value || alarmLimited.value || historyError.value || oeeError.value || (library.error && usesLibrary.value) || (deviceError.value && needsDeviceCatalog.value) || tagError.value || unverifiedCount.value || sequenceError.value || recipeError.value || commandError.value || newerRevision.value))
watch(boundIds, () => { void resubscribe(); void poll() })
watch(lineTagIds, () => { void loadHistory() })
let replayAuxAt = 0
watch(() => props.replayAt, async (at, previous) => {
  if (disposed) return
  if (at == null) {
    replayAuxAt = 0
    if (previous != null && document.value) await load()
    return
  }
  if (previous == null) {
    releaseAllJogs()
    subscription?.dispose(); subscription = undefined; subscriptionSeq++
  }
  const jumped = previous == null || Math.abs(at - previous) >= 10000
  if (jumped || Date.now() - replayAuxAt >= 5000) {
    replayAuxAt = Date.now()
    await Promise.all([poll(), loadAux(false)])
  } else {
    await poll()
  }
})

function applyValue(value: TagValue): void {
  if (!disposed && (!values[value.tagId] || value.ts >= values[value.tagId]!.ts)) values[value.tagId] = value
}

// 共享连接按 tagId 只回传本画面订阅的测点；订阅走引用计数，
// 多个画面（主画面 + 弹窗）订阅同一测点时互不干扰，离开时只释放自己那份。
async function resubscribe(): Promise<void> {
  if (disposed) return
  if (replayMode.value) {
    subscription?.dispose(); subscription = undefined; subscriptionSeq++
    return
  }
  const seq = ++subscriptionSeq, currentGeneration = generation
  const next = await subscribeTags(boundIds.value, value => {
    if (currentGeneration === generation) applyValue(value)
  })
  if (disposed || seq !== subscriptionSeq || currentGeneration !== generation) { next.dispose(); return }
  // 先订阅新的再释放旧的：画面切换时重叠测点不会出现取消/重订抖动
  subscription?.dispose()
  subscription = next
}
function canCommandTag(tagId: number | null): boolean { return !replayMode.value && canControlBoundTag(tagId) }
// 配方控件按模板区域及全部参数测点的控制权限预判；服务端在预检查与下发时再次完整校验。
function canControlRecipeTemplate(templateId: number): boolean {
  if (props.monitorMode || replayMode.value) return false
  const template = recipeTemplates.value.find(item => item.id === templateId)
  if (!template || !can(auth.user, 'Control', template.areaId)) return false
  return template.parameters.every(parameter => canControlBoundTag(parameter.tagId))
}
// 趋势详情弹窗：画布十字线点击、趋势图键盘回车或历史表格行双击打开。
interface TrendSeriesView { tagId: number; name: string; unit: string; color: string; points: HistoryPoint[] }
const trendVisible = ref(false), trendTitle = ref(''), trendTags = ref<number[]>([]), trendDecimals = ref(2)
const trendSeries = computed<TrendSeriesView[]>(() => trendTags.value.map((id, index) => ({
  tagId: id, name: tagMeta.value[id]?.name ?? `#${id}`, unit: tagMeta.value[id]?.unit ?? '', color: seriesColor(index), points: history[id] ?? []
})))
function openTrend(node: ScreenNode): void {
  const tags = node.tags ?? []
  if (!tags.length) return
  trendTags.value = tags; trendDecimals.value = node.decimals
  trendTitle.value = node.text ? `${node.text} · ${tr('hmi.trend')}` : tr('hmi.trend')
  trendVisible.value = true
}
function openTagTrend(node: ScreenNode, tagId: number): void {
  trendTags.value = [tagId]; trendDecimals.value = node.decimals
  const name = tagMeta.value[tagId]?.name ?? `#${tagId}`
  trendTitle.value = `${node.text ? `${node.text} · ` : ''}${name} · ${tr('hmi.trend')}`
  trendVisible.value = true
}
const recipeDialogVisible = ref(false), recipeTarget = ref<Recipe | null>(null)
let recipePollTimer: number | undefined
function openRecipeDownload(_node: ScreenNode, recipeId: number): void {
  if (replayMode.value) { commandError.value = tr('screenReplay.readOnly'); return }
  if (isPreview.value) { commandError.value = tr('screenRuntime.previewModeHint'); return }
  if (props.monitorMode) { commandError.value = tr('runtimeImprovements.monitorReadOnly'); return }
  const recipe = recipes.value.find(item => item.id === recipeId)
  if (!recipe) { recipeError.value = tr('hmi.recipeUnavailable'); return }
  if (!can(auth.user, 'Control', recipe.areaId)) { commandError.value = tr('hmi.recipeDenied'); return }
  recipeTarget.value = recipe; recipeDialogVisible.value = true
}
function onRecipeDownloaded(download: RecipeDownload): void {
  if (['Pending', 'Running'].includes(download.status)) void watchRecipeDownload(download.id)
}
async function watchRecipeDownload(id: number): Promise<void> {
  const deadline = Date.now() + 30000
  const tick = async (): Promise<void> => {
    if (disposed || Date.now() > deadline) return
    try {
      const download = await recipeApi.downloadStatus(id)
      if (['Completed', 'Failed', 'PartiallyApplied', 'Aborted', 'Rejected'].includes(download.status)) {
        if (download.status === 'Completed') ElMessage.success(tr('hmi.recipeCompleted'))
        else ElMessage.warning(download.message || tr('hmi.recipeUnfinished', [download.statusLabel]))
        await poll(); return
      }
    } catch { /* 轮询期间忽略瞬时错误，下一次继续 */ }
    recipePollTimer = window.setTimeout(() => void tick(), 2000)
  }
  window.clearTimeout(recipePollTimer)
  recipePollTimer = window.setTimeout(() => void tick(), 1500)
}
// 脚本动作：只运行已发布任务，区域控制权限由服务端复核。
async function startScript(node: ScreenNode): Promise<void> {
  if (isPreview.value) { commandError.value = tr('screenRuntime.previewModeHint'); return }
  if (props.monitorMode) { commandError.value = tr('runtimeImprovements.monitorReadOnly'); return }
  if (nodeIsBusy(node.id)) return
  const job = scriptJobs.value.find(item => item.id === node.action.jobId)
  if (!job || !job.publishedRevision) { commandError.value = scriptError.value || tr('hmi.scriptUnavailable'); return }
  if (!can(auth.user, 'Control', job.areaId)) { commandError.value = tr('hmi.scriptDenied'); return }
  if (controlBlockedOnMobile()) { commandError.value = tr('screenRuntime.mobileControlBlocked'); return }
  try { await ElMessageBox.confirm(tr('hmi.scriptConfirm', [job.name, node.action.value]), tr('hmi.scriptAction'), { type: 'warning', confirmButtonText: tr('hmi.confirm'), cancelButtonText: tr('hmi.cancel') }) }
  catch { return }
  setNodeBusy(node.id, true); commandError.value = ''; setFeedback(node.id, 'pending', tr('screenUx.pending'))
  try {
    await calcJobApi.invoke(job.id, node.action.value)
    setFeedback(node.id, 'ok', tr('screenUx.done'))
    ElMessage.success(tr('hmi.scriptDone', [job.name]))
  } catch (e) { commandError.value = commandErrorMessage(e, tr('hmi.scriptFailed')); setFeedback(node.id, 'rejected', tr('screenUx.rejected')); ElMessage.error({ message: commandError.value, duration: 5000, showClose: true }) }
  finally { setNodeBusy(node.id, false) }
}
// 顺控动作：确认后走服务端顺控启动端点（区域控制权限、审批与审计由服务端执行）。
async function startSequence(node: ScreenNode): Promise<void> {
  if (isPreview.value) { commandError.value = tr('screenRuntime.previewModeHint'); return }
  if (props.monitorMode) { commandError.value = tr('runtimeImprovements.monitorReadOnly'); return }
  if (nodeIsBusy(node.id)) return
  const sequence = sequences.value.find(item => item.id === node.action.sequenceId)
  if (!sequence) { commandError.value = tr('hmi.sequenceUnavailable'); return }
  if (!can(auth.user, 'Control', sequence.areaId)) { commandError.value = tr('hmi.sequenceDenied'); return }
  if (controlBlockedOnMobile()) { commandError.value = tr('screenRuntime.mobileControlBlocked'); return }
  try { await ElMessageBox.confirm(tr('hmi.sequenceConfirm', [sequence.name, sequence.steps.length]), tr('hmi.sequence'), { type: 'warning', confirmButtonText: tr('hmi.confirm'), cancelButtonText: tr('hmi.cancel') }) }
  catch { return }
  setNodeBusy(node.id, true); commandError.value = ''; setFeedback(node.id, 'pending', tr('screenUx.pending'))
  try {
    const result = await controlApi.start(sequence.id)
    if (result.pending) { setFeedback(node.id, 'pending', tr('hmi.sequencePending')); ElMessage.warning(tr('hmi.sequencePending')) }
    else { setFeedback(node.id, 'ok', tr('screenUx.done')); ElMessage.success(tr('hmi.sequenceStarted', [result.runId ?? '-'])) }
  } catch (e) {
    commandError.value = commandErrorMessage(e, tr('hmi.sequenceFailed'))
    setFeedback(node.id, 'rejected', tr('screenUx.rejected'))
    ElMessage.error({ message: commandError.value, duration: 5000, showClose: true })
  } finally { setNodeBusy(node.id, false) }
}
async function poll(): Promise<void> {
  if (pollingGeneration === generation || !document.value || disposed) return
  if (replayMode.value) { await pollReplay(); return }
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
      samplesReady.value = true
    }
  } catch (e) {
    if (generation === currentGeneration && !disposed) valueError.value = errorMessage(e, tr('screenRuntime.valueReadFailed'))
  } finally { if (pollingGeneration === currentGeneration) pollingGeneration = undefined }
}
// 历史复盘：按回放时刻取每个测点最后一条历史采样；ts 使用回放时刻，sourceTs 保留真实采样时间。
let replayPollSeq = 0
async function pollReplay(): Promise<void> {
  const at = props.replayAt
  if (at == null || !document.value || disposed) return
  const seq = ++replayPollSeq
  const ids = boundIds.value
  const currentGeneration = generation
  try {
    const result = ids.length ? await historyApi.snapshot(ids, at) : { ts: at, items: [] }
    if (seq !== replayPollSeq || generation !== currentGeneration || disposed) return
    for (const key of Object.keys(values)) delete values[Number(key)]
    for (const item of result.items) {
      const quality: TagValue['quality'] = item.value == null ? 'Bad' : item.quality ?? 'Bad'
      values[item.tagId] = {
        tagId: item.tagId,
        value: item.value ?? 0,
        quality,
        ts: at,
        sourceTs: item.sourceTs ?? null,
        error: item.error ?? (item.value == null ? tr('screenReplay.noData') : null)
      }
    }
    samplesReady.value = true
    valueError.value = ''
  } catch (e) {
    if (generation === currentGeneration && !disposed) valueError.value = errorMessage(e, tr('screenReplay.loadFailed'))
  }
}
function refresh(): void { void load(true) }
async function checkNewerRevision(): Promise<void> {
  if (replayMode.value) return
  const screenId = previewTargetId.value ?? props.screenId
  if (isPreview.value || !screenId) return
  try {
    const latest = (await publishedRevisions()).find(item => item.id === screenId)?.publishedRevision ?? null
    const current = detail.value?.publishedRevision ?? null
    newerRevision.value = latest != null && current != null && latest > current ? latest : null
  } catch { /* 版本检查失败不阻塞运行 */ }
}
async function load(retryCatalogs = false): Promise<void> {
  releaseAllJogs()
  const currentGeneration = ++generation
  newerRevision.value = null
  const screenId = previewTargetId.value ?? props.screenId
  loading.value = true
  error.value = ''; detail.value = null
  valueError.value = ''; lastSnapshot = 0; samplesReady.value = false
  resetAux()
  for (const key of Object.keys(values)) delete values[Number(key)]
  try {
    if (retryCatalogs) await Promise.all([draftPreview.value ? library.reload() : undefined, loadDevices(), loadTags()])
    if (disposed || generation !== currentGeneration) return
    if (props.previewDocument && !previewTargetId.value) { await Promise.all([poll(), resubscribe(), loadAux(retryCatalogs), loadTags()]); return }
    if (!screenId) return
    const incoming = await screenApi.get(screenId, draftPreview.value)
    if (generation !== currentGeneration || disposed) return
    if (incoming.requiresRepublish) { error.value = incoming.message ?? tr('screenRuntime.failedToLoadScreen'); return }
    detail.value = incoming
    await Promise.all([poll(), resubscribe(), loadAux(retryCatalogs), loadTags()])
  }
  catch (e) { if (!disposed && generation === currentGeneration) error.value = errorMessage(e, tr('screenRuntime.failedToLoadScreen')) }
  finally { if (generation === currentGeneration) loading.value = false }
}
async function activate(node: ScreenNode): Promise<void> {
  // 历史复盘只允许浏览和导航，任何控制、复位、消音、配方、顺控都拒绝。
  const navigation = node.action.type === 'navigate' || node.action.type === 'popup'
  if (replayMode.value && !navigation) { commandError.value = tr('screenReplay.readOnly'); return }
  // 复位（报警）与消音按当前用户可见范围批量执行，走报警/声光端点而不是画面控制闭环。
  if (node.type === 'reset' && node.resetMode !== 'tag') { await resetVisibleAlarms(); return }
  if (node.type === 'silence') { await silenceVisibleAnnunciators(node); return }
  if (node.action.type === 'sequence') { await startSequence(node); return }
  if (node.action.type === 'script') { await startScript(node); return }
  if (node.action.type === 'recipe') { if (node.action.recipeId) openRecipeDownload(node, node.action.recipeId); return }
  if (isPreview.value && node.action.type === 'command') { commandError.value = tr('screenRuntime.previewModeHint'); return }
  if (props.navigateMode === 'emit' && !isPreview.value && (node.action.type === 'navigate' || node.action.type === 'popup') && node.action.screenId) {
    if (node.action.type === 'navigate') emit('navigate', node.action.screenId); else emit('popup', node.action.screenId)
    return
  }
  if (node.action.type === 'navigate' && node.action.screenId) {
    if (isPreview.value) { previewTargetId.value = node.action.screenId; await load() }
    else if (route.path.startsWith('/kiosk/screens/')) {
      await router.push({ path: `/kiosk/screens/${node.action.screenId}`, query: props.monitorMode ? { monitorOnly: '1' } : {} })
    } else {
      const current = Number(route.params.id)
      const previous = [...trailIds(route.query), current].filter(item => Number.isInteger(item) && item > 0 && item !== node.action.screenId).slice(-8)
      await router.push(screenRunLocation(node.action.screenId, route.query, previous))
    }
    popupVisible.value = false
  }
  if (node.action.type === 'popup' && node.action.screenId) {
    if (props.depth >= 3) { commandError.value = tr('screenRuntime.popupsCanNestAtMostThreeLevels'); return }
    popupId.value = node.action.screenId
    popupTitle.value = ''
    const width = Math.min(640, window.innerWidth - 32)
    popupPos.x = Math.max(16, window.innerWidth - width - 16)
    popupPos.y = 64 + props.depth * 28
    popupVisible.value = true
  }
  if (node.action.type !== 'command') return
  await activateCommand(node, node.action.value, undefined, node.type === 'pulse' ? 'pulse' : undefined)
}
// 复位/消音：服务端逐面板按区域校验确认权限并审计；前端只做入口权限提示与结果反馈。
async function announceAckDenied(): Promise<boolean> {
  if (!can(auth.user, 'AckAlarm')) { commandError.value = tr('hmi.ackDenied'); ElMessage.error(commandError.value); return true }
  return false
}
async function resetVisibleAlarms(): Promise<void> {
  if (isPreview.value) { commandError.value = tr('screenRuntime.previewModeHint'); return }
  if (props.monitorMode) { commandError.value = tr('runtimeImprovements.monitorReadOnly'); return }
  if (await announceAckDenied()) return
  try { await ElMessageBox.confirm(tr('hmi.resetConfirm'), tr('hmi.reset'), { type: 'warning', confirmButtonText: tr('hmi.confirm'), cancelButtonText: tr('hmi.cancel') }) }
  catch { return }
  try {
    const result = await annunciatorApi.ackVisible(tr('hmi.resetNote'))
    ElMessage.success(tr('hmi.resetDone', [result.acked, result.total]))
    await loadAlarms(true)
  } catch (e) { commandError.value = errorMessage(e, tr('hmi.resetFailed')); ElMessage.error({ message: commandError.value, duration: 5000, showClose: true }) }
}
async function silenceVisibleAnnunciators(node: ScreenNode): Promise<void> {
  if (isPreview.value) { commandError.value = tr('screenRuntime.previewModeHint'); return }
  if (props.monitorMode) { commandError.value = tr('runtimeImprovements.monitorReadOnly'); return }
  if (await announceAckDenied()) return
  const minutes = node.silenceMinutes ?? 5
  try { await ElMessageBox.confirm(tr('hmi.silenceConfirm', [minutes]), tr('hmi.silence'), { type: 'warning', confirmButtonText: tr('hmi.confirm'), cancelButtonText: tr('hmi.cancel') }) }
  catch { return }
  try {
    const result = await annunciatorApi.silenceVisible(minutes, tr('hmi.silenceNote'))
    ElMessage.success(tr('hmi.silenceDone', [result.silenced, result.total]))
  } catch (e) { commandError.value = errorMessage(e, tr('hmi.silenceFailed')); ElMessage.error({ message: commandError.value, duration: 5000, showClose: true }) }
}
// 点动：按下持有租约，松开立即释放；请求编号在按下前生成，释放复用同一编号定位租约。
// 松开早于按下响应时先发一次释放并向服务端登记，按下完成后再补发，覆盖请求乱序。
interface JogState { requestId: string; pressing: boolean; released: boolean }
const jogStates = reactive<Record<string, JogState>>({})
async function activateMomentary(node: ScreenNode, phase: 'press' | 'release'): Promise<void> {
  if (replayMode.value) { commandError.value = tr('screenReplay.readOnly'); return }
  if (phase === 'press') { if (!jogStates[node.id]) void pressJog(node); return }
  const state = jogStates[node.id]
  if (!state || state.released) return
  state.released = true
  if (!state.pressing) delete jogStates[node.id]
  await releaseJog(node, state)
}
async function pressJog(node: ScreenNode): Promise<void> {
  const requestId = createCommandId()
  const state: JogState = { requestId, pressing: true, released: false }
  jogStates[node.id] = state
  const attempt = await activateCommand(node, node.action.value, undefined, 'press', requestId)
  state.pressing = false
  if (!attempt.ok) { if (jogStates[node.id] === state) delete jogStates[node.id]; return }
  if (state.released) { if (jogStates[node.id] === state) delete jogStates[node.id]; await releaseJog(node, state) }
}
async function releaseJog(node: ScreenNode, state: JogState): Promise<void> {
  if (!detail.value) return
  let failure: string
  try {
    const result = await screenApi.command(detail.value.id, node.id, detail.value.publishedRevision!, state.requestId, true, undefined, undefined, 'release')
    if (result.success) { await poll(); return }
    failure = result.message || tr('hmi.jogReleaseFailed')
  } catch (e) {
    failure = (e as { response?: unknown }).response ? errorMessage(e, tr('hmi.jogReleaseFailed')) : tr('commandUx.jogReleaseUnknown')
  }
  commandError.value = failure
  ElMessage.error({ message: failure, duration: 5000, showClose: true })
}
// 窗口失焦/页面隐藏/卸载时没有任何 pointerup 可用，主动释放所有保持中的点动；服务端看门狗兜底。
function releaseAllJogs(): void {
  const nodes = new Map(runtimeNodes.value.map(node => [node.id, node]))
  for (const [nodeId, state] of Object.entries(jogStates)) {
    const node = nodes.get(nodeId)
    if (!state.released) { state.released = true; if (!state.pressing) delete jogStates[nodeId] }
    if (node) void releaseJog(node, state)
  }
}
function onVisibilityChange(): void { if (window.document.visibilityState === 'hidden') releaseAllJogs() }
// 写交互控件（开关/滑块/步进）下发运行期取值；与固定值按钮共用同一控制闭环。
// 默认静默预检查后直接下发，结果用非阻塞通知呈现；仅控制点显式要求“操作确认”时才弹确认框。
async function activateCommand(node: ScreenNode, value: number, operation?: string, phase?: string, fixedRequestId?: string): Promise<{ ok: boolean; requestId?: string }> {
  if (replayMode.value) { commandError.value = tr('screenReplay.readOnly'); return { ok: false } }
  if (isPreview.value) { commandError.value = tr('screenRuntime.previewModeHint'); return { ok: false } }
  if (props.monitorMode) { commandError.value = tr('runtimeImprovements.monitorReadOnly'); return { ok: false } }
  const command = operation ? node.commands?.find(item => item.key === operation) : undefined
  const tagId = operation ? command?.tagId : node.action.tagId
  if (nodeIsBusy(node.id) || !detail.value || tagId == null || !Number.isFinite(value)) return { ok: false }
  if (controlBlockedOnMobile()) { commandError.value = tr('screenRuntime.mobileControlBlocked'); setFeedback(node.id, 'rejected', tr('screenUx.rejected')); return { ok: false } }
  const tag = tagsById.value.get(tagId)
  if (tag && deviceCatalogBlocks(tag)) { commandError.value = deviceError.value; setFeedback(node.id, 'rejected', tr('screenUx.rejected')); return { ok: false } }
  if (!canControlBoundTag(tagId)) { commandError.value = tr('screenRuntime.currentUserHasNoControlPermission'); setFeedback(node.id, 'rejected', tr('screenUx.rejected')); return { ok: false } }
  setNodeBusy(node.id, true)
  commandError.value = ''
  setFeedback(node.id, 'pending', tr('screenUx.pending'))
  const label = `${node.text || node.id}${command ? ` / ${command.label}` : ''}`
  let check: ControlCheck
  try {
    // 服务端预检查（限幅、变化率、许可、就地/远程、挂牌），拒绝原因以非阻塞通知呈现。
    check = await controlApi.check(tagId, value)
  } catch (e) {
    setNodeBusy(node.id, false)
    commandError.value = tr('commandUx.checkFailed', [errorMessage(e, tr('controlConfirmDialog.k831e7c'))])
    setFeedback(node.id, 'rejected', tr('screenUx.rejected'))
    ElMessage.error({ message: commandError.value, duration: 5000, showClose: true })
    return { ok: false }
  }
  if (check.refusals.length) {
    setNodeBusy(node.id, false)
    commandError.value = check.refusals.join('；')
    setFeedback(node.id, 'rejected', tr('screenUx.rejected'))
    ElMessage.error({ message: commandError.value, duration: 5000, showClose: true })
    return { ok: false }
  }
  const needsConfirm = !!check.requireConfirm
  const confirmed = needsConfirm ? await confirmDialog.value?.open(tagId, value, label, check) ?? false : false
  if (needsConfirm && !confirmed) { setNodeBusy(node.id, false); clearFeedback(node.id); return { ok: false } }
  let requestId: string | undefined
  let ok = false
  try {
    requestId = fixedRequestId ?? createCommandId()
    const result = await screenApi.command(detail.value.id, node.id, detail.value.publishedRevision!, requestId, confirmed, value, operation, phase)
    receiptId.value = result.commandId ?? null
    ok = result.success
    if (result.pending) { setFeedback(node.id, 'pending', tr('screenRuntime.dualApprovalPending')); ElMessage.warning(tr('screenRuntime.dualApprovalPending')) }
    else if (result.success) { setFeedback(node.id, 'ok', tr('screenUx.readBack', [result.readBack ?? ''])); ElMessage.success(tr('screenRuntime.executedReadBack', [result.readBack])) }
    else { commandError.value = result.message || tr('screenRuntime.controlFailed'); setFeedback(node.id, 'rejected', tr('screenUx.rejected')) }
    await poll()
  } catch (e) {
    if (e !== 'cancel' && e !== 'close') { receiptId.value = requestId ?? null; commandError.value = commandErrorMessage(e, tr('screenRuntime.controlFailed')); setFeedback(node.id, 'rejected', tr('screenUx.rejected')) }
  }
  finally { setNodeBusy(node.id, false) }
  return { ok, requestId }
}
function onPopupLoaded(screen: { id: number; name: string } | null): void {
  if (screen && screen.id === popupId.value) popupTitle.value = screen.name
}
function startPopupMove(event: PointerEvent): void {
  if ((event.target as HTMLElement).closest('button')) return
  const handle = event.currentTarget as HTMLElement
  const startX = event.clientX, startY = event.clientY, originX = popupPos.x, originY = popupPos.y
  const move = (e: PointerEvent) => {
    popupPos.x = Math.min(window.innerWidth - 120, Math.max(0, originX + e.clientX - startX))
    popupPos.y = Math.min(window.innerHeight - 80, Math.max(0, originY + e.clientY - startY))
  }
  const stop = () => { handle.removeEventListener('pointermove', move); handle.removeEventListener('pointerup', stop) }
  handle.setPointerCapture(event.pointerId)
  handle.addEventListener('pointermove', move)
  handle.addEventListener('pointerup', stop)
}
async function previewHome(): Promise<void> { previewTargetId.value = undefined; await load() }
watch(() => props.screenId, () => { popupVisible.value = false; void load() })
// 兜底轮询：未连接、或距上次 HTTP 快照超过 5s 时拉取；lastSnapshot 只由 poll() 更新，因此连接正常时仍约每 5s 补拉一次，画面切到后台自动暂停。
usePolling(() => { if (replayMode.value) return; if (!connected.value || Date.now() - lastSnapshot >= 5000) return poll() }, 1000)

onMounted(async () => {
  if (isMobile.value) void loadClientPolicy()
  await Promise.all([draftPreview.value ? library.ensure() : undefined, loadTags(), loadDevices()])
  if (disposed) return
  await load(); if (disposed) return
  await resubscribe()
  if (disposed) return
  startAuxTimers()
  revisionTimer = setInterval(() => void checkNewerRevision(), 20000)
  // 重连后共享模块已恢复订阅，这里只补快照，避免断线期间的值缺口
  disposeReconnected = onHubReconnected(() => { if (!disposed) void poll() })
  window.addEventListener('blur', releaseAllJogs)
  window.addEventListener('pagehide', releaseAllJogs)
  window.document.addEventListener('visibilitychange', onVisibilityChange)
})
onBeforeUnmount(() => { releaseAllJogs(); window.clearTimeout(recipePollTimer); window.removeEventListener('blur', releaseAllJogs); window.removeEventListener('pagehide', releaseAllJogs); window.document.removeEventListener('visibilitychange', onVisibilityChange); resizeObserver?.disconnect(); detachPinch?.(); detachScroll?.(); disposed = true; generation++; subscription?.dispose(); disposeReconnected?.(); stopAuxTimers(); clearInterval(revisionTimer) })
</script>

<style scoped>
.runtime-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 12px; padding: 12px 0; }
.runtime-toolbar > .el-tag,
.runtime-live { flex-shrink: 0; }
.runtime-live { font-size: 12px; line-height: 20px; opacity: 0.8; }
.runtime-toolbar > strong { min-width: 0; overflow-wrap: anywhere; }
@media (max-width: 600px) { .runtime-toolbar > strong { flex-basis: 100%; } .runtime-toolbar .spacer { display: none; } }
.spacer { flex: 1; }
.runtime-status { display: flex; flex-wrap: wrap; gap: 6px; max-height: 72px; overflow: auto; margin-bottom: 6px; }
.runtime-status-item { flex: 1 1 220px; min-width: 0; margin: 0; padding: 4px 8px; border-radius: 6px; font-size: 12px; line-height: 1.4; }
.runtime-status-item.danger { background: color-mix(in srgb, var(--el-color-danger) 14%, transparent); color: var(--el-color-danger); }
.runtime-status-item.warn { background: color-mix(in srgb, var(--el-color-warning) 16%, transparent); color: var(--el-color-warning); }
.runtime-status-item.info { display: flex; align-items: center; gap: 8px; background: color-mix(in srgb, var(--el-color-info) 14%, transparent); color: var(--el-color-info); }
.runtime-stage { overflow: auto; max-height: 75vh; border-radius: 8px; border: 1px solid #294260; }
.runtime-stage.fill { max-height: none; flex: 1; min-height: 0; }
.runtime { display: flex; flex-direction: column; min-height: 0; height: 100%; }
.runtime-stage.touch { max-height: none; -webkit-overflow-scrolling: touch; }
.runtime-stage.touch :deep(.screen-canvas) { touch-action: pan-x pan-y; }
.runtime-toolbar.compact { padding: 4px 0; gap: 6px; font-size: 12px; }
.runtime-toolbar.embedded { min-height: 28px; padding: 2px 0 4px; gap: 6px; }
.runtime-toolbar.embedded :deep(.el-tag) { font-size: 11px; }
.runtime-options { display: flex; flex-direction: column; align-items: stretch; gap: 12px; }
.runtime-options > .el-tag { align-self: flex-start; }
.runtime-options > .el-button { margin-left: 0; }
.runtime-zoom-option { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.runtime-zoom-option > .el-select { width: 120px; }
.runtime-popup { position: fixed; z-index: 4100; width: min(640px, calc(100vw - 32px)); height: min(480px, calc(100vh - 96px)); display: flex; flex-direction: column; background: var(--el-bg-color-overlay, #132033); color: var(--el-text-color-primary, #dce6f0); border: 1px solid var(--scada-border, #294260); border-radius: 8px; box-shadow: 0 12px 40px rgb(0 0 0 / 35%); overflow: hidden; }
.runtime-popup-head { display: flex; align-items: center; gap: 8px; padding: 8px 8px 8px 12px; cursor: grab; user-select: none; border-bottom: 1px solid var(--scada-border, #294260); }
.runtime-popup-head strong { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.runtime-popup-close { width: 32px; height: 32px; border: 0; border-radius: 6px; background: transparent; color: inherit; font: inherit; cursor: pointer; }
.runtime-popup-body { flex: 1; min-height: 0; overflow: auto; padding: 0 12px 12px; }
.runtime-popup-body :deep(.runtime) { height: 100%; }
</style>
