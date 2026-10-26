<template>
  <div class="console" data-test="console">
    <header class="console-bar">
      <strong>{{ layout?.name ?? $t('consoleView.k0dd591') }}</strong>
      <el-select v-if="layouts.length" :model-value="layout?.id" size="small" style="width: 180px" data-test="console-layout" @change="(id: number) => router.push(`/console/${id}?monitor=0`)">
        <el-option v-for="l in layouts" :key="l.id" :value="l.id" :label="l.name" />
      </el-select>
      <el-radio-group v-if="layout && layout.document.monitors.length > 1" :model-value="monitorIndex" size="small" data-test="console-monitor" @change="(i: number) => router.replace({ query: { monitor: i } })">
        <el-radio-button v-for="(m, i) in layout.document.monitors" :key="i" :value="i">{{ m.name || $t('consoleView.k47eff4', [i + 1]) }}</el-radio-button>
      </el-radio-group>
      <el-tag v-if="station" size="small" :type="station.allowControl ? 'success' : 'warning'" data-test="console-station">{{ $t('consoleView.k327883', [station.name, station.allowControl ? '' : $t('consoleView.k44b8e4')]) }}</el-tag>
      <span class="spacer" />
      <el-button v-if="layout && layout.document.monitors.length > 1" size="small" data-test="console-open-all" @click="openAll()">{{ $t('consoleView.k6f3df7') }}</el-button>
      <el-button size="small" @click="toggleFullscreen()">{{ $t('consoleView.k93c44f') }}</el-button>
      <el-button size="small" @click="router.push('/overview')">{{ $t('consoleView.k11d024') }}</el-button>
    </header>
    <el-alert v-if="ackError" :title="ackError" type="error" :closable="false" show-icon data-test="console-ack-error" />
    <el-empty v-if="!loading && !layout" data-test="console-layout-error" :description="layoutError || $t('consoleView.kc4e153')" :image-size="64" />
    <p v-else-if="layoutError" class="layout-error" data-test="console-layout-error">{{ layoutError }}</p>
    <main v-if="monitor" class="grid" :style="{ gridTemplateColumns: `repeat(${monitor.columns}, 1fr)`, gridTemplateRows: `repeat(${monitor.rows}, 1fr)` }">
      <section v-for="r in monitor.regions" :key="`${monitorIndex}-${r.id}`" class="region" :style="{ gridColumn: `${r.col} / span ${r.colSpan}`, gridRow: `${r.row} / span ${r.rowSpan}` }">
        <RegionScreen v-if="r.kind === 'screen' && r.screenId" :ref="el => setRegionRef(r.id, el)" :region="r" :screen-name="screenName" @popup="openFaceplate" @detach="id => detach(id)" />
        <div v-else-if="r.kind === 'alarmBanner'" class="banner" :class="topAlarm ? `lvl-${topAlarm.level}` : ''" data-test="alarm-banner">
          <template v-if="topAlarm"><strong data-test="alarm-banner-level">{{ levelLabel(topAlarm.level) }}</strong> {{ topAlarm.message }} <span class="muted">{{ formatTime(topAlarm.startTs) }}</span>
            <span class="spacer" /><span data-test="alarm-banner-count">{{ $t('consoleView.kded3fc', [unackShown, activeShown]) }}</span>
            <span v-if="truncated" data-test="alarm-truncated">{{ $t('consoleView.truncated', [ALARM_LIMIT]) }}</span>
            <span v-if="alarmError" class="tiny" data-test="console-alarm-error">{{ alarmError }}</span></template>
          <span v-else-if="alarmError" data-test="console-alarm-error">{{ alarmError }}</span>
          <span v-else>{{ $t('consoleView.k9d0a39') }}</span>
        </div>
        <div v-else-if="r.kind === 'alarmList'" class="alarm-list" data-test="alarm-list">
          <div class="region-head">{{ r.title || $t('consoleView.k8f7167') }}</div>
          <p v-if="alarmError" class="alarm-error" data-test="console-alarm-error">{{ alarmError }}</p>
          <p v-if="truncated" class="alarm-error" data-test="alarm-truncated">{{ $t('consoleView.truncated', [ALARM_LIMIT]) }}</p>
          <div v-for="a in alarms" :key="a.id" class="alarm-row" :class="`lvl-${a.level}`">
            <span class="tiny">{{ formatTime(a.startTs) }}</span> {{ a.message }}
            <el-button v-if="!a.ackTs && canAckAlarm(a)" size="small" link @click="ack(a.id)">{{ $t('consoleView.kb56d9a') }}</el-button><span v-else-if="a.ackTs" class="tiny muted">{{ $t('consoleView.kd9fea6') }}</span>
          </div>
          <p v-if="!alarms.length && !alarmError" class="muted tiny">{{ $t('consoleView.k9d0a39') }}</p>
        </div>
        <div v-else-if="r.kind === 'navigation'" class="nav-list" data-test="nav-list">
          <div class="region-head">{{ r.title || $t('consoleView.k6be0ed') }}</div>
          <button v-for="s in screens" :key="s.id" :data-test="`nav-${s.id}`" @click="navigateMain(s.id)">{{ s.name }}</button>
        </div>
      </section>
    </main>
    <!-- 浮动面板窗口：可同时打开多个、拖动、置顶、移到新窗口 -->
    <div v-for="(f, i) in faceplates" :key="f.key" class="faceplate" :style="{ left: `${f.x}px`, top: `${f.y}px`, zIndex: 100 + f.z }" data-test="faceplate" @pointerdown="raise(f)">
      <div class="faceplate-head" @pointerdown="startMove($event, f)">
        <span>{{ screenName(f.screenId) }}</span><span class="spacer" />
        <el-button size="small" link @click.stop="detach(f.screenId, f.key)">{{ $t('consoleView.kad9ce6') }}</el-button>
        <el-button size="small" link data-test="faceplate-close" @click.stop="faceplates.splice(i, 1)">✕</el-button>
      </div>
      <div class="faceplate-body"><ScreenRuntime :screen-id="f.screenId" navigate-mode="emit" compact fill initial-zoom="fit" @navigate="navigateMain" @popup="openFaceplate" /></div>
    </div>
    <el-dialog v-model="monitorPick" :title="$t('consoleView.k1782d1')" width="420px">
      <p class="tiny muted">{{ windowManagementSupported() ? $t('consoleView.k2f5ceb') : $t('consoleView.k226c52') }}</p>
      <el-radio-group v-model="pickIndex" style="display: flex; flex-direction: column; align-items: flex-start">
        <el-radio v-for="(m, i) in monitors" :key="i" :value="i">{{ m.label }}（{{ m.width }}×{{ m.height }}{{ m.isPrimary ? $t('consoleView.k0f9378') : '' }}）</el-radio>
      </el-radio-group>
      <template #footer><el-button @click="monitorPick = false">{{ $t('consoleView.k4d0b46') }}</el-button><el-button type="primary" data-test="monitor-open" @click="confirmDetach()">{{ $t('consoleView.k65fc81') }}</el-button></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch, type ComponentPublicInstance } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
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
const monitorIndex = computed(() => Math.max(0, Math.min(Number(route.query.monitor ?? 0) || 0, (layout.value?.document.monitors.length ?? 1) - 1)))
const monitor = computed(() => layout.value?.document.monitors[monitorIndex.value] ?? null)
const screenName = (id: number) => screens.value.find(s => s.id === id)?.name ?? tr('consoleView.k570a22', [id])
const deviceAreas = ref<Map<number, number>>(new Map())
function canAckAlarm(alarm: AlarmEvent): boolean {
  return canAckAlarmEvent(auth.user, alarm, deviceAreas.value)
}

async function load(): Promise<void> {
  loading.value = true
  try {
    ;[layouts.value, screens.value] = await Promise.all([hmiLayoutApi.list(), screenApi.list()])
    void deviceApi.list().then(list => {
      const map = new Map<number, number>()
      for (const d of list) if (d.areaId != null) map.set(d.id, d.areaId)
      deviceAreas.value = map
    }).catch(() => {})
    const name = localStorage.getItem(WORKSTATION_KEY)
    if (name) { try { const r = await hmiLayoutApi.resolve(name); station.value = r.workstation; if (!route.params.layoutId && r.layout) layout.value = r.layout } catch { station.value = null } }
    const id = Number(route.params.layoutId)
    if (id) layout.value = layouts.value.find(l => l.id === id) ?? null
    layout.value ??= layouts.value[0] ?? null
    layoutError.value = ''
  } catch (e) {
    layoutError.value = errorMessage(e, tr('consoleView.k9f7b8c'))
  } finally { loading.value = false }
}
watch(() => route.params.layoutId, () => void load())

// 报警横幅与列表：轮询活动报警（3 秒）。
const ALARM_LIMIT = 200
const alarms = ref<AlarmEvent[]>([])
const summary = ref<AlarmSummary | null>(null)
const truncated = ref(false)
const alarmError = ref('')
const ackError = ref('')
const unacked = computed(() => alarms.value.filter(a => !a.ackTs))
const levelRank: Record<string, number> = { Critical: 0, Warning: 1, Info: 2 }
const ranked = (rows: AlarmEvent[]) => [...rows].sort((a, b) => (levelRank[a.level] ?? 9) - (levelRank[b.level] ?? 9) || b.startTs - a.startTs)
const topAlarm = computed(() => ranked(unacked.value.length ? unacked.value : alarms.value)[0] ?? null)
const unackShown = computed(() => summary.value?.unack ?? unacked.value.length)
const activeShown = computed(() => summary.value ? summary.value.unack + summary.value.acked : alarms.value.length)
async function refreshAlarms(): Promise<void> {
  try {
    const [list, sum] = await Promise.all([
      alarmApi.query({ activeOnly: true, priorityFirst: true, limit: ALARM_LIMIT + 1 }),
      alarmApi.summary().catch(() => null)
    ])
    truncated.value = list.length > ALARM_LIMIT
    alarms.value = ranked(truncated.value ? list.slice(0, ALARM_LIMIT) : list)
    if (sum) summary.value = sum
    alarmError.value = ''
  } catch (e) {
    alarmError.value = errorMessage(e, tr('consoleView.alarmError'))
  }
}
async function ack(id: number): Promise<void> {
  ackError.value = ''
  try { await alarmApi.ack(id); await refreshAlarms() } catch (e) { ackError.value = errorMessage(e, tr('consoleView.kc490c1')) }
}

// 区域导航：画面目录点选时在第一个画面区域中打开。
const regionRefs = new Map<string, { navigate: (id: number) => void }>()
function setRegionRef(id: string, el: Element | ComponentPublicInstance | null): void { if (el) regionRefs.set(id, el as unknown as { navigate: (id: number) => void }); else regionRefs.delete(id) }
function navigateMain(id: number): void {
  const first = monitor.value?.regions.find(r => r.kind === 'screen'); const target = first ? regionRefs.get(first.id) : null
  if (target) target.navigate(id); else openFaceplate(id)
}

// 浮动面板
interface Faceplate { key: number; screenId: number; x: number; y: number; z: number }
const faceplates = ref<Faceplate[]>([]); let seq = 0, topZ = 0
function openFaceplate(screenId: number): void {
  if (faceplates.value.length >= 6) { ElMessage.warning(tr('consoleView.k994124')); return }
  faceplates.value.push({ key: ++seq, screenId, x: 120 + (seq % 5) * 30, y: 90 + (seq % 5) * 30, z: ++topZ })
}
function raise(f: Faceplate): void { f.z = ++topZ }
let moving: { f: Faceplate; dx: number; dy: number } | null = null
function startMove(ev: PointerEvent, f: Faceplate): void { moving = { f, dx: ev.clientX - f.x, dy: ev.clientY - f.y } }
function onMove(ev: PointerEvent): void { if (moving) { moving.f.x = Math.max(0, ev.clientX - moving.dx); moving.f.y = Math.max(0, ev.clientY - moving.dy) } }
function onUp(): void { moving = null }

// 新窗口/多显示器
const monitors = ref<MonitorInfo[]>([]), monitorPick = ref(false), pickIndex = ref(0), pendingScreen = ref<number | null>(null), pendingFaceplate = ref<number | null>(null)
async function detach(screenId: number, faceplateKey?: number): Promise<void> {
  monitors.value = await listMonitors()
  pendingScreen.value = screenId
  pendingFaceplate.value = faceplateKey ?? null
  pickIndex.value = Math.min(1, monitors.value.length - 1)
  monitorPick.value = true
}
function confirmDetach(): void {
  if (pendingScreen.value == null) return
  if (!openOnMonitor(`/kiosk/screens/${pendingScreen.value}`, `scada-screen-${pendingScreen.value}`, monitors.value[pickIndex.value])) {
    ElMessage.warning(tr('consoleView.kee31b7'))
    return
  }
  if (pendingFaceplate.value != null) faceplates.value = faceplates.value.filter(f => f.key !== pendingFaceplate.value)
  pendingFaceplate.value = null
  monitorPick.value = false
}
async function openAll(): Promise<void> {
  if (!layout.value) return
  const list = await listMonitors()
  let blocked = 0
  layout.value.document.monitors.forEach((_, i) => {
    if (i === monitorIndex.value) return
    if (!openOnMonitor(`/console/${layout.value!.id}?monitor=${i}`, `scada-console-${layout.value!.id}-${i}`, list.length > 1 ? list[i % list.length] : undefined)) blocked++
  })
  if (blocked > 0) { ElMessage.warning(tr('consoleView.kee31b7')); return }
  if (list.length < 2) ElMessage.info(windowManagementSupported() ? tr('consoleView.k60fbc4') : tr('consoleView.k2e0c15'))
}
function toggleFullscreen(): void { if (document.fullscreenElement) void document.exitFullscreen(); else void document.documentElement.requestFullscreen?.() }

// 操作台报警条 3s 刷新；窗口被最小化/切走时暂停，回到前台补一次。
usePolling(refreshAlarms, 3000)
onMounted(async () => { await load(); await refreshAlarms(); window.addEventListener('pointermove', onMove); window.addEventListener('pointerup', onUp) })
onBeforeUnmount(() => { window.removeEventListener('pointermove', onMove); window.removeEventListener('pointerup', onUp) })
</script>

<style scoped>
.console { height: 100vh; display: flex; flex-direction: column; background: #0d1622; color: #dce6f0; overflow: hidden; }
.console-bar { display: flex; align-items: center; gap: 10px; padding: 6px 10px; background: #101b2d; border-bottom: 1px solid #22344d; flex-wrap: wrap; }
.spacer { flex: 1; }
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
.faceplate { position: fixed; width: 560px; height: 420px; background: #101b2d; border: 1px solid #3b5878; border-radius: 6px; box-shadow: 0 8px 30px #000a; display: flex; flex-direction: column; resize: both; overflow: hidden; }
.faceplate-head { display: flex; align-items: center; gap: 6px; padding: 4px 8px; background: #22344d; cursor: move; font-size: 12px; user-select: none; }
.faceplate-body { flex: 1; min-height: 0; padding: 4px; }
.muted { color: #8796a8; }
.console :deep(.el-empty) { margin-top: 14vh; }
.console :deep(.el-empty__description p) { color: #8796a8; }
.console :deep(.el-empty__image) { opacity: 0.45; }
</style>
