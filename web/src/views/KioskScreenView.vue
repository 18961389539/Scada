<template>
  <div class="kiosk" role="main">
    <h1 class="kiosk-title">{{ $t('common.kiosk') }}</h1>
    <RuntimeBanners :health="health" />
    <div v-if="playlistLoadError" class="kiosk-error" role="alert" data-test="kiosk-playlist-error">{{ playlistLoadError }}</div>
    <div v-else-if="invalidId" class="kiosk-error" role="alert" data-test="kiosk-invalid-id">
      {{ $t('screenUx.invalidScreen') }}
    </div>
    <ScreenRuntime v-else :key="current" :screen-id="current" :monitor-mode="monitorOnly" fill initial-zoom="fit" auto-refresh />
    <div v-if="showStatusBar" class="kiosk-status-bar" role="status" data-test="kiosk-status-bar">
      <span v-if="hubIssue" data-test="kiosk-hub-issue">{{ hubIssue }}</span>
      <span v-if="isStandby" data-test="kiosk-standby">{{ $t('kioskStatus.standby') }}<template v-if="primaryUrl"> · {{ $t('kioskStatus.primary', [primaryUrl]) }}</template>
        <a v-if="primaryUrl" data-test="kiosk-open-primary" :href="primaryUrl" target="_blank" rel="noopener">{{ $t('runtimeBanners.openPrimary') }}</a></span>
      <span v-else-if="healthAbnormal" data-test="kiosk-health">{{ $t('kioskStatus.healthIssue') }}</span>
    </div>
    <div v-if="criticalBanner" class="kiosk-critical-banner" role="alert" data-test="kiosk-critical-banner">
      <strong>{{ $t('kioskUx.criticalTitle') }}</strong>
      <span>{{ criticalBanner }}</span>
    </div>
    <div v-if="missedCriticalNotice" class="kiosk-critical-banner" role="alert" data-test="kiosk-missed-critical">
      <strong>{{ $t('alarmReconnect.missedTitle') }}</strong>
      <span>{{ $t('alarmReconnect.missedBody', [missedCriticalNotice.count]) }}</span>
    </div>
    <div class="kiosk-chrome">
      <button
        type="button"
        class="kiosk-mute"
        data-test="kiosk-sound-switch"
        :aria-pressed="!soundEnabled"
        :aria-label="$t('appLayout.soundHint')"
        @click="soundEnabled = !soundEnabled"
      >{{ soundEnabled ? $t('kioskUx.soundOn') : $t('kioskUx.soundMuted') }}</button>
      <div v-if="!invalidId && (playlist.length > 1 || alarmPaused)" class="kiosk-playlist" aria-live="polite" data-test="kiosk-playlist">
        <span v-if="playlist.length > 1">{{ currentIndex + 1 }} / {{ playlist.length }}</span>
        <span v-if="alarmPaused" data-test="kiosk-alarm-paused">{{ $t('kioskUx.alarmPaused') }}</span>
      </div>
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import ScreenRuntime from '../components/ScreenRuntime.vue'
import RuntimeBanners from '../components/RuntimeBanners.vue'
import { useAlarmSession } from '../composables/useAlarmSession'
import { applyKioskAlarmEvent, emptyKioskAlarmPause, type KioskAlarmPauseState } from '../composables/kioskAlarmPause'
import { kioskPlaylistApi, type KioskPlaylist } from '../api/kioskPlaylists'
import { alarmApi } from '../api'
import { setMonitorOnlyClient } from '../api/http'
import { onHubEvent, onHubReconnected } from '../realtime/hub'
import { useServiceStatus } from '../composables/useServiceStatus'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'
import { alarmMessage } from '../utils'
import type { AlarmEvent } from '../types'

const route = useRoute()
const { soundEnabled, missedCriticalNotice } = useAlarmSession()
const { health, healthAbnormal, hubIssue, isStandby, primaryUrl, showServiceStatus, refreshHealth } = useServiceStatus()
const showStatusBar = showServiceStatus
const routeId = computed(() => {
  const value = Number(route.params.id)
  return Number.isSafeInteger(value) && value > 0 ? value : Number.NaN
})
// playlist 为逗号分隔的画面编号；第一个画面仍由路由参数提供，便于保持现有单画面地址兼容。
const savedPlaylist = ref<KioskPlaylist | null>(null)
const playlistLoadError = ref('')
const playlistIds = computed(() => {
  if (savedPlaylist.value) return savedPlaylist.value.screenIds
  const raw = typeof route.query.playlist === 'string' ? route.query.playlist : ''
  return [...new Set(raw.split(',').map(value => Number(value.trim())).filter(value => Number.isSafeInteger(value) && value > 0))]
})
const id = computed(() => Number.isFinite(routeId.value) ? routeId.value : playlistIds.value[0] ?? Number.NaN)
const invalidId = computed(() => !Number.isFinite(id.value) && !playlistLoadError.value)
const monitorOnly = computed(() => savedPlaylist.value?.monitorOnly === true || route.query.monitorOnly === '1')
watch(monitorOnly, value => { setMonitorOnlyClient(value) }, { immediate: true })
onBeforeUnmount(() => { setMonitorOnlyClient(false) })
const intervalSec = computed(() => {
  if (savedPlaylist.value) return Math.max(2, Math.min(600, savedPlaylist.value.intervalSeconds))
  const value = Number(route.query.interval)
  return Number.isFinite(value) ? Math.max(2, Math.min(600, Math.round(value))) : 20
})
const playlist = computed(() => {
  const ids = Number.isFinite(routeId.value) ? [routeId.value, ...playlistIds.value] : playlistIds.value
  return [...new Set(ids)]
})
const current = ref(Number.isFinite(id.value) ? id.value : 0)
const currentIndex = computed(() => Math.max(0, playlist.value.indexOf(current.value)))
const alarmPaused = ref(false)
const criticalBanner = ref('')
let openAlarms = new Map<number, AlarmEvent>()
let timer: number | undefined
let disposeAlarm: (() => void) | undefined

watch(() => route.fullPath, () => { void loadSavedPlaylist() })
watch(id, value => { if (Number.isFinite(value)) current.value = value })
watch(playlist, () => {
  if (playlist.value.length && !playlist.value.includes(current.value)) current.value = playlist.value[0]!
  startTimer()
})
watch(intervalSec, startTimer)
function stopTimer(): void {
  if (timer !== undefined) window.clearInterval(timer)
  timer = undefined
}
function startTimer(): void {
  stopTimer()
  if (playlist.value.length < 2) return
  timer = window.setInterval(() => {
    if (alarmPaused.value) return
    const ids = playlist.value
    if (!ids.length) return
    current.value = ids[(ids.indexOf(current.value) + 1) % ids.length]!
  }, intervalSec.value * 1000)
}
function applyPause(state: KioskAlarmPauseState): void {
  alarmPaused.value = state.paused
  criticalBanner.value = state.banner
}
function onAlarmEvent(action: string, alarm: AlarmEvent): void {
  const applied = applyKioskAlarmEvent(openAlarms, action, alarm, alarmMessage)
  openAlarms = applied.open
  applyPause(applied.state)
}
async function hydrateActiveAlarms(): Promise<void> {
  try {
    const page = await alarmApi.queryPage({ live: true, state: 'unack', offset: 0, limit: 200 })
    for (const alarm of page.items) onAlarmEvent('raise', alarm)
  } catch {
    /* 启动时拉不到活动报警不阻断大屏，后续仍按推送更新。 */
  }
}
let disposeStatusReconnect: (() => void) | undefined
async function loadSavedPlaylist(): Promise<void> {
  const raw = Number(route.params.id)
  const fromPlaylistRoute = route.path.startsWith('/kiosk/playlists/')
  if (!fromPlaylistRoute || !Number.isSafeInteger(raw) || raw <= 0) { savedPlaylist.value = null; playlistLoadError.value = ''; return }
  try {
    savedPlaylist.value = await kioskPlaylistApi.get(raw)
    playlistLoadError.value = ''
    if (savedPlaylist.value.screenIds[0]) current.value = savedPlaylist.value.screenIds[0]
  } catch (reason) {
    savedPlaylist.value = null
    playlistLoadError.value = errorMessage(reason, tr('kioskPlaylists.loadFailed'))
  }
}
onMounted(() => {
  void loadSavedPlaylist()
  startTimer()
  void refreshHealth()
  disposeStatusReconnect = onHubReconnected(() => { void refreshHealth() })
  // 无人值守大屏在紧急/警告活动期间停在当前画面并保持横幅，恢复或确认后再继续轮播。
  disposeAlarm = onHubEvent('alarm', (payload: { action?: string; alarm?: AlarmEvent } | null) => {
    if (payload?.action && payload.alarm) onAlarmEvent(payload.action, payload.alarm)
  })
  void hydrateActiveAlarms()
})
onBeforeUnmount(() => {
  stopTimer()
  disposeStatusReconnect?.()
  disposeAlarm?.()
  openAlarms = new Map()
  applyPause(emptyKioskAlarmPause())
})
</script>
<style scoped>
.kiosk { position: relative; height: 100vh; padding: 4px 8px; background: #0d1622; color: #dce6f0; display: flex; flex-direction: column; }
.kiosk-title { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0; }
.kiosk-error { margin: 16px auto; max-width: 640px; padding: 12px 16px; border: 1px solid #7d3535; border-radius: 6px; background: #341b20; color: #ffd9d9; }
.kiosk-status-bar { position: absolute; top: 8px; left: 8px; z-index: 5; display: flex; flex-wrap: wrap; gap: 8px; max-width: min(640px, calc(100% - 24px)); padding: 6px 10px; border: 1px solid #8a6a2b; border-radius: 6px; background: #2a2214cc; color: #f0d48a; font-size: 12px; }
.kiosk-critical-banner { position: absolute; top: 8px; left: 50%; transform: translateX(-50%); z-index: 5; display: flex; gap: 8px; max-width: min(920px, calc(100% - 24px)); padding: 8px 14px; border: 1px solid #7d3535; border-radius: 6px; background: #341b20; color: #ffd9d9; font-size: 13px; }
.kiosk-chrome { position: absolute; right: 14px; bottom: 10px; display: flex; align-items: center; gap: 8px; z-index: 5; }
.kiosk-mute { padding: 3px 8px; border: 1px solid #36516f; border-radius: 10px; background: #0b1624cc; color: #9fb3c8; font-size: 11px; cursor: pointer; }
.kiosk-mute[aria-pressed="true"] { border-color: #8a6a2b; color: #f0d48a; }
.kiosk-playlist { display: flex; gap: 8px; padding: 3px 8px; border: 1px solid #36516f; border-radius: 10px; background: #0b1624cc; color: #9fb3c8; font-size: 11px; pointer-events: none; }
</style>
