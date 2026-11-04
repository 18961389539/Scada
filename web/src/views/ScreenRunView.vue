<template>
  <div class="page page--fill screen-run">
    <el-alert v-if="invalidId" type="error" :title="$t('screenUx.invalidScreen')" :closable="false" show-icon data-test="screen-invalid-id" />
    <ScreenRuntime v-else :key="id" class="screen-run-body" :screen-id="id" fill initial-zoom="fit" @screen-loaded="onScreenLoaded">
      <template #toolbar>
        <el-button data-test="screen-back" @click="back">{{ $t(trail.length ? 'screenUx.backToPrevious' : 'screenRunView.backToScreens') }}</el-button>
        <nav v-if="trail.length" class="screen-trail" data-test="screen-trail" :aria-label="$t('screenUx.trail')">
          <button v-for="(crumb, index) in trail" :key="`${crumb}-${index}`" type="button" @click="jumpTrail(index)">{{ names[crumb] || `#${crumb}` }}</button>
          <span class="screen-trail-current" data-test="screen-trail-current" aria-current="page">{{ currentName }}</span>
        </nav>
      </template>
      <template #options>
        <el-button data-test="screen-new-window" @click="pick()">{{ $t('screenRunView.openInNewWindow') }}</el-button>
        <el-button data-test="screen-choose-monitor" @click="pick(true)">{{ $t('screenUx.chooseMonitor') }}</el-button>
      </template>
    </ScreenRuntime>
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
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter, type LocationQuery } from 'vue-router'
import ScreenRuntime from '../components/ScreenRuntime.vue'
import { queryMonitors, openOnMonitor, type MonitorInfo, type MonitorNotice } from '../components/console/windowing'
import { screenApi } from '../api/screens'
import { listQuery, screenRunLocation, trailIds } from '../screens/navigation'
import { matchMonitor, readScreenMonitor, rememberScreen, writeScreenMonitor } from '../screens/screenMemory'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'
const route = useRoute(), router = useRouter(), auth = useAuthStore()
const id = computed(() => { const value = Number(route.params.id); return Number.isInteger(value) && value > 0 ? value : Number.NaN })
const invalidId = computed(() => !Number.isFinite(id.value))
const trail = computed(() => trailIds(route.query))
const names = ref<Record<number, string>>({})
const currentName = computed(() => names.value[id.value] || (Number.isFinite(id.value) ? `#${id.value}` : ''))
const picking = ref(false), monitors = ref<MonitorInfo[]>([]), pickIndex = ref(0), windowError = ref(''), monitorNotice = ref<MonitorNotice>('')
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
