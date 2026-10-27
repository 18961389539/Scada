<template>
  <div class="console" data-test="console">
    <header class="console-bar">
      <strong>{{ layout?.name ?? $t('consoleView.k0dd591') }}</strong>
      <el-tag v-if="layout" :disable-transitions="true" size="small" type="info" data-test="console-version">V{{ layout.revision }}</el-tag>
      <el-select v-if="layouts.length" :model-value="layout?.id" :aria-label="$t('consoleView.k0dd591')" size="small" class="layout-select" data-test="console-layout" @change="router.push(`/console/${$event}?monitor=0`)">
        <el-option v-for="l in layouts" :key="l.id" :value="l.id" :label="l.name" />
      </el-select>
      <el-radio-group v-if="layout && layout.document.monitors.length > 1" :model-value="monitorIndex" size="small" data-test="console-monitor" @change="router.replace({ query: { monitor: $event } })">
        <el-radio-button v-for="(m, i) in layout.document.monitors" :key="i" :value="i">{{ m.name || $t('consoleView.k47eff4', [i + 1]) }}</el-radio-button>
      </el-radio-group>
      <el-tag v-if="station" size="small" :type="station.allowControl ? 'success' : 'warning'" data-test="console-station">{{ $t('consoleView.k327883', [station.name, station.allowControl ? '' : $t('consoleView.k44b8e4')]) }}</el-tag>
      <el-select v-if="screenRegions.length > 1" v-model="activeRegionId" :aria-label="$t('layoutMonitor.targetRegion')" size="small" class="layout-select" data-test="console-target-region">
        <el-option v-for="region in screenRegions" :key="region.id" :value="region.id" :label="region.title || region.id" />
      </el-select>
      <span class="spacer" />
      <el-button size="small" :loading="loading" data-test="console-refresh" @click="layout ? checkVersion() : load(); refreshAlarms()">{{ $t('layoutsView.k38108e') }}</el-button>
      <el-button v-if="layout && layout.document.monitors.length > 1" size="small" data-test="console-open-all" @click="prepareWindows()">{{ $t('consoleView.k6f3df7') }}</el-button>
      <el-button size="small" @click="toggleFullscreen()">{{ $t('consoleView.k93c44f') }}</el-button>
      <el-button size="small" @click="router.push('/overview')">{{ $t('consoleView.k11d024') }}</el-button>
    </header>
    <el-alert v-if="screensError" :title="screensError" type="warning" :closable="false" show-icon data-test="console-screens-error" />
    <el-alert v-if="summaryError || summaryStale" :title="summaryError || $t('layoutMonitor.summaryStale')" type="warning" :closable="false" show-icon data-test="console-summary-error" />
    <el-alert v-if="versionError" :title="versionError" type="warning" :closable="false" show-icon data-test="console-version-error" />
    <div v-if="pendingLayout" class="update-notice" data-test="console-update">
      <span>{{ $t('layoutMonitor.updateAvailable', [pendingLayout.revision]) }}</span>
      <el-button size="small" type="primary" @click="applyUpdate">{{ $t('layoutMonitor.applyUpdate') }}</el-button>
    </div>
    <div v-if="layout" class="console-freshness" data-test="console-freshness">{{ $t('layoutMonitor.updatedAt', [alarmListAt ? formatTime(alarmListAt) : '--', summaryAt ? formatTime(summaryAt) : '--']) }}</div>
    <el-alert v-if="ackError" :title="ackError" type="error" :closable="false" show-icon data-test="console-ack-error" />
    <el-alert v-if="clientError" :title="clientError" type="warning" :closable="false" show-icon data-test="console-client-error" />
    <el-empty v-if="!loading && !layout" data-test="console-layout-error" :description="layoutError || $t('consoleView.kc4e153')" :image-size="64" />
    <p v-else-if="layoutError" class="layout-error" data-test="console-layout-error">{{ layoutError }}</p>
    <main v-if="monitor" class="grid" :style="{ gridTemplateColumns: `repeat(${monitor.columns}, 1fr)`, gridTemplateRows: `repeat(${monitor.rows}, 1fr)` }">
      <section v-for="r in monitor.regions" :key="`${layout?.id}-${layout?.revision}-${monitorIndex}-${r.id}`" class="region" :class="{ 'is-active': r.kind === 'screen' && activeRegionId === r.id }" :style="{ gridColumn: `${r.col} / span ${r.colSpan}`, gridRow: `${r.row} / span ${r.rowSpan}` }" @pointerdown.capture="r.kind === 'screen' && (activeRegionId = r.id)" @focusin="r.kind === 'screen' && (activeRegionId = r.id)">
        <RegionScreen v-if="r.kind === 'screen' && r.screenId" :ref="el => setRegionRef(r.id, el)" :region="r" :screen-name="screenName" @popup="openFaceplate" @detach="id => detach(id)" />
        <div v-else-if="r.kind === 'alarmBanner'" class="banner" :class="!alarmsUnavailable && topAlarm ? `lvl-${topAlarm.level}` : ''" data-test="alarm-banner">
          <template v-if="!alarmsUnavailable && topAlarm"><strong data-test="alarm-banner-level">{{ levelLabel(topAlarm.level) }}</strong> {{ topAlarm.message }} <span class="muted">{{ formatTime(topAlarm.startTs) }}</span></template>
          <span v-else-if="alarmsUnavailable" data-test="console-alarm-error">{{ alarmError || $t('layoutMonitor.alarmsStale') }}</span>
          <span v-else>{{ $t('consoleView.k9d0a39') }}</span>
          <span class="spacer" /><span data-test="alarm-banner-count">{{ $t('consoleView.kded3fc', [unackShown, activeShown]) }}</span>
          <span v-if="truncated" data-test="alarm-truncated">{{ $t('consoleView.truncated', [ALARM_LIMIT]) }}</span>
        </div>
        <div v-else-if="r.kind === 'alarmList'" class="alarm-list" data-test="alarm-list">
          <div class="region-head">{{ r.title || $t('consoleView.k8f7167') }}</div>
          <p v-if="alarmsUnavailable" class="alarm-error" data-test="console-alarm-error">{{ alarmError || $t('layoutMonitor.alarmsStale') }}</p>
          <p v-if="truncated" class="alarm-error" data-test="alarm-truncated">{{ $t('consoleView.truncated', [ALARM_LIMIT]) }}</p>
          <div v-for="a in alarms" :key="a.id" class="alarm-row" :class="`lvl-${a.level}`">
            <span class="tiny">{{ formatTime(a.startTs) }}</span> {{ a.message }}
            <el-button v-if="!a.ackTs && canAckAlarm(a)" :disabled="alarmsUnavailable" size="small" link @click="ack(a.id)">{{ $t('consoleView.kb56d9a') }}</el-button><span v-else-if="a.ackTs" class="tiny muted">{{ $t('consoleView.kd9fea6') }}</span>
          </div>
          <p v-if="!alarms.length && !alarmsUnavailable" class="muted tiny">{{ $t('consoleView.k9d0a39') }}</p>
        </div>
        <div v-else-if="r.kind === 'navigation'" class="nav-list" data-test="nav-list">
          <div class="region-head">{{ r.title || $t('consoleView.k6be0ed') }}</div>
          <button v-for="s in screens" :key="s.id" :data-test="`nav-${s.id}`" @click="navigateMain(s.id)">{{ s.name }}</button>
        </div>
      </section>
    </main>
    <!-- 浮动面板窗口：可同时打开多个、拖动、置顶、移到新窗口 -->
    <div v-for="(f, i) in faceplates" :key="f.key" :ref="element => setFaceplateRef(f.key, element)" class="faceplate" :style="{ left: `${f.x}px`, top: `${f.y}px`, zIndex: 100 + f.z }" :data-faceplate-key="f.key" data-test="faceplate" @pointerdown="raise(f)">
      <div class="faceplate-head" @pointerdown="startMove($event, f)">
        <span>{{ screenName(f.screenId) }}</span><span class="spacer" />
        <el-button size="small" link @click.stop="detach(f.screenId, f.key)">{{ $t('consoleView.kad9ce6') }}</el-button>
        <el-button size="small" link :icon="Close" :aria-label="$t('layoutMonitor.closePanel')" data-test="faceplate-close" @click.stop="faceplates.splice(i, 1)" />
      </div>
      <div class="faceplate-body"><ScreenRuntime :screen-id="f.screenId" navigate-mode="emit" compact fill initial-zoom="fit" @navigate="navigateMain" @popup="openFaceplate" /></div>
    </div>
    <el-dialog v-model="mappingVisible" :title="$t('layoutMonitor.monitorMapping')" class="monitor-dialog" data-test="console-mapping-dialog">
      <el-alert v-if="monitors.length < (layout?.document.monitors.length ?? 0) || !windowManagementSupported()" :title="$t('layoutMonitor.monitorFallback')" type="warning" :closable="false" show-icon />
      <el-alert v-if="mappingDuplicates" :title="$t('layoutMonitor.monitorDuplicates')" type="warning" :closable="false" show-icon />
      <el-alert v-if="windowError" :title="windowError" type="error" :closable="false" show-icon />
      <el-form label-position="top">
        <el-form-item v-for="(logical, index) in layout?.document.monitors ?? []" :key="index" :label="logical.name || $t('consoleView.k47eff4', [index + 1])">
          <span v-if="index === monitorIndex">{{ $t('layoutMonitor.currentWindow') }}<template v-if="currentPhysical >= 0"> ({{ monitors[currentPhysical]?.label }})</template></span>
          <el-select v-else v-model="monitorMappings[index]" :aria-label="logical.name || String(index + 1)" :data-test="`console-monitor-map-${index}`">
            <el-option :value="-1" :label="$t('layoutMonitor.regularWindow')" />
            <el-option v-for="(physical, physicalIndex) in monitors" :key="physicalIndex" :value="physicalIndex" :label="`${physical.label} (${physical.width}×${physical.height})`" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer><el-button @click="mappingVisible = false">{{ $t('consoleView.k4d0b46') }}</el-button><el-button type="primary" data-test="console-open-mapped" @click="openMappedWindows">{{ $t('consoleView.k65fc81') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="monitorPick" :title="$t('consoleView.k1782d1')" class="monitor-dialog">
      <p class="tiny muted">{{ windowManagementSupported() ? $t('consoleView.k2f5ceb') : $t('consoleView.k226c52') }}</p>
      <el-alert v-if="windowError" :title="windowError" type="warning" :closable="false" show-icon data-test="console-window-error" />
      <el-radio-group v-model="pickIndex" style="display: flex; flex-direction: column; align-items: flex-start">
        <el-radio v-for="(m, i) in monitors" :key="i" :value="i">{{ m.label }}（{{ m.width }}×{{ m.height }}{{ m.isPrimary ? $t('consoleView.k0f9378') : '' }}）</el-radio>
      </el-radio-group>
      <template #footer><el-button @click="monitorPick = false">{{ $t('consoleView.k4d0b46') }}</el-button><el-button type="primary" data-test="monitor-open" @click="confirmDetach()">{{ $t('consoleView.k65fc81') }}</el-button></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch, type ComponentPublicInstance } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessageBox } from 'element-plus'
import { Close } from '@element-plus/icons-vue'
import RegionScreen from '../components/console/RegionScreen.vue'
import ScreenRuntime from '../components/ScreenRuntime.vue'
import { hmiLayoutApi, WORKSTATION_KEY, type HmiLayout, type HmiWorkstation } from '../api/hmiLayouts'
import { alarmApi, deviceApi } from '../api'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { listMonitors, openOnMonitor, windowManagementSupported, type MonitorInfo } from '../components/console/windowing'
import { usePolling } from '../composables/usePolling'
import { useAuthStore } from '../stores/auth'
import { canAckAlarmEvent, formatTime, levelLabel } from '../utils'
import type { AlarmEvent, AlarmSummary } from '../types'
import type { ScreenSummary } from '../screens/model'
import { tr } from '../i18n'

const route = useRoute(), router = useRouter(), auth = useAuthStore()
const layouts = ref<HmiLayout[]>([]), layout = ref<HmiLayout | null>(null), station = ref<HmiWorkstation | null>(null), loading = ref(true), layoutError = ref('')
const screens = ref<ScreenSummary[]>([])
const screensError = ref(''), versionError = ref(''), pendingLayout = ref<HmiLayout | null>(null)
let loadSequence = 0, disposed = false
const monitorIndex = computed(() => Math.max(0, Math.min(Number(route.query.monitor ?? 0) || 0, (layout.value?.document.monitors.length ?? 1) - 1)))
const monitor = computed(() => layout.value?.document.monitors[monitorIndex.value] ?? null)
const screenRegions = computed(() => monitor.value?.regions.filter(region => region.kind === 'screen' && region.screenId) ?? [])
const activeRegionId = ref('')
watch(monitor, () => { activeRegionId.value = screenRegions.value[0]?.id ?? '' })
const screenName = (id: number) => screens.value.find(s => s.id === id)?.name ?? tr('consoleView.k570a22', [id])
const deviceAreas = ref<Map<number, number>>(new Map())
function canAckAlarm(alarm: AlarmEvent): boolean {
  return canAckAlarmEvent(auth.user, alarm, deviceAreas.value)
}

async function load(): Promise<void> {
  const sequence = ++loadSequence
  loading.value = true
  layout.value = null; station.value = null; pendingLayout.value = null; versionError.value = ''; clientError.value = ''
  try {
    const [layoutResult, screenResult] = await Promise.allSettled([hmiLayoutApi.list(), screenApi.list()])
    if (disposed || sequence !== loadSequence) return
    if (screenResult.status === 'fulfilled') { screens.value = screenResult.value; screensError.value = '' }
    else screensError.value = errorMessage(screenResult.reason, tr('layoutMonitor.screensFailed'))
    if (layoutResult.status === 'rejected') throw layoutResult.reason
    layouts.value = layoutResult.value
    void deviceApi.list().then(list => {
      const map = new Map<number, number>()
      for (const d of list) if (d.areaId != null) map.set(d.id, d.areaId)
      deviceAreas.value = map
    }).catch(() => {})
    const name = localStorage.getItem(WORKSTATION_KEY)
    station.value = null
    layout.value = null
    if (name) {
      try {
        const resolved = await hmiLayoutApi.resolve(name)
        station.value = resolved.workstation
        if (!route.params.layoutId) {
          if (!resolved.layout) throw new Error(tr('layoutMonitor.unassigned'))
          layout.value = resolved.layout
        }
      } catch (e) {
        if (!route.params.layoutId) throw e
        clientError.value = errorMessage(e, tr('layoutMonitor.stationFailed'))
      }
    }
    if (route.params.layoutId) {
      const id = Number(route.params.layoutId)
      layout.value = layouts.value.find(item => item.id === id) ?? null
      if (!layout.value) throw new Error(tr('layoutMonitor.notFound'))
    } else if (!name) layout.value = layouts.value[0] ?? null
    layoutError.value = ''
  } catch (e) {
    layout.value = null
    layoutError.value = errorMessage(e, tr('consoleView.k9f7b8c'))
  } finally { if (sequence === loadSequence) loading.value = false }
}
watch(() => route.params.layoutId, () => void load())
async function checkVersion(): Promise<void> {
  const current = layout.value
  if (!current || loading.value) return
  try {
    const list = await hmiLayoutApi.list()
    if (disposed || layout.value !== current) return
    layouts.value = list
    const latest = list.find(item => item.id === current.id)
    pendingLayout.value = latest && latest.revision !== current.revision ? latest : null
    versionError.value = latest ? '' : tr('layoutMonitor.notFound')
  } catch (e) { if (!disposed && layout.value === current) versionError.value = errorMessage(e, tr('layoutMonitor.versionFailed')) }
}
async function applyUpdate(): Promise<void> {
  try { await ElMessageBox.confirm(tr('layoutMonitor.applyConfirm'), tr('layoutMonitor.applyUpdate'), { type: 'warning' }) } catch { return }
  faceplates.value = []
  await load()
}

// 报警横幅与列表：轮询活动报警（3 秒）。
const ALARM_LIMIT = 200
const alarms = ref<AlarmEvent[]>([])
const summary = ref<AlarmSummary | null>(null)
const truncated = ref(false)
const alarmError = ref('')
const summaryError = ref(''), alarmListAt = ref(0), summaryAt = ref(0), now = ref(Date.now())
let alarmsRefreshing = false
const alarmsUnavailable = computed(() => !!alarmError.value || !alarmListAt.value || now.value - alarmListAt.value > 10000)
const summaryStale = computed(() => !summaryAt.value || now.value - summaryAt.value > 10000)
const ackError = ref('')
const clientError = ref('')
const windowError = ref('')
const unacked = computed(() => alarms.value.filter(a => !a.ackTs))
const levelRank: Record<string, number> = { Critical: 0, Warning: 1, Info: 2 }
const ranked = (rows: AlarmEvent[]) => [...rows].sort((a, b) => (levelRank[a.level] ?? 9) - (levelRank[b.level] ?? 9) || b.startTs - a.startTs)
const topAlarm = computed(() => ranked(unacked.value.length ? unacked.value : alarms.value)[0] ?? null)
const unackShown = computed(() => !summaryError.value && !summaryStale.value && summary.value ? summary.value.unack : '--')
const activeShown = computed(() => !summaryError.value && !summaryStale.value && summary.value ? summary.value.unack + summary.value.acked : '--')
async function refreshAlarms(): Promise<void> {
  if (alarmsRefreshing || disposed) return
  alarmsRefreshing = true
  try {
    const [list, sum] = await Promise.allSettled([
      alarmApi.query({ activeOnly: true, live: true, priorityFirst: true, limit: ALARM_LIMIT + 1 }),
      alarmApi.summary()
    ])
    if (disposed) return
    now.value = Date.now()
    if (list.status === 'fulfilled') {
      truncated.value = list.value.length > ALARM_LIMIT
      alarms.value = ranked(truncated.value ? list.value.slice(0, ALARM_LIMIT) : list.value)
      alarmListAt.value = now.value; alarmError.value = ''
    } else alarmError.value = errorMessage(list.reason, tr('consoleView.alarmError'))
    if (sum.status === 'fulfilled') { summary.value = sum.value; summaryAt.value = now.value; summaryError.value = '' }
    else summaryError.value = errorMessage(sum.reason, tr('layoutMonitor.summaryFailed'))
  } finally { alarmsRefreshing = false }
}
async function ack(id: number): Promise<void> {
  ackError.value = ''
  try { await alarmApi.ack(id); await refreshAlarms() } catch (e) { ackError.value = errorMessage(e, tr('consoleView.kc490c1')) }
}

// 区域导航：画面目录点选时在当前选中的画面区域中打开。
const regionRefs = new Map<string, { navigate: (id: number) => void }>()
function setRegionRef(id: string, el: Element | ComponentPublicInstance | null): void { if (el) regionRefs.set(id, el as unknown as { navigate: (id: number) => void }); else regionRefs.delete(id) }
watch([monitor, () => route.query.runtimeRegion, () => route.query.runtimeScreen], async () => {
  const regionId = route.query.runtimeRegion, rawId = route.query.runtimeScreen, activeMonitor = monitor.value
  if (typeof regionId !== 'string' || typeof rawId !== 'string' || !/^[1-9]\d*$/.test(rawId)) return
  const screenId = Number(rawId)
  if (!activeMonitor?.regions.some(r => r.id === regionId && r.kind === 'screen') || !screens.value.some(s => s.id === screenId)) return
  await nextTick()
  if (monitor.value === activeMonitor) regionRefs.get(regionId)?.navigate(screenId)
}, { flush: 'post' })
function navigateMain(id: number): void {
  const region = screenRegions.value.find(item => item.id === activeRegionId.value) ?? screenRegions.value[0]
  const target = region ? regionRefs.get(region.id) : null
  if (target) target.navigate(id); else openFaceplate(id)
}

// 浮动面板
interface Faceplate { key: number; screenId: number; x: number; y: number; z: number }
const faceplates = ref<Faceplate[]>([]); let seq = 0, topZ = 0
const faceplateElements = new Map<number, HTMLElement>()
const faceplateObserver = new ResizeObserver(constrainFaceplates)
function setFaceplateRef(key: number, element: Element | ComponentPublicInstance | null): void {
  const previous = faceplateElements.get(key)
  if (previous) faceplateObserver.unobserve(previous)
  faceplateElements.delete(key)
  if (element instanceof HTMLElement) { faceplateElements.set(key, element); faceplateObserver.observe(element) }
}
function openFaceplate(screenId: number): void {
  if (faceplates.value.length >= 6) { clientError.value = tr('consoleView.k994124'); return }
  clientError.value = ''
  faceplates.value.push({ key: ++seq, screenId, x: 120 + (seq % 5) * 30, y: 90 + (seq % 5) * 30, z: ++topZ })
  void nextTick(constrainFaceplates)
}
function raise(f: Faceplate): void { f.z = ++topZ }
function constrainFaceplates(): void {
  for (const element of faceplateElements.values()) {
    const panel = faceplates.value.find(item => item.key === Number(element.dataset.faceplateKey))
    if (!panel) continue
    panel.x = Math.max(8, Math.min(panel.x, window.innerWidth - element.offsetWidth - 8))
    panel.y = Math.max(8, Math.min(panel.y, window.innerHeight - element.offsetHeight - 8))
  }
}
let moving: { f: Faceplate; dx: number; dy: number; width: number; height: number } | null = null
function startMove(ev: PointerEvent, f: Faceplate): void {
  if ((ev.target as Element).closest('button')) return
  const element = (ev.currentTarget as Element).closest('.faceplate')!
  const bounds = element.getBoundingClientRect()
  moving = { f, dx: ev.clientX - f.x, dy: ev.clientY - f.y, width: bounds.width, height: bounds.height }
  ev.preventDefault()
}
function onMove(ev: PointerEvent): void {
  if (!moving) return
  moving.f.x = Math.max(8, Math.min(window.innerWidth - moving.width - 8, ev.clientX - moving.dx))
  moving.f.y = Math.max(8, Math.min(window.innerHeight - moving.height - 8, ev.clientY - moving.dy))
}
function onUp(): void { moving = null }

// 新窗口/多显示器
const monitors = ref<MonitorInfo[]>([]), monitorPick = ref(false), pickIndex = ref(0), pendingScreen = ref<number | null>(null), pendingFaceplate = ref<number | null>(null)
watch(monitorPick, open => { if (!open) windowError.value = '' })
async function detach(screenId: number, faceplateKey?: number): Promise<void> {
  windowError.value = ''
  monitors.value = await listMonitors()
  pendingScreen.value = screenId
  pendingFaceplate.value = faceplateKey ?? null
  pickIndex.value = Math.min(1, monitors.value.length - 1)
  monitorPick.value = true
}
function confirmDetach(): void {
  if (pendingScreen.value == null) return
  if (!openOnMonitor(`/kiosk/screens/${pendingScreen.value}`, `scada-screen-${pendingScreen.value}`, monitors.value[pickIndex.value])) {
    windowError.value = tr('consoleView.kee31b7')
    return
  }
  if (pendingFaceplate.value != null) faceplates.value = faceplates.value.filter(f => f.key !== pendingFaceplate.value)
  pendingFaceplate.value = null
  monitorPick.value = false
}
const mappingVisible = ref(false), monitorMappings = ref<number[]>([]), currentPhysical = ref(-1)
let mappedLayout = ''
const mappingDuplicates = computed(() => {
  const targets = monitorMappings.value.filter(target => target >= 0)
  return new Set(targets).size < targets.length
})
async function prepareWindows(): Promise<void> {
  if (!layout.value) return
  const current = layout.value
  monitors.value = await listMonitors()
  if (layout.value !== current || disposed) return
  const centerX = window.screenX + window.outerWidth / 2, centerY = window.screenY + window.outerHeight / 2
  currentPhysical.value = monitors.value.findIndex(physical => centerX >= physical.left && centerX < physical.left + physical.width && centerY >= physical.top && centerY < physical.top + physical.height)
  monitorMappings.value = current.document.monitors.map((_, index) => index === monitorIndex.value ? currentPhysical.value : -1)
  mappedLayout = `${current.id}:${current.revision}`
  windowError.value = ''
  mappingVisible.value = true
}
function openMappedWindows(): void {
  if (!layout.value || mappedLayout !== `${layout.value.id}:${layout.value.revision}`) return
  let blocked = 0
  layout.value.document.monitors.forEach((_, i) => {
    if (i === monitorIndex.value) return
    const target = monitorMappings.value[i] ?? -1
    if (!openOnMonitor(`/console/${layout.value!.id}?monitor=${i}`, `scada-console-${layout.value!.id}-${i}`, target >= 0 ? monitors.value[target] : undefined)) blocked++
  })
  if (blocked > 0) { windowError.value = tr('consoleView.kee31b7'); clientError.value = windowError.value; return }
  mappingVisible.value = false
  clientError.value = ''
}
function toggleFullscreen(): void { if (document.fullscreenElement) void document.exitFullscreen(); else void document.documentElement.requestFullscreen?.() }

// 操作台报警条 3s 刷新；窗口被最小化/切走时暂停，回到前台补一次。
usePolling(refreshAlarms, 3000)
usePolling(checkVersion, 10000)
usePolling(() => { now.value = Date.now() }, 1000)
onMounted(async () => { await load(); await refreshAlarms(); if (disposed) return; window.addEventListener('pointermove', onMove); window.addEventListener('pointerup', onUp); window.addEventListener('resize', constrainFaceplates) })
onBeforeUnmount(() => { disposed = true; faceplateObserver.disconnect(); window.removeEventListener('pointermove', onMove); window.removeEventListener('pointerup', onUp); window.removeEventListener('resize', constrainFaceplates) })
</script>

<style scoped>
.console { height: 100vh; display: flex; flex-direction: column; background: #0d1622; color: #dce6f0; overflow: hidden; }
.console-bar { display: flex; align-items: center; gap: 10px; padding: 6px 10px; background: #101b2d; border-bottom: 1px solid #22344d; flex-wrap: wrap; }
.spacer { flex: 1; }
.layout-select { width: 180px; }
.update-notice, .console-freshness { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; padding: 4px 10px; font-size: 12px; }
.update-notice { background: #5c3b10; }
.console-freshness { color: #b6c4d5; }
.region.is-active { outline: 2px solid #61ddaa; outline-offset: -2px; }
.grid { flex: 1; display: grid; gap: 4px; padding: 4px; min-height: 0; }
.region { background: #101b2d; border: 1px solid #22344d; border-radius: 4px; overflow: hidden; min-height: 0; min-width: 0; }
.banner { height: 100%; display: flex; align-items: center; gap: 10px; padding: 0 12px; font-size: 14px; background: #1a2533; }
.banner.lvl-Critical { background: #7a1010; } .banner.lvl-Warning { background: #8a4b00; } .banner.lvl-Info { background: #1a3a5c; }
.region-head { font-weight: 600; padding: 4px 8px; background: #1b2838; font-size: 12px; }
.alarm-list, .nav-list { height: 100%; overflow: auto; font-size: 12px; }
.alarm-row { padding: 4px 8px; border-bottom: 1px solid #1f2d3f; } .alarm-row.lvl-Critical { border-left: 4px solid #e03131; } .alarm-row.lvl-Warning { border-left: 4px solid #f08c00; } .alarm-row.lvl-Info { border-left: 4px solid #4dabf7; }
.alarm-error { margin: 0; padding: 4px 8px; color: #ffc9c9; background: #5c1a1a; }
.layout-error { margin: 8px; padding: 8px 12px; color: #ffc9c9; background: #5c1a1a; border-radius: 4px; }
.nav-list button { display: block; width: 100%; text-align: left; padding: 8px; border: 0; border-bottom: 1px solid #1f2d3f; background: transparent; color: #dce6f0; cursor: pointer; }
.nav-list button:hover { background: #1b2838; }
.faceplate { position: fixed; width: 560px; height: 420px; max-width: calc(100vw - 16px); max-height: calc(100dvh - 16px); background: #101b2d; border: 1px solid #3b5878; border-radius: 6px; box-shadow: 0 8px 30px #000a; display: flex; flex-direction: column; resize: both; overflow: hidden; }
.faceplate-head { display: flex; align-items: center; gap: 6px; padding: 4px 8px; background: #22344d; cursor: move; font-size: 12px; user-select: none; touch-action: none; }
.faceplate-head > span:first-child { min-width: 0; overflow-wrap: anywhere; }
.faceplate-head .el-button { flex-shrink: 0; color: #dce6f0; }
.faceplate-body { flex: 1; min-height: 0; padding: 4px; }
.muted { color: #8796a8; }
.console :deep(.el-empty) { margin-top: 14vh; }
.console :deep(.el-empty__description p) { color: #8796a8; }
.console :deep(.el-empty__image) { opacity: 0.45; }
:global(.monitor-dialog) { width: min(460px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); overflow: auto; }
@media (max-width: 760px) {
  .console { height: 100dvh; }
  .console-bar { gap: 6px; }
  .console-bar > strong { min-width: 0; overflow-wrap: anywhere; }
  .layout-select { width: min(180px, 100%); }
  .grid { display: flex; flex-direction: column; overflow: auto; }
  .region { flex-shrink: 0; }
  .region:has(.region-screen) { height: 65dvh; }
  .banner { flex-wrap: wrap; height: auto; min-height: 48px; padding: 6px 8px; }
  .nav-list { max-height: 180px; }
  .alarm-list { max-height: 40dvh; }
  .faceplate { resize: none; }
}
</style>
