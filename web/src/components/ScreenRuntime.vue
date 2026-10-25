<template>
  <div v-loading="loading" class="runtime" data-testid="screen-runtime">
    <div class="runtime-toolbar" :class="{ compact }">
      <strong>{{ previewTargetId ? detail?.name : previewName ?? detail?.name ?? '加载画面' }}</strong>
      <el-tag v-if="isPreview" type="warning">草稿预览 · 控制动作禁用</el-tag>
      <el-tag v-else type="success" size="small">运行版本 {{ detail?.publishedRevision }}</el-tag>
      <el-tag :type="connected ? 'success' : 'warning'" size="small">{{ connected ? '实时连接' : '重连中 · 快照刷新' }}</el-tag>
      <span class="spacer" />
      <el-button v-if="previewTargetId && previewDocument" @click="previewHome">返回预览首页</el-button>
      <slot name="toolbar" />
      <el-select v-model="zoomChoice" aria-label="画面缩放" style="width: 100px" data-test="runtime-zoom"><el-option label="适应" value="fit" /><el-option v-for="z in [.5, .75, 1, 1.25, 1.5]" :key="z" :label="`${z * 100}%`" :value="z" /></el-select>
      <el-button @click="load">刷新</el-button>
    </div>
    <el-alert v-if="error" type="error" :title="error" :closable="false" show-icon />
    <CommandReceipt v-if="receiptId" :id="receiptId" />
    <ControlConfirmDialog ref="confirmDialog" />
    <div v-if="document" ref="stage" class="runtime-stage" :class="{ fill: fill }" data-test="runtime-stage">
      <ScreenCanvas :document="document" :values="values" :zoom="zoom" :library="library.items" @activate="activate" />
    </div>
    <el-empty v-else-if="!loading && !error" description="暂无可运行画面" />
    <el-dialog v-model="popupVisible" title="设备画面" width="90%" append-to-body destroy-on-close>
      <ScreenRuntime v-if="popupVisible && popupId" :key="popupId" :screen-id="popupId" :preview="isPreview" :depth="depth + 1" />
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import ControlConfirmDialog from './ControlConfirmDialog.vue'
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
const confirmDialog = ref<InstanceType<typeof ControlConfirmDialog> | null>(null)
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import ScreenCanvas from './ScreenCanvas.vue'
import { screenApi } from '../api/screens'
import { realtimeApi } from '../api'
import CommandReceipt from './CommandReceipt.vue'
import { createCommandId } from '../api/commandTypes'
import { errorMessage } from '../api/http'
import type { ScreenDetail, ScreenDocument, ScreenNode } from '../screens/model'
import { expandNodes } from '../screens/model'
import { useLibraryStore } from '../stores/library'
import type { TagValue } from '../types'
import { useAuthStore } from '../stores/auth'
import { hasAny } from '../utils'
import * as signalR from '@microsoft/signalr'
import { TOKEN_KEY } from '../api/http'
const props = withDefaults(defineProps<{ screenId?: number; previewDocument?: ScreenDocument; previewName?: string; preview?: boolean; depth?: number
  // emit：导航与弹窗交给外层（运行布局的区域历史与浮动面板窗口）处理。
  navigateMode?: 'router' | 'emit'; compact?: boolean; fill?: boolean; initialZoom?: number | 'fit' }>(), { depth: 0, preview: false, navigateMode: 'router', compact: false, fill: false, initialZoom: .75 })
const emit = defineEmits<{ navigate: [screenId: number]; popup: [screenId: number] }>()
const router = useRouter(), auth = useAuthStore()
const detail = ref<ScreenDetail | null>(null), loading = ref(false), error = ref('')
// 缩放：固定比例或“适应”（按区域大小等比缩放，窗口/区域尺寸变化时自动重算）。
const zoomChoice = ref<number | 'fit'>(props.initialZoom), stage = ref<HTMLDivElement>(), stageSize = reactive({ w: 0, h: 0 })
const zoom = computed(() => {
  if (zoomChoice.value !== 'fit') return zoomChoice.value
  const doc = document.value; if (!doc || !stageSize.w) return .75
  return Math.max(.1, Math.min((stageSize.w - 4) / doc.width, (stageSize.h > 40 ? (stageSize.h - 4) / doc.height : Infinity), 3))
})
let resizeObserver: ResizeObserver | undefined
watch(stage, el => { resizeObserver?.disconnect(); if (!el) return; resizeObserver = new ResizeObserver(() => { stageSize.w = el.clientWidth; stageSize.h = el.clientHeight }); resizeObserver.observe(el) })
const values = reactive<Record<number, TagValue>>({}), popupId = ref<number>(), popupVisible = ref(false), busy = ref(false)
const connected = ref(false)
const receiptId = ref<string | null>(null)
const previewTargetId = ref<number>()
const isPreview = computed(() => props.preview || !!props.previewDocument)
const document = computed(() => previewTargetId.value ? detail.value?.document : props.previewDocument ?? detail.value?.document)
let timer: ReturnType<typeof setInterval> | undefined, generation = 0, polling = false, disposed = false
let hub: signalR.HubConnection | undefined, retry: ReturnType<typeof setTimeout> | undefined, lastSnapshot = 0
let subscribedIds: number[] = []
const library = useLibraryStore()
const boundIds = computed(() => [...new Set(expandNodes(document.value?.nodes ?? [], library.items).flatMap(n => [n.tagId, n.alarmTagId].filter((x): x is number => !!x)))])
watch(boundIds, () => { void subscribe(); void poll() })
async function subscribe(): Promise<void> {
  if (hub?.state !== signalR.HubConnectionState.Connected) return
  if (subscribedIds.length) await hub.invoke('Unsubscribe', subscribedIds)
  await hub.invoke('Subscribe', boundIds.value); subscribedIds = [...boundIds.value]
}
async function startHub(): Promise<void> {
  if (disposed) return
  hub ??= new signalR.HubConnectionBuilder().withUrl('/hubs/realtime', { accessTokenFactory: () => localStorage.getItem(TOKEN_KEY) ?? '' }).withAutomaticReconnect().build()
  hub.off('values')
  hub.on('values', (incoming: TagValue[]) => { if (!disposed) for (const value of incoming) values[value.tagId] = value })
  try {
    await hub.start()
    if (disposed) { await hub.stop(); return }
    connected.value = true; await subscribe(); await poll()
  } catch { if (!disposed) retry = setTimeout(() => void startHub(), 5000) }
}
async function poll(): Promise<void> {
  if (polling || !document.value || disposed) return
  const ids = boundIds.value
  if (!ids.length) return
  const currentGeneration = generation
  polling = true
  try { const incoming = await realtimeApi.get(ids); if (generation === currentGeneration && !disposed) { for (const value of incoming) values[value.tagId] = value; lastSnapshot = Date.now() } }
  catch { /* Keep last timestamp; the canvas marks stale values as unknown. */ }
  finally { polling = false }
}
async function load(): Promise<void> {
  const currentGeneration = ++generation
  error.value = ''; detail.value = null
  for (const key of Object.keys(values)) delete values[Number(key)]
  if (props.previewDocument && !previewTargetId.value) { await poll(); await subscribe(); return }
  const screenId = previewTargetId.value ?? props.screenId
  if (!screenId) return
  loading.value = true
  try { const incoming = await screenApi.get(screenId, isPreview.value); if (generation === currentGeneration && !disposed) { detail.value = incoming; await poll(); await subscribe() } }
  catch (e) { if (generation === currentGeneration) error.value = errorMessage(e, '画面读取失败') }
  finally { if (generation === currentGeneration) loading.value = false }
}
async function activate(node: ScreenNode): Promise<void> {
  if (isPreview.value && node.action.type === 'command') { ElMessage.info('预览模式不执行控制，请发布后在运行画面验证'); return }
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
    if (props.depth >= 3) { ElMessage.warning('弹窗最多嵌套三层'); return }
    popupId.value = node.action.screenId; popupVisible.value = true
  }
  if (node.action.type !== 'command' || busy.value || !detail.value) return
  if (!hasAny(auth.user, 'Control')) { ElMessage.warning('当前用户没有控制权限'); return }
  busy.value = true
  let requestId: string | undefined
  try {
    // 与实时监视同一控制闭环：服务端预检查 → 确认 → （需要时）双人复核 → 下发回读与反馈。
    if (!await confirmDialog.value?.open(node.action.tagId!, node.action.value, `${node.text || node.id}`)) return
    requestId = createCommandId()
    const result = await screenApi.command(detail.value.id, node.id, detail.value.publishedRevision!, requestId, true)
    receiptId.value = result.commandId ?? null
    if (result.pending) ElMessage.warning('已提交双人复核，等待另一位用户批准')
    else if (result.success) ElMessage.success(`执行成功，回读 ${result.readBack}`)
    else ElMessage.error(result.message)
    await poll()
  } catch (e) { if (e !== 'cancel' && e !== 'close') { receiptId.value = requestId ?? null; ElMessage.error(errorMessage(e, '控制失败')) } }
  finally { busy.value = false }
}
async function previewHome(): Promise<void> { previewTargetId.value = undefined; await load() }
watch(() => props.screenId, () => { popupVisible.value = false; void load() })
onMounted(async () => {
  await library.ensure()
  await load(); if (disposed) return
  await startHub()
  if (disposed) return
  hub?.onreconnecting(() => { connected.value = false })
  hub?.onreconnected(async () => { connected.value = true; await subscribe(); await poll() })
  hub?.onclose(() => { connected.value = false; if (!disposed) retry = setTimeout(() => void startHub(), 5000) })
  timer = setInterval(() => { if (!connected.value || Date.now() - lastSnapshot >= 5000) void poll() }, 1000)
})
onBeforeUnmount(() => { resizeObserver?.disconnect(); disposed = true; generation++; clearInterval(timer); clearTimeout(retry); void hub?.stop() })
</script>

<style scoped>
.runtime-toolbar { display: flex; align-items: center; gap: 12px; padding: 12px 0; }
.spacer { flex: 1; }
.runtime-stage { overflow: auto; max-height: 75vh; border-radius: 8px; border: 1px solid #294260; }
.runtime-stage.fill { max-height: none; flex: 1; min-height: 0; }
.runtime { display: flex; flex-direction: column; min-height: 0; height: 100%; }
.runtime-toolbar.compact { padding: 4px 0; gap: 6px; font-size: 12px; }
</style>
