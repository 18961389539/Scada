<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>{{ $t('calcJobsView.k8efdcf') }}</strong>
      <el-input v-model="search" :placeholder="$t('calcJobsView.k5e33fd')" clearable style="width: 220px" data-test="calc-job-search" /><span class="spacer" />
      <el-switch v-model="automatic" :active-text="$t('calcJobsView.kd8fee6')" />
      <el-button :loading="loading" @click="refresh()">{{ $t('calcJobsView.k38108e') }}</el-button>
      <el-button v-if="canConfigure" type="primary" data-test="calc-job-new" @click="open(null)">{{ $t('calcJobsView.k26083b') }}</el-button>
    </div>
    <el-alert v-if="readError" :title="readError" type="error" :closable="false" />
    <p class="tiny muted">{{ $t('calcJobsView.k42436e') }}</p>
    <el-table :data="filtered" v-loading="loading" row-key="job.id" :empty-text="$t('calcJobsView.kad5cdc')" data-test="calc-job-table">
      <el-table-column :label="$t('calcJobsView.k3172b3')" min-width="170"><template #default="{ row }"><strong>#{{ row.job.id }} · {{ row.job.name }}</strong>
        <div class="tiny muted">{{ $t('calcJobsView.k22d559', [kindLabels[row.job.kind as CalcJobKind], areaName(row.job.areaId), row.job.revision]) }}</div></template></el-table-column>
      <el-table-column :label="$t('calcJobsView.k9ac85a')" min-width="160"><template #default="{ row }">
        <span v-if="row.job.kind !== 'Script'">#{{ row.job.inputTagId }} {{ tagName(row.job.inputTagId) }}</span><span v-else>{{ $t('calcJobsView.k8280de', [row.job.readTags || $t('calcJobsView.k720777'), row.job.writeTags || $t('calcJobsView.k720777')]) }}</span>
        <div class="tiny muted">{{ $t('calcJobsView.k1ccc51', [row.job.enabled ? $t('calcJobsView.kd4e9ca') : $t('calcJobsView.kd989e5'), row.job.intervalMs, resetLabels[row.job.resetPeriod as CalcResetPeriod]]) }}</div></template></el-table-column>
      <el-table-column :label="$t('calcJobsView.kb5259e')" min-width="260"><template #default="{ row }">
        <div v-for="output in row.outputs" :key="output.tagId" class="tiny"><code>{{ output.name }}</code> = <strong>{{ value(row, output.name) }}</strong> <span class="muted">#{{ output.tagId }}</span></div>
        <el-tag v-if="row.status?.values?.maintenanceDue === 1" type="danger" size="small">{{ $t('calcJobsView.k1f37f2') }}</el-tag></template></el-table-column>
      <el-table-column :label="$t('calcJobsView.kea815f')" min-width="200"><template #default="{ row }"><div class="tiny">{{ row.status?.periodKey || '--' }}</div>
        <div class="tiny" :class="row.status?.lastError ? 'danger' : 'muted'">{{ row.status?.lastError || row.status?.lastEvent || '' }}</div>
        <div v-if="row.status?.lastScriptMs != null" class="tiny muted">{{ $t('calcJobsView.k31d47d', [row.status.lastScriptSteps, row.status.lastScriptMs.toFixed(2)]) }}</div></template></el-table-column>
      <el-table-column :label="$t('calcJobsView.kf3ea6d')" width="230"><template #default="{ row }">
        <el-button v-if="canConfigure" link type="primary" @click="open(row.job)">{{ $t('calcJobsView.ka7f814') }}</el-button>
        <el-button link @click="showSnapshots(row.job)">{{ $t('calcJobsView.k83caf1') }}</el-button>
        <el-button v-if="canControl && row.job.kind !== 'Script'" link type="warning" @click="reset(row.job)">{{ $t('calcJobsView.k52a5c3') }}</el-button>
        <el-button v-if="canControl && row.job.kind === 'RunHours'" link type="success" @click="maintenance(row.job)">{{ $t('calcJobsView.k33a9d1') }}</el-button>
        <el-button v-if="canConfigure" link type="danger" @click="remove(row.job)">{{ $t('calcJobsView.k3755f5') }}</el-button></template></el-table-column>
    </el-table>
  </div>

  <el-dialog v-model="editor" :title="editing === null ? $t('calcJobsView.k26083b') : $t('calcJobsView.keba89c')" width="860px" :close-on-click-modal="false">
    <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" data-test="calc-job-error" />
    <el-form label-width="130px" :disabled="saving" @submit.prevent>
      <el-row :gutter="12">
        <el-col :span="12"><el-form-item :label="$t('calcJobsView.k1be7ae')" required><el-input v-model="form.name" maxlength="80" data-test="calc-job-name" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('calcJobsView.ke4e46c')"><el-select v-model="form.kind" :disabled="editing !== null" data-test="calc-job-kind">
          <el-option v-for="(label, key) in kindLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('calcJobsView.k17fc93')" required><el-select v-model="form.areaId" data-test="calc-job-area"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('calcJobsView.kd4e9ca')"><el-switch v-model="form.enabled" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('calcJobsView.kb7b729')" label-width="80px"><el-input-number v-model="form.intervalMs" :min="200" :max="3600000" :step="500" controls-position="right" /></el-form-item></el-col>
      </el-row>
      <template v-if="form.kind !== 'Script'">
        <el-form-item :label="inputLabel" required><el-select v-model="form.inputTagId" filterable style="width: 100%" data-test="calc-job-input">
          <el-option v-for="t in tags" :key="t.id" :value="t.id" :label="`#${t.id} · ${t.name}${t.unit ? ` (${t.unit})` : ''}`" /></el-select></el-form-item>
        <el-row :gutter="12">
          <el-col v-if="form.kind === 'Accumulator'" :span="12"><el-form-item :label="$t('calcJobsView.k4c8020')"><el-radio-group v-model="form.mode"><el-radio-button value="Pulse">{{ $t('calcJobsView.k43dbfd') }}</el-radio-button><el-radio-button value="Flow">{{ $t('calcJobsView.k43fc5e') }}</el-radio-button></el-radio-group></el-form-item></el-col>
          <el-col v-if="form.kind !== 'RunHours'" :span="12"><el-form-item :label="form.kind === 'Accumulator' && form.mode === 'Pulse' ? $t('calcJobsView.k1b30a5') : $t('calcJobsView.k70993e')"><el-input-number v-model="form.factor" :min="0" :step="0.1" :precision="8" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'Accumulator' && form.mode === 'Pulse'" :span="12"><el-form-item :label="$t('calcJobsView.k855788')"><el-input-number v-model="form.counterMax" :min="1" :placeholder="$t('calcJobsView.k25c5d5')" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'Accumulator' && form.mode === 'Pulse'" :span="12"><el-form-item :label="$t('calcJobsView.kb486bb')"><el-input-number v-model="form.maxDelta" :min="0" :placeholder="$t('calcJobsView.kca6ffc')" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'RunHours'" :span="12"><el-form-item :label="$t('calcJobsView.kff9e08')"><el-input-number v-model="form.runThreshold" :step="0.5" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'RunHours'" :span="12"><el-form-item :label="$t('calcJobsView.k58aab1')"><el-input-number v-model="form.maintenanceHours" :min="0" :placeholder="$t('calcJobsView.k2712e9')" controls-position="right" data-test="calc-job-maintenance" /></el-form-item></el-col>
          <el-col v-if="!(form.kind === 'Accumulator' && form.mode === 'Pulse')" :span="12"><el-form-item :label="$t('calcJobsView.kaf3bcd')"><el-select v-model="form.badQuality">
            <el-option value="Hold" :label="$t('calcJobsView.k2057e7')" /><el-option value="Skip" :label="$t('calcJobsView.k8ad6f0')" /><el-option value="Zero" :label="$t('calcJobsView.k9c7de4')" /></el-select></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calcJobsView.kf0d705')"><el-input-number v-model="form.maxGapSec" :min="1" :max="86400" controls-position="right" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calcJobsView.k1bb3f8')"><el-select v-model="form.resetPeriod" data-test="calc-job-reset"><el-option v-for="(label, key) in resetLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item></el-col>
          <el-col v-if="form.resetPeriod === 'Shift'" :span="12"><el-form-item :label="$t('calcJobsView.kb41bd4')"><el-input v-model="form.shiftStarts" placeholder="08:00,20:00" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calcJobsView.k8ec8c8')"><el-input v-model="form.unit" maxlength="32" :placeholder="$t('calcJobsView.kbc0598')" /></el-form-item></el-col>
        </el-row>
        <el-form-item v-if="form.kind === 'Energy'" :label="$t('calcJobsView.kdef2e5')">
          <div style="width: 100%">
            <div v-for="(p, i) in tou" :key="i" class="tou-row">
              <el-select v-model="p.band" style="width: 110px"><el-option v-for="(label, key) in bandLabels" :key="key" :value="key" :label="label" /></el-select>
              <el-input v-model="p.start" style="width: 90px" placeholder="08:00" /> {{ $t('calcJobsView.k43401e') }} <el-input v-model="p.end" style="width: 90px" placeholder="11:00" />
              <el-button link type="danger" @click="tou.splice(i, 1)">{{ $t('calcJobsView.k2f752c') }}</el-button>
            </div>
            <el-button size="small" data-test="calc-job-add-tou" @click="tou.push({ band: 'peak', start: '08:00', end: '11:00' })">{{ $t('calcJobsView.k0c4fd5') }}</el-button>
            <div class="tiny muted">{{ $t('calcJobsView.k91cf8b') }}</div>
          </div>
        </el-form-item>
        <p class="tiny muted">{{ help[form.kind] }}</p>
      </template>
      <template v-else>
        <el-row :gutter="12">
          <el-col :span="12"><el-form-item :label="$t('calcJobsView.kb9c22b')"><el-input v-model="form.readTags" :placeholder="$t('calcJobsView.k6f16c5')" data-test="calc-job-read" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calcJobsView.k2f2422')"><el-input v-model="form.writeTags" :placeholder="$t('calcJobsView.k8d64bf')" data-test="calc-job-write" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calcJobsView.k053a91')"><el-input-number v-model="form.maxGapSec" :min="1" :max="86400" controls-position="right" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calcJobsView.k0f1b6b')"><el-input v-model="form.unit" maxlength="32" /></el-form-item></el-col>
        </el-row>
        <el-form-item :label="$t('calcJobsView.k7fbccb')" required><el-input v-model="form.script" type="textarea" :rows="8" maxlength="16384" show-word-limit class="mono" data-test="calc-job-script"
          placeholder="$avg = (#1 + #2) / 2;&#10;@avg = $avg;&#10;#9 = if($avg > 80, 1, 0)" /></el-form-item>
        <p class="tiny muted">{{ $t('calcJobsView.k29d1b6') }}<code>{{ $t('calcJobsView.k578abf') }}</code>、<code>{{ $t('calcJobsView.k72ff89') }}</code>{{ $t('calcJobsView.k800829') }}<code>{{ $t('calcJobsView.kbb6bda') }}</code>{{ $t('calcJobsView.k480763', [limits]) }}</p>
      </template>
    </el-form>
    <template #footer><el-button :disabled="saving" @click="editor = false">{{ $t('calcJobsView.k4d0b46') }}</el-button><el-button type="primary" :loading="saving" data-test="calc-job-save" @click="save">{{ $t('calcJobsView.kfadf24') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="snapshotDialog" :title="$t('calcJobsView.k96e23b', [snapshotJob?.name ?? ''])" width="760px">
    <el-table :data="snapshots" :empty-text="$t('calcJobsView.kc33c8e')" max-height="420">
      <el-table-column :label="$t('calcJobsView.k89b4aa')" width="170"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
      <el-table-column prop="reason" :label="$t('calcJobsView.k1ff9c3')" width="100" />
      <el-table-column prop="periodKey" :label="$t('calcJobsView.k8a12b1')" width="150" />
      <el-table-column :label="$t('calcJobsView.kf7a3d0')"><template #default="{ row }"><code class="tiny">{{ snapshotText(row.valuesJson) }}</code></template></el-table-column>
    </el-table>
  </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { calcJobApi, type CalcJob, type CalcJobInfo, type CalcJobInput, type CalcJobKind, type CalcJobSnapshot, type CalcResetPeriod } from '../api/calcJobs'
import { areaApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import { formatTime, formatValue, hasAny } from '../utils'
import { useAuthStore } from '../stores/auth'
import type { Area, Tag } from '../types'
import { tr } from '../i18n'

const auth = useAuthStore()
const canConfigure = computed(() => hasAny(auth.user, 'Configure'))
const canControl = computed(() => hasAny(auth.user, 'Control'))
const kindLabels: Record<CalcJobKind, string> = { Accumulator: tr('calcJobsView.kd0029b'), RunHours: tr('calcJobsView.kb722d8'), Energy: tr('calcJobsView.kd3aac1'), Script: tr('calcJobsView.k708f3a') }
const resetLabels: Record<CalcResetPeriod, string> = { None: tr('calcJobsView.k8146b1'), Shift: tr('calcJobsView.k204b6f'), Day: tr('calcJobsView.k5bbcdd'), Month: tr('calcJobsView.k7bae1e') }
const bandLabels: Record<string, string> = { sharp: tr('calcJobsView.ka8477c'), peak: tr('calcJobsView.k9a4761'), flat: tr('calcJobsView.k3c9aba'), valley: tr('calcJobsView.kce0b6d') }
const help: Record<CalcJobKind, string> = {
  Accumulator: tr('calcJobsView.k2c1e99'),
  RunHours: tr('calcJobsView.k39124b'),
  Energy: tr('calcJobsView.k5d90b7'),
  Script: ''
}
const limits = tr('calcJobsView.k370020')
const rows = ref<CalcJobInfo[]>([]), tags = ref<Tag[]>([]), areas = ref<Area[]>([]), search = ref(''), automatic = ref(true)
const loading = ref(false), saving = ref(false), readError = ref(''), mutationError = ref(''), editor = ref(false), editing = ref<number | null>(null)
const snapshotDialog = ref(false), snapshots = ref<CalcJobSnapshot[]>([]), snapshotJob = ref<CalcJob | null>(null)
const tou = ref<{ band: string; start: string; end: string }[]>([])
const empty = (): CalcJobInput => ({ name: '', kind: 'Accumulator', enabled: true, areaId: 1, intervalMs: 1000, revision: 0, inputTagId: null, mode: 'Pulse', factor: 1, counterMax: null, maxDelta: null,
  badQuality: 'Hold', maxGapSec: 60, runThreshold: 0.5, maintenanceHours: null, resetPeriod: 'Day', shiftStarts: '08:00,20:00', touJson: '[]', script: '', readTags: '', writeTags: '', unit: '' })
const form = reactive<CalcJobInput>(empty())
const inputLabel = computed(() => form.kind === 'RunHours' ? tr('calcJobsView.k0def07') : form.kind === 'Energy' ? tr('calcJobsView.kd92f80') : form.mode === 'Pulse' ? tr('calcJobsView.kb3e788') : tr('calcJobsView.kb93fca'))
const filtered = computed(() => rows.value.filter(x => `${x.job.id} ${x.job.name} ${kindLabels[x.job.kind]}`.toLowerCase().includes(search.value.trim().toLowerCase())))
const areaName = (id: number) => areas.value.find(a => a.id === id)?.name ?? tr('calcJobsView.k90f301', [id])
const tagName = (id: number | null) => tags.value.find(t => t.id === id)?.name ?? ''
const value = (row: CalcJobInfo, name: string) => { const v = row.status?.values?.[name]; return v === undefined ? '--' : formatValue(v) }
const snapshotText = (json: string) => { try { return Object.entries(JSON.parse(json) as Record<string, number>).map(([k, v]) => `${k}=${formatValue(v)}`).join('  ') } catch { return json } }

let timer: number | undefined, alive = true
let refreshing = false
// background=true 表示 3 秒定时轮询：不点亮全表 loading 遮罩，也不与用户手动刷新抢占
async function refresh(background = false): Promise<void> {
  if (background && refreshing) return
  refreshing = true
  if (!background) loading.value = true
  try {
    const [list, tagList, areaList] = await Promise.all([calcJobApi.list(), tagApi.list(), areaApi.list().catch(() => areas.value)])
    if (!alive) return
    rows.value = list; tags.value = tagList; areas.value = areaList; readError.value = ''
  } catch (e) { readError.value = errorMessage(e, tr('calcJobsView.k38b211')) }
  finally { refreshing = false; if (!background) loading.value = false }
}
function open(job: CalcJob | null): void {
  editing.value = job?.id ?? null
  const unit = job ? (tags.value.find(t => t.jobId === job.id && !['hours', 'periodHours', 'sinceMaintenance', 'starts', 'periodStarts', 'maintenanceDue'].includes(t.jobOutput ?? ''))?.unit ?? '') : ''
  Object.assign(form, job ? { ...empty(), ...job, unit } : { ...empty(), areaId: areas.value[0]?.id ?? 1 })
  try { tou.value = JSON.parse(form.touJson || '[]') } catch { tou.value = [] }
  mutationError.value = ''; editor.value = true
}
async function save(): Promise<void> {
  saving.value = true; mutationError.value = ''
  try {
    form.touJson = JSON.stringify(tou.value)
    await calcJobApi.save(editing.value, { ...form })
    editor.value = false; ElMessage.success(tr('calcJobsView.kcdfab9')); await refresh()
  } catch (e) { mutationError.value = errorMessage(e, tr('calcJobsView.k40525a')) }
  finally { saving.value = false }
}
async function remove(job: CalcJob): Promise<void> {
  try { await ElMessageBox.confirm(tr('calcJobsView.kdbecb4', [job.name]), tr('calcJobsView.k3c06ab'), { type: 'warning' }) } catch { return }
  try { await calcJobApi.remove(job); ElMessage.success(tr('calcJobsView.kfb5fe1')); await refresh() } catch (e) { ElMessage.error(errorMessage(e, tr('calcJobsView.k72250c'))) }
}
async function reset(job: CalcJob): Promise<void> {
  try { await ElMessageBox.confirm(tr('calcJobsView.k932c29', [job.name]), tr('calcJobsView.k193804'), { type: 'warning' }) } catch { return }
  try { await calcJobApi.reset(job.id); ElMessage.success(tr('calcJobsView.k960e8b')); await refresh() } catch (e) { ElMessage.error(errorMessage(e, tr('calcJobsView.kff5f6b'))) }
}
async function maintenance(job: CalcJob): Promise<void> {
  try { await ElMessageBox.confirm(tr('calcJobsView.k1b5f0e', [job.name]), tr('calcJobsView.k33a9d1'), { type: 'info' }) } catch { return }
  try { await calcJobApi.maintenanceDone(job.id); ElMessage.success(tr('calcJobsView.k53002a')); await refresh() } catch (e) { ElMessage.error(errorMessage(e, tr('calcJobsView.k09e424'))) }
}
async function showSnapshots(job: CalcJob): Promise<void> {
  snapshotJob.value = job; snapshots.value = []; snapshotDialog.value = true
  try { snapshots.value = await calcJobApi.snapshots(job.id) } catch (e) { ElMessage.error(errorMessage(e, tr('calcJobsView.k2dd7e3'))) }
}
function schedule(): void { window.clearInterval(timer); if (automatic.value) timer = window.setInterval(() => { if (!editor.value && !loading.value) void refresh(true) }, 3000) }
watch(automatic, schedule)
onMounted(async () => { await refresh(); schedule() })
onBeforeUnmount(() => { alive = false; window.clearInterval(timer) })
</script>
<style scoped>
.tou-row { display: flex; gap: 8px; align-items: center; margin-bottom: 6px; }
.danger { color: var(--el-color-danger); }
.mono :deep(textarea) { font-family: ui-monospace, Consolas, monospace; }
</style>
