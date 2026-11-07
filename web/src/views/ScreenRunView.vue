<template>
  <div class="page page--fill screen-run">
    <el-alert v-if="invalidId" type="error" :title="$t('screenUx.invalidScreen')" :closable="false" show-icon data-test="screen-invalid-id" />
    <ScreenRuntime v-else :key="id" class="screen-run-body" :screen-id="id" fill initial-zoom="fit" :replay-at="replayAt" @screen-loaded="onScreenLoaded">
      <template #toolbar>
        <el-button data-test="screen-back" @click="back">{{ $t(trail.length ? 'screenUx.backToPrevious' : 'screenRunView.backToScreens') }}</el-button>
        <nav v-if="trail.length" class="screen-trail" data-test="screen-trail" :aria-label="$t('screenUx.trail')">
          <button v-for="(crumb, index) in trail" :key="`${crumb}-${index}`" type="button" @click="jumpTrail(index)">{{ names[crumb] || `#${crumb}` }}</button>
          <span class="screen-trail-current" data-test="screen-trail-current" aria-current="page">{{ currentName }}</span>
        </nav>
        <el-button v-if="replayAt == null" data-test="screen-replay-open" @click="openReplay">{{ $t('screenReplay.open') }}</el-button>
        <template v-else>
          <el-tag type="warning" data-test="screen-replay-active">{{ $t('screenReplay.active', [formatTime(replayAt ?? 0)]) }}</el-tag>
          <el-button size="small" data-test="screen-replay-play" @click="toggleReplayPlay">{{ replayPlaying ? $t('screenReplay.pause') : $t('screenReplay.play') }}</el-button>
          <el-button size="small" @click="stepReplay(-60)">-1m</el-button>
          <el-button size="small" @click="stepReplay(60)">+1m</el-button>
          <el-select v-model="replaySpeed" size="small" style="width: 90px" :aria-label="$t('screenReplay.speed')">
            <el-option v-for="speed in [0.5, 1, 2, 5]" :key="speed" :label="`${speed}x`" :value="speed" />
          </el-select>
          <el-button size="small" type="danger" plain data-test="screen-replay-exit" @click="exitReplay">{{ $t('screenReplay.exit') }}</el-button>
        </template>
      </template>
      <template #options>
        <template v-if="replayAt == null">
          <el-button data-test="screen-new-window" @click="pick()">{{ $t('screenRunView.openInNewWindow') }}</el-button>
          <el-button data-test="screen-choose-monitor" @click="pick(true)">{{ $t('screenUx.chooseMonitor') }}</el-button>
        </template>
      </template>
    </ScreenRuntime>
    <el-dialog v-model="replayDialog" :title="$t('screenReplay.open')" width="460px" data-test="screen-replay-dialog">
      <el-alert :title="$t('screenReplay.hint')" type="info" :closable="false" show-icon style="margin-bottom: 12px" />
      <el-form label-width="96px">
        <el-form-item :label="$t('screenReplay.time')">
          <el-date-picker v-model="replayDate" type="datetime" :disabled-date="(date: Date) => date.getTime() > Date.now()" style="width: 100%" data-test="screen-replay-time" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="replayDialog = false">{{ $t('screenReplay.cancel') }}</el-button>
        <el-button type="primary" data-test="screen-replay-start" @click="startReplay">{{ $t('screenReplay.start') }}</el-button>
      </template>
    </el-dialog>
    <el-dialog v-model="picking" :title="$t('screenRunView.selectMonitor')" width="420px">
      <el-alert v-if="windowError" :title="windowError" type="warning" :closable="false" show-icon data-test="screen-window-error" />
      <el-alert v-else-if="monitorNotice" :title="$t(monitorNotice === 'denied' ? 'screenUx.monitorsDenied' : 'screenUx.monitorsUnavailable')" type="info" :closable="false" show-icon data-test="screen-monitor-notice" />
      <el-radio-group v-model="pickIndex" style="display: flex; flex-direction: column; align-items: flex-start">
        <el-radio v-for="(m, i) in monitors" :key="i" :value="i">{{ m.label }}（{{ m.width }}×{{ m.height }}）</el-radio>
      </el-radio-group>
      <template #footer>
        <el-button @click="picking = false">{{ $t('screenRunView.cancel') }}</el-button>
        <el-button type="primary" data-test="screen-open-window" :disabled="!monitors.length" @click="open()">{{ $t('screenRunView.open') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute, useRouter, type LocationQuery } from 'vue-router'
import { ElMessage } from 'element-plus'
import ScreenRuntime from '../components/ScreenRuntime.vue'
import { queryMonitors, openOnMonitor, type MonitorInfo, type MonitorNotice } from '../components/console/windowing'
import { screenApi } from '../api/screens'
import { listQuery, screenRunLocation, trailIds } from '../screens/navigation'
import { matchMonitor, readScreenMonitor, rememberScreen, writeScreenMonitor } from '../screens/screenMemory'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'
import { formatTime } from '../utils'
const route = useRoute(), router = useRouter(), auth = useAuthStore()
const id = computed(() => { const value = Number(route.params.id); return Number.isInteger(value) && value > 0 ? value : Number.NaN })
const invalidId = computed(() => !Number.isFinite(id.value))
const trail = computed(() => trailIds(route.query))
const names = ref<Record<number, string>>({})
const currentName = computed(() => names.value[id.value] || (Number.isFinite(id.value) ? `#${id.value}` : ''))
const picking = ref(false), monitors = ref<MonitorInfo[]>([]), pickIndex = ref(0), windowError = ref(''), monitorNotice = ref<MonitorNotice>('')
const initialReplay = Number(route.query.replayAt)
const replayAt = ref<number | null>(Number.isFinite(initialReplay) && initialReplay > 0 && initialReplay <= Date.now() ? initialReplay : null)
const replayDialog = ref(false), replayDate = ref<Date | null>(new Date(Date.now() - 3600_000))
const replayPlaying = ref(false), replaySpeed = ref(1)
let replayTimer: number | undefined
function openReplay(): void {
  replayDate.value = new Date((replayAt.value ?? Date.now()) - 3600_000)
  replayDialog.value = true
}
function startReplay(): void {
  const at = replayDate.value?.getTime()
  if (!at || !Number.isFinite(at) || at > Date.now()) { ElMessage.warning(tr('screenReplay.future')); return }
  replayAt.value = at
  replayDialog.value = false
  replayPlaying.value = false
  stopReplayTimer()
  syncReplayQuery(at)
}
function toggleReplayPlay(): void {
  const current = replayAt.value
  if (current == null) return
  if (replayPlaying.value) {
    replayPlaying.value = false
    stopReplayTimer()
    syncReplayQuery(current)
    return
  }
  replayPlaying.value = true
  stopReplayTimer()
  replayTimer = window.setInterval(() => {
    const at = replayAt.value
    if (at == null) { stopReplayTimer(); return }
    const now = Date.now()
    const next = Math.min(now, at + 1000 * replaySpeed.value)
    replayAt.value = next
    if (next >= now) {
      replayPlaying.value = false
      stopReplayTimer()
      syncReplayQuery(next)
    }
  }, 1000)
}
function stopReplayTimer(): void {
  if (replayTimer !== undefined) window.clearInterval(replayTimer)
  replayTimer = undefined
}
function stepReplay(seconds: number): void {
  const current = replayAt.value
  if (current == null) return
  const next = Math.max(1, Math.min(Date.now(), current + seconds * 1000))
  replayAt.value = next
  syncReplayQuery(next)
}
function exitReplay(): void {
  replayPlaying.value = false
  stopReplayTimer()
  replayAt.value = null
  syncReplayQuery(null)
}
function syncReplayQuery(at: number | null): void {
  const query: LocationQuery = { ...route.query }
  if (at == null) delete query.replayAt
  else query.replayAt = String(at)
  void router.replace({ path: route.path, query })
}
onBeforeUnmount(stopReplayTimer)
onMounted(async () => {
  try {
    const list = await screenApi.list(false)
    names.value = Object.fromEntries(list.map(item => [item.id, item.name]))
  } catch { /* 名称读不到时路径仍显示编号 */ }
})
function onScreenLoaded(screen: { id: number; name: string } | null): void {
  if (!screen) return
  names.value = { ...names.value, [screen.id]: screen.name }
  rememberScreen(auth.user?.id, screen.id)
}
function listBack(): LocationQuery { return listQuery(route.query) }
function back(): void {
  const stack = trail.value
  if (!stack.length) { void router.push({ path: '/screens', query: listBack() }); return }
  void router.push(screenRunLocation(stack[stack.length - 1], route.query, stack.slice(0, -1)))
}
function jumpTrail(index: number): void {
  const stack = trail.value
  void router.push(screenRunLocation(stack[index], route.query, stack.slice(0, index)))
}
async function pick(force = false): Promise<void> {
  windowError.value = ''
  monitorNotice.value = ''
  const result = await queryMonitors()
  monitors.value = result.monitors
  monitorNotice.value = result.notice
  const saved = readScreenMonitor(auth.user?.id)
  const index = saved ? matchMonitor(saved, result.monitors) : -1
  pickIndex.value = index >= 0 ? index : Math.min(1, Math.max(0, monitors.value.length - 1))
  if (!force && index >= 0 && result.notice === '') { open(); return }
  picking.value = true
}
function open(): void {
  const monitor = monitors.value[pickIndex.value]
  if (!monitor || !openOnMonitor(`/kiosk/screens/${id.value}`, `scada-screen-${id.value}`, monitor)) {
    windowError.value = tr('screenRunView.popupBlocked')
    picking.value = true
    return
  }
  writeScreenMonitor(auth.user?.id, monitor)
  windowError.value = ''
  picking.value = false
}
</script>
<style scoped>
.screen-run { min-height: 0; }
.screen-run-body { flex: 1; min-height: 0; }
.screen-trail { display: flex; flex-wrap: wrap; gap: 4px; align-items: center; }
.screen-trail button { border: 0; background: transparent; color: var(--scada-primary); cursor: pointer; font: inherit; padding: 0; }
.screen-trail button:hover { text-decoration: underline; }
.screen-trail-current { color: var(--scada-text, inherit); }
.screen-trail-current::before { content: '/'; margin-right: 4px; color: var(--scada-text-muted); }
</style>
