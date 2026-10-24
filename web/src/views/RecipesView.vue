<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>配方管理</strong><span class="spacer" /><el-button @click="refresh()">刷新</el-button></div>
    <p class="tiny muted">配方下发经过现场操作的同一控制闭环：全部参数先整体预检查（控制权、挂牌、限幅、变化率、许可、配方上下限），全部通过才逐个写入并回读核对；需要时整批双人复核。任一参数失败立即停止，结果明确标记“部分下发”，不会自动重发或回退。</p>
    <el-tabs v-model="tab">
      <el-tab-pane label="配方" name="recipes">
        <div class="toolbar">
          <el-select v-model="templateFilter" clearable placeholder="全部模板" style="width: 220px" data-test="recipe-template-filter"><el-option v-for="t in templates" :key="t.id" :value="t.id" :label="t.name" /></el-select>
          <el-button type="primary" :disabled="!templates.length" data-test="recipe-new" @click="editRecipe()">新建配方</el-button>
          <el-button :disabled="!templateFilter" data-test="recipe-upload" @click="upload(null)">从现场上载为新配方</el-button>
        </div>
        <el-table :data="filteredRecipes" size="small" data-test="recipes" empty-text="尚无配方">
          <el-table-column label="配方" min-width="180"><template #default="{ row }"><strong>{{ row.name }}</strong> <span class="tiny muted">{{ row.templateName }} · v{{ row.version }}</span><div class="tiny muted">{{ row.description }}</div></template></el-table-column>
          <el-table-column label="参数" min-width="300"><template #default="{ row }"><span class="tiny">{{ valuesText(row) }}</span></template></el-table-column>
          <el-table-column label="最近修改" min-width="150"><template #default="{ row }"><span class="tiny">{{ row.updatedBy }} · {{ formatTime(row.updatedTs) }}</span><div class="tiny muted">{{ row.note }}</div></template></el-table-column>
          <el-table-column label="操作" width="440"><template #default="{ row }">
            <el-button size="small" type="primary" :data-test="`recipe-download-${row.id}`" @click="prepareDownload(row)">下发</el-button>
            <el-button size="small" @click="editRecipe(row)">编辑</el-button>
            <el-button size="small" @click="showVersions(row)">版本</el-button>
            <el-button size="small" :data-test="`recipe-compare-live-${row.id}`" @click="compare(row, null, 'live', null)">对比现场</el-button>
            <el-button size="small" @click="upload(row)">上载</el-button>
            <el-button size="small" type="danger" @click="removeRecipe(row)">删除</el-button>
          </template></el-table-column>
        </el-table>
        <h4>下发记录</h4>
        <el-table :data="downloads" size="small" data-test="recipe-downloads" empty-text="尚无下发记录">
          <el-table-column label="下发" min-width="150"><template #default="{ row }">#{{ row.id }} {{ row.recipeName }} v{{ row.version }}</template></el-table-column>
          <el-table-column label="状态" width="100"><template #default="{ row }"><el-tag size="small" :type="downloadType[row.status] ?? 'info'" :data-test="`download-status-${row.id}`">{{ row.statusLabel }}</el-tag></template></el-table-column>
          <el-table-column label="参数" min-width="320"><template #default="{ row }">
            <div v-for="r in row.results" :key="r.key" class="tiny"><el-tag size="small" :type="paramType[r.status] ?? 'info'">{{ paramLabels[r.status] ?? r.status }}</el-tag> {{ r.name }}：{{ fmt(r.before) }} → <strong>{{ r.target }}</strong><span v-if="r.readBack != null">（回读 {{ r.readBack }}）</span> <span class="muted">{{ r.status === 'done' ? '' : r.message }}</span></div>
          </template></el-table-column>
          <el-table-column label="执行" min-width="220"><template #default="{ row }"><span class="tiny">{{ row.requestedBy }}{{ row.approvedBy ? `（复核 ${row.approvedBy}）` : '' }} · {{ formatTime(row.createdTs) }}</span><div class="tiny" :class="row.status === 'PartiallyApplied' ? 'danger' : 'muted'">{{ row.message }}</div></template></el-table-column>
          <el-table-column label="" width="80"><template #default="{ row }"><el-button v-if="row.status === 'Running'" size="small" type="danger" @click="abort(row)">中止</el-button></template></el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane label="模板" name="templates">
        <div class="toolbar"><el-button type="primary" data-test="template-new" @click="editTemplate()">新建模板</el-button></div>
        <el-table :data="templates" size="small" data-test="recipe-templates" empty-text="尚无配方模板">
          <el-table-column prop="name" label="模板" min-width="160" />
          <el-table-column label="区域" width="120"><template #default="{ row }">{{ areaName(row.areaId) }}</template></el-table-column>
          <el-table-column label="参数" min-width="320"><template #default="{ row }"><span class="tiny">{{ row.parameters.map((p: RecipeParameter) => `${p.name}(${tagName(p.tagId)}${p.unit ? ' ' + p.unit : ''} [${p.min ?? '-'}, ${p.max ?? '-'}])`).join('；') }}</span></template></el-table-column>
          <el-table-column label="复核" width="80"><template #default="{ row }">{{ row.dualApproval ? '双人' : '-' }}</template></el-table-column>
          <el-table-column label="操作" width="150"><template #default="{ row }"><el-button size="small" @click="editTemplate(row)">编辑</el-button><el-button size="small" type="danger" @click="removeTemplate(row)">删除</el-button></template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
  </div>

  <el-dialog v-model="templateDialog" :title="templateForm.id ? '编辑配方模板' : '新建配方模板'" width="900px">
    <el-form label-width="90px">
      <el-form-item label="名称"><el-input v-model="templateForm.name" maxlength="100" data-test="template-name" /></el-form-item>
      <el-form-item label="区域"><el-select v-model="templateForm.areaId"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
      <el-form-item label="说明"><el-input v-model="templateForm.description" maxlength="500" /></el-form-item>
      <el-form-item label="下发复核"><el-checkbox v-model="templateForm.dualApproval">整批双人复核</el-checkbox></el-form-item>
    </el-form>
    <el-table :data="templateForm.parameters" size="small" data-test="template-params">
      <el-table-column label="键" width="120"><template #default="{ row }"><el-input v-model="row.key" size="small" placeholder="temp" /></template></el-table-column>
      <el-table-column label="名称" width="130"><template #default="{ row }"><el-input v-model="row.name" size="small" /></template></el-table-column>
      <el-table-column label="测点" min-width="200"><template #default="{ row }"><el-select v-model="row.tagId" filterable size="small"><el-option v-for="t in writableTags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select></template></el-table-column>
      <el-table-column label="单位" width="80"><template #default="{ row }"><el-input v-model="row.unit" size="small" /></template></el-table-column>
      <el-table-column label="下限" width="100"><template #default="{ row }"><el-input-number v-model="row.min" size="small" :controls="false" style="width: 80px" /></template></el-table-column>
      <el-table-column label="上限" width="100"><template #default="{ row }"><el-input-number v-model="row.max" size="small" :controls="false" style="width: 80px" /></template></el-table-column>
      <el-table-column label="" width="60"><template #default="{ $index }"><el-button size="small" link type="danger" @click="templateForm.parameters.splice($index, 1)">删</el-button></template></el-table-column>
    </el-table>
    <el-button size="small" style="margin-top: 8px" data-test="template-add-param" @click="templateForm.parameters.push({ key: `p${templateForm.parameters.length + 1}`, name: '', tagId: 0, unit: null, min: null, max: null, defaultValue: null })">添加参数</el-button>
    <template #footer><el-button @click="templateDialog = false">取消</el-button><el-button type="primary" data-test="template-save" @click="saveTemplate()">保存</el-button></template>
  </el-dialog>

  <el-dialog v-model="recipeDialog" :title="recipeForm.id ? `编辑配方（保存后生成 v${recipeForm.version + 1}）` : '新建配方'" width="680px">
    <el-form label-width="90px">
      <el-form-item label="模板"><el-select v-model="recipeForm.templateId" :disabled="!!recipeForm.id" data-test="recipe-template" @change="fillDefaults()"><el-option v-for="t in templates" :key="t.id" :value="t.id" :label="t.name" /></el-select></el-form-item>
      <el-form-item label="名称"><el-input v-model="recipeForm.name" maxlength="100" data-test="recipe-name" /></el-form-item>
      <el-form-item label="说明"><el-input v-model="recipeForm.description" maxlength="500" /></el-form-item>
      <el-form-item v-for="p in formTemplate?.parameters ?? []" :key="p.key" :label="p.name">
        <el-input-number v-model="recipeForm.values[p.key]" :min="p.min ?? undefined" :max="p.max ?? undefined" :data-test="`recipe-value-${p.key}`" />
        <span class="tiny muted" style="margin-left: 8px">{{ p.unit }} [{{ p.min ?? '-' }}, {{ p.max ?? '-' }}] · {{ tagName(p.tagId) }}</span>
      </el-form-item>
      <el-form-item label="版本说明"><el-input v-model="recipeForm.note" maxlength="200" data-test="recipe-note" /></el-form-item>
    </el-form>
    <template #footer><el-button @click="recipeDialog = false">取消</el-button><el-button type="primary" data-test="recipe-save" @click="saveRecipe()">保存</el-button></template>
  </el-dialog>

  <el-dialog v-model="downloadDialog" title="下发配方确认" width="760px">
    <template v-if="checkResult">
      <p>配方 <strong>{{ downloadRecipe?.name }}</strong> v{{ checkResult.version }}<el-tag v-if="checkResult.dualApproval" size="small" type="warning" style="margin-left: 8px">需双人复核</el-tag></p>
      <el-table :data="checkResult.parameters" size="small" data-test="recipe-check">
        <el-table-column prop="name" label="参数" width="120" />
        <el-table-column prop="tagName" label="测点" min-width="160" />
        <el-table-column label="当前 → 目标" width="170"><template #default="{ row }">{{ fmt(row.current) }} → <strong>{{ row.target }}</strong> {{ row.unit }}</template></el-table-column>
        <el-table-column label="检查" min-width="220"><template #default="{ row }"><span v-if="row.refusals.length" class="tiny danger">{{ row.refusals.join('；') }}</span><el-tag v-else size="small" type="success">通过</el-tag></template></el-table-column>
      </el-table>
      <p v-if="!checkResult.ok" class="danger tiny">有参数未通过检查，整批不能下发（不会只下发其中一部分）。</p>
    </template>
    <template #footer><el-button @click="downloadDialog = false">取消</el-button><el-button type="primary" :disabled="!checkResult?.ok" data-test="recipe-download-confirm" @click="confirmDownload()">{{ checkResult?.dualApproval ? '提交复核' : '确认下发' }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="versionsDialog" :title="`版本历史：${versionsRecipe?.name ?? ''}`" width="760px">
    <el-table :data="versions" size="small" data-test="recipe-versions">
      <el-table-column label="版本" width="70"><template #default="{ row }">v{{ row.version }}</template></el-table-column>
      <el-table-column label="来源" width="80"><template #default="{ row }">{{ sourceLabels[row.source] ?? row.source }}</template></el-table-column>
      <el-table-column label="值" min-width="260"><template #default="{ row }"><span class="tiny">{{ row.values.map((v: RecipeValue) => `${v.key}=${v.value}`).join('，') }}</span></template></el-table-column>
      <el-table-column label="修改" min-width="160"><template #default="{ row }"><span class="tiny">{{ row.createdBy }} · {{ formatTime(row.createdTs) }}</span><div class="tiny muted">{{ row.note }}</div></template></el-table-column>
      <el-table-column label="" width="150"><template #default="{ row }">
        <el-button size="small" @click="compare(versionsRecipe!, row.version, String(versionsRecipe!.id), versionsRecipe!.version)">对比当前</el-button>
      </template></el-table-column>
    </el-table>
  </el-dialog>

  <el-dialog v-model="compareDialog" title="配方对比" width="640px">
    <template v-if="compareResult">
      <p class="tiny">{{ compareResult.leftLabel }} ↔ {{ compareResult.rightLabel }}：{{ compareResult.differences }} 处不同</p>
      <el-table :data="compareResult.rows" size="small" data-test="recipe-compare" :row-class-name="diffClass">
        <el-table-column prop="name" label="参数" width="140" />
        <el-table-column label="左" width="140"><template #default="{ row }">{{ fmt(row.left) }} {{ row.unit }}</template></el-table-column>
        <el-table-column label="右" width="140"><template #default="{ row }">{{ fmt(row.right) }} {{ row.unit }}</template></el-table-column>
        <el-table-column label="" width="80"><template #default="{ row }"><el-tag v-if="row.different" size="small" type="warning">不同</el-tag></template></el-table-column>
      </el-table>
    </template>
  </el-dialog>
</div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { areaApi, deviceApi, tagApi } from '../api'
import { recipeApi, type Recipe, type RecipeCheck, type RecipeCompare, type RecipeDownload, type RecipeParameter, type RecipeTemplate, type RecipeValue, type RecipeVersion } from '../api/recipes'
import { errorMessage } from '../api/http'
import { formatTime } from '../utils'

interface NamedTag { id: number; name: string; deviceId: number | null; writable: boolean }
const tab = ref('recipes')
const templates = ref<RecipeTemplate[]>([]), recipes = ref<Recipe[]>([]), downloads = ref<RecipeDownload[]>([])
const tags = ref<NamedTag[]>([]), devices = ref<{ id: number; name: string }[]>([]), areas = ref<{ id: number; name: string }[]>([])
const templateFilter = ref<number | null>(null)
const filteredRecipes = computed(() => recipes.value.filter(x => !templateFilter.value || x.templateId === templateFilter.value))
const writableTags = computed(() => tags.value.filter(x => x.writable && x.deviceId != null))
const downloadType: Record<string, string> = { Pending: 'warning', Running: 'warning', Completed: 'success', Failed: 'danger', PartiallyApplied: 'danger', Aborted: 'info', Rejected: 'info' }
const paramLabels: Record<string, string> = { pending: '等待', done: '完成', failed: '失败', unknown: '未知', skipped: '未写' }
const paramType: Record<string, string> = { done: 'success', failed: 'danger', unknown: 'danger' }
const sourceLabels: Record<string, string> = { manual: '编辑', upload: '上载', project: '工程' }
const fmt = (v: number | null | undefined) => v == null ? '-' : Number(v.toFixed(3))
const areaName = (id: number) => areas.value.find(x => x.id === id)?.name ?? `#${id}`
function tagName(id: number): string {
  const t = tags.value.find(x => x.id === id); if (!t) return `#${id}`
  const d = devices.value.find(x => x.id === t.deviceId); return d ? `${d.name}/${t.name}` : t.name
}
function valuesText(r: Recipe): string {
  const t = templates.value.find(x => x.id === r.templateId)
  return r.values.map(v => { const p = t?.parameters.find(x => x.key === v.key); return `${p?.name ?? v.key}=${v.value}${p?.unit ?? ''}` }).join('，')
}
async function guard(action: () => Promise<unknown>, fallback: string): Promise<boolean> {
  try { await action(); return true } catch (e) { ElMessage.error(errorMessage(e, fallback)); return false }
}
async function refresh(): Promise<void> {
  await guard(async () => {
    const [d, t, a] = await Promise.all([deviceApi.list(), tagApi.list(), areaApi.list().catch(() => [])])
    devices.value = d as unknown as { id: number; name: string }[]; tags.value = t as unknown as NamedTag[]; areas.value = a as { id: number; name: string }[]
    ;[templates.value, recipes.value, downloads.value] = await Promise.all([recipeApi.templates(), recipeApi.list(), recipeApi.downloads()])
  }, '加载失败')
}

const templateDialog = ref(false)
const templateForm = reactive<{ id?: number; name: string; areaId: number; description: string | null; dualApproval: boolean; revision: number; parameters: RecipeParameter[] }>({ name: '', areaId: 1, description: null, dualApproval: false, revision: 0, parameters: [] })
function editTemplate(t?: RecipeTemplate): void {
  Object.assign(templateForm, t ? { ...t, parameters: t.parameters.map(p => ({ ...p })) } : { id: undefined, name: '', areaId: areas.value[0]?.id ?? 1, description: null, dualApproval: false, revision: 0, parameters: [] })
  templateDialog.value = true
}
async function saveTemplate(): Promise<void> {
  if (await guard(() => recipeApi.saveTemplate({ ...templateForm }), '保存失败')) { templateDialog.value = false; ElMessage.success('已保存'); await refresh() }
}
async function removeTemplate(t: RecipeTemplate): Promise<void> {
  try { await ElMessageBox.confirm(`删除模板 ${t.name} 及其全部配方？下发记录保留。`, '删除', { type: 'warning' }) } catch { return }
  if (await guard(() => recipeApi.removeTemplate(t.id), '删除失败')) await refresh()
}

const recipeDialog = ref(false)
const recipeForm = reactive<{ id?: number; templateId: number; name: string; description: string | null; note: string | null; version: number; values: Record<string, number> }>({ templateId: 0, name: '', description: null, note: null, version: 0, values: {} })
const formTemplate = computed(() => templates.value.find(x => x.id === recipeForm.templateId))
function fillDefaults(): void {
  const values: Record<string, number> = {}
  for (const p of formTemplate.value?.parameters ?? []) values[p.key] = recipeForm.values[p.key] ?? p.defaultValue ?? p.min ?? 0
  recipeForm.values = values
}
function editRecipe(r?: Recipe): void {
  Object.assign(recipeForm, r ? { id: r.id, templateId: r.templateId, name: r.name, description: r.description, note: null, version: r.version, values: Object.fromEntries(r.values.map(v => [v.key, v.value])) }
    : { id: undefined, templateId: templateFilter.value ?? templates.value[0]?.id ?? 0, name: '', description: null, note: null, version: 0, values: {} })
  fillDefaults(); recipeDialog.value = true
}
async function saveRecipe(): Promise<void> {
  const values = Object.entries(recipeForm.values).map(([key, value]) => ({ key, value: Number(value) }))
  if (await guard(async () => { const r = await recipeApi.save({ ...recipeForm, values }); if (r.warnings.length) ElMessage.warning(r.warnings.join('；')) }, '保存失败')) {
    recipeDialog.value = false; ElMessage.success('已保存为新版本'); await refresh()
  }
}
async function removeRecipe(r: Recipe): Promise<void> {
  try { await ElMessageBox.confirm(`删除配方 ${r.name} 及全部版本？`, '删除', { type: 'warning' }) } catch { return }
  if (await guard(() => recipeApi.remove(r.id), '删除失败')) await refresh()
}
async function upload(r: Recipe | null): Promise<void> {
  const templateId = r?.templateId ?? templateFilter.value; if (!templateId) return
  let input = ''
  try { input = (await ElMessageBox.prompt(r ? `把参数测点的当前值保存为 ${r.name} 的新版本。版本说明：` : '新配方名称：', '从现场上载', { inputValue: r ? '从现场上载' : '', inputPattern: r ? undefined : /\S/, inputErrorMessage: '请填写名称' })).value ?? '' } catch { return }
  if (await guard(async () => { const res = await recipeApi.upload(templateId, r?.id ?? null, r ? null : input.trim(), r ? input : null); if (res.warnings.length) ElMessage.warning(res.warnings.join('；')) }, '上载失败')) {
    ElMessage.success('已上载'); await refresh()
  }
}

const downloadDialog = ref(false), downloadRecipe = ref<Recipe | null>(null), checkResult = ref<RecipeCheck | null>(null)
async function prepareDownload(r: Recipe): Promise<void> {
  downloadRecipe.value = r; checkResult.value = null
  if (await guard(async () => { checkResult.value = await recipeApi.check(r.id, r.version) }, '预检查失败')) downloadDialog.value = true
}
async function confirmDownload(): Promise<void> {
  const r = downloadRecipe.value!; const version = checkResult.value!.version
  await guard(async () => {
    const d = await recipeApi.download(r.id, version)
    if (d.status === 'Pending') ElMessage.success('已提交，等待另一位有权限的用户在“现场操作 → 待复核”中批准')
    else if (d.status === 'Rejected') ElMessage.error(d.message ?? '未下发')
    else ElMessage.success(`开始下发 #${d.id}`)
  }, '下发失败')
  downloadDialog.value = false; await refresh()
}
async function abort(d: RecipeDownload): Promise<void> { if (await guard(() => recipeApi.abort(d.id), '中止失败')) await refresh() }

const versionsDialog = ref(false), versionsRecipe = ref<Recipe | null>(null), versions = ref<RecipeVersion[]>([])
async function showVersions(r: Recipe): Promise<void> {
  versionsRecipe.value = r
  if (await guard(async () => { versions.value = await recipeApi.versions(r.id) }, '加载失败')) versionsDialog.value = true
}
const diffClass = ({ row }: { row: { different: boolean } }) => row.different ? 'diff-row' : ''
const compareDialog = ref(false), compareResult = ref<RecipeCompare | null>(null)
async function compare(r: Recipe, leftVersion: number | null, right: string, rightVersion: number | null): Promise<void> {
  if (await guard(async () => { compareResult.value = await recipeApi.compare(r.id, leftVersion, right, rightVersion) }, '对比失败')) compareDialog.value = true
}

let timer: number | undefined
onMounted(async () => {
  await refresh()
  timer = window.setInterval(async () => { if (downloads.value.some(x => x.status === 'Running')) downloads.value = await recipeApi.downloads().catch(() => downloads.value) }, 1000)
})
onBeforeUnmount(() => window.clearInterval(timer))
</script>

<style scoped>
.danger { color: var(--el-color-danger); }
:deep(.diff-row) { background: var(--el-color-warning-light-9); }
</style>
