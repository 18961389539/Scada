<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>工程管理</strong><span class="spacer" />
      <el-button data-test="project-export" @click="exportProject()">导出工程</el-button>
    </div>
    <p class="tiny muted">工程包按名称引用（不含数据库编号），可在另一套系统导入。离线编辑工程包后在“导入发布”中预览差异再一次性发布；发布、回滚、画面发布都会自动打配置快照。所有操作按区域权限过滤并写审计。</p>
    <el-tabs v-model="tab">
      <el-tab-pane label="版本快照" name="versions">
        <div class="toolbar">
          <el-input v-model="note" placeholder="快照说明，如：投运前基线" style="width: 280px" maxlength="200" data-test="project-note" />
          <el-button type="primary" :loading="busy" data-test="project-snapshot" @click="snapshot()">打快照</el-button>
          <span class="spacer" />
          <span class="tiny">比较</span>
          <el-select v-model="compareFrom" style="width: 160px" data-test="project-from"><el-option v-for="v in versions" :key="v.id" :value="String(v.id)" :label="`#${v.id} ${v.note}`" /></el-select>
          <span class="tiny">→</span>
          <el-select v-model="compareTo" style="width: 160px" data-test="project-to"><el-option value="current" label="当前配置" /><el-option v-for="v in versions" :key="v.id" :value="String(v.id)" :label="`#${v.id} ${v.note}`" /></el-select>
          <el-button :disabled="!compareFrom" data-test="project-compare" @click="compare()">比较</el-button>
        </div>
        <el-alert v-if="readError" :title="readError" type="error" :closable="false" />
        <el-table :data="versions" row-key="id" size="small" empty-text="尚无快照" data-test="project-versions" max-height="360">
          <el-table-column label="版本" width="80"><template #default="{ row }">#{{ row.id }}</template></el-table-column>
          <el-table-column label="类型" width="110"><template #default="{ row }"><el-tag size="small" :type="row.kind === 'manual' ? 'primary' : 'info'">{{ kindLabels[row.kind] ?? row.kind }}</el-tag></template></el-table-column>
          <el-table-column prop="note" label="说明" min-width="200" />
          <el-table-column label="创建" min-width="170"><template #default="{ row }"><span class="tiny">{{ formatTime(row.createdTs) }} · {{ row.createdBy }}</span></template></el-table-column>
          <el-table-column label="内容" min-width="200"><template #default="{ row }"><span class="tiny muted">{{ summaryText(row.summary) }}</span></template></el-table-column>
          <el-table-column label="操作" width="160"><template #default="{ row }">
            <el-button link type="warning" :data-test="`project-rollback-${row.id}`" @click="previewRollback(row as ConfigVersion)">回滚到此</el-button>
            <el-button v-if="globalConfigure" link @click="downloadVersion(row as ConfigVersion)">下载</el-button></template></el-table-column>
        </el-table>
        <template v-if="diff">
          <h4>差异：{{ diffTitle }}</h4>
          <plan-table :plan="diff" />
        </template>
      </el-tab-pane>
      <el-tab-pane label="导入发布" name="import">
        <div class="toolbar">
          <input ref="fileInput" type="file" accept=".json,application/json" data-test="project-file" @change="loadFile" />
          <span class="tiny">冲突默认处理</span>
          <el-select v-model="resolution" style="width: 120px" data-test="project-resolution" @change="previewImport()">
            <el-option value="Skip" label="跳过" /><el-option value="Overwrite" label="覆盖" /><el-option value="Rename" label="改名导入" /></el-select>
          <el-button :disabled="!pkg" :loading="busy" data-test="project-preview" @click="previewImport()">预览差异</el-button>
          <el-button type="primary" :disabled="!canApply" :loading="busy" data-test="project-apply" @click="applyImport()">发布导入</el-button>
        </div>
        <p v-if="pkg" class="tiny muted">工程包：{{ fileName }} · 导出于 {{ formatTime(pkg.exportedTs) }} · {{ pkg.exportedBy }}{{ pkg.partial ? ' · 按权限部分导出' : '' }}</p>
        <plan-table v-if="importPlan" :plan="importPlan" :resolutions="resolutions" @resolve="resolveOne" />
      </el-tab-pane>
    </el-tabs>
  </div>

  <el-dialog v-model="rollbackDialog" :title="`回滚到配置版本 #${rollbackTarget?.id}`" width="1000px">
    <p class="tiny">以下是回滚对当前配置的影响：新增/恢复、覆盖、删除。删除测点会结束其活动报警，历史数据保留。回滚前自动保存当前配置快照。</p>
    <plan-table v-if="rollbackPlan" :plan="rollbackPlan" />
    <template #footer><el-button @click="rollbackDialog = false">取消</el-button>
      <el-button type="danger" :disabled="!rollbackPlan || rollbackPlan.errors.length > 0 || rollbackPlan.forbidden.length > 0 || rollbackPlan.entries.length === 0" :loading="busy" data-test="project-rollback-confirm" @click="rollback()">确认回滚</el-button></template>
  </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { computed, defineComponent, h, onMounted, ref, type PropType } from 'vue'
import { ElAlert, ElMessage, ElMessageBox, ElOption, ElSelect, ElTable, ElTableColumn, ElTag } from 'element-plus'
import { projectApi, type ConfigVersion, type ConflictResolution, type PlanEntry, type PlanView, type ProjectPackage } from '../api/project'
import { errorMessage } from '../api/http'
import { formatTime, hasGlobal } from '../utils'
import { useAuthStore } from '../stores/auth'

const actionLabels: Record<string, string> = { add: '新增', update: '覆盖', rename: '改名新增', skip: '跳过', delete: '删除' }
const actionTypes: Record<string, string> = { add: 'success', update: 'warning', rename: 'primary', skip: 'info', delete: 'danger' }
const sectionLabels: Record<string, string> = { areas: '区域', roles: '角色', devices: '设备', tags: '测点', calcJobs: '计算任务', alarmRules: '报警规则', reportTemplates: '报表模板', objectTemplates: '对象模板', screens: '画面' }

// 差异/计划表：按条目展示动作与字段级变化；导入时冲突条目可单独选择处理方式。
const PlanTable = defineComponent({
  props: { plan: { type: Object as PropType<PlanView>, required: true }, resolutions: { type: Object as PropType<Record<string, ConflictResolution>>, default: null } },
  emits: ['resolve'],
  setup(props, { emit }) {
    return () => {
      const p = props.plan
      const counts = Object.entries(p.counts).map(([k, v]) => `${actionLabels[k] ?? '不变'} ${v}`).join(' · ')
      const alerts = [
        ...p.forbidden.map(x => h(ElAlert, { type: 'error', title: '权限不足：' + x, closable: false, 'data-test': 'plan-forbidden' })),
        ...p.errors.map(x => h(ElAlert, { type: 'error', title: x, closable: false, 'data-test': 'plan-error' })),
        ...p.warnings.map(x => h(ElAlert, { type: 'warning', title: x, closable: false }))
      ]
      const conflict = (row: PlanEntry) => props.resolutions && (row.action === 'update' || row.action === 'skip' || (row.action === 'rename' && row.key !== row.finalKey && row.section !== 'tags'))
      return h('div', { class: 'plan', 'data-test': 'plan-table' }, [
        h('p', { class: 'tiny' }, `${counts || '无差异'}${p.hidden ? ` · 另有 ${p.hidden} 项不在你的权限范围内` : ''}`),
        ...alerts,
        h(ElTable, { data: p.entries, size: 'small', maxHeight: 420, rowKey: ((r: PlanEntry) => `${r.section}:${r.key}:${r.action}`) as never, emptyText: '无差异' }, () => [
          h(ElTableColumn, { type: 'expand' }, { default: ({ row }: { row: PlanEntry }) => h('table', { class: 'changes' }, row.changes.map(c => h('tr', [h('td', { class: 'mono' }, c.field), h('td', { class: 'old' }, c.old ?? '—'), h('td', { class: 'new' }, c.new ?? '—')]))) }),
          h(ElTableColumn, { label: '类别', width: 100 }, { default: ({ row }: { row: PlanEntry }) => sectionLabels[row.section] ?? row.section }),
          h(ElTableColumn, { label: '对象', minWidth: 220 }, { default: ({ row }: { row: PlanEntry }) => row.finalKey !== row.key ? `${row.key} → ${row.finalKey}` : row.key }),
          h(ElTableColumn, { label: '动作', width: 100 }, { default: ({ row }: { row: PlanEntry }) => h(ElTag, { size: 'small', type: actionTypes[row.action] as never }, () => actionLabels[row.action] ?? row.action) }),
          h(ElTableColumn, { label: '区域', width: 120 }, { default: ({ row }: { row: PlanEntry }) => row.areas.join('、') || '全局' }),
          h(ElTableColumn, { label: '变化字段', minWidth: 200 }, { default: ({ row }: { row: PlanEntry }) => h('span', { class: 'tiny muted' }, row.changes.map(c => c.field).slice(0, 6).join('、')) }),
          props.resolutions ? h(ElTableColumn, { label: '冲突处理', width: 130 }, { default: ({ row }: { row: PlanEntry }) => conflict(row)
            ? h(ElSelect, { size: 'small', modelValue: props.resolutions[`${row.section}:${row.key}`] ?? '', placeholder: '默认', clearable: true, 'onUpdate:modelValue': (v: string) => emit('resolve', `${row.section}:${row.key}`, v) },
              () => [h(ElOption, { value: 'Overwrite', label: '覆盖' }), h(ElOption, { value: 'Skip', label: '跳过' }), h(ElOption, { value: 'Rename', label: '改名导入' })])
            : '' }) : null
        ])
      ])
    }
  }
})

const auth = useAuthStore()
const globalConfigure = computed(() => hasGlobal(auth.user, 'Configure'))
const kindLabels: Record<string, string> = { manual: '手动', import: '导入发布', 'pre-import': '导入前', rollback: '回滚后', 'pre-rollback': '回滚前', 'screen-publish': '画面发布' }
const tab = ref('versions'), versions = ref<ConfigVersion[]>([]), note = ref(''), busy = ref(false), readError = ref('')
const compareFrom = ref(''), compareTo = ref('current'), diff = ref<PlanView | null>(null), diffTitle = ref('')
const rollbackDialog = ref(false), rollbackTarget = ref<ConfigVersion | null>(null), rollbackPlan = ref<PlanView | null>(null)
const fileInput = ref<HTMLInputElement | null>(null), fileName = ref(''), pkg = ref<ProjectPackage | null>(null)
const resolution = ref<ConflictResolution>('Skip'), resolutions = ref<Record<string, ConflictResolution>>({}), importPlan = ref<PlanView | null>(null)
const canApply = computed(() => !!importPlan.value?.baseSha && importPlan.value.errors.length === 0 && importPlan.value.forbidden.length === 0 && importPlan.value.entries.some(x => x.action !== 'skip'))
const summaryText = (s: Record<string, number>) => Object.entries(s).filter(([, v]) => v > 0).map(([k, v]) => `${sectionLabels[k] ?? k} ${v}`).join(' · ')

async function refresh(): Promise<void> {
  try { versions.value = await projectApi.versions(); readError.value = ''; if (!compareFrom.value && versions.value[0]) compareFrom.value = String(versions.value[0].id) }
  catch (e) { readError.value = errorMessage(e, '读取版本失败') }
}
function save(blob: Blob, name: string): void { const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name; a.click(); URL.revokeObjectURL(a.href) }
async function exportProject(): Promise<void> {
  try { save(await projectApi.exportPackage(), `scada-project-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '')}.json`) } catch (e) { ElMessage.error(errorMessage(e, '导出失败')) }
}
async function downloadVersion(v: ConfigVersion): Promise<void> {
  try { save(await projectApi.downloadVersion(v.id), `scada-version-${v.id}.json`) } catch (e) { ElMessage.error(errorMessage(e, '下载失败')) }
}
async function snapshot(): Promise<void> {
  if (!note.value.trim()) { ElMessage.warning('请填写快照说明'); return }
  busy.value = true
  try { const v = await projectApi.snapshot(note.value.trim()); note.value = ''; ElMessage.success(`已创建快照 #${v.id}`); await refresh() }
  catch (e) { ElMessage.error(errorMessage(e, '创建快照失败')) } finally { busy.value = false }
}
async function compare(): Promise<void> {
  try { diff.value = await projectApi.diff(compareFrom.value, compareTo.value); diffTitle.value = `#${compareFrom.value} → ${compareTo.value === 'current' ? '当前配置' : '#' + compareTo.value}` }
  catch (e) { ElMessage.error(errorMessage(e, '比较失败')) }
}
async function previewRollback(v: ConfigVersion): Promise<void> {
  rollbackTarget.value = v; rollbackPlan.value = null; rollbackDialog.value = true
  try { rollbackPlan.value = await projectApi.rollbackPreview(v.id) } catch (e) { ElMessage.error(errorMessage(e, '影响预览失败')) }
}
async function rollback(): Promise<void> {
  if (!rollbackTarget.value || !rollbackPlan.value?.baseSha) return
  const n = rollbackPlan.value.entries.length
  try { await ElMessageBox.confirm(`确认把 ${n} 项配置回滚到版本 #${rollbackTarget.value.id}？`, '确认回滚', { type: 'warning', confirmButtonText: '回滚' }) } catch { return }
  busy.value = true
  try {
    const result = await projectApi.rollback(rollbackTarget.value.id, rollbackPlan.value.baseSha)
    ElMessage.success(`已回滚，生成版本 #${result.versionId}`); rollbackDialog.value = false; await refresh()
  } catch (e) { ElMessage.error(errorMessage(e, '回滚失败')); await previewRollback(rollbackTarget.value) }
  finally { busy.value = false }
}
async function loadFile(event: Event): Promise<void> {
  const file = (event.target as HTMLInputElement).files?.[0]; if (!file) return
  try { pkg.value = JSON.parse(await file.text()); fileName.value = file.name; resolutions.value = {}; await previewImport() }
  catch { ElMessage.error('文件不是有效的工程包 JSON'); pkg.value = null }
}
function resolveOne(key: string, value: string): void {
  if (value) resolutions.value[key] = value as ConflictResolution; else delete resolutions.value[key]
  void previewImport()
}
async function previewImport(): Promise<void> {
  if (!pkg.value) return
  busy.value = true
  try { importPlan.value = await projectApi.importPreview(pkg.value, resolution.value, resolutions.value) }
  catch (e) { ElMessage.error(errorMessage(e, '预览失败')) } finally { busy.value = false }
}
async function applyImport(): Promise<void> {
  if (!pkg.value || !importPlan.value?.baseSha) return
  const n = importPlan.value.entries.filter(x => x.action !== 'skip').length
  try { await ElMessageBox.confirm(`发布导入 ${n} 项变更？任一项失败将全部回滚。`, '确认发布', { type: 'warning', confirmButtonText: '发布' }) } catch { return }
  busy.value = true
  try {
    const result = await projectApi.importApply(pkg.value, resolution.value, resolutions.value, importPlan.value.baseSha)
    ElMessage.success(`已发布，生成版本 #${result.versionId}`); importPlan.value = null; pkg.value = null; if (fileInput.value) fileInput.value.value = ''; await refresh()
  } catch (e) { ElMessage.error(errorMessage(e, '导入失败，已全部回滚')); await previewImport() }
  finally { busy.value = false }
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
