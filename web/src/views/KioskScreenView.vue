<template>
  <div class="kiosk" role="main">
    <h1 class="kiosk-title">{{ $t('common.kiosk') }}</h1>
    <div v-if="invalidId" class="kiosk-error" role="alert" data-test="kiosk-invalid-id">
      {{ $t('screenUx.invalidScreen') }}
    </div>
    <ScreenRuntime v-else :key="current" :screen-id="current" :monitor-mode="monitorOnly" fill initial-zoom="fit" auto-refresh />
    <div v-if="!invalidId && playlist.length > 1" class="kiosk-playlist" aria-live="polite" data-test="kiosk-playlist">
      <span>{{ currentIndex + 1 }} / {{ playlist.length }}</span>
      <span v-if="alarmPaused">{{ $t('kioskUx.alarmPaused') }}</span>
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import ScreenRuntime from '../components/ScreenRuntime.vue'
import { onHubEvent } from '../realtime/hub'

const route = useRoute()
const routeId = computed(() => {
  const value = Number(route.params.id)
  return Number.isSafeInteger(value) && value > 0 ? value : Number.NaN
})
// playlist 为逗号分隔的画面编号；第一个画面仍由路由参数提供，便于保持现有单画面地址兼容。
const playlistIds = computed(() => {
  const raw = typeof route.query.playlist === 'string' ? route.query.playlist : ''
  return [...new Set(raw.split(',').map(value => Number(value.trim())).filter(value => Number.isSafeInteger(value) && value > 0))]
})
const id = computed(() => Number.isFinite(routeId.value) ? routeId.value : playlistIds.value[0] ?? Number.NaN)
const invalidId = computed(() => !Number.isFinite(id.value))
const monitorOnly = computed(() => route.query.monitorOnly === '1')
const intervalSec = computed(() => {
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
let timer: number | undefined
let pauseTimer: number | undefined
let disposeAlarm: (() => void) | undefined

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
function pauseForAlarm(): void {
  alarmPaused.value = true
  if (pauseTimer !== undefined) window.clearTimeout(pauseTimer)
  pauseTimer = window.setTimeout(() => { alarmPaused.value = false }, Math.max(30_000, intervalSec.value * 1000))
}
onMounted(() => {
  startTimer()
  // 无人值守大屏遇到重要报警先停在当前画面，避免轮播把报警画面切走。
  disposeAlarm = onHubEvent('alarm', (payload: { action?: string; alarm?: { level?: string } } | null) => {
    if (payload?.action === 'raise' && (payload.alarm?.level === 'Critical' || payload.alarm?.level === 'Warning')) pauseForAlarm()
  })
})
onBeforeUnmount(() => {
  stopTimer()
  if (pauseTimer !== undefined) window.clearTimeout(pauseTimer)
  disposeAlarm?.()
})
</script>
<style scoped>
.kiosk { position: relative; height: 100vh; padding: 4px 8px; background: #0d1622; color: #dce6f0; display: flex; flex-direction: column; }
.kiosk-title { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0; }
.kiosk-error { margin: 16px auto; max-width: 640px; padding: 12px 16px; border: 1px solid #7d3535; border-radius: 6px; background: #341b20; color: #ffd9d9; }
.kiosk-playlist { position: absolute; right: 14px; bottom: 10px; display: flex; gap: 8px; padding: 3px 8px; border: 1px solid #36516f; border-radius: 10px; background: #0b1624cc; color: #9fb3c8; font-size: 11px; pointer-events: none; }
</style>
