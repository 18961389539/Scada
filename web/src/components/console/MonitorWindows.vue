<template>
  <el-dialog v-model="visible" :title="$t('layoutMonitor.monitorMapping')" class="monitor-windows-dialog" data-test="console-mapping-dialog">
    <el-alert v-if="monitors.length < layout.document.monitors.length || !windowManagementSupported()" :title="$t('layoutMonitor.monitorFallback')" type="warning" :closable="false" show-icon />
    <el-alert v-if="mappingUnavailable" :title="$t('monitorImprovements.mappingUnavailable')" type="warning" :closable="false" show-icon />
    <el-alert v-if="mappingDuplicates" :title="$t('layoutMonitor.monitorDuplicates')" type="warning" :closable="false" show-icon />
    <el-alert v-if="windowError" :title="windowError" type="error" :closable="false" show-icon data-test="console-mapped-window-error" />
    <p class="mapping-help">{{ $t('monitorImprovements.remembered') }}</p>
    <el-form label-position="top">
      <el-form-item v-for="(logical, index) in layout.document.monitors" :key="index" :label="logical.name || $t('consoleView.k47eff4', [index + 1])">
        <div class="monitor-mapping-row">
          <span v-if="index === monitorIndex">{{ $t('layoutMonitor.currentWindow') }}<template v-if="currentPhysical >= 0"> ({{ monitors[currentPhysical]?.label }})</template></span>
          <template v-else>
            <el-select v-model="mappings[index]" :aria-label="logical.name || String(index + 1)" :data-test="`console-monitor-map-${index}`" @change="saveMappings">
              <el-option :value="-1" :label="$t('layoutMonitor.regularWindow')" />
              <el-option v-for="(physical, physicalIndex) in monitors" :key="physicalIndex" :value="physicalIndex" :label="`${physical.label} (${physical.width}×${physical.height})`" />
            </el-select>
            <el-tag :type="statuses[index] === 'open' ? 'success' : 'info'" size="small" :data-test="`console-monitor-status-${index}`">{{ statusLabel(index) }}</el-tag>
            <el-button size="small" :data-test="`console-monitor-reopen-${index}`" @click="openOne(index)">{{ $t('monitorImprovements.openScreen') }}</el-button>
            <el-button size="small" :disabled="statuses[index] !== 'open'" :data-test="`console-monitor-focus-${index}`" @click="focusWindow(index)">{{ $t('monitorImprovements.findWindow') }}</el-button>
          </template>
        </div>
      </el-form-item>
    </el-form>
    <template #footer><el-button @click="visible = false">{{ $t('consoleView.k4d0b46') }}</el-button><el-button type="primary" data-test="console-open-mapped" @click="openAll">{{ $t('consoleView.k65fc81') }}</el-button></template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { HmiLayout } from '../../api/hmiLayouts'
import { usePolling } from '../../composables/usePolling'
import { tr } from '../../i18n'
import { listMonitors, openOnMonitor, windowManagementSupported, type MonitorInfo } from './windowing'

const props = defineProps<{ layout: HmiLayout; monitorIndex: number; stationKey: string }>()
const visible = ref(false), monitors = ref<MonitorInfo[]>([]), mappings = ref<number[]>([]), currentPhysical = ref(-1)
const windowError = ref(''), mappingUnavailable = ref(false)
type WindowStatus = 'open' | 'closed' | 'unknown'
const statuses = ref<Record<number, WindowStatus>>({})
const handles = new Map<number, Window>()
let preparedLayout = '', preparation = 0
const storageKey = computed(() => `scada_monitor_mapping:${props.stationKey}:${props.layout.id}`)
const mappingDuplicates = computed(() => {
  const targets = mappings.value.filter(target => target >= 0)
  return targets.length !== new Set(targets).size
})

function statusLabel(index: number): string {
  return tr(statuses.value[index] === 'open' ? 'monitorImprovements.open' : statuses.value[index] === 'closed' ? 'monitorImprovements.closed' : 'monitorImprovements.unknown')
}
function updateStatuses(): void {
  for (const [index, handle] of handles) {
    try { statuses.value[index] = handle.closed ? 'closed' : 'open' }
    catch { statuses.value[index] = 'unknown'; handles.delete(index) }
  }
}
usePolling(updateStatuses, 1000, { enabled: () => visible.value })
watch(() => `${props.stationKey}:${props.layout.id}`, () => {
  preparation++; visible.value = false; handles.clear(); statuses.value = {}; windowError.value = ''
})

function findPhysical(saved: MonitorInfo): number {
  const exact = monitors.value.findIndex(item => item.label === saved.label && item.width === saved.width && item.height === saved.height && item.left === saved.left && item.top === saved.top)
  if (exact >= 0) return exact
  const matches = monitors.value.map((item, index) => ({ item, index })).filter(({ item }) => item.label === saved.label && item.width === saved.width && item.height === saved.height)
  return matches.length === 1 ? matches[0].index : -1
}
function readMappings(): number[] {
  mappingUnavailable.value = false
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey.value) ?? 'null') as unknown
    if (!Array.isArray(stored)) return []
    return stored.map(value => {
      if (value == null) return -1
      if (typeof value !== 'object' || typeof value.label !== 'string' || !['width', 'height', 'left', 'top'].every(key => Number.isFinite(value[key]))) { mappingUnavailable.value = true; return -1 }
      const index = findPhysical(value as MonitorInfo)
      if (index < 0) mappingUnavailable.value = true
      return index
    })
  } catch { mappingUnavailable.value = true; return [] }
}
function saveMappings(): void {
  try { localStorage.setItem(storageKey.value, JSON.stringify(mappings.value.map(index => monitors.value[index] ?? null))) }
  catch { windowError.value = tr('monitorImprovements.storageFailed') }
}
async function prepare(): Promise<void> {
  const sequence = ++preparation
  const current = `${props.layout.id}:${props.layout.revision}`
  const physical = await listMonitors()
  if (sequence !== preparation || `${props.layout.id}:${props.layout.revision}` !== current) return
  monitors.value = physical
  const centerX = window.screenX + window.outerWidth / 2, centerY = window.screenY + window.outerHeight / 2
  currentPhysical.value = physical.findIndex(item => centerX >= item.left && centerX < item.left + item.width && centerY >= item.top && centerY < item.top + item.height)
  const remembered = readMappings()
  mappings.value = props.layout.document.monitors.map((_, index) => index === props.monitorIndex ? currentPhysical.value : remembered[index] ?? -1)
  preparedLayout = current; windowError.value = ''; updateStatuses(); visible.value = true
}
function isPrepared(): boolean { return preparedLayout === `${props.layout.id}:${props.layout.revision}` }
function openOne(index: number): boolean {
  if (!isPrepared() || index === props.monitorIndex) return false
  saveMappings()
  try {
    const target = monitors.value[mappings.value[index]]
    const opened = openOnMonitor(`/console/${props.layout.id}?monitor=${index}`, `scada-console-${props.layout.id}-${index}`, target)
    if (!opened || opened.closed) { windowError.value = tr('consoleView.kee31b7'); updateStatuses(); return false }
    handles.set(index, opened); statuses.value[index] = 'open'; windowError.value = ''; return true
  } catch { windowError.value = tr('consoleView.kee31b7'); updateStatuses(); return false }
}
function openAll(): void {
  if (!isPrepared()) return
  let blocked = false
  for (let index = 0; index < props.layout.document.monitors.length; index++) {
    if (index !== props.monitorIndex && !openOne(index)) blocked = true
  }
  if (blocked) windowError.value = tr('consoleView.kee31b7')
  else visible.value = false
}
function focusWindow(index: number): void {
  updateStatuses()
  const handle = handles.get(index)
  if (!handle || statuses.value[index] !== 'open') return
  try { handle.focus(); windowError.value = '' }
  catch { statuses.value[index] = 'unknown'; handles.delete(index); windowError.value = tr('monitorImprovements.focusFailed') }
}
defineExpose({ prepare })
</script>

<style scoped>
.mapping-help { font-size: 12px; color: var(--scada-text-muted); margin: 10px 0; }
.monitor-mapping-row { width: 100%; display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.monitor-mapping-row .el-select { min-width: 160px; flex: 1 1 220px; }
:global(.monitor-windows-dialog) { width: min(680px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); overflow: auto; }
</style>
