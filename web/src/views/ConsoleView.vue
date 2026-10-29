<template>
  <div class="console" role="main" data-test="console" :inert="!!previewLayout">
    <div class="console-chrome">
    <div class="console-controls">
    <header class="console-bar">
      <h1 class="console-title" :title="layout?.name">{{ layout?.name ?? $t('consoleView.k0dd591') }}</h1>
      <el-tag v-if="layout" :disable-transitions="true" size="small" type="info" data-test="console-version">{{ previewLayout ? $t('screenRuntime.kf0ab76') : `V${layout.revision}` }}</el-tag>
      <el-select v-if="layouts.length" :model-value="layout?.id" :aria-label="$t('consoleView.k0dd591')" size="small" class="layout-select" data-test="console-layout" @change="router.push(`/console/${$event}?monitor=0`)">
        <el-option v-for="l in layouts" :key="l.id" :value="l.id" :label="l.name" />
      </el-select>
      <el-radio-group v-if="layout && layout.document.monitors.length > 1" :model-value="monitorIndex" size="small" data-test="console-monitor" @change="router.replace({ query: { monitor: $event } })">
        <el-radio-button v-for="(m, i) in layout.document.monitors" :key="i" :value="i" :title="m.name">{{ m.name || $t('consoleView.k47eff4', [i + 1]) }}</el-radio-button>
      </el-radio-group>
      <el-tag v-if="station" size="small" :type="station.allowControl ? 'success' : 'warning'" data-test="console-station">{{ $t('consoleView.k327883', [station.name, station.allowControl ? '' : $t('consoleView.k44b8e4')]) }}</el-tag>
      <label v-if="screenRegions.length" class="target-region"><span>{{ $t('consoleImprovements.openInto') }}</span>
      <el-select v-model="activeRegionId" :aria-label="$t('layoutMonitor.targetRegion')" size="small" class="layout-select" data-test="console-target-region">
        <el-option v-for="region in screenRegions" :key="region.id" :value="region.id" :label="region.title || region.id" />
      </el-select></label>
      <span class="spacer" />
      <el-switch v-model="monitorMode" :active-text="$t('consoleImprovements.monitorOnly')" data-test="console-monitor-mode" />
      <el-button size="small" :loading="loading || refreshing" data-test="console-refresh" @click="refreshConsole">{{ $t('layoutsView.k38108e') }}</el-button>
      <el-button v-if="layout && layout.document.monitors.length > 1" size="small" data-test="console-open-all" @click="monitorWindows?.prepare()">{{ $t('consoleView.k6f3df7') }}</el-button>
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
    </div>
        <div class="banner global-banner" :class="!alarmsUnavailable && topAlarm && topAlarm.endTs == null ? `lvl-${topAlarm.level}` : ''" data-test="alarm-banner" role="status">
          <template v-if="!alarmsUnavailable && topAlarm"><strong data-test="alarm-banner-level">{{ levelLabel(topAlarm.level) }}</strong><span data-test="alarm-banner-state">{{ alarmStateLabel(topAlarm) }}</span><span class="alarm-message" :title="topAlarm.message">{{ topAlarm.message }}</span><span class="muted">{{ formatTime(topAlarm.startTs) }}</span></template>
          <span v-else-if="alarmsUnavailable" data-test="console-alarm-error">{{ alarmError || $t('layoutMonitor.alarmsStale') }}</span>
          <span v-else>{{ $t('consoleView.k9d0a39') }}</span>
          <span class="spacer" /><span data-test="alarm-banner-count">{{ $t('consoleView.kded3fc', [unackShown, activeShown]) }}</span>
          <span data-test="alarm-returned-count">{{ $t('layoutMonitor.returnedCount', [returnedShown]) }}</span>
          <span v-if="truncated" data-test="alarm-truncated">{{ $t('consoleView.truncated', [ALARM_LIMIT]) }}</span>
        </div>
    </div>
    <div v-if="monitor" class="console-workspace" data-test="console-workspace">
    <main class="grid" :style="{ gridTemplateColumns: `repeat(${monitor.columns}, 1fr)`, gridTemplateRows: `repeat(${monitor.rows}, 1fr)` }">
      <section v-for="r in monitor.regions" :key="`${layout?.id}-${layout?.revision}-${monitorIndex}-${r.id}`" class="region" :class="{ 'is-active': r.kind === 'screen' && activeRegionId === r.id }" :style="{ gridColumn: `${r.col} / span ${r.colSpan}`, gridRow: `${r.row} / span ${r.rowSpan}` }" @pointerdown.capture="r.kind === 'screen' && (activeRegionId = r.id)" @focusin="r.kind === 'screen' && (activeRegionId = r.id)">
        <RegionScreen v-if="r.kind === 'screen' && r.screenId" :ref="el => setRegionRef(r.id, el)" :region="r" :screen-name="screenName" :monitor-mode="monitorMode" @screen-change="setRegionScreen(r.id, $event)" @popup="openFaceplate" @detach="id => detach(id)" />
        <div v-else-if="r.kind === 'alarmList'" class="alarm-list" data-test="alarm-list">
          <div class="region-head alarm-list-head">
            <strong :id="`alarm-heading-${r.id}`">{{ $t('layoutMonitor.alarmTitle') }}</strong>
            <span v-if="r.title" class="alarm-context" :title="r.title">{{ $t('layoutMonitor.alarmRegion', [r.title]) }}</span>
            <span class="spacer" />
            <span v-if="truncated" class="alarm-limit" data-test="alarm-truncated" role="status" :title="$t('consoleView.truncated', [ALARM_LIMIT])">{{ $t('layoutMonitor.alarmLimit', [ALARM_LIMIT]) }}</span>
            <router-link to="/alarms?state=live" class="alarm-all">{{ $t('layoutMonitor.allAlarms') }}</router-link>
          </div>
          <p v-if="alarmsUnavailable" class="alarm-error" data-test="console-alarm-error">{{ alarmError || $t('layoutMonitor.alarmsStale') }}</p>
          <div class="alarm-table-scroll" role="region" tabindex="0" :aria-label="$t('layoutMonitor.alarmTable')">
            <table v-if="alarms.length" class="alarm-table" :aria-labelledby="`alarm-heading-${r.id}`">
              <colgroup><col class="alarm-level-col" /><col class="alarm-state-col" /><col class="alarm-time-col" /><col /><col class="alarm-action-col" /></colgroup>
              <thead><tr>
                <th scope="col">{{ $t('layoutMonitor.alarmLevel') }}</th><th scope="col">{{ $t('layoutMonitor.alarmState') }}</th>
                <th scope="col">{{ $t('layoutMonitor.alarmStartedAt') }}</th><th scope="col">{{ $t('layoutMonitor.alarmMessage') }}</th><th scope="col">{{ $t('layoutMonitor.alarmAction') }}</th>
              </tr></thead>
              <tbody><tr v-for="a in alarms" :key="a.id" class="alarm-row" :class="`lvl-${a.level}`">
                <td><span class="alarm-level">{{ levelLabel(a.level) }}</span></td>
                <td><span class="alarm-state" :class="{ 'is-returned': a.endTs != null }">{{ alarmStateLabel(a) }}</span></td>
                <td class="alarm-time">{{ formatTime(a.startTs) }}</td>
                <td><span class="alarm-content" :title="a.message">{{ a.message }}</span></td>
                <td class="alarm-action">
                  <el-button v-if="!a.ackTs && canAckAlarm(a)" :disabled="alarmsUnavailable" :aria-label="$t('layoutMonitor.ackAlarm', [a.message])" size="small" link @click="ack(a.id)">{{ $t('consoleView.kb56d9a') }}</el-button>
                  <span v-else-if="a.ackTs" class="muted">{{ $t('consoleView.kd9fea6') }}</span><span v-else class="muted">{{ $t('layoutMonitor.ackUnavailable') }}</span>
                </td>
              </tr></tbody>
            </table>
            <p v-else-if="!alarmsUnavailable" class="alarm-empty">{{ $t('layoutMonitor.emptyAlarms') }}</p>
          </div>
        </div>
        <ScreenDirectory v-else-if="r.kind === 'navigation'" :screens="screens" :active-screen-id="activeScreenId" :storage-key="directoryKey" @navigate="navigateMain" />
      </section>
    </main>
    <FaceplateWorkspace ref="faceplateWorkspace" :screen-name="screenName" :monitor-mode="monitorMode" @detach="detach" @error="clientError = $event" />
    </div>
    <MonitorWindows v-if="layout" ref="monitorWindows" :key="layout.id" :layout="layout" :monitor-index="monitorIndex" :station-key="workstationKey" />
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
import RegionScreen from '../components/console/RegionScreen.vue'
import FaceplateWorkspace from '../components/console/FaceplateWorkspace.vue'
import ScreenDirectory from '../components/console/ScreenDirectory.vue'
import MonitorWindows from '../components/console/MonitorWindows.vue'
import { hmiLayoutApi, getLocalWorkstation, WORKSTATION_ID_KEY, WORKSTATION_KEY, type HmiLayout, type HmiWorkstation } from '../api/hmiLayouts'
import { alarmApi, deviceApi } from '../api'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { listMonitors, openOnMonitor, windowManagementSupported, type MonitorInfo } from '../components/console/windowing'
import { usePolling } from '../composables/usePolling'
import { useNowTicker } from '../composables/useNowTicker'
import { refreshActiveAlarms } from '../composables/useActiveAlarms'
import { useAuthStore } from '../stores/auth'
import { canAckAlarmEvent, formatTime, levelLabel } from '../utils'
import type { AlarmEvent, AlarmSummary } from '../types'
import type { ScreenSummary } from '../screens/model'
import { tr } from '../i18n'
import { contentMonitor } from '../components/console/layoutGeometry'

const props = defineProps<{ previewLayout?: HmiLayout; previewMonitor?: number }>()
const route = useRoute(), router = useRouter(), auth = useAuthStore()
const layouts = ref<HmiLayout[]>([]), layout = ref<HmiLayout | null>(null), station = ref<HmiWorkstation | null>(null), loading = ref(true), layoutError = ref('')
const screens = ref<ScreenSummary[]>([])
const screensError = ref(''), versionError = ref(''), pendingLayout = ref<HmiLayout | null>(null)
const refreshing = ref(false)
let loadSequence = 0, detachSequence = 0, disposed = false
const monitorIndex = computed(() => {
  const requested = Number(props.previewLayout ? props.previewMonitor ?? 0 : route.query.monitor ?? 0)
  const index = Number.isInteger(requested) ? requested : 0
  return Math.max(0, Math.min(index, (layout.value?.document.monitors.length ?? 1) - 1))
})
const monitor = computed(() => {
  const current = layout.value?.document.monitors[monitorIndex.value]
  return current ? contentMonitor(current) : null
})
const screenRegions = computed(() => monitor.value?.regions.filter(region => region.kind === 'screen' && region.screenId) ?? [])
const activeRegionId = ref('')
const regionScreens = ref<Record<string, number>>({})
watch(monitor, () => { activeRegionId.value = screenRegions.value[0]?.id ?? ''; regionScreens.value = {} })
function setRegionScreen(regionId: string, screenId: number): void { regionScreens.value[regionId] = screenId }
const activeScreenId = computed(() => regionScreens.value[activeRegionId.value] ?? screenRegions.value.find(region => region.id === activeRegionId.value)?.screenId ?? undefined)
const workstationKey = computed(() => { const binding = getLocalWorkstation(); return station.value ? `station-${station.value.id}` : binding.id ? `station-${binding.id}` : binding.name ? `legacy-${binding.name}` : 'unassigned' })
const directoryKey = computed(() => `${auth.user?.id ?? auth.user?.userName ?? 'unknown'}:${workstationKey.value}:${layout.value?.id ?? ''}`)
const monitorModeKey = computed(() => `scada-console-monitor-mode:${auth.user?.id ?? auth.user?.userName ?? 'unknown'}`)
const monitorMode = ref(!!props.previewLayout || localStorage.getItem(monitorModeKey.value) === 'true')
watch(monitorModeKey, key => { if (!props.previewLayout) monitorMode.value = localStorage.getItem(key) === 'true' })
watch(monitorMode, value => { if (props.previewLayout) return; try { localStorage.setItem(monitorModeKey.value, String(value)) } catch { /* This preference remains available for the current window. */ } })
const faceplateWorkspace = ref<InstanceType<typeof FaceplateWorkspace>>()
const monitorWindows = ref<InstanceType<typeof MonitorWindows>>()
const screenName = (id: number) => screens.value.find(s => s.id === id)?.name ?? tr('consoleView.k570a22', [id])
const deviceAreas = ref<Map<number, number>>(new Map())
function canAckAlarm(alarm: AlarmEvent): boolean {
  return !props.previewLayout && canAckAlarmEvent(auth.user, alarm, deviceAreas.value)
}

async function load(): Promise<void> {
  const sequence = ++loadSequence
  const requestedLayoutId = route.params.layoutId
  const isCurrent = () => !disposed && sequence === loadSequence
  loading.value = true
  faceplateWorkspace.value?.clear(); monitorPick.value = false; windowError.value = ''; detachSequence++; pendingScreen.value = null; pendingFaceplate.value = null
  layout.value = null; station.value = null; pendingLayout.value = null; versionError.value = ''; clientError.value = ''
  try {
    if (props.previewLayout) {
      layout.value = props.previewLayout
      layouts.value = [props.previewLayout]
      layoutError.value = ''
      try { screens.value = (await screenApi.list()).filter(screen => screen.publishedRevision != null); screensError.value = '' }
      catch (reason) { screensError.value = errorMessage(reason, tr('layoutMonitor.screensFailed')) }
      return
    }
    const [layoutResult, screenResult] = await Promise.allSettled([hmiLayoutApi.list(), screenApi.list()])
    if (!isCurrent()) return
    if (screenResult.status === 'fulfilled') { screens.value = screenResult.value.filter(screen => screen.publishedRevision != null); screensError.value = '' }
    else screensError.value = errorMessage(screenResult.reason, tr('layoutMonitor.screensFailed'))
    if (layoutResult.status === 'rejected') throw layoutResult.reason
    layouts.value = layoutResult.value
    void deviceApi.list().then(list => {
      if (!isCurrent()) return
      const map = new Map<number, number>()
      for (const d of list) if (d.areaId != null) map.set(d.id, d.areaId)
      deviceAreas.value = map
    }).catch(() => {})
    const binding = getLocalWorkstation()
    const hasBinding = localStorage.getItem(WORKSTATION_ID_KEY) != null || !!binding.name
    station.value = null
    layout.value = null
    if (hasBinding) {
      try {
        const resolved = await hmiLayoutApi.resolveLocal()
        if (!isCurrent()) return
        if (!resolved) throw new Error(tr('layoutMonitor.stationFailed'))
        station.value = resolved.workstation
        if (!requestedLayoutId) {
          if (!resolved.layout) throw new Error(tr('layoutMonitor.unassigned'))
          layout.value = resolved.layout
        }
      } catch (e) {
        if (!isCurrent()) return
        if (!requestedLayoutId) throw e
        clientError.value = errorMessage(e, tr('layoutMonitor.stationFailed'))
      }
    }
    if (requestedLayoutId) {
      const id = Number(requestedLayoutId)
      layout.value = layouts.value.find(item => item.id === id) ?? null
      if (!layout.value) throw new Error(tr('layoutMonitor.notFound'))
    } else if (!hasBinding) layout.value = layouts.value[0] ?? null
    layoutError.value = ''
  } catch (e) {
    if (!isCurrent()) return
    layout.value = null
    layoutError.value = errorMessage(e, tr('consoleView.k9f7b8c'))
  } finally { if (sequence === loadSequence) loading.value = false }
}
watch(() => route.params.layoutId, () => void load())
async function refreshConsole(): Promise<void> {
  if (loading.value || refreshing.value) return
  refreshing.value = true
  const sequence = loadSequence
  try {
    if (!layout.value) { await Promise.all([load(), refreshAlarms()]); return }
    await Promise.all([
      checkVersion(), refreshAlarms(),
      screenApi.list().then(list => {
        if (disposed || sequence !== loadSequence) return
        screens.value = list.filter(screen => screen.publishedRevision != null); screensError.value = ''
      }).catch(error => {
        if (!disposed && sequence === loadSequence) screensError.value = errorMessage(error, tr('layoutMonitor.screensFailed'))
      })
    ])
  } finally { refreshing.value = false }
}
async function checkVersion(): Promise<void> {
  if (props.previewLayout) return
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
  faceplateWorkspace.value?.clear()
  await load()
}

// 报警横幅与列表：轮询活动报警（3 秒）。
const ALARM_LIMIT = 200
const alarms = ref<AlarmEvent[]>([])
const summary = ref<AlarmSummary | null>(null)
const truncated = ref(false)
const alarmError = ref('')
const summaryError = ref(''), alarmListAt = ref(0), summaryAt = ref(0)
const { now } = useNowTicker()
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
const unackShown = computed(() => !summaryError.value && !summaryStale.value && summary.value ? summary.value.unack + summary.value.rtnUnack : '--')
const activeShown = computed(() => !summaryError.value && !summaryStale.value && summary.value ? summary.value.unack + summary.value.acked : '--')
const returnedShown = computed(() => !summaryError.value && !summaryStale.value && summary.value ? summary.value.rtnUnack : '--')
function alarmStateLabel(alarm: AlarmEvent): string {
  if (alarm.endTs != null) return tr('layoutMonitor.returnedUnack')
  return alarm.ackTs == null ? tr('layoutMonitor.activeUnack') : tr('layoutMonitor.activeAcked')
}
async function refreshAlarms(): Promise<void> {
  if (alarmsRefreshing || disposed) return
  alarmsRefreshing = true
  try {
    const [list, sum] = await Promise.allSettled([
      // 与各区域画面共用同一份活动报警缓存：N 个区域同时刷新只发一次请求。
      refreshActiveAlarms(2500),
      alarmApi.summary()
    ])
    if (disposed) return
    const at = Date.now()
    if (list.status === 'fulfilled') {
      truncated.value = list.value.length > ALARM_LIMIT
      alarms.value = ranked(truncated.value ? list.value.slice(0, ALARM_LIMIT) : list.value)
      alarmListAt.value = at; alarmError.value = ''
    } else alarmError.value = errorMessage(list.reason, tr('consoleView.alarmError'))
    if (sum.status === 'fulfilled') { summary.value = sum.value; summaryAt.value = at; summaryError.value = '' }
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

function openFaceplate(screenId: number): void { faceplateWorkspace.value?.open(screenId) }

// 新窗口/多显示器
const monitors = ref<MonitorInfo[]>([]), monitorPick = ref(false), pickIndex = ref(0), pendingScreen = ref<number | null>(null), pendingFaceplate = ref<number | null>(null)
watch(monitorPick, open => { if (!open) windowError.value = '' })
async function detach(screenId: number, faceplateKey?: number): Promise<void> {
  const sequence = ++detachSequence, currentLayout = layout.value
  windowError.value = ''
  const available = await listMonitors()
  if (disposed || sequence !== detachSequence || layout.value !== currentLayout) return
  monitors.value = available
  pendingScreen.value = screenId
  pendingFaceplate.value = faceplateKey ?? null
  pickIndex.value = Math.min(1, monitors.value.length - 1)
  monitorPick.value = true
}
function confirmDetach(): void {
  if (pendingScreen.value == null) return
  if (!openOnMonitor(`/kiosk/screens/${pendingScreen.value}${monitorMode.value ? '?monitorOnly=1' : ''}`, `scada-screen-${pendingScreen.value}`, monitors.value[pickIndex.value])) {
    windowError.value = tr('consoleView.kee31b7')
    return
  }
  if (pendingFaceplate.value != null) faceplateWorkspace.value?.close(pendingFaceplate.value)
  pendingFaceplate.value = null
  monitorPick.value = false
}
function toggleFullscreen(): void { if (document.fullscreenElement) void document.exitFullscreen(); else void document.documentElement.requestFullscreen?.() }
function syncPreferences(event: StorageEvent): void {
  if (props.previewLayout) return
  if (event.key === monitorModeKey.value) monitorMode.value = event.newValue === 'true'
  if (event.key === WORKSTATION_ID_KEY || event.key === WORKSTATION_KEY) void load()
}

// 操作台报警条 3s 刷新；窗口被最小化/切走时暂停，回到前台补一次。
usePolling(refreshAlarms, 3000)
usePolling(checkVersion, 10000)

onMounted(async () => { window.addEventListener('storage', syncPreferences); await load(); await refreshAlarms() })
onBeforeUnmount(() => { disposed = true; detachSequence++; window.removeEventListener('storage', syncPreferences) })
</script>

<style scoped>
.console { height: 100vh; display: flex; flex-direction: column; background: #0d1622; color: #dce6f0; overflow: hidden; }
.console-chrome { position: relative; z-index: 2; flex: 0 0 auto; background: #0d1622; }
.console-controls { max-height: 38dvh; overflow: auto; }
.console-workspace { position: relative; flex: 1; min-height: 0; isolation: isolate; overflow: hidden; }
.target-region { display: flex; align-items: center; gap: 6px; font-size: 12px; min-width: 0; flex: 0 0 auto; white-space: nowrap; }
.target-region > span { flex-shrink: 0; }
.console-bar :deep(.el-switch__label) { color: #dce6f0; }
.console-bar { display: flex; align-items: center; gap: 10px; padding: 6px 10px; background: #101b2d; border-bottom: 1px solid #22344d; flex-wrap: wrap; }
.console-bar > .console-title { margin: 0; font-size: 14px; font-weight: 600; min-width: 0; max-width: min(360px, calc(100vw - 20px)); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.console-bar :deep(.el-radio-group) { max-width: 100%; flex-wrap: wrap; }
.console-bar :deep(.el-radio-button__inner) { display: block; max-width: 180px; overflow: hidden; text-overflow: ellipsis; }
.console :deep(.runtime-toolbar > strong) { min-width: 0; max-width: min(280px, 100%); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.spacer { flex: 1; }
.layout-select { width: 180px; }
.update-notice, .console-freshness { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; padding: 4px 10px; font-size: 12px; }
.update-notice { background: #5c3b10; }
.console-freshness { color: #b6c4d5; }
.region.is-active { outline: 2px solid #61ddaa; outline-offset: -2px; }
.grid { height: 100%; display: grid; gap: 4px; padding: 4px; min-height: 0; }
.region { background: #101b2d; border: 1px solid #22344d; border-radius: 4px; overflow: hidden; min-height: 0; min-width: 0; }
.banner { height: 100%; display: flex; align-items: center; align-content: center; flex-wrap: wrap; gap: 4px 10px; padding: 4px 8px; overflow: auto; font-size: 14px; background: #1a2533; }
.global-banner { height: auto; min-height: 48px; max-height: 112px; border-bottom: 1px solid #3b5878; }
.alarm-message { flex: 1 1 160px; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.banner .muted { color: #e6edf5; }
.alarm-state { display: inline-block; margin: 0 6px; font-size: 12px; }
.banner.lvl-Critical { background: #7a1010; } .banner.lvl-Warning { background: #8a4b00; } .banner.lvl-Info { background: #1a3a5c; }
.region-head { font-weight: 600; padding: 4px 8px; background: #1b2838; font-size: 12px; }
.alarm-list { height: 100%; display: flex; flex-direction: column; overflow: hidden; font-size: 12px; }
.alarm-list-head { display: flex; align-items: center; flex-wrap: wrap; flex: 0 0 auto; gap: 4px 12px; min-height: 32px; }
.alarm-context { max-width: 220px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #b6c4d5; font-weight: 400; }
.alarm-limit { color: #b6c4d5; font-weight: 400; }
.alarm-all { flex-shrink: 0; padding: 2px; color: #afd5ff; text-underline-offset: 3px; }
.alarm-table-scroll { flex: 1; min-height: 0; overflow: auto; scrollbar-color: #708298 #152238; }
.alarm-table-scroll:focus-visible { outline: 2px solid #94c9ff; outline-offset: -2px; }
.alarm-table { width: 100%; min-width: 800px; border-collapse: collapse; table-layout: fixed; }
.alarm-level-col { width: 72px; } .alarm-state-col { width: 152px; } .alarm-time-col { width: 164px; } .alarm-action-col { width: 88px; }
.alarm-table th { position: sticky; top: 0; z-index: 1; background: #1b2838; color: #b6c4d5; text-align: left; font-weight: 500; white-space: nowrap; }
.alarm-table th, .alarm-table td { padding: 4px 8px; border-bottom: 1px solid #304159; line-height: 20px; vertical-align: top; }
.alarm-row.lvl-Critical { border-left: 4px solid #e03131; } .alarm-row.lvl-Warning { border-left: 4px solid #f08c00; } .alarm-row.lvl-Info { border-left: 4px solid #4dabf7; }
.alarm-level { display: inline-block; padding: 0 5px; border-radius: 3px; font-weight: 600; }
.lvl-Critical .alarm-level { color: #ffb3b3; background: #55232c; } .lvl-Warning .alarm-level { color: #ffdb8a; background: #4c3919; } .lvl-Info .alarm-level { color: #acd6ff; background: #203e5b; }
.alarm-row .alarm-state { margin: 0; padding: 0 5px; border: 1px solid #52647a; border-radius: 3px; line-height: 18px; }
.alarm-row .alarm-state.is-returned { border-style: dashed; color: #c4cfdd; }
.alarm-time { color: #b6c4d5; font-variant-numeric: tabular-nums; white-space: nowrap; }
.alarm-content { display: block; overflow-wrap: anywhere; }
.alarm-action, .alarm-table th:last-child { position: sticky; right: 0; background: #101b2d; }
.alarm-table th:last-child { z-index: 2; background: #1b2838; }
.alarm-action .muted { color: #b6c4d5; }
.alarm-action :deep(.el-button) { --el-button-text-color: #afd5ff; --el-button-hover-text-color: #e2efff; --el-button-disabled-text-color: #9ca8b8; min-height: 24px; margin: 0; padding: 2px 6px; }
.alarm-all:focus-visible, .alarm-action :deep(.el-button:focus-visible) { outline: 2px solid #94c9ff; outline-offset: 2px; }
.alarm-empty { margin: 0; padding: 12px 8px; color: #b6c4d5; }
.alarm-error { margin: 0; padding: 4px 8px; color: #ffc9c9; background: #5c1a1a; }
.layout-error { margin: 8px; padding: 8px 12px; color: #ffc9c9; background: #5c1a1a; border-radius: 4px; }
.muted { color: #8796a8; }
.console :deep(.el-empty) { margin-top: 14vh; }
.console :deep(.el-empty__description p) { color: #8796a8; }
.console :deep(.el-empty__image) { opacity: 0.45; }
:global(.monitor-dialog) { width: min(460px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); overflow: auto; }
@media (max-width: 760px) {
  .console { height: 100dvh; }
  .console-bar { gap: 6px; }
  .console-bar > .spacer { display: none; }
  .target-region { width: 100%; }
  .target-region .layout-select { flex: 1; min-width: 0; width: auto; }
  .console-bar > .console-title { min-width: 0; overflow-wrap: anywhere; }
  .layout-select { width: min(180px, 100%); }
  .grid { display: flex; flex-direction: column; overflow: auto; }
  .region { flex-shrink: 0; }
  .region:has(.region-screen) { height: 65dvh; }
  .banner { flex-wrap: wrap; height: auto; min-height: 48px; padding: 6px 8px; }
  .region:has(.screen-directory) { height: 240px; }
  .alarm-list { max-height: 40dvh; }
}
</style>
