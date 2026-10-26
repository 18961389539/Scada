<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><span class="spacer" />
      <el-button data-test="project-export" @click="exportProject()">{{ $t('projectView.kd5d9e8') }}</el-button>
    </div>
    <p class="page-desc">{{ $t('projectView.kfbbdd8') }}</p>
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="project-action-error" />
    <el-tabs v-model="tab">
      <el-tab-pane :label="$t('projectView.kc9dcd9')" name="versions">
        <div class="toolbar">
          <el-input v-model="note" :placeholder="$t('projectView.k72fa80')" style="width: 280px" maxlength="200" data-test="project-note" />
          <el-button type="primary" :loading="busy" data-test="project-snapshot" @click="snapshot()">{{ $t('projectView.kba8bbb') }}</el-button>
          <span class="spacer" />
          <span class="tiny">{{ $t('projectView.kea9402') }}</span>
          <el-select v-model="compareFrom" style="width: 160px" data-test="project-from"><el-option v-for="v in versions" :key="v.id" :value="String(v.id)" :label="`#${v.id} ${v.note}`" /></el-select>
          <span class="tiny">→</span>
          <el-select v-model="compareTo" style="width: 160px" data-test="project-to"><el-option value="current" :label="$t('projectView.kfe6728')" /><el-option v-for="v in versions" :key="v.id" :value="String(v.id)" :label="`#${v.id} ${v.note}`" /></el-select>
          <el-button :disabled="!compareFrom" data-test="project-compare" @click="compare()">{{ $t('projectView.kea9402') }}</el-button>
        </div>
        <el-alert v-if="readError" :title="readError" type="error" :closable="false" show-icon data-test="project-read-error" />
        <el-alert v-if="compareError" :title="compareError" type="error" :closable="false" show-icon data-test="project-compare-error" />
        <el-table :data="versions" row-key="id" size="small" :empty-text="readError && !versions.length ? readError : $t('projectView.kd5f899')" data-test="project-versions" max-height="360">
          <el-table-column :label="$t('projectView.k989d1a')" width="80"><template #default="{ row }">#{{ row.id }}</template></el-table-column>
          <el-table-column :label="$t('projectView.ke4e46c')" width="110"><template #default="{ row }"><el-tag size="small" :type="row.kind === 'manual' ? 'primary' : 'info'">{{ kindLabels[row.kind] ?? row.kind }}</el-tag></template></el-table-column>
          <el-table-column prop="note" :label="$t('projectView.k26670d')" min-width="200" />
          <el-table-column :label="$t('projectView.kfcbd09')" min-width="170"><template #default="{ row }"><span class="tiny">{{ formatTime(row.createdTs) }} · {{ row.createdBy }}</span></template></el-table-column>
          <el-table-column :label="$t('projectView.k163aec')" min-width="200"><template #default="{ row }"><span class="tiny muted">{{ summaryText(row.summary) }}</span></template></el-table-column>
          <el-table-column :label="$t('projectView.kf3ea6d')" width="160"><template #default="{ row }">
            <el-button link type="warning" :data-test="`project-rollback-${row.id}`" @click="previewRollback(row as ConfigVersion)">{{ $t('projectView.kb65c5d') }}</el-button>
            <el-button v-if="globalConfigure" link @click="downloadVersion(row as ConfigVersion)">{{ $t('projectView.k2b9d01') }}</el-button></template></el-table-column>
        </el-table>
        <template v-if="diff">
          <h4>{{ $t('projectView.k2e757d', [diffTitle]) }}</h4>
          <plan-table :plan="diff" />
        </template>
      </el-tab-pane>
      <el-tab-pane :label="$t('projectView.k15e669')" name="import">
        <div class="toolbar">
          <input ref="fileInput" type="file" accept=".json,application/json" data-test="project-file" @change="loadFile" />
          <span class="tiny">{{ $t('projectView.k603573') }}</span>
          <el-select v-model="resolution" style="width: 120px" data-test="project-resolution" @change="previewImport()">
            <el-option value="Skip" :label="$t('projectView.k31a985')" /><el-option value="Overwrite" :label="$t('projectView.ka26056')" /><el-option value="Rename" :label="$t('projectView.kfd87e2')" /></el-select>
          <el-button :disabled="!pkg" :loading="busy" data-test="project-preview" @click="previewImport()">{{ $t('projectView.kc284ba') }}</el-button>
          <el-button type="primary" :disabled="!canApply" :loading="busy" data-test="project-apply" @click="applyImport()">{{ $t('projectView.kbc83af') }}</el-button>
        </div>
        <el-alert v-if="importError" :title="importError" type="error" :closable="false" show-icon data-test="project-import-error" />
        <p v-if="pkg" class="tiny muted">{{ $t('projectView.k6e5034', [fileName, formatTime(pkg.exportedTs), pkg.exportedBy, pkg.partial ? $t('projectView.k1dff7e') : '']) }}</p>
        <plan-table v-if="importPlan" :plan="importPlan" :resolutions="resolutions" @resolve="resolveOne" />
      </el-tab-pane>
    </el-tabs>
  </div>

  <el-dialog v-model="rollbackDialog" :title="$t('projectView.k55c3a4', [rollbackTarget?.id])" width="1000px">
    <p class="tiny">{{ $t('projectView.k5c28f2') }}</p>
    <el-alert v-if="rollbackError" :title="rollbackError" type="error" :closable="false" show-icon data-test="project-rollback-error" />
    <plan-table v-if="rollbackPlan" :plan="rollbackPlan" />
    <template #footer><el-button @click="rollbackDialog = false">{{ $t('projectView.k4d0b46') }}</el-button>
      <el-button type="danger" :disabled="!rollbackPlan || rollbackPlan.errors.length > 0 || rollbackPlan.forbidden.length > 0 || rollbackPlan.entries.length === 0" :loading="busy" data-test="project-rollback-confirm" @click="rollback()">{{ $t('projectView.k2a814f') }}</el-button></template>
  </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { computed, defineComponent, h, onMounted, ref, type PropType } from 'vue'
import { ElAlert, ElMessage, ElMessageBox, ElOption, ElSelect, ElTable, ElTableColumn, ElTag } from 'element-plus'
import { projectApi, type ConfigVersion, type ConflictResolution, type PlanEntry, type PlanView, type ProjectPackage } from '../api/project'
import { errorMessage, errorText } from '../api/http'
import { formatTime, hasGlobal } from '../utils'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'

const actionLabels: Record<string, string> = { add: tr('projectView.k2cd9e6'), update: tr('projectView.ka26056'), rename: tr('projectView.kda615e'), skip: tr('projectView.k31a985'), delete: tr('projectView.k3755f5') }
const actionTypes: Record<string, string> = { add: 'success', update: 'warning', rename: 'primary', skip: 'info', delete: 'danger' }
const sectionLabels: Record<string, string> = {
  areas: tr('projectView.k17fc93'), roles: tr('projectView.k6b2669'), devices: tr('projectView.k01f2c1'), tags: tr('projectView.k8cff1c'),
  calcJobs: tr('projectView.k8efdcf'), alarmRules: tr('projectView.k1c7490'), reportTemplates: tr('projectView.ka34221'), objectTemplates: tr('projectView.k6cee79'),
  hmiLibrary: tr('projectView.section.hmiLibrary'), hmiLayouts: tr('projectView.section.hmiLayouts'), screens: tr('projectView.kbfffd0'),
  controlPoints: tr('projectView.k6a4d18'), controlSequences: tr('projectView.k31d7f3'), recipeTemplates: tr('projectView.k85ad5d'),
  productionLines: tr('projectView.section.productionLines'), productionProducts: tr('projectView.section.productionProducts'),
  productionShifts: tr('projectView.section.productionShifts'), downtimeReasons: tr('projectView.section.downtimeReasons'),
  annunciatorPanels: tr('projectView.section.annunciatorPanels')
}

// 差异/计划表：按条目展示动作与字段级变化；导入时冲突条目可单独选择处理方式。
const PlanTable = defineComponent({
  props: { plan: { type: Object as PropType<PlanView>, required: true }, resolutions: { type: Object as PropType<Record<string, ConflictResolution>>, default: null } },
  emits: ['resolve'],
  setup(props, { emit }) {
    return () => {
      const p = props.plan
      const counts = Object.entries(p.counts).map(([k, v]) => tr('projectView.kb245f5', [actionLabels[k] ?? tr('projectView.k46b24e'), v])).join(' · ')
      const alerts = [
        ...p.forbidden.map(x => h(ElAlert, { type: 'error', title: tr('projectView.k1a4e1b') + x, closable: false, 'data-test': 'plan-forbidden' })),
        ...p.errors.map(x => h(ElAlert, { type: 'error', title: x, closable: false, 'data-test': 'plan-error' })),
        ...p.warnings.map(x => h(ElAlert, { type: 'warning', title: x, closable: false }))
      ]
      const conflict = (row: PlanEntry) => props.resolutions && (row.action === 'update' || row.action === 'skip' || (row.action === 'rename' && row.key !== row.finalKey && row.section !== 'tags'))
      return h('div', { class: 'plan', 'data-test': 'plan-table' }, [
        h('p', { class: 'tiny' }, tr('projectView.k6fe2eb', [counts || tr('projectView.k70e654'), p.hidden ? tr('projectView.kcbb892', [p.hidden]) : ''])),
        ...alerts,
        h(ElTable, { data: p.entries, size: 'small', maxHeight: 420, rowKey: ((r: PlanEntry) => `${r.section}:${r.key}:${r.action}`) as never, emptyText: tr('projectView.k70e654') }, () => [
          h(ElTableColumn, { type: 'expand' }, { default: ({ row }: { row: PlanEntry }) => h('table', { class: 'changes' }, row.changes.map(c => h('tr', [h('td', { class: 'mono' }, c.field), h('td', { class: 'old' }, c.old ?? '—'), h('td', { class: 'new' }, c.new ?? '—')]))) }),
          h(ElTableColumn, { label: tr('projectView.ked9f6d'), width: 100 }, { default: ({ row }: { row: PlanEntry }) => sectionLabels[row.section] ?? row.section }),
          h(ElTableColumn, { label: tr('projectView.k539cb3'), minWidth: 220 }, { default: ({ row }: { row: PlanEntry }) => row.finalKey !== row.key ? `${row.key} → ${row.finalKey}` : row.key }),
          h(ElTableColumn, { label: tr('projectView.kd9d982'), width: 100 }, { default: ({ row }: { row: PlanEntry }) => h(ElTag, { size: 'small', type: actionTypes[row.action] as never }, () => actionLabels[row.action] ?? row.action) }),
          h(ElTableColumn, { label: tr('projectView.k17fc93'), width: 120 }, { default: ({ row }: { row: PlanEntry }) => row.areas.join('、') || tr('projectView.ka5644f') }),
          h(ElTableColumn, { label: tr('projectView.k4aa26b'), minWidth: 200 }, { default: ({ row }: { row: PlanEntry }) => h('span', { class: 'tiny muted' }, row.changes.map(c => c.field).slice(0, 6).join('、')) }),
          props.resolutions ? h(ElTableColumn, { label: tr('projectView.k4aeeac'), width: 130 }, { default: ({ row }: { row: PlanEntry }) => conflict(row)
            ? h(ElSelect, { size: 'small', modelValue: props.resolutions[`${row.section}:${row.key}`] ?? '', placeholder: tr('projectView.kc8d09c'), clearable: true, 'onUpdate:modelValue': (v: string) => emit('resolve', `${row.section}:${row.key}`, v) },
              () => [h(ElOption, { value: 'Overwrite', label: tr('projectView.ka26056') }), h(ElOption, { value: 'Skip', label: tr('projectView.k31a985') }), h(ElOption, { value: 'Rename', label: tr('projectView.kfd87e2') })])
            : '' }) : null
        ])
      ])
    }
  }
})

const auth = useAuthStore()
const globalConfigure = computed(() => hasGlobal(auth.user, 'Configure'))
const kindLabels: Record<string, string> = { manual: tr('projectView.k2a4a4d'), import: tr('projectView.k15e669'), 'pre-import': tr('projectView.kc270a3'), rollback: tr('projectView.k80155a'), 'pre-rollback': tr('projectView.kdca265'), 'screen-publish': tr('projectView.kb36d6d') }
const tab = ref('versions'), versions = ref<ConfigVersion[]>([]), note = ref(''), busy = ref(false), readError = ref('')
const compareFrom = ref(''), compareTo = ref('current'), diff = ref<PlanView | null>(null), diffTitle = ref(''), compareError = ref('')
const rollbackDialog = ref(false), rollbackTarget = ref<ConfigVersion | null>(null), rollbackPlan = ref<PlanView | null>(null), rollbackError = ref('')
const fileInput = ref<HTMLInputElement | null>(null), fileName = ref(''), pkg = ref<ProjectPackage | null>(null)
const resolution = ref<ConflictResolution>('Skip'), resolutions = ref<Record<string, ConflictResolution>>({}), importPlan = ref<PlanView | null>(null), importError = ref(''), actionError = ref('')
const canApply = computed(() => !!importPlan.value?.baseSha && importPlan.value.errors.length === 0 && importPlan.value.forbidden.length === 0 && importPlan.value.entries.some(x => x.action !== 'skip'))
const summaryText = (s: Record<string, number>) => Object.entries(s).filter(([, v]) => v > 0).map(([k, v]) => `${sectionLabels[k] ?? k} ${v}`).join(' · ')

async function refresh(): Promise<void> {
  try { versions.value = await projectApi.versions(); readError.value = ''; if (!compareFrom.value && versions.value[0]) compareFrom.value = String(versions.value[0].id) }
  catch (e) { readError.value = errorMessage(e, tr('projectView.kd9e096')) }
}
function save(blob: Blob, name: string): void { const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name; a.click(); URL.revokeObjectURL(a.href) }
async function exportProject(): Promise<void> {
  actionError.value = ''
  try { save(await projectApi.exportPackage(), `scada-project-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '')}.json`) } catch (e) { actionError.value = await errorText(e, tr('projectView.k675338')) }
}
async function downloadVersion(v: ConfigVersion): Promise<void> {
  actionError.value = ''
  try { save(await projectApi.downloadVersion(v.id), `scada-version-${v.id}.json`) } catch (e) { actionError.value = await errorText(e, tr('projectView.ke0dab2')) }
}
async function snapshot(): Promise<void> {
  if (!note.value.trim()) { actionError.value = tr('projectView.k903a2d'); return }
  busy.value = true
  actionError.value = ''
  try { const v = await projectApi.snapshot(note.value.trim()); note.value = ''; ElMessage.success(tr('projectView.k2fee94', [v.id])); await refresh() }
  catch (e) { actionError.value = errorMessage(e, tr('projectView.k74ef95')) } finally { busy.value = false }
}
async function compare(): Promise<void> {
  compareError.value = ''
  try {
    diff.value = await projectApi.diff(compareFrom.value, compareTo.value)
    diffTitle.value = tr('projectView.k637dbe', [compareFrom.value, compareTo.value === 'current' ? tr('projectView.kfe6728') : '#' + compareTo.value])
    compareError.value = ''
  } catch (e) { compareError.value = errorMessage(e, tr('projectView.k426a09')); diff.value = null }
}
async function previewRollback(v: ConfigVersion): Promise<void> {
  rollbackTarget.value = v; rollbackPlan.value = null; rollbackError.value = ''; rollbackDialog.value = true
  try { rollbackPlan.value = await projectApi.rollbackPreview(v.id); rollbackError.value = '' } catch (e) { rollbackError.value = errorMessage(e, tr('projectView.k97d717')) }
}
async function rollback(): Promise<void> {
  if (!rollbackTarget.value || !rollbackPlan.value?.baseSha) return
  const n = rollbackPlan.value.entries.length
  try { await ElMessageBox.confirm(tr('projectView.k35915e', [n, rollbackTarget.value.id]), tr('projectView.k2a814f'), { type: 'warning', confirmButtonText: tr('projectView.k8a2c43') }) } catch { return }
  busy.value = true; rollbackError.value = ''
  try {
    const result = await projectApi.rollback(rollbackTarget.value.id, rollbackPlan.value.baseSha)
    ElMessage.success(tr('projectView.k6f39b0', [result.versionId])); rollbackDialog.value = false; await refresh()
  } catch (e) {
    const message = errorMessage(e, tr('projectView.kdcefe0'))
    await previewRollback(rollbackTarget.value)
    if (!rollbackError.value) rollbackError.value = message
  } finally { busy.value = false }
}
async function loadFile(event: Event): Promise<void> {
  const file = (event.target as HTMLInputElement).files?.[0]; if (!file) return
  try { pkg.value = JSON.parse(await file.text()); fileName.value = file.name; resolutions.value = {}; importError.value = ''; await previewImport() }
  catch { importError.value = tr('projectView.k1a7a9a'); pkg.value = null; importPlan.value = null }
}
function resolveOne(key: string, value: string): void {
  if (value) resolutions.value[key] = value as ConflictResolution; else delete resolutions.value[key]
  void previewImport()
}
async function previewImport(): Promise<void> {
  if (!pkg.value) return
  busy.value = true; importError.value = ''
  try { importPlan.value = await projectApi.importPreview(pkg.value, resolution.value, resolutions.value); importError.value = '' }
  catch (e) { importError.value = errorMessage(e, tr('projectView.kd28588')); importPlan.value = null } finally { busy.value = false }
}
async function applyImport(): Promise<void> {
  if (!pkg.value || !importPlan.value?.baseSha) return
  const n = importPlan.value.entries.filter(x => x.action !== 'skip').length
  try { await ElMessageBox.confirm(tr('projectView.k097bf9', [n]), tr('projectView.k221f71'), { type: 'warning', confirmButtonText: tr('projectView.k94f172') }) } catch { return }
  busy.value = true; importError.value = ''
  try {
    const result = await projectApi.importApply(pkg.value, resolution.value, resolutions.value, importPlan.value.baseSha)
    ElMessage.success(tr('projectView.kacffcc', [result.versionId])); importPlan.value = null; pkg.value = null; if (fileInput.value) fileInput.value.value = ''; await refresh()
  } catch (e) {
    const message = errorMessage(e, tr('projectView.kb6a155'))
    await previewImport()
    if (!importError.value) importError.value = message
  } finally { busy.value = false }
}
onMounted(refresh)
</script>
<style scoped>
:deep(.changes) { font-size: 12px; border-collapse: collapse; margin: 4px 24px; }
:deep(.changes td) { padding: 2px 8px; border-bottom: 1px solid var(--el-border-color-lighter); vertical-align: top; word-break: break-all; }
:deep(.changes .old) { color: var(--el-color-danger); }
:deep(.changes .new) { color: var(--el-color-success); }
:deep(.mono) { font-family: ui-monospace, Consolas, monospace; }
h4 { margin: 16px 0 6px; }
</style>
