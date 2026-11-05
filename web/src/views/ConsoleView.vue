<template>
  <div class="console" :class="{ 'is-fullscreen': fullscreen }" role="main" data-test="console" :inert="!!previewLayout">
    <div class="console-chrome">
    <div class="console-controls">
    <header class="console-bar">
      <h1 class="console-title" :title="layout?.name">{{ layout?.name ?? $t('workbench.runtimeTitle') }}</h1>
      <el-tag v-if="layout" :disable-transitions="true" size="small" :class="{ 'version-quiet': !previewLayout }" :type="previewLayout ? 'warning' : undefined" data-test="console-version">{{ previewLayout ? $t('screenRuntime.draftPreviewControlActionsDisabled') : `V${layout.revision}` }}</el-tag>
      <el-radio-group v-if="layout && layout.document.monitors.length > 1" :model-value="monitorIndex" size="small" data-test="console-monitor" @change="router.replace({ query: { monitor: $event } })">
        <el-radio-button v-for="(m, i) in layout.document.monitors" :key="i" :value="i" :title="m.name">{{ m.name || $t('consoleView.k47eff4', [i + 1]) }}</el-radio-button>
      </el-radio-group>
      <el-tag v-if="station" size="small" effect="plain" :class="{ 'station-quiet': station.allowControl }" :type="station.allowControl ? undefined : 'warning'" data-test="console-station">{{ $t('consoleView.k327883', [station.name, station.allowControl ? '' : $t('consoleView.k44b8e4')]) }}</el-tag>
      <span class="spacer" />
      <PageHelpButton v-if="!previewLayout" help-key="console" compact />
      <el-button size="small" data-test="console-more" :aria-expanded="moreOpen" @click="moreOpen = !moreOpen">{{ $t('consoleUx.more') }}</el-button>
    </header>
    <div v-show="moreOpen" class="console-more-panel" data-test="console-more-panel">
      <WorkbenchTooltip v-if="layouts.length" :content="$t('workbenchTips.runtimeLayout')"><el-select :model-value="layout?.id" :aria-label="$t('workbench.runtimeTitle')" size="small" class="layout-select" data-test="console-layout" @change="selectLayout">
        <el-option v-for="l in layouts" :key="l.id" :value="l.id" :label="l.name" />
      </el-select></WorkbenchTooltip>
      <label v-if="screenRegions.length" class="target-region"><span>{{ $t('consoleImprovements.openInto') }}</span>
      <WorkbenchTooltip :content="$t('workbenchTips.targetRegion')"><el-select v-model="activeRegionId" :aria-label="$t('layoutMonitor.targetRegion')" size="small" class="layout-select" data-test="console-target-region">
        <el-option v-for="region in screenRegions" :key="region.id" :value="region.id" :label="region.title || region.id" />
      </el-select></WorkbenchTooltip></label>
      <WorkbenchTooltip :content="$t('workbenchTips.monitorOnly')"><el-switch :model-value="monitorMode" :active-text="$t('consoleImprovements.monitorOnly')" data-test="console-monitor-mode" @change="setMonitorMode" /></WorkbenchTooltip>
      <div class="console-more-actions">
        <WorkbenchTooltip :content="$t('workbenchTips.refreshRuntime')" :disabled-trigger="(loading || refreshing)"><el-button size="small" :loading="loading || refreshing" data-test="console-refresh" @click="refreshFromMore">{{ $t('layoutsView.k38108e') }}</el-button></WorkbenchTooltip>
        <el-button v-if="layout && layout.document.monitors.length > 1 && !previewLayout" size="small" data-test="console-open-monitors" @click="openMonitorsFromMore">{{ $t('consoleUx.openMonitors') }}</el-button>
        <el-button v-if="canConfigure" size="small" data-test="console-configure" @click="runMore('configure')">{{ $t('workbench.configure') }}</el-button>
        <el-button v-if="layout && layout.document.monitors.length > 1" size="small" data-test="console-open-all" @click="runMore('monitors')">{{ $t('consoleView.k6f3df7') }}</el-button>
        <el-button size="small" data-test="console-fullscreen" @click="runMore('fullscreen')">{{ $t('consoleView.k93c44f') }}</el-button>
        <el-button size="small" data-test="console-overview" @click="runMore('overview')">{{ $t('consoleView.k11d024') }}</el-button>
      </div>
    </div>
    <div v-if="statusItems.length" class="console-status" data-test="console-status">
      <p v-for="item in statusItems" :key="item.test" class="console-status-item" :data-test="item.test">{{ item.text }}</p>
    </div>
    <div v-if="pendingLayout" class="update-notice" data-test="console-update">
      <span>{{ $t('layoutMonitor.updateAvailable', [pendingLayout.revision]) }}</span>
      <WorkbenchTooltip :content="$t('workbenchTips.applyUpdate')"><el-button size="small" type="primary" @click="applyUpdate">{{ $t('layoutMonitor.applyUpdate') }}</el-button></WorkbenchTooltip>
    </div>
    </div>
        <div class="banner global-banner" :class="!alarmsUnavailable && topAlarm && topAlarm.endTs == null ? `lvl-${topAlarm.level}` : ''" data-test="alarm-banner" role="status">
          <button v-if="!alarmsUnavailable && topAlarm" type="button" class="banner-jump" data-test="alarm-banner-jump" :aria-label="$t('consoleUx.bannerJump')" @click="focusTopAlarm" @keydown="onBannerKey">
            <strong data-test="alarm-banner-level">{{ levelLabel(topAlarm.level) }}</strong><span data-test="alarm-banner-state">{{ alarmStateLabel(topAlarm) }}</span><span class="alarm-message" :title="alarmMessage(topAlarm)">{{ alarmMessage(topAlarm) }}</span><span class="muted">{{ formatTime(topAlarm.startTs) }}</span>
          </button>
          <span v-else-if="alarmsUnavailable" data-test="console-alarm-error">{{ alarmError || $t('layoutMonitor.alarmsStale') }}</span>
          <span v-else>{{ $t('consoleView.k9d0a39') }}</span>
          <span class="spacer" />
          <span v-if="silenceUntil > now" class="banner-silence" data-test="console-silence-until">{{ $t('consoleUx.silenceUntil', [formatTime(silenceUntil)]) }}</span>
          <el-button v-if="fullscreen" size="small" link data-test="console-exit-fullscreen" @click="toggleFullscreen">{{ $t('consoleUx.exitFullscreen') }}</el-button>
          <el-button v-if="!previewLayout" size="small" link data-test="console-silence" @click="silenceBanner">{{ $t('consoleUx.silence') }}</el-button>
          <span data-test="alarm-banner-count">{{ $t('consoleView.kded3fc', [unackShown, activeShown]) }}</span>
          <span data-test="alarm-returned-count">{{ $t('layoutMonitor.returnedCount', [returnedShown]) }}</span>
          <span v-if="truncated" data-test="alarm-truncated">{{ $t('consoleView.truncated', [ALARM_LIMIT]) }}</span>
          <span v-if="layout" class="banner-fresh" data-test="console-freshness">{{ $t('consoleUx.freshness', [alarmListAt ? formatTime(alarmListAt) : '--']) }}</span>
        </div>
    </div>
    <div v-if="controlLock" class="control-lock" data-test="console-control-lock">{{ controlLock }}</div>
    <div v-if="monitor" class="console-workspace" data-test="console-workspace">
    <main class="grid" :class="{ 'is-focused': !!focusedRegionId }" :style="{ gridTemplateColumns: `repeat(${monitor.columns}, 1fr)`, gridTemplateRows: `repeat(${monitor.rows}, 1fr)` }">
      <section v-for="r in monitor.regions" v-show="!focusedRegionId || focusedRegionId === r.id" :key="`${layout?.id}-${monitorIndex}-${r.id}`" class="region" :class="{ 'is-active': r.kind === 'screen' && activeRegionId === r.id, 'is-focused': focusedRegionId === r.id }" :style="regionStyle(r)" @pointerdown.capture="r.kind === 'screen' && (activeRegionId = r.id)" @focusin="r.kind === 'screen' && (activeRegionId = r.id)">
        <span v-if="r.kind === 'screen' && activeRegionId === r.id" class="region-mark" data-test="region-active-name">{{ r.title || r.id }}</span>
        <RegionScreen v-if="r.kind === 'screen' && r.screenId" :ref="el => setRegionRef(r.id, el)" :region="r" :layout-id="layout?.id" :monitor-index="monitorIndex" :screen-name="screenName" :monitor-mode="monitorMode" :focused="focusedRegionId === r.id" @screen-change="setRegionScreen(r.id, $event)" @popup="openFaceplate" @detach="id => detach(id)" @focus="toggleRegionFocus(r.id)" />
        <div v-else-if="r.kind === 'alarmList'" class="alarm-list" data-test="alarm-list">
          <div class="region-head alarm-list-head">
            <strong :id="`alarm-heading-${r.id}`">{{ $t('layoutMonitor.alarmTitle') }}</strong>
            <span v-if="alarmScopeLabel(r)" class="alarm-context" :title="alarmScopeLabel(r)">{{ alarmScopeLabel(r) }}</span>
            <span v-else-if="r.title" class="alarm-context" :title="r.title">{{ r.title }}</span>
            <span class="spacer" />
            <span class="alarm-keys" data-test="console-alarm-keys">{{ $t('consoleUx.alarmKeys') }}</span>
            <span v-if="truncated && !alarmScopeBound(r)" class="alarm-limit" data-test="alarm-truncated" role="status" :title="$t('consoleView.truncated', [ALARM_LIMIT])">{{ $t('layoutMonitor.alarmLimit', [ALARM_LIMIT]) }}</span>
            <el-button v-if="!previewLayout && ackableFor(r).length" size="small" link data-test="alarm-ack-visible" :disabled="alarmsUnavailable || !!alarmScopeProblem(r)" @click="ackVisible(r)">{{ $t('consoleUx.ackVisible') }}</el-button>
            <router-link to="/alarms?state=live" class="alarm-all">{{ $t('layoutMonitor.allAlarms') }}</router-link>
          </div>
          <p v-if="alarmsUnavailable" class="alarm-error" data-test="console-alarm-error">{{ alarmError || $t('layoutMonitor.alarmsStale') }}</p>
          <p v-else-if="alarmScopeProblem(r)" class="alarm-error" data-test="console-alarm-scope">{{ alarmScopeProblem(r) }}</p>
          <div class="alarm-table-scroll" role="region" tabindex="0" :aria-label="$t('layoutMonitor.alarmTable')" @keydown="onAlarmKey($event, r)">
            <table v-if="alarmsFor(r).length" class="alarm-table" :aria-labelledby="`alarm-heading-${r.id}`">
              <colgroup><col class="alarm-level-col" /><col class="alarm-state-col" /><col class="alarm-time-col" /><col /><col class="alarm-action-col" /></colgroup>
              <thead><tr>
                <th scope="col">{{ $t('layoutMonitor.alarmLevel') }}</th><th scope="col">{{ $t('layoutMonitor.alarmState') }}</th>
                <th scope="col">{{ $t('layoutMonitor.alarmStartedAt') }}</th><th scope="col">{{ $t('layoutMonitor.alarmMessage') }}</th><th scope="col">{{ $t('layoutMonitor.alarmAction') }}</th>
              </tr></thead>
              <tbody><tr v-for="a in alarmsFor(r)" :key="a.id" class="alarm-row" :class="[`lvl-${a.level}`, { 'is-selected': focusedAlarmId === a.id }]" tabindex="0" :data-alarm-id="a.id" @focus="focusedAlarmId = a.id">
                <td><span class="alarm-level">{{ levelLabel(a.level) }}</span></td>
                <td><span class="alarm-state" :class="{ 'is-returned': a.endTs != null }">{{ alarmStateLabel(a) }}</span></td>
                <td class="alarm-time">{{ formatTime(a.startTs) }}</td>
                <td><span class="alarm-content" :title="alarmMessage(a)">{{ alarmMessage(a) }}<el-tag v-if="a.firstOut" size="small" effect="dark" type="danger" data-test="console-first-out">{{ $t('alarmsView.firstOut') }}</el-tag></span></td>
                <td class="alarm-action">
                  <WorkbenchTooltip v-if="!a.ackTs && canAckAlarm(a)" :content="$t('workbenchTips.acknowledge')" :disabled-trigger="(alarmsUnavailable)"><el-button :disabled="alarmsUnavailable" :aria-label="$t('layoutMonitor.ackAlarm', [alarmMessage(a)])" size="small" link @click="ack(a)">{{ $t('consoleView.kb56d9a') }}</el-button></WorkbenchTooltip>
                  <el-button v-else-if="a.tagId" size="small" link data-test="alarm-open-screen" @click="openAlarmHere(a)">{{ $t('alarmUx.openScreen') }}</el-button>
                  <span v-else-if="a.ackTs" class="muted">{{ $t('consoleView.kd9fea6') }}</span>
                  <span v-else class="muted">{{ $t('layoutMonitor.ackUnavailable') }}</span>
                  <el-dropdown v-if="a.tagId && !a.ackTs && canAckAlarm(a)" trigger="click">
                    <el-button size="small" link data-test="alarm-row-more" :aria-label="$t('alarmUx.rowMore')">{{ $t('alarmUx.rowMore') }}</el-button>
                    <template #dropdown>
                      <el-dropdown-menu>
                        <el-dropdown-item data-test="alarm-open-screen" @click="openAlarmHere(a)">{{ $t('alarmUx.openScreen') }}</el-dropdown-item>
                      </el-dropdown-menu>
                    </template>
                  </el-dropdown>
                </td>
              </tr></tbody>
            </table>
            <p v-else-if="!alarmsUnavailable && !alarmScopeProblem(r)" class="alarm-empty">{{ $t('layoutMonitor.emptyAlarms') }}</p>
          </div>
        </div>
        <ScreenDirectory v-else-if="r.kind === 'navigation'" :screens="screens" :active-screen-id="activeScreenId" :storage-key="directoryKey" :target-label="activeRegionLabel" @navigate="navigateMain" />
      </section>
    </main>
    <FaceplateWorkspace ref="faceplateWorkspace" :screen-name="screenName" :monitor-mode="monitorMode" :memory-key="panelMemoryKey" :screens-ready="screensLoaded" :verify-screens="!screensError" :known-screen-ids="screens.map(item => item.id)" @detach="detach" @error="clientError = $event" />
    </div>
    <div v-else-if="!loading" class="console-empty-workspace" data-test="console-empty-workspace">
      <el-empty data-test="console-layout-error" :description="layoutError || $t('workbench.emptyTitle')" :image-size="64">
        <p v-if="!layoutError" class="empty-help">{{ $t('workbench.emptyHelp') }}</p>
        <p v-if="!canConfigure && !previewLayout" class="empty-help">{{ $t('workbench.contactAdmin') }}</p>
        <div v-if="!previewLayout" class="empty-actions">
          <WorkbenchTooltip v-if="canConfigure" :content="$t('workbenchTips.quickCreate')"><el-button type="primary" data-test="console-create-workbench" @click="quickCreate?.open()">{{ $t('workbench.create') }}</el-button></WorkbenchTooltip>
          <WorkbenchTooltip v-if="canConfigure" :content="$t('workbenchTips.configure')"><el-button data-test="console-empty-configure" @click="router.push('/config/layouts')">{{ $t('workbench.configure') }}</el-button></WorkbenchTooltip>
          <WorkbenchTooltip :content="$t('workbenchTips.openScreen')"><el-button data-test="console-open-screens" @click="router.push('/screens')">{{ $t('workbench.openScreens') }}</el-button></WorkbenchTooltip>
          <el-button v-if="layoutError" data-test="console-layout-retry" @click="refreshConsole">{{ $t('workbench.retry') }}</el-button>
        </div>
      </el-empty>
    </div>
    <QuickLayoutCreate v-if="canConfigure" ref="quickCreate" @published="id => router.push(`/console/${id}`)" />
    <MonitorWindows v-if="layout" ref="monitorWindows" :key="layout.id" :layout="layout" :monitor-index="monitorIndex" :station-key="workstationKey" />
    <el-dialog v-model="screenPicker" :title="$t('alarmUx.pickScreen')" class="monitor-dialog" data-test="console-screen-picker">
      <el-radio-group v-model="pickedScreenId" style="display: flex; flex-direction: column; align-items: flex-start">
        <el-radio v-for="item in screenChoices" :key="item.id" :value="item.id">{{ item.name }}</el-radio>
      </el-radio-group>
      <template #footer><el-button @click="screenPicker = false">{{ $t('consoleView.k4d0b46') }}</el-button><el-button type="primary" data-test="console-screen-open" :disabled="pickedScreenId == null" @click="pickedScreenId != null && showAlarmScreen(pickedScreenId)">{{ $t('alarmUx.openScreen') }}</el-button></template>
    </el-dialog>
    <el-drawer v-model="timelineVisible" :title="$t('alarmsView.kb2da68')" size="min(440px, 100%)" append-to-body data-test="alarm-timeline">
      <div v-if="timelineAlarm" class="alarm-dialog-context"><strong>{{ alarmMessage(timelineAlarm) }}</strong></div>
      <div v-if="timelineLoading" v-loading="true" style="min-height: 80px" data-test="alarm-timeline-loading" />
      <el-alert v-if="timelineError" :title="timelineError" type="error" :closable="false" show-icon data-test="alarm-timeline-error" />
      <el-timeline v-if="timeline">
        <el-timeline-item :timestamp="formatTime(timeline.alarm.startTs)" type="danger">{{ alarmMessage(timeline.alarm) }}</el-timeline-item>
        <el-timeline-item v-for="item in timeline.activities" :key="item.id" :timestamp="formatTime(item.ts)" data-test="timeline-item">{{ item.action }}</el-timeline-item>
      </el-timeline>
    </el-drawer>
    <el-dialog v-model="monitorPick" :title="$t('consoleView.k1782d1')" class="monitor-dialog">
      <p class="tiny muted">{{ windowManagementSupported() ? $t('consoleView.k2f5ceb') : $t('consoleView.k226c52') }}</p>
      <el-alert v-if="windowError" :title="windowError" type="warning" :closable="false" show-icon data-test="console-window-error" />
      <el-alert v-else-if="monitorNotice" :title="$t(monitorNotice === 'denied' ? 'screenUx.monitorsDenied' : 'screenUx.monitorsUnavailable')" type="info" :closable="false" show-icon data-test="console-monitor-notice" />
      <el-radio-group v-model="pickIndex" style="display: flex; flex-direction: column; align-items: flex-start">
        <el-radio v-for="(m, i) in monitors" :key="i" :value="i">{{ m.label }}（{{ m.width }}×{{ m.height }}{{ m.isPrimary ? $t('consoleView.k0f9378') : '' }}）</el-radio>
      </el-radio-group>
      <template #footer><el-button @click="monitorPick = false">{{ $t('consoleView.k4d0b46') }}</el-button><WorkbenchTooltip :content="$t('workbenchTips.detach')"><el-button type="primary" data-test="monitor-open" @click="confirmDetach()">{{ $t('consoleView.k65fc81') }}</el-button></WorkbenchTooltip></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import WorkbenchTooltip from '../components/console/WorkbenchTooltip.vue'
import PageHelpButton from '../components/PageHelpButton.vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch, type ComponentPublicInstance } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessageBox } from 'element-plus'
import RegionScreen from '../components/console/RegionScreen.vue'
import FaceplateWorkspace from '../components/console/FaceplateWorkspace.vue'
import ScreenDirectory from '../components/console/ScreenDirectory.vue'
import MonitorWindows from '../components/console/MonitorWindows.vue'
import QuickLayoutCreate from '../components/console/QuickLayoutCreate.vue'
import { canGlobal } from '../access/policies'
import { hmiLayoutApi, getLocalWorkstation, WORKSTATION_ID_KEY, WORKSTATION_KEY, type HmiLayout, type HmiRegion, type HmiWorkstation } from '../api/hmiLayouts'
import { alarmApi, areaApi, deviceApi } from '../api'
import { annunciatorApi } from '../api/annunciators'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { queryMonitors, openOnMonitor, windowManagementSupported, type MonitorInfo, type MonitorNotice } from '../components/console/windowing'
import { usePolling } from '../composables/usePolling'
import { useNowTicker } from '../composables/useNowTicker'
import { refreshActiveAlarms } from '../composables/useActiveAlarms'
import { useAuthStore } from '../stores/auth'
import { alarmMessage, canAckAlarmEvent, formatTime, levelLabel } from '../utils'
import type { AlarmEvent, AlarmSummary, AlarmTimeline, Area, Device } from '../types'
import type { ScreenSummary } from '../screens/model'
import { tr } from '../i18n'
import { contentMonitor } from '../components/console/layoutGeometry'
import { alarmMatchesScope, alarmScopeBound } from '../components/console/alarmScope'
import { collectScreenTags } from '../screens/screenTags'
import { registerShortcuts } from '../shortcuts/registry'

const props = defineProps<{ previewLayout?: HmiLayout; previewMonitor?: number }>()
const route = useRoute(), router = useRouter(), auth = useAuthStore()
const canConfigure = computed(() => !props.previewLayout && canGlobal(auth.user, 'Configure'))
const quickCreate = ref<InstanceType<typeof QuickLayoutCreate>>()
const layouts = ref<HmiLayout[]>([]), layout = ref<HmiLayout | null>(null), station = ref<HmiWorkstation | null>(null), loading = ref(true), layoutError = ref('')
const screens = ref<ScreenSummary[]>([]), screensLoaded = ref(false)
const areas = ref<Area[]>([]), devices = ref<Device[]>([])
const screensError = ref(''), versionError = ref(''), pendingLayout = ref<HmiLayout | null>(null)
const fullscreen = ref(typeof document !== 'undefined' && document.fullscreenElement === document.documentElement)
const panelMemoryKey = computed(() => `scada-console-panels:${auth.user?.id ?? auth.user?.userName ?? 'unknown'}:${layout.value?.id ?? 'none'}`)
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
const focusedRegionId = ref('')
const regionScreens = ref<Record<string, number>>({})
watch(monitor, () => { activeRegionId.value = screenRegions.value[0]?.id ?? ''; regionScreens.value = {}; focusedRegionId.value = '' })
const activeRegionLabel = computed(() => {
  const region = screenRegions.value.find(item => item.id === activeRegionId.value) ?? screenRegions.value[0]
  return region ? (region.title || region.id) : ''
})
function regionStyle(region: HmiRegion): Record<string, string> {
  if (focusedRegionId.value === region.id) return { gridColumn: '1 / -1', gridRow: '1 / -1' }
  return { gridColumn: `${region.col} / span ${region.colSpan}`, gridRow: `${region.row} / span ${region.rowSpan}` }
}
function toggleRegionFocus(id: string): void { focusedRegionId.value = focusedRegionId.value === id ? '' : id }
function monitorMemoryKey(layoutId: number): string { return `scada-console-monitor:${auth.user?.id ?? auth.user?.userName ?? 'unknown'}:${layoutId}` }
function selectLayout(id: number): void {
  moreOpen.value = false
  let remembered = 0
  try {
    const saved = Number(localStorage.getItem(monitorMemoryKey(id)))
    if (Number.isInteger(saved) && saved >= 0) remembered = saved
  } catch { /* a layout without a saved display opens on the first one */ }
  void router.push(`/console/${id}?monitor=${remembered}`)
}
watch([monitorIndex, () => layout.value?.id], () => {
  const id = Number(route.params.layoutId)
  if (props.previewLayout || !layout.value || layout.value.id !== id) return
  try { localStorage.setItem(monitorMemoryKey(id), String(monitorIndex.value)) } catch { /* the choice stays in this window */ }
})
function setRegionScreen(regionId: string, screenId: number): void { regionScreens.value[regionId] = screenId }
const activeScreenId = computed(() => regionScreens.value[activeRegionId.value] ?? screenRegions.value.find(region => region.id === activeRegionId.value)?.screenId ?? undefined)
const workstationKey = computed(() => { const binding = getLocalWorkstation(); return station.value ? `station-${station.value.id}` : binding.id ? `station-${binding.id}` : binding.name ? `legacy-${binding.name}` : 'unassigned' })
const directoryKey = computed(() => `${auth.user?.id ?? auth.user?.userName ?? 'unknown'}:${workstationKey.value}:${layout.value?.id ?? ''}`)
const monitorModeKey = computed(() => `scada-console-monitor-mode:${auth.user?.id ?? auth.user?.userName ?? 'unknown'}`)
const monitorMode = ref(!!props.previewLayout || localStorage.getItem(monitorModeKey.value) === 'true')
watch(monitorModeKey, key => { if (!props.previewLayout) monitorMode.value = localStorage.getItem(key) === 'true' })
watch(monitorMode, value => { if (props.previewLayout) return; try { localStorage.setItem(monitorModeKey.value, String(value)) } catch { /* This preference remains available for the current window. */ } })
const controlLock = computed(() => {
  if (monitorMode.value) return tr('consoleUx.monitorOnlyLock')
  if (station.value && !station.value.allowControl) return tr('consoleUx.stationLock', [station.value.name])
  return ''
})
async function setMonitorMode(value: boolean | string | number): Promise<void> {
  const next = value === true
  if (props.previewLayout || next === monitorMode.value) return
  if (!next) {
    try { await ElMessageBox.confirm(tr('consoleUx.enableControlConfirm'), tr('consoleUx.enableControlTitle'), { type: 'warning' }) }
    catch { return }
  }
  monitorMode.value = next
}
const moreOpen = ref(false)
function onMoreClick(event: Event): void {
  if (!moreOpen.value) return
  const target = event.target
  if (!(target instanceof Element)) return
  if (target.closest('.console-more-panel, [data-test="console-more"], .el-popper')) return
  moreOpen.value = false
}
function runMore(command: string | number): void {
  moreOpen.value = false
  if (command === 'configure') void router.push('/config/layouts')
  else if (command === 'monitors') void monitorWindows.value?.prepare()
  else if (command === 'fullscreen') toggleFullscreen()
  else if (command === 'overview') void router.push('/overview')
}
function refreshFromMore(): void {
  moreOpen.value = false
  void refreshConsole()
}
function openMonitorsFromMore(): void {
  moreOpen.value = false
  void monitorWindows.value?.openPublished()
}
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
  faceplateWorkspace.value?.clear(false); monitorPick.value = false; windowError.value = ''; detachSequence++; pendingScreen.value = null; pendingFaceplate.value = null
  layout.value = null; station.value = null; pendingLayout.value = null; versionError.value = ''; clientError.value = ''
  try {
    if (props.previewLayout) {
      layout.value = props.previewLayout
      layouts.value = [props.previewLayout]
      layoutError.value = ''
      try { screens.value = (await screenApi.list()).filter(screen => screen.publishedRevision != null); screensError.value = '' }
      catch (reason) { screensError.value = errorMessage(reason, tr('layoutMonitor.screensFailed')) }
      screensLoaded.value = true
      return
    }
    const [layoutResult, screenResult] = await Promise.allSettled([hmiLayoutApi.list(), screenApi.list()])
    if (!isCurrent()) return
    if (screenResult.status === 'fulfilled') { screens.value = screenResult.value.filter(screen => screen.publishedRevision != null); screensError.value = '' }
    else screensError.value = errorMessage(screenResult.reason, tr('layoutMonitor.screensFailed'))
    screensLoaded.value = true
    if (layoutResult.status === 'rejected') throw layoutResult.reason
    layouts.value = layoutResult.value
    void deviceApi.list().then(list => {
      if (!isCurrent()) return
      devices.value = list
      const map = new Map<number, number>()
      for (const d of list) if (d.areaId != null) map.set(d.id, d.areaId)
      deviceAreas.value = map
    }).catch(() => {})
    void areaApi.list().then(list => { if (isCurrent()) areas.value = list }).catch(() => {})
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
        screens.value = list.filter(screen => screen.publishedRevision != null); screensError.value = ''; screensLoaded.value = true
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
  const next = pendingLayout.value
  if (!layout.value || !next) return
  const nextRaw = next.document.monitors[Math.min(monitorIndex.value, Math.max(0, next.document.monitors.length - 1))]
  const nextIds = new Set((nextRaw ? contentMonitor(nextRaw).regions : []).map(region => region.id))
  const currentScreens = (monitor.value?.regions ?? []).filter(region => region.kind === 'screen')
  const label = (region: { title?: string | null; id: string }) => region.title || region.id
  const kept = currentScreens.filter(region => nextIds.has(region.id)).map(label)
  const closed = currentScreens.filter(region => !nextIds.has(region.id)).map(label)
  try {
    await ElMessageBox.confirm(
      `${tr('layoutMonitor.applyConfirm')}\n${tr('consoleUx.applyDetail', [kept.join('、') || tr('consoleUx.none'), closed.join('、') || tr('consoleUx.none')])}`,
      tr('layoutMonitor.applyUpdate'), { type: 'warning' })
  } catch { return }
  const snapshot = new Map<string, { stack: number[]; index: number }>()
  for (const region of currentScreens) {
    if (!nextIds.has(region.id)) continue
    const history = regionRefs.get(region.id)?.history()
    if (history) snapshot.set(region.id, history)
  }
  faceplateWorkspace.value?.clear(false)
  await load()
  await nextTick()
  for (const [id, history] of snapshot) regionRefs.get(id)?.restore(history)
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
const silenceError = ref('')
const silenceUntil = ref(0)
const clientError = ref(''), screenOpenError = ref('')
const windowError = ref('')
const monitorNotice = ref<MonitorNotice>('')
const statusItems = computed(() => {
  const items: { test: string; text: string }[] = []
  if (screensError.value) items.push({ test: 'console-screens-error', text: screensError.value })
  if (summaryError.value || summaryStale.value) items.push({ test: 'console-summary-error', text: summaryError.value || tr('layoutMonitor.summaryStale') })
  if (versionError.value) items.push({ test: 'console-version-error', text: versionError.value })
  if (ackError.value) items.push({ test: 'console-ack-error', text: ackError.value })
  if (silenceError.value) items.push({ test: 'console-silence-error', text: silenceError.value })
  if (clientError.value) items.push({ test: 'console-client-error', text: clientError.value })
  if (screenOpenError.value) items.push({ test: 'console-screen-error', text: screenOpenError.value })
  return items
})
const screenTags = ref(new Map<number, Set<number>>())
const screenTagState = ref(new Map<number, 'loading' | 'error'>())
function alarmScopeProblem(region: HmiRegion): string {
  const screenId = region.alarmScreenId
  if (!screenId) return ''
  const state = screenTagState.value.get(screenId)
  if (state === 'error') return tr('consoleUx.scopeFailed')
  if (!screenTags.value.has(screenId)) return tr('consoleUx.scopePending')
  return ''
}
function alarmScopeLabel(region: HmiRegion): string {
  if (region.alarmDeviceId) return tr('layoutMonitor.alarmRegion', [devices.value.find(item => item.id === region.alarmDeviceId)?.name || region.title || `#${region.alarmDeviceId}`])
  if (region.alarmAreaId) return tr('layoutMonitor.alarmRegion', [areas.value.find(item => item.id === region.alarmAreaId)?.name || region.title || `#${region.alarmAreaId}`])
  if (region.alarmScreenId) return tr('layoutMonitor.alarmRegion', [screens.value.find(item => item.id === region.alarmScreenId)?.name || region.title || `#${region.alarmScreenId}`])
  return ''
}
function alarmsFor(region: HmiRegion): AlarmEvent[] {
  if (alarmScopeProblem(region)) return []
  const tags = region.alarmScreenId ? screenTags.value.get(region.alarmScreenId) : null
  return alarms.value.filter(alarm => alarmMatchesScope(region, alarm, tags))
}
function ackableFor(region: HmiRegion): AlarmEvent[] {
  return alarmsFor(region).filter(alarm => !alarm.ackTs && alarm.level !== 'Critical' && canAckAlarm(alarm))
}
async function ensureScreenTags(screenId: number): Promise<void> {
  if (screenTags.value.has(screenId) || screenTagState.value.get(screenId) === 'loading') return
  screenTagState.value = new Map(screenTagState.value).set(screenId, 'loading')
  try {
    const detail = await screenApi.get(screenId)
    const tags = new Set<number>()
    for (const node of detail.document.nodes ?? []) collectScreenTags(node, tags)
    screenTags.value = new Map(screenTags.value).set(screenId, tags)
    const next = new Map(screenTagState.value); next.delete(screenId); screenTagState.value = next
  } catch {
    if (!disposed) screenTagState.value = new Map(screenTagState.value).set(screenId, 'error')
  }
}
watch(monitor, current => {
  for (const region of current?.regions ?? []) if (region.kind === 'alarmList' && region.alarmScreenId) void ensureScreenTags(region.alarmScreenId)
}, { immediate: true })
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
async function ack(alarm: AlarmEvent): Promise<void> {
  ackError.value = ''
  let note: string | undefined
  if (alarm.level === 'Critical') {
    try {
      const result = await ElMessageBox.prompt(tr('alarmsView.k8ea5fe'), tr('alarmsView.kff8fce'), {
        inputType: 'textarea',
        inputValidator: (value: string) => (value && value.trim() ? true : tr('alarmsView.k8ea5fe')),
        confirmButtonText: tr('alarmsView.kb56d9a'),
        cancelButtonText: tr('alarmsView.k4d0b46')
      })
      note = result.value.trim()
    } catch { return }
  }
  try { await alarmApi.ack(alarm.id, note); await refreshAlarms() } catch (e) { ackError.value = errorMessage(e, tr('consoleView.kc490c1')) }
}
function focusAlarmRow(alarm: AlarmEvent | undefined): void {
  if (!alarm) return
  focusedAlarmId.value = alarm.id
  void nextTick(() => {
    const row = document.querySelector(`[data-alarm-id="${alarm.id}"]`)
    if (!(row instanceof HTMLElement)) return
    row.scrollIntoView({ block: 'nearest' })
    const button = row.querySelector('button')
    if (button instanceof HTMLElement) button.focus()
    else row.focus()
  })
}
async function focusTopAlarm(): Promise<void> {
  const alarm = topAlarm.value
  if (!alarm) return
  await openAlarmHere(alarm, true)
  focusAlarmRow(alarm)
}
function onBannerKey(event: KeyboardEvent): void {
  if (event.key !== 'ArrowDown' && event.key !== 'j' && event.key !== 'J') return
  const region = monitor.value?.regions.find(item => item.kind === 'alarmList')
  if (!region) return
  event.preventDefault()
  focusAlarmRow(alarmsFor(region)[0])
}
async function ackVisible(region: HmiRegion): Promise<void> {
  const rows = ackableFor(region)
  if (!rows.length || alarmsUnavailable.value) return
  try { await ElMessageBox.confirm(tr('consoleUx.ackVisibleConfirm', [rows.length]), tr('consoleUx.ackVisible'), { type: 'warning' }) }
  catch { return }
  const failed: string[] = []
  for (const alarm of rows) {
    try { await alarmApi.ack(alarm.id) } catch (error) { failed.push(errorMessage(error, alarmMessage(alarm))) }
  }
  ackError.value = failed.length ? tr('consoleUx.ackVisiblePartial', [failed.length, failed.slice(0, 3).join('、')]) : ''
  await refreshAlarms()
}
async function silenceBanner(): Promise<void> {
  silenceError.value = ''
  try { await ElMessageBox.confirm(tr('consoleUx.silenceConfirm', [5]), tr('consoleUx.silence'), { type: 'warning' }) }
  catch { return }
  try {
    const result = await annunciatorApi.silenceVisible(5, tr('consoleUx.silenceNote'))
    if (!result.silenced) { silenceError.value = tr('consoleUx.silenceNone'); silenceUntil.value = 0; return }
    silenceUntil.value = Date.now() + 5 * 60_000
  } catch (error) { silenceError.value = errorMessage(error, tr('consoleUx.silenceNone')) }
}

// 区域导航：画面目录点选时在当前选中的画面区域中打开。
type RegionHandle = { navigate: (id: number) => void; history: () => { stack: number[]; index: number }; restore: (state: { stack: number[]; index: number }) => void }
const regionRefs = new Map<string, RegionHandle>()
function setRegionRef(id: string, el: Element | ComponentPublicInstance | null): void { if (el) regionRefs.set(id, el as unknown as RegionHandle); else regionRefs.delete(id) }
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
const focusedAlarmId = ref<number | null>(null)
const screenPicker = ref(false), screenChoices = ref<{ id: number; name: string }[]>([]), pickedScreenId = ref<number | null>(null)
const timelineVisible = ref(false), timelineLoading = ref(false), timelineError = ref(''), timeline = ref<AlarmTimeline | null>(null), timelineAlarm = ref<AlarmEvent | null>(null)
let timelineSeq = 0
let screenIndex: Map<number, { id: number; name: string }[]> | null = null
let screenIndexPromise: Promise<void> | null = null
function isTypingTarget(target: EventTarget | null): boolean {
  const element = target as HTMLElement | null
  return !!element && (element.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(element.tagName))
}
function alarmKeysBlocked(): boolean {
  return timelineVisible.value || screenPicker.value || !!document.querySelector('.el-message-box, .el-overlay')
}
async function ensureScreenIndex(): Promise<void> {
  if (screenIndex) return
  if (!screenIndexPromise) screenIndexPromise = (async () => {
    const published = screens.value.length ? screens.value : (await screenApi.list()).filter(screen => screen.publishedRevision != null)
    const loaded = await Promise.all(published.map(async screen => {
      try { return { screen, detail: await screenApi.get(screen.id) } }
      catch { return null }
    }))
    if (published.length && loaded.every(item => item == null)) throw new Error('screen-index')
    const index = new Map<number, { id: number; name: string }[]>()
    for (const item of loaded) {
      if (!item) continue
      const tags = new Set<number>()
      for (const node of item.detail.document.nodes ?? []) collectScreenTags(node, tags)
      for (const tagId of tags) {
        const rows = index.get(tagId) ?? []
        rows.push({ id: item.screen.id, name: item.screen.name })
        index.set(tagId, rows)
      }
    }
    screenIndex = index
  })().catch(error => { screenIndexPromise = null; throw error })
  await screenIndexPromise
}
function showAlarmScreen(screenId: number): void {
  screenPicker.value = false
  const region = screenRegions.value.find(item => item.id === activeRegionId.value) ?? screenRegions.value[0]
  const target = region ? regionRefs.get(region.id) : null
  if (target) target.navigate(screenId)
  else openFaceplate(screenId)
}
async function openAlarmHere(alarm: AlarmEvent, quiet = false): Promise<boolean> {
  if (!quiet) screenOpenError.value = ''
  if (!alarm.tagId) { if (!quiet) screenOpenError.value = tr('alarmUx.noScreen'); return false }
  try { await ensureScreenIndex() }
  catch (error) { if (!quiet) screenOpenError.value = errorMessage(error, tr('alarmUx.screenLookupFailed')); return false }
  const matches = screenIndex?.get(alarm.tagId) ?? []
  if (!matches.length) { if (!quiet) screenOpenError.value = tr('alarmUx.noScreen'); return false }
  const openIds = new Set(screenRegions.value.map(region => regionScreens.value[region.id] ?? region.screenId).filter((id): id is number => id != null))
  const preferred = matches.find(item => openIds.has(item.id)) ?? (matches.length === 1 ? matches[0] : null)
  if (!preferred) {
    if (quiet) return false
    screenChoices.value = matches
    pickedScreenId.value = matches[0]?.id ?? null
    screenPicker.value = true
    return false
  }
  showAlarmScreen(preferred.id)
  return true
}
async function openTimeline(alarm: AlarmEvent): Promise<void> {
  const seq = ++timelineSeq
  timelineAlarm.value = alarm
  timeline.value = null
  timelineError.value = ''
  timelineLoading.value = true
  timelineVisible.value = true
  try {
    const result = await alarmApi.timeline(alarm.id)
    if (!disposed && seq === timelineSeq) timeline.value = result
  } catch (error) {
    if (!disposed && seq === timelineSeq) timelineError.value = errorMessage(error, tr('alarmsView.k760fd5'))
  } finally { if (!disposed && seq === timelineSeq) timelineLoading.value = false }
}
function onAlarmKey(event: KeyboardEvent, region: HmiRegion): void {
  if (disposed || event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey || isTypingTarget(event.target) || alarmKeysBlocked()) return
  if (window.matchMedia?.('(max-width: 768px)').matches) return
  const rows = alarmsFor(region)
  const index = rows.findIndex(alarm => alarm.id === focusedAlarmId.value)
  const move = (next: number) => { event.preventDefault(); focusAlarmRow(rows[next]) }
  if (event.key === 'ArrowDown' || event.key === 'j' || event.key === 'J') { if (rows.length) move(index < 0 ? 0 : Math.min(rows.length - 1, index + 1)); return }
  if (event.key === 'ArrowUp' || event.key === 'k' || event.key === 'K') { if (rows.length) move(index < 0 ? rows.length - 1 : Math.max(0, index - 1)); return }
  const alarm = rows[index]
  if (!alarm) return
  if (event.key === 't' || event.key === 'T') { event.preventDefault(); void openTimeline(alarm); return }
  if ((event.key === 'a' || event.key === 'A') && !alarm.ackTs && canAckAlarm(alarm)) { event.preventDefault(); void ack(alarm) }
}

// 新窗口/多显示器
const monitors = ref<MonitorInfo[]>([]), monitorPick = ref(false), pickIndex = ref(0), pendingScreen = ref<number | null>(null), pendingFaceplate = ref<number | null>(null)
watch(monitorPick, open => { if (!open) windowError.value = '' })
async function detach(screenId: number, faceplateKey?: number): Promise<void> {
  const sequence = ++detachSequence, currentLayout = layout.value
  windowError.value = ''; monitorNotice.value = ''
  const available = await queryMonitors()
  if (disposed || sequence !== detachSequence || layout.value !== currentLayout) return
  monitors.value = available.monitors
  monitorNotice.value = available.notice
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

function onFullscreenChange(): void { fullscreen.value = document.fullscreenElement === document.documentElement }
let unregisterShortcuts = () => {}
onMounted(async () => {
  window.addEventListener('storage', syncPreferences)
  document.addEventListener('click', onMoreClick, true)
  document.addEventListener('fullscreenchange', onFullscreenChange)
  unregisterShortcuts = registerShortcuts({
    id: 'console-alarms',
    titleKey: 'shortcuts.alarms',
    items: [
      { keys: ['↓', 'J'], descKey: 'shortcuts.alarmDown' },
      { keys: ['↑', 'K'], descKey: 'shortcuts.alarmUp' },
      { keys: ['A'], descKey: 'shortcuts.alarmAck' },
      { keys: ['T'], descKey: 'shortcuts.alarmTimeline' }
    ]
  })
  await load(); await refreshAlarms()
})
onBeforeUnmount(() => { disposed = true; detachSequence++; timelineSeq++; unregisterShortcuts(); window.removeEventListener('storage', syncPreferences); document.removeEventListener('click', onMoreClick, true); document.removeEventListener('fullscreenchange', onFullscreenChange) })
</script>

<style scoped>
.console { height: 100vh; display: flex; flex-direction: column; background: #0d1622; color: #dce6f0; overflow: hidden; }
.console.is-fullscreen .console-bar { display: none; }
.console-chrome { position: relative; z-index: 2; flex: 0 0 auto; background: #0d1622; }
.console-controls { max-height: 38dvh; overflow: auto; }
.console-workspace { position: relative; flex: 1; min-height: 0; isolation: isolate; overflow: hidden; }
.console-empty-workspace { flex: 1; min-height: 0; overflow: auto; padding: 24px 16px; }
.console-empty-workspace :deep(.el-empty) { max-width: 760px; margin: 0 auto; padding: 16px 0; }
.empty-help { margin: 0 0 16px; color: #b6c4d5; font-size: 14px; line-height: 1.7; overflow-wrap: anywhere; }
.empty-actions { display: flex; flex-wrap: wrap; justify-content: center; gap: 10px; }
.empty-actions :deep(.el-button) { margin: 0; }
.target-region { display: flex; align-items: center; gap: 6px; font-size: 12px; min-width: 0; flex: 0 0 auto; white-space: nowrap; }
.target-region > span { flex-shrink: 0; }
.console-bar :deep(.el-switch__label) { color: #dce6f0; }
.console-bar { display: flex; align-items: center; gap: 10px; padding: 6px 10px; background: #101b2d; border-bottom: 1px solid #22344d; flex-wrap: wrap; }
.station-quiet, .version-quiet { --el-tag-bg-color: transparent; --el-tag-border-color: #3b5878; --el-tag-text-color: #dce6f0; }
.console-more-panel { display: flex; flex-direction: column; align-items: stretch; gap: 10px; padding: 8px 10px 10px; background: #101b2d; border-bottom: 1px solid #22344d; color: #dce6f0; }
.console-more-panel :deep(.el-switch__label) { color: #dce6f0; }
.console-more-panel .layout-select,
.console-more-panel .target-region { width: 100%; }
.console-more-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.console-more-actions :deep(.el-button) { margin: 0; }
.console-bar > .console-title { margin: 0; font-size: 14px; font-weight: 600; min-width: 0; max-width: min(360px, calc(100vw - 20px)); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.console-bar :deep(.el-radio-group) { max-width: 100%; flex-wrap: wrap; }
.console-bar :deep(.el-radio-button__inner) { display: block; max-width: 180px; overflow: hidden; text-overflow: ellipsis; }
.console :deep(.runtime-toolbar > strong) { min-width: 0; max-width: min(280px, 100%); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.spacer { flex: 1; }
.layout-select { width: 180px; }
.update-notice { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; padding: 4px 10px; font-size: 12px; background: #5c3b10; }
.console-status { display: flex; flex-wrap: wrap; gap: 4px 12px; padding: 4px 10px; background: #3a2a10; color: #ffe8c2; font-size: 12px; }
.console-status-item { margin: 0; overflow-wrap: anywhere; }
.control-lock { padding: 4px 10px; background: #3a2a10; color: #ffe8c2; font-size: 12px; }
.region { position: relative; }
.region.is-active { outline: 3px solid #61ddaa; outline-offset: -3px; }
.region-mark { position: absolute; z-index: 3; top: 4px; right: 6px; max-width: 40%; padding: 1px 6px; border-radius: 3px; background: #14634a; color: #e7fff6; font-size: 11px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; pointer-events: none; }
.grid { height: 100%; display: grid; gap: 4px; padding: 4px; min-height: 0; }
.region { background: #101b2d; border: 1px solid #22344d; border-radius: 4px; overflow: hidden; min-height: 0; min-width: 0; }
.banner { height: 100%; display: flex; align-items: center; align-content: center; flex-wrap: wrap; gap: 4px 10px; padding: 4px 8px; overflow: auto; font-size: 14px; background: #1a2533; }
.banner-jump { display: flex; align-items: center; gap: 10px; min-width: 0; flex: 1 1 180px; margin: 0; padding: 0; border: 0; background: transparent; color: inherit; font: inherit; text-align: left; cursor: pointer; }
.banner-fresh, .banner-silence { font-size: 12px; color: #e6edf5; white-space: nowrap; }
.global-banner { height: auto; min-height: 48px; max-height: 112px; border-bottom: 1px solid #3b5878; }
.alarm-message { flex: 1 1 160px; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.banner .muted { color: #e6edf5; }
.alarm-state { display: inline-block; margin: 0 6px; font-size: 12px; }
.banner.lvl-Critical { background: var(--scada-alarm-critical-solid); } .banner.lvl-Warning { background: var(--scada-alarm-warning-solid); } .banner.lvl-Info { background: var(--scada-alarm-info-solid); }
.region-head { font-weight: 600; padding: 4px 8px; background: #1b2838; font-size: 12px; }
.alarm-list { height: 100%; display: flex; flex-direction: column; overflow: hidden; font-size: 12px; container-type: inline-size; }
.alarm-list-head { display: flex; align-items: center; flex-wrap: wrap; flex: 0 0 auto; gap: 4px 12px; min-height: 32px; }
.alarm-context { max-width: 220px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: #b6c4d5; font-weight: 400; }
.alarm-limit { color: #b6c4d5; font-weight: 400; }
.alarm-all { flex-shrink: 0; padding: 2px; color: #afd5ff; text-underline-offset: 3px; }
.alarm-table-scroll { flex: 1; min-height: 0; overflow: auto; scrollbar-color: #708298 #152238; }
.alarm-table-scroll:focus-visible { outline: 2px solid #94c9ff; outline-offset: -2px; }
.alarm-table { width: 100%; min-width: 800px; border-collapse: collapse; table-layout: fixed; }
.alarm-level-col { width: 72px; } .alarm-state-col { width: 152px; } .alarm-time-col { width: 164px; } .alarm-action-col { width: 132px; }
.alarm-table th { position: sticky; top: 0; z-index: 1; background: #1b2838; color: #b6c4d5; text-align: left; font-weight: 500; white-space: nowrap; }
.alarm-table th, .alarm-table td { padding: 4px 8px; border-bottom: 1px solid #304159; line-height: 20px; vertical-align: top; }
.alarm-row.lvl-Critical { border-left: 4px solid var(--scada-alarm-critical); } .alarm-row.lvl-Warning { border-left: 4px solid var(--scada-alarm-warning); } .alarm-row.lvl-Info { border-left: 4px solid var(--scada-alarm-info); }
.alarm-row.is-selected { background: #1c334c; }
.alarm-row:focus-visible { outline: 2px solid #94c9ff; outline-offset: -2px; }
.alarm-keys { color: #b6c4d5; font-weight: 400; }
.alarm-content :deep(.el-tag) { margin-left: 6px; }
.alarm-level { display: inline-block; padding: 0 5px; border-radius: 3px; font-weight: 600; }
.lvl-Critical .alarm-level { color: var(--scada-alarm-critical-ink); background: var(--scada-alarm-critical-chip); } .lvl-Warning .alarm-level { color: var(--scada-alarm-warning-ink); background: var(--scada-alarm-warning-chip); } .lvl-Info .alarm-level { color: var(--scada-alarm-info-ink); background: var(--scada-alarm-info-chip); }
.alarm-row .alarm-state { margin: 0; padding: 0 5px; border: 1px solid #52647a; border-radius: 3px; line-height: 18px; }
.alarm-row .alarm-state.is-returned { border-style: dashed; color: var(--scada-alarm-returned-ink); border-color: var(--scada-alarm-returned); }
.alarm-time { color: #b6c4d5; font-variant-numeric: tabular-nums; white-space: nowrap; }
.alarm-content { display: block; overflow-wrap: anywhere; }
.alarm-action, .alarm-table th:last-child { position: sticky; right: 0; background: #101b2d; }
.alarm-table th:last-child { z-index: 2; background: #1b2838; }
.alarm-action .muted { color: #b6c4d5; }
.alarm-action :deep(.el-button) { --el-button-text-color: #afd5ff; --el-button-hover-text-color: #e2efff; --el-button-disabled-text-color: #9ca8b8; min-height: 24px; margin: 0; padding: 2px 6px; }
.alarm-all:focus-visible, .alarm-action :deep(.el-button:focus-visible) { outline: 2px solid #94c9ff; outline-offset: 2px; }
.alarm-empty { margin: 0; padding: 12px 8px; color: #b6c4d5; }
@container (max-width: 640px) {
  .alarm-table { min-width: 0; }
  .alarm-table thead { display: none; }
  .alarm-table, .alarm-table tbody, .alarm-table tr, .alarm-table td { display: block; width: auto; }
  .alarm-table tr { padding: 6px 0; border-bottom: 1px solid #304159; }
  .alarm-table td { border: 0; padding: 2px 8px; }
  .alarm-action, .alarm-table th:last-child { position: static; }
}
.alarm-error { margin: 0; padding: 4px 8px; color: #ffc9c9; background: #5c1a1a; }
.muted { color: #8796a8; }
.console :deep(.el-empty__description p) { color: #b6c4d5; line-height: 1.6; overflow-wrap: anywhere; }
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
  .alarm-keys { display: none; }
}
</style>
