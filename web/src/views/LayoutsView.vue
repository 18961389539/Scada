<template>
  <div class="page">
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="layouts-load-error" />
    <el-alert v-if="stationsError" :title="stationsError" type="error" :closable="false" show-icon data-test="stations-load-error" />
    <el-alert v-if="screensError" :title="screensError" type="warning" :closable="false" show-icon data-test="layouts-screens-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="layouts-action-error" />
    <div class="panel">
    <div class="toolbar"><h3>{{ $t('layoutsView.k0dd591') }}</h3><span class="spacer" />
      <el-button type="primary" data-test="layout-new" :disabled="!!loadError" @click="edit()">{{ $t('layoutsView.kd6308f') }}</el-button><el-button :loading="loading" @click="load()">{{ $t('layoutsView.k38108e') }}</el-button></div>
    <el-table :data="layouts" size="small" data-test="layouts" :empty-text="loadError && !layouts.length ? loadError : $t('layoutsView.kd0ba6b')">
      <el-table-column prop="name" :label="$t('layoutsView.k1be7ae')" min-width="160" />
      <el-table-column :label="$t('layoutsView.k43b338')" min-width="260"><template #default="{ row }"><span class="tiny">{{ row.document.monitors.map((m: HmiMonitor, i: number) => $t('layoutsView.k6a8e4d', [m.name || i + 1, m.regions.length])).join('；') }}</span></template></el-table-column>
      <el-table-column :label="$t('layoutsView.kc9c775')" width="170"><template #default="{ row }"><span class="tiny">r{{ row.revision }} · {{ row.updatedBy }}</span></template></el-table-column>
      <el-table-column :label="$t('layoutsView.kf3ea6d')" width="230"><template #default="{ row }">
        <el-button size="small" type="primary" @click="router.push(`/console/${row.id}`)">{{ $t('layoutsView.k65fc81') }}</el-button>
        <el-button size="small" @click="edit(row)">{{ $t('layoutsView.ka7f814') }}</el-button><el-button size="small" type="danger" @click="remove(row)">{{ $t('layoutsView.k3755f5') }}</el-button>
      </template></el-table-column>
    </el-table>
  </div>
  <div class="panel" style="margin-top: 16px">
    <div class="toolbar"><h3>{{ $t('layoutsView.k90ec9a') }}</h3><el-tag size="small" :type="local ? 'success' : 'info'" data-test="local-station">{{ $t('layoutsView.kfedb9d', [local || $t('layoutsView.ke6c8c4')]) }}</el-tag>
      <el-button v-if="local" size="small" link @click="setLocal(null)">{{ $t('layoutsView.kb398ea') }}</el-button><span class="spacer" />
      <el-button data-test="station-new" @click="editStation()">{{ $t('layoutsView.kb321d8') }}</el-button></div>
    <p class="page-desc">{{ $t('layoutsView.kc0704a') }}</p>
    <el-table :data="stations" size="small" data-test="stations" :empty-text="(stationsError || loadError) && !stations.length ? (stationsError || loadError) : $t('layoutsView.kfe2078')">
      <el-table-column prop="name" :label="$t('layoutsView.k1be7ae')" min-width="140" />
      <el-table-column prop="description" :label="$t('layoutsView.k26670d')" min-width="160" />
      <el-table-column :label="$t('layoutsView.kb0961e')" min-width="140"><template #default="{ row }">{{ layouts.find(l => l.id === row.layoutId)?.name ?? '-' }}</template></el-table-column>
      <el-table-column :label="$t('layoutsView.k223826')" width="90"><template #default="{ row }"><el-tag size="small" :type="row.allowControl ? 'success' : 'warning'">{{ row.allowControl ? $t('layoutsView.k4c0c0a') : $t('layoutsView.ka18e06') }}</el-tag></template></el-table-column>
      <el-table-column :label="$t('layoutsView.kf3ea6d')" width="230"><template #default="{ row }">
        <el-button size="small" :data-test="`station-local-${row.name}`" @click="setLocal(row.name)">{{ $t('layoutsView.kbffbf3') }}</el-button>
        <el-button size="small" @click="editStation(row)">{{ $t('layoutsView.ka7f814') }}</el-button><el-button size="small" type="danger" @click="removeStation(row)">{{ $t('layoutsView.k3755f5') }}</el-button>
      </template></el-table-column>
    </el-table>
  </div>

  <el-dialog v-model="visible" :title="form.id ? $t('layoutsView.kf7d900') : $t('layoutsView.kd6308f')" class="layout-editor" :before-close="closeLayout" data-test="layout-dialog">
    <el-alert v-if="layoutError" :title="layoutError" type="error" :closable="false" show-icon data-test="layout-save-error" />
    <el-form label-width="80px" size="small" inline @submit.prevent>
      <el-form-item :label="$t('layoutsView.k1be7ae')"><el-input v-model="form.name" maxlength="100" style="width: 220px" data-test="layout-name" /></el-form-item>
      <el-form-item><el-button data-test="layout-preset" @click="replacePreset">{{ $t('layoutsView.k12e496') }}</el-button><el-button data-test="layout-add-monitor" @click="addMonitor()">{{ $t('layoutsView.k968c70') }}</el-button></el-form-item>
    </el-form>
    <el-tabs v-model="monitorTab" type="card">
      <el-tab-pane v-for="(m, mi) in form.document.monitors" :key="mi" :label="m.name || $t('layoutsView.k47eff4', [mi + 1])" :name="String(mi)">
        <div class="toolbar">
          {{ $t('layoutsView.k1be7ae') }} <el-input v-model="m.name" size="small" style="width: 120px" />
          {{ $t('layoutsView.kad3ee6') }} <el-input-number v-model="m.columns" :min="1" :max="24" size="small" /> {{ $t('layoutsView.k2a2b77') }} <el-input-number v-model="m.rows" :min="1" :max="24" size="small" />
          <el-button size="small" :data-test="`layout-add-region-${mi}`" @click="addRegion(m)">{{ $t('layoutsView.k39b1a6') }}</el-button>
          <el-button v-if="form.document.monitors.length > 1" size="small" type="danger" link data-test="layout-remove-monitor" @click="removeMonitor(mi)">{{ $t('layoutsView.k33160a') }}</el-button>
        </div>
        <p class="drag-hint">{{ $t('layoutsView.dragHint') }}</p>
        <el-alert v-if="monitorIssues(m).length" type="warning" :closable="false" show-icon :title="$t('layoutsView.fixIssues', [monitorIssues(m).length])" data-test="layout-region-issues">
          <div v-for="(issue, ii) in monitorIssues(m)" :key="ii" class="tiny">{{ issue }}</div>
        </el-alert>
        <div class="editor">
          <div class="region-properties"><el-table :data="m.regions" size="small" style="min-width: 820px">
            <el-table-column :label="$t('layoutsView.k9f42da')" width="90"><template #default="{ row }"><el-input v-model="row.id" size="small" /></template></el-table-column>
            <el-table-column :label="$t('layoutsView.ke4e46c')" width="120"><template #default="{ row }"><el-select v-model="row.kind" size="small" data-test="region-kind"><el-option v-for="(label, k) in kinds" :key="k" :value="k" :label="label" /></el-select></template></el-table-column>
            <el-table-column :label="$t('layoutsView.kbfffd0')" min-width="150"><template #default="{ row }"><el-select v-if="row.kind === 'screen'" v-model="row.screenId" size="small" filterable data-test="region-screen"><el-option v-for="s in screens" :key="s.id" :value="s.id" :label="s.name" /></el-select></template></el-table-column>
            <el-table-column :label="$t('layoutsView.k748d7d')" width="110"><template #default="{ row }"><el-input v-model="row.title" size="small" /></template></el-table-column>
            <el-table-column :label="$t('layoutsView.k557316')" width="270"><template #default="{ row }">
              <el-input-number v-model="row.row" :min="1" :max="m.rows" size="small" controls-position="right" style="width: 60px" />
              <el-input-number v-model="row.col" :min="1" :max="m.columns" size="small" controls-position="right" style="width: 60px" />
              <el-input-number v-model="row.rowSpan" :min="1" :max="m.rows" size="small" controls-position="right" style="width: 60px" />
              <el-input-number v-model="row.colSpan" :min="1" :max="m.columns" size="small" controls-position="right" style="width: 60px" />
            </template></el-table-column>
            <el-table-column width="50"><template #default="{ $index }"><el-button size="small" link type="danger" @click="m.regions.splice($index, 1)">{{ $t('layoutsView.k38cadf') }}</el-button></template></el-table-column>
          </el-table></div>
          <div class="preview" :style="gridStyle(m)" data-test="layout-preview">
            <div
              v-for="(r, ri) in m.regions"
              :key="r.id"
              :class="[`k-${r.kind}`, { 'is-overlap': regionOverlaps(m, ri), 'is-out': outOfBounds(m, r), 'is-dragging': drag?.region === r }]"
              :style="regionStyle(r)"
              data-test="layout-region"
              :title="$t('layoutsView.dragHint')"
              @pointerdown.stop="startDrag($event, m, r)"
            >
              {{ r.title || kinds[r.kind] }}<small v-if="r.kind === 'screen'">{{ screens.find(s => s.id === r.screenId)?.name }}</small>
              <span class="resize" data-test="layout-region-resize" @pointerdown.stop="startResize($event, m, r)" />
            </div>
          </div>
        </div>
      </el-tab-pane>
    </el-tabs>
    <template #footer><el-button :disabled="saving" @click="closeLayout()">{{ $t('layoutsView.k4d0b46') }}</el-button><el-button type="primary" data-test="layout-save" :loading="saving" :disabled="layoutIssues.length > 0 || !form.name.trim() || !!screensError" @click="save()">{{ $t('layoutsView.kfadf24') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="stationVisible" :title="stationForm.id ? $t('layoutsView.k460078') : $t('layoutsView.kb321d8')" class="station-editor" :before-close="closeStation">
    <el-alert v-if="stationError" :title="stationError" type="error" :closable="false" show-icon data-test="station-save-error" />
    <el-form label-width="90px" size="small">
      <el-form-item :label="$t('layoutsView.k1be7ae')"><el-input v-model="stationForm.name" maxlength="60" :placeholder="$t('layoutsView.k803edb')" data-test="station-name" /></el-form-item>
      <el-form-item :label="$t('layoutsView.k26670d')"><el-input v-model="stationForm.description" /></el-form-item>
      <el-form-item :label="$t('layoutsView.kb0961e')"><el-select v-model="stationForm.layoutId" clearable data-test="station-layout"><el-option v-for="l in layouts" :key="l.id" :value="l.id" :label="l.name" /></el-select></el-form-item>
      <el-form-item :label="$t('layoutsView.k54d7df')"><el-switch v-model="stationForm.allowControl" data-test="station-control" /></el-form-item>
    </el-form>
    <template #footer><el-button :disabled="stationSaving" @click="closeStation()">{{ $t('layoutsView.k4d0b46') }}</el-button><el-button type="primary" :loading="stationSaving" data-test="station-save" @click="saveStation()">{{ $t('layoutsView.kfadf24') }}</el-button></template>
  </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { onBeforeRouteLeave, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { hmiLayoutApi, WORKSTATION_KEY, type HmiLayout, type HmiLayoutDocument, type HmiMonitor, type HmiRegion, type HmiWorkstation, type RegionKind } from '../api/hmiLayouts'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { useActionError } from '../composables/useActionError'
import type { ScreenSummary } from '../screens/model'
import { tr } from '../i18n'

const router = useRouter()
const layouts = ref<HmiLayout[]>([]), stations = ref<HmiWorkstation[]>([]), screens = ref<ScreenSummary[]>([]), loadError = ref(''), layoutError = ref(''), stationError = ref('')
const { actionError, guard: runGuarded } = useActionError()
const stationsError = ref(''), screensError = ref(''), loading = ref(false), saving = ref(false), stationSaving = ref(false)
const kinds: Record<RegionKind, string> = { screen: tr('layoutsView.k8a1840'), alarmBanner: tr('layoutsView.k77911d'), alarmList: tr('layoutsView.k459887'), navigation: tr('layoutsView.k6be0ed') }
const local = ref(localStorage.getItem(WORKSTATION_KEY))

async function load(): Promise<void> {
  if (loading.value) return
  loading.value = true
  const [layoutResult, stationResult, screenResult] = await Promise.allSettled([hmiLayoutApi.list(), hmiLayoutApi.workstations(), screenApi.list()])
  if (layoutResult.status === 'fulfilled') { layouts.value = layoutResult.value; loadError.value = '' }
  else loadError.value = errorMessage(layoutResult.reason, tr('layoutsView.kf6b7a4'))
  if (stationResult.status === 'fulfilled') { stations.value = stationResult.value; stationsError.value = '' }
  else stationsError.value = errorMessage(stationResult.reason, tr('layoutMonitor.stationsFailed'))
  if (screenResult.status === 'fulfilled') { screens.value = screenResult.value; screensError.value = '' }
  else screensError.value = errorMessage(screenResult.reason, tr('layoutMonitor.screensFailed'))
  loading.value = false
}

const visible = ref(false), monitorTab = ref('0')
const form = reactive<{ id?: number; name: string; revision: number; document: HmiLayoutDocument }>({ name: '', revision: 0, document: { monitors: [] } })
const layoutBaseline = ref(''), stationBaseline = ref('')
const layoutDirty = computed(() => visible.value && JSON.stringify(form) !== layoutBaseline.value)
const stationDirty = computed(() => stationVisible.value && JSON.stringify(stationForm) !== stationBaseline.value)
async function confirmDiscard(): Promise<boolean> {
  try { await ElMessageBox.confirm(tr('layoutMonitor.discardChanges'), tr('layoutMonitor.unsaved'), { type: 'warning' }); return true } catch { return false }
}
async function closeLayout(done?: () => void): Promise<void> {
  if (saving.value || (layoutDirty.value && !await confirmDiscard())) return
  endDrag(); if (done) done(); else visible.value = false
}
async function closeStation(done?: () => void): Promise<void> {
  if (stationSaving.value || (stationDirty.value && !await confirmDiscard())) return
  if (done) done(); else stationVisible.value = false
}
onBeforeRouteLeave(async () => !(layoutDirty.value || stationDirty.value) || await confirmDiscard())
function beforeUnload(event: BeforeUnloadEvent): void { if (layoutDirty.value || stationDirty.value) { event.preventDefault(); event.returnValue = '' } }
function edit(row?: HmiLayout): void {
  Object.assign(form, row ? { id: row.id, name: row.name, revision: row.revision, document: JSON.parse(JSON.stringify(row.document)) } : { id: undefined, name: '', revision: 0, document: { monitors: [] } })
  if (!form.document.monitors.length) preset()
  layoutError.value = ''
  layoutBaseline.value = JSON.stringify(form)
  monitorTab.value = '0'; visible.value = true
}
async function replacePreset(): Promise<void> {
  try { await ElMessageBox.confirm(tr('layoutMonitor.replacePreset'), tr('layoutsView.k12e496'), { type: 'warning' }) } catch { return }
  endDrag(); preset(); monitorTab.value = '0'
}
async function removeMonitor(index: number): Promise<void> {
  try { await ElMessageBox.confirm(tr('layoutMonitor.removeMonitor'), tr('layoutsView.k33160a'), { type: 'warning' }) } catch { return }
  endDrag(); form.document.monitors.splice(index, 1); monitorTab.value = '0'
}
function preset(): void {
  const s = screens.value[0]?.id ?? null
  form.document.monitors = [
    { name: tr('layoutsView.kc122e1'), columns: 12, rows: 12, regions: [{ id: 'banner', kind: 'alarmBanner', row: 1, col: 1, rowSpan: 1, colSpan: 12 }, { id: 'nav', kind: 'navigation', row: 2, col: 1, rowSpan: 11, colSpan: 2 }, { id: 'main', kind: 'screen', screenId: s, row: 2, col: 3, rowSpan: 11, colSpan: 10 }] },
    { name: tr('layoutsView.k4f55ee'), columns: 12, rows: 12, regions: [{ id: 'detail', kind: 'screen', screenId: s, row: 1, col: 1, rowSpan: 8, colSpan: 12 }, { id: 'alarms', kind: 'alarmList', row: 9, col: 1, rowSpan: 4, colSpan: 12 }] }
  ]
}
function addMonitor(): void { form.document.monitors.push({ name: '', columns: 12, rows: 12, regions: [{ id: 'main', kind: 'screen', screenId: screens.value[0]?.id ?? null, row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }); monitorTab.value = String(form.document.monitors.length - 1) }
function addRegion(m: HmiMonitor): void { let n = m.regions.length + 1; while (m.regions.some(r => r.id === `r${n}`)) n++; m.regions.push({ id: `r${n}`, kind: 'screen', screenId: screens.value[0]?.id ?? null, row: 1, col: 1, rowSpan: 1, colSpan: 1 }) }

type DragMode = 'move' | 'resize'
interface DragState {
  mode: DragMode
  monitor: HmiMonitor
  region: HmiRegion
  startX: number
  startY: number
  row: number
  col: number
  rowSpan: number
  colSpan: number
  cellW: number
  cellH: number
}
const drag = ref<DragState | null>(null)
const clamp = (value: number, min: number, max: number) => Math.min(Math.max(value, min), max)

function gridStyle(m: HmiMonitor): Record<string, string> {
  return { gridTemplateColumns: `repeat(${m.columns}, 1fr)`, gridTemplateRows: `repeat(${m.rows}, 1fr)` }
}
function regionStyle(r: HmiRegion): Record<string, string> {
  return { gridColumn: `${r.col} / span ${r.colSpan}`, gridRow: `${r.row} / span ${r.rowSpan}` }
}
function overlaps(a: HmiRegion, b: HmiRegion): boolean {
  return a.row < b.row + b.rowSpan && b.row < a.row + a.rowSpan && a.col < b.col + b.colSpan && b.col < a.col + a.colSpan
}
function outOfBounds(m: HmiMonitor, r: HmiRegion): boolean {
  return r.row < 1 || r.col < 1 || r.rowSpan < 1 || r.colSpan < 1 || r.row + r.rowSpan - 1 > m.rows || r.col + r.colSpan - 1 > m.columns
}
function regionOverlaps(m: HmiMonitor, index: number): boolean {
  return m.regions.some((other, i) => i !== index && overlaps(m.regions[index], other))
}
// 与服务端 HmiLayoutValidator 同口径：保存前先提示，避免“摆好了才发现被拒”。
function monitorIssues(m: HmiMonitor): string[] {
  const issues: string[] = []
  const ids = new Set<string>()
  m.regions.forEach((r, i) => {
    if (!r.id?.trim() || ids.has(r.id)) issues.push(tr('layoutsView.regionIdInvalid', [r.id || '?']))
    ids.add(r.id)
    if (outOfBounds(m, r)) issues.push(tr('layoutsView.regionOut', [r.id, m.columns, m.rows]))
    if (r.kind === 'screen' && !r.screenId) issues.push(tr('layoutsView.regionScreen', [r.id]))
    const previous = m.regions.findIndex(other => overlaps(other, r))
    if (previous >= 0 && previous < i) issues.push(tr('layoutsView.regionOverlap', [r.id, m.regions[previous].id]))
  })
  return issues
}
const layoutIssues = computed(() => form.document.monitors.flatMap((m, i) => monitorIssues(m).map(issue => tr('layoutsView.monitorIssue', [m.name || i + 1, issue]))))

function beginDrag(e: PointerEvent, m: HmiMonitor, r: HmiRegion, mode: DragMode): void {
  if (e.button !== 0) return
  endDrag()
  const preview = (e.currentTarget as HTMLElement).closest('.preview') as HTMLElement | null
  if (!preview) return
  const rect = preview.getBoundingClientRect()
  drag.value = {
    mode, monitor: m, region: r,
    startX: e.clientX, startY: e.clientY,
    row: r.row, col: r.col, rowSpan: r.rowSpan, colSpan: r.colSpan,
    cellW: rect.width / Math.max(1, m.columns),
    cellH: rect.height / Math.max(1, m.rows)
  }
  window.addEventListener('pointermove', onDragMove)
  window.addEventListener('pointerup', endDrag, { once: true })
  e.preventDefault()
}
function startDrag(e: PointerEvent, m: HmiMonitor, r: HmiRegion): void { beginDrag(e, m, r, 'move') }
function startResize(e: PointerEvent, m: HmiMonitor, r: HmiRegion): void { beginDrag(e, m, r, 'resize') }
function onDragMove(e: PointerEvent): void {
  const d = drag.value
  if (!d) return
  const dx = Math.round((e.clientX - d.startX) / d.cellW)
  const dy = Math.round((e.clientY - d.startY) / d.cellH)
  if (d.mode === 'move') {
    d.region.col = clamp(d.col + dx, 1, d.monitor.columns - d.region.colSpan + 1)
    d.region.row = clamp(d.row + dy, 1, d.monitor.rows - d.region.rowSpan + 1)
  } else {
    d.region.colSpan = clamp(d.colSpan + dx, 1, d.monitor.columns - d.region.col + 1)
    d.region.rowSpan = clamp(d.rowSpan + dy, 1, d.monitor.rows - d.region.row + 1)
  }
}
function endDrag(): void {
  drag.value = null
  window.removeEventListener('pointermove', onDragMove)
}
onBeforeUnmount(endDrag)
async function save(): Promise<void> {
  if (saving.value || !form.name.trim() || screensError.value) return
  if (layoutIssues.value.length) {
    layoutError.value = tr('layoutsView.fixIssues', [layoutIssues.value.length])
    return
  }
  saving.value = true
  const saved = await runGuarded(() => hmiLayoutApi.save({ ...form, name: form.name.trim(), document: JSON.parse(JSON.stringify(form.document)) }), tr('layoutsView.k40525a'), layoutError)
  saving.value = false
  if (saved) {
    ElMessage.success(tr('layoutsView.kcdfab9'))
    visible.value = false
    await load()
  }
}
async function remove(row: HmiLayout): Promise<void> { try { await ElMessageBox.confirm(tr('layoutsView.kb8c7a3', [row.name]), tr('layoutsView.k3755f5'), { type: 'warning' }) } catch { return } if (await runGuarded(() => hmiLayoutApi.remove(row.id), tr('layoutsView.k72250c'))) await load() }

const stationVisible = ref(false)
const stationForm = reactive<{ id?: number; name: string; description: string | null; layoutId: number | null; allowControl: boolean }>({ name: '', description: null, layoutId: null, allowControl: true })
function editStation(row?: HmiWorkstation): void { Object.assign(stationForm, row ? { ...row } : { id: undefined, name: '', description: null, layoutId: layouts.value[0]?.id ?? null, allowControl: true }); stationBaseline.value = JSON.stringify(stationForm); stationError.value = ''; stationVisible.value = true }
async function saveStation(): Promise<void> {
  if (stationSaving.value) return
  stationForm.name = stationForm.name.trim()
  if (!/^[A-Za-z0-9_.-]{1,60}$/.test(stationForm.name)) { stationError.value = tr('layoutsView.stationNameRule'); return }
  stationSaving.value = true
  const saved = await runGuarded(() => hmiLayoutApi.saveWorkstation({ ...stationForm }), tr('layoutsView.k40525a'), stationError)
  stationSaving.value = false
  if (saved) { ElMessage.success(tr('layoutsView.kcdfab9')); stationVisible.value = false; await load() }
}
async function removeStation(row: HmiWorkstation): Promise<void> { try { await ElMessageBox.confirm(tr('layoutsView.k54ad42', [row.name]), tr('layoutsView.k3755f5'), { type: 'warning' }) } catch { return } if (await runGuarded(() => hmiLayoutApi.removeWorkstation(row.id), tr('layoutsView.k72250c'))) await load() }
function setLocal(name: string | null): void { if (name) localStorage.setItem(WORKSTATION_KEY, name); else localStorage.removeItem(WORKSTATION_KEY); local.value = name; ElMessage.success(name ? tr('layoutsView.k0a2885', [name]) : tr('layoutsView.k7507bc')) }
onMounted(() => { void load(); window.addEventListener('beforeunload', beforeUnload) })
onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload))
</script>

<style scoped>
.editor { display: grid; grid-template-columns: minmax(0, 1fr) 300px; gap: 12px; }
.region-properties { min-width: 0; overflow-x: auto; }
:global(.layout-editor) { width: min(1100px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); margin: 16px auto; display: flex; flex-direction: column; }
:global(.layout-editor .el-dialog__body) { overflow: auto; min-height: 0; }
:global(.station-editor) { width: min(480px, calc(100vw - 24px)); }
@media (max-width: 1000px) {
  .editor { grid-template-columns: minmax(0, 1fr); }
  .editor .preview { grid-row: 1; width: min(100%, 380px); height: auto; aspect-ratio: 3 / 2; }
}
@media (max-width: 600px) {
  :global(.layout-editor .el-form-item) { display: flex; margin-right: 0; }
  :global(.layout-editor .el-form-item__content) { min-width: 0; flex-wrap: wrap; gap: 8px; }
  :global(.layout-editor .el-input) { max-width: 100%; }
}
.preview { width: 300px; height: 200px; display: grid; gap: 2px; background: #0d1622; padding: 4px; border-radius: 4px; flex-shrink: 0; }
.preview > div { background: #22344d; color: #dce6f0; font-size: 11px; padding: 2px 4px; overflow: hidden; border-radius: 2px; position: relative; cursor: move; touch-action: none; user-select: none; }
.preview > div.is-out { outline: 2px solid var(--scada-danger); outline-offset: -2px; }
.preview > div.is-overlap { box-shadow: inset 0 0 0 2px var(--scada-danger); }
.preview > div.is-dragging { opacity: 0.78; }
.preview .resize {
  position: absolute;
  right: 0;
  bottom: 0;
  width: 11px;
  height: 11px;
  cursor: nwse-resize;
  touch-action: none;
  background: linear-gradient(135deg, transparent 48%, #8fb3d9 48%);
}
.drag-hint { margin: 0 0 6px; font-size: 12px; color: var(--scada-text-muted); }
.preview small { display: block; color: #8fb3d9; }
.preview .k-alarmBanner { background: #7a1010; } .preview .k-alarmList { background: #5c3b10; } .preview .k-navigation { background: #2c3e50; }
</style>
