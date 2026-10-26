<template>
  <div class="page">
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="layouts-load-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="layouts-action-error" />
    <div class="panel">
    <div class="toolbar"><h3>{{ $t('layoutsView.k0dd591') }}</h3><span class="spacer" />
      <el-button type="primary" data-test="layout-new" @click="edit()">{{ $t('layoutsView.kd6308f') }}</el-button><el-button @click="load()">{{ $t('layoutsView.k38108e') }}</el-button></div>
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
    <el-table :data="stations" size="small" data-test="stations" :empty-text="loadError && !stations.length ? loadError : $t('layoutsView.kfe2078')">
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

  <el-dialog v-model="visible" :title="form.id ? $t('layoutsView.kf7d900') : $t('layoutsView.kd6308f')" width="1100px" data-test="layout-dialog">
    <el-alert v-if="layoutError" :title="layoutError" type="error" :closable="false" show-icon data-test="layout-save-error" />
    <el-form label-width="80px" size="small" inline @submit.prevent>
      <el-form-item :label="$t('layoutsView.k1be7ae')"><el-input v-model="form.name" maxlength="100" style="width: 220px" data-test="layout-name" /></el-form-item>
      <el-form-item><el-button @click="preset()">{{ $t('layoutsView.k12e496') }}</el-button><el-button data-test="layout-add-monitor" @click="addMonitor()">{{ $t('layoutsView.k968c70') }}</el-button></el-form-item>
    </el-form>
    <el-tabs v-model="monitorTab" type="card">
      <el-tab-pane v-for="(m, mi) in form.document.monitors" :key="mi" :label="m.name || $t('layoutsView.k47eff4', [mi + 1])" :name="String(mi)">
        <div class="toolbar">
          {{ $t('layoutsView.k1be7ae') }} <el-input v-model="m.name" size="small" style="width: 120px" />
          {{ $t('layoutsView.kad3ee6') }} <el-input-number v-model="m.columns" :min="1" :max="24" size="small" /> {{ $t('layoutsView.k2a2b77') }} <el-input-number v-model="m.rows" :min="1" :max="24" size="small" />
          <el-button size="small" :data-test="`layout-add-region-${mi}`" @click="addRegion(m)">{{ $t('layoutsView.k39b1a6') }}</el-button>
          <el-button v-if="form.document.monitors.length > 1" size="small" type="danger" link @click="form.document.monitors.splice(mi, 1); monitorTab = '0'">{{ $t('layoutsView.k33160a') }}</el-button>
        </div>
        <div class="editor">
          <el-table :data="m.regions" size="small" style="flex: 1">
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
          </el-table>
          <div class="preview" :style="{ gridTemplateColumns: `repeat(${m.columns}, 1fr)`, gridTemplateRows: `repeat(${m.rows}, 1fr)` }">
            <div v-for="r in m.regions" :key="r.id" :class="`k-${r.kind}`" :style="{ gridColumn: `${r.col} / span ${r.colSpan}`, gridRow: `${r.row} / span ${r.rowSpan}` }">{{ r.title || kinds[r.kind] }}<small v-if="r.kind === 'screen'">{{ screens.find(s => s.id === r.screenId)?.name }}</small></div>
          </div>
        </div>
      </el-tab-pane>
    </el-tabs>
    <template #footer><el-button @click="visible = false">{{ $t('layoutsView.k4d0b46') }}</el-button><el-button type="primary" data-test="layout-save" @click="save()">{{ $t('layoutsView.kfadf24') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="stationVisible" :title="stationForm.id ? $t('layoutsView.k460078') : $t('layoutsView.kb321d8')" width="480px">
    <el-alert v-if="stationError" :title="stationError" type="error" :closable="false" show-icon data-test="station-save-error" />
    <el-form label-width="90px" size="small">
      <el-form-item :label="$t('layoutsView.k1be7ae')"><el-input v-model="stationForm.name" maxlength="60" :placeholder="$t('layoutsView.k803edb')" data-test="station-name" /></el-form-item>
      <el-form-item :label="$t('layoutsView.k26670d')"><el-input v-model="stationForm.description" /></el-form-item>
      <el-form-item :label="$t('layoutsView.kb0961e')"><el-select v-model="stationForm.layoutId" clearable data-test="station-layout"><el-option v-for="l in layouts" :key="l.id" :value="l.id" :label="l.name" /></el-select></el-form-item>
      <el-form-item :label="$t('layoutsView.k54d7df')"><el-switch v-model="stationForm.allowControl" data-test="station-control" /></el-form-item>
    </el-form>
    <template #footer><el-button @click="stationVisible = false">{{ $t('layoutsView.k4d0b46') }}</el-button><el-button type="primary" data-test="station-save" @click="saveStation()">{{ $t('layoutsView.kfadf24') }}</el-button></template>
  </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { hmiLayoutApi, WORKSTATION_KEY, type HmiLayout, type HmiLayoutDocument, type HmiMonitor, type HmiWorkstation, type RegionKind } from '../api/hmiLayouts'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import type { ScreenSummary } from '../screens/model'
import { tr } from '../i18n'

const router = useRouter()
const layouts = ref<HmiLayout[]>([]), stations = ref<HmiWorkstation[]>([]), screens = ref<ScreenSummary[]>([]), loadError = ref(''), actionError = ref(''), layoutError = ref(''), stationError = ref('')
const kinds: Record<RegionKind, string> = { screen: tr('layoutsView.k8a1840'), alarmBanner: tr('layoutsView.k77911d'), alarmList: tr('layoutsView.k459887'), navigation: tr('layoutsView.k6be0ed') }
const local = ref(localStorage.getItem(WORKSTATION_KEY))
async function guard(fn: () => Promise<unknown>, target: typeof actionError, fallback: string): Promise<boolean> {
  target.value = ''
  try { await fn(); target.value = ''; return true } catch (e) { target.value = errorMessage(e, fallback); return false }
}
async function load(): Promise<void> {
  try {
    ;[layouts.value, stations.value, screens.value] = await Promise.all([hmiLayoutApi.list(), hmiLayoutApi.workstations(), screenApi.list()])
    loadError.value = ''
  } catch (e) {
    loadError.value = errorMessage(e, tr('layoutsView.kf6b7a4'))
  }
}

const visible = ref(false), monitorTab = ref('0')
const form = reactive<{ id?: number; name: string; revision: number; document: HmiLayoutDocument }>({ name: '', revision: 0, document: { monitors: [] } })
function edit(row?: HmiLayout): void {
  Object.assign(form, row ? { id: row.id, name: row.name, revision: row.revision, document: JSON.parse(JSON.stringify(row.document)) } : { id: undefined, name: '', revision: 0, document: { monitors: [] } })
  if (!form.document.monitors.length) preset()
  layoutError.value = ''
  monitorTab.value = '0'; visible.value = true
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
async function save(): Promise<void> { if (await guard(() => hmiLayoutApi.save({ ...form }), layoutError, tr('layoutsView.k40525a'))) { ElMessage.success(tr('layoutsView.kcdfab9')); visible.value = false; await load() } }
async function remove(row: HmiLayout): Promise<void> { try { await ElMessageBox.confirm(tr('layoutsView.kb8c7a3', [row.name]), tr('layoutsView.k3755f5'), { type: 'warning' }) } catch { return } if (await guard(() => hmiLayoutApi.remove(row.id), actionError, tr('layoutsView.k72250c'))) await load() }

const stationVisible = ref(false)
const stationForm = reactive<{ id?: number; name: string; description: string | null; layoutId: number | null; allowControl: boolean }>({ name: '', description: null, layoutId: null, allowControl: true })
function editStation(row?: HmiWorkstation): void { Object.assign(stationForm, row ? { ...row } : { id: undefined, name: '', description: null, layoutId: layouts.value[0]?.id ?? null, allowControl: true }); stationError.value = ''; stationVisible.value = true }
async function saveStation(): Promise<void> {
  stationForm.name = stationForm.name.trim()
  if (!/^[A-Za-z0-9_.-]{1,60}$/.test(stationForm.name)) { stationError.value = tr('layoutsView.stationNameRule'); return }
  if (await guard(() => hmiLayoutApi.saveWorkstation({ ...stationForm }), stationError, tr('layoutsView.k40525a'))) { ElMessage.success(tr('layoutsView.kcdfab9')); stationVisible.value = false; await load() }
}
async function removeStation(row: HmiWorkstation): Promise<void> { try { await ElMessageBox.confirm(tr('layoutsView.k54ad42', [row.name]), tr('layoutsView.k3755f5'), { type: 'warning' }) } catch { return } if (await guard(() => hmiLayoutApi.removeWorkstation(row.id), actionError, tr('layoutsView.k72250c'))) await load() }
function setLocal(name: string | null): void { if (name) localStorage.setItem(WORKSTATION_KEY, name); else localStorage.removeItem(WORKSTATION_KEY); local.value = name; ElMessage.success(name ? tr('layoutsView.k0a2885', [name]) : tr('layoutsView.k7507bc')) }
onMounted(load)
</script>

<style scoped>
.editor { display: flex; gap: 12px; }
.preview { width: 300px; height: 200px; display: grid; gap: 2px; background: #0d1622; padding: 4px; border-radius: 4px; flex-shrink: 0; }
.preview > div { background: #22344d; color: #dce6f0; font-size: 11px; padding: 2px 4px; overflow: hidden; border-radius: 2px; }
.preview small { display: block; color: #8fb3d9; }
.preview .k-alarmBanner { background: #7a1010; } .preview .k-alarmList { background: #5c3b10; } .preview .k-navigation { background: #2c3e50; }
</style>
