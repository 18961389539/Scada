<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><span class="spacer" /><el-button @click="refresh()">{{ $t('recipesView.k38108e') }}</el-button></div>
    <p class="page-desc">{{ $t('recipesView.k654ddd') }}</p>
    <el-tabs v-model="tab">
      <el-tab-pane :label="$t('recipesView.k85ad5d')" name="recipes">
        <div class="toolbar">
          <el-select v-model="templateFilter" clearable :placeholder="$t('recipesView.k146966')" style="width: 220px" data-test="recipe-template-filter"><el-option v-for="t in templates" :key="t.id" :value="t.id" :label="t.name" /></el-select>
          <el-button type="primary" :disabled="!templates.length" data-test="recipe-new" @click="editRecipe()">{{ $t('recipesView.k91fabd') }}</el-button>
          <el-button :disabled="!templateFilter" data-test="recipe-upload" @click="upload(null)">{{ $t('recipesView.k1680aa') }}</el-button>
        </div>
        <el-table :data="filteredRecipes" size="small" data-test="recipes" :empty-text="$t('recipesView.kd7a48c')">
          <el-table-column :label="$t('recipesView.k85ad5d')" min-width="180"><template #default="{ row }"><strong>{{ row.name }}</strong> <span class="tiny muted">{{ row.templateName }} · v{{ row.version }}</span><div class="tiny muted">{{ row.description }}</div></template></el-table-column>
          <el-table-column :label="$t('recipesView.k749d76')" min-width="300"><template #default="{ row }"><span class="tiny">{{ valuesText(row) }}</span></template></el-table-column>
          <el-table-column :label="$t('recipesView.k97bfba')" min-width="150"><template #default="{ row }"><span class="tiny">{{ row.updatedBy }} · {{ formatTime(row.updatedTs) }}</span><div class="tiny muted">{{ row.note }}</div></template></el-table-column>
          <el-table-column :label="$t('recipesView.kf3ea6d')" width="440"><template #default="{ row }">
            <el-button size="small" type="primary" :data-test="`recipe-download-${row.id}`" @click="prepareDownload(row)">{{ $t('recipesView.ka1468f') }}</el-button>
            <el-button size="small" @click="editRecipe(row)">{{ $t('recipesView.ka7f814') }}</el-button>
            <el-button size="small" @click="showVersions(row)">{{ $t('recipesView.k989d1a') }}</el-button>
            <el-button size="small" :data-test="`recipe-compare-live-${row.id}`" @click="compare(row, null, 'live', null)">{{ $t('recipesView.kaf8b2e') }}</el-button>
            <el-button size="small" @click="upload(row)">{{ $t('recipesView.kb45fe4') }}</el-button>
            <el-button size="small" type="danger" @click="removeRecipe(row)">{{ $t('recipesView.k3755f5') }}</el-button>
          </template></el-table-column>
        </el-table>
        <h4>{{ $t('recipesView.kde9501') }}</h4>
        <el-table :data="downloads" size="small" data-test="recipe-downloads" :empty-text="$t('recipesView.k099342')">
          <el-table-column :label="$t('recipesView.ka1468f')" min-width="150"><template #default="{ row }">#{{ row.id }} {{ row.recipeName }} v{{ row.version }}</template></el-table-column>
          <el-table-column :label="$t('recipesView.k62e951')" width="100"><template #default="{ row }"><el-tag size="small" :type="downloadType[row.status] ?? 'info'" :data-test="`download-status-${row.id}`">{{ row.statusLabel }}</el-tag></template></el-table-column>
          <el-table-column :label="$t('recipesView.k749d76')" min-width="320"><template #default="{ row }">
            <div v-for="r in row.results" :key="r.key" class="tiny"><el-tag size="small" :type="paramType[r.status] ?? 'info'">{{ paramLabels[r.status] ?? r.status }}</el-tag> {{ r.name }}：{{ fmt(r.before) }} → <strong>{{ r.target }}</strong><span v-if="r.readBack != null">{{ $t('recipesView.k08b3a6', [r.readBack]) }}</span> <span class="muted">{{ r.status === 'done' ? '' : r.message }}</span></div>
          </template></el-table-column>
          <el-table-column :label="$t('recipesView.k28febb')" min-width="220"><template #default="{ row }"><span class="tiny">{{ row.requestedBy }}{{ row.approvedBy ? $t('recipesView.kabef81', [row.approvedBy]) : '' }} · {{ formatTime(row.createdTs) }}</span><div class="tiny" :class="row.status === 'PartiallyApplied' ? 'danger' : 'muted'">{{ row.message }}</div></template></el-table-column>
          <el-table-column label="" width="80"><template #default="{ row }"><el-button v-if="row.status === 'Running'" size="small" type="danger" @click="abort(row)">{{ $t('recipesView.k67847e') }}</el-button></template></el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane :label="$t('recipesView.k06d0f3')" name="templates">
        <div class="toolbar"><el-button type="primary" data-test="template-new" @click="editTemplate()">{{ $t('recipesView.ka38a51') }}</el-button></div>
        <el-table :data="templates" size="small" data-test="recipe-templates" :empty-text="$t('recipesView.kdf2250')">
          <el-table-column prop="name" :label="$t('recipesView.k06d0f3')" min-width="160" />
          <el-table-column :label="$t('recipesView.k17fc93')" width="120"><template #default="{ row }">{{ areaName(row.areaId) }}</template></el-table-column>
          <el-table-column :label="$t('recipesView.k749d76')" min-width="320"><template #default="{ row }"><span class="tiny">{{ row.parameters.map((p: RecipeParameter) => `${p.name}(${tagName(p.tagId)}${p.unit ? ' ' + p.unit : ''} [${p.min ?? '-'}, ${p.max ?? '-'}])`).join('；') }}</span></template></el-table-column>
          <el-table-column :label="$t('recipesView.kb38c92')" width="80"><template #default="{ row }">{{ row.dualApproval ? $t('recipesView.k1b5fdf') : '-' }}</template></el-table-column>
          <el-table-column :label="$t('recipesView.kf3ea6d')" width="150"><template #default="{ row }"><el-button size="small" @click="editTemplate(row)">{{ $t('recipesView.ka7f814') }}</el-button><el-button size="small" type="danger" @click="removeTemplate(row)">{{ $t('recipesView.k3755f5') }}</el-button></template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
  </div>

  <el-dialog v-model="templateDialog" :title="templateForm.id ? $t('recipesView.k472812') : $t('recipesView.ka50f21')" width="900px">
    <el-form label-width="90px">
      <el-form-item :label="$t('recipesView.k1be7ae')"><el-input v-model="templateForm.name" maxlength="100" data-test="template-name" /></el-form-item>
      <el-form-item :label="$t('recipesView.k17fc93')"><el-select v-model="templateForm.areaId"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
      <el-form-item :label="$t('recipesView.k26670d')"><el-input v-model="templateForm.description" maxlength="500" /></el-form-item>
      <el-form-item :label="$t('recipesView.k15f0c7')"><el-checkbox v-model="templateForm.dualApproval">{{ $t('recipesView.kbca2dc') }}</el-checkbox></el-form-item>
    </el-form>
    <el-table :data="templateForm.parameters" size="small" data-test="template-params">
      <el-table-column :label="$t('recipesView.k5a8c89')" width="120"><template #default="{ row }"><el-input v-model="row.key" size="small" placeholder="temp" /></template></el-table-column>
      <el-table-column :label="$t('recipesView.k1be7ae')" width="130"><template #default="{ row }"><el-input v-model="row.name" size="small" /></template></el-table-column>
      <el-table-column :label="$t('recipesView.k8cff1c')" min-width="200"><template #default="{ row }"><el-select v-model="row.tagId" filterable size="small"><el-option v-for="t in writableTags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select></template></el-table-column>
      <el-table-column :label="$t('recipesView.k8ec8c8')" width="80"><template #default="{ row }"><el-input v-model="row.unit" size="small" /></template></el-table-column>
      <el-table-column :label="$t('recipesView.kaf3af1')" width="100"><template #default="{ row }"><el-input-number v-model="row.min" size="small" :controls="false" style="width: 80px" /></template></el-table-column>
      <el-table-column :label="$t('recipesView.k0517c1')" width="100"><template #default="{ row }"><el-input-number v-model="row.max" size="small" :controls="false" style="width: 80px" /></template></el-table-column>
      <el-table-column label="" width="60"><template #default="{ $index }"><el-button size="small" link type="danger" @click="templateForm.parameters.splice($index, 1)">{{ $t('recipesView.k38cadf') }}</el-button></template></el-table-column>
    </el-table>
    <el-button size="small" style="margin-top: 8px" data-test="template-add-param" @click="templateForm.parameters.push({ key: `p${templateForm.parameters.length + 1}`, name: '', tagId: 0, unit: null, min: null, max: null, defaultValue: null })">{{ $t('recipesView.k4be702') }}</el-button>
    <template #footer><el-button @click="templateDialog = false">{{ $t('recipesView.k4d0b46') }}</el-button><el-button type="primary" data-test="template-save" @click="saveTemplate()">{{ $t('recipesView.kfadf24') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="recipeDialog" :title="recipeForm.id ? $t('recipesView.k3d3567', [recipeForm.version + 1]) : $t('recipesView.k91fabd')" width="680px">
    <el-form label-width="90px">
      <el-form-item :label="$t('recipesView.k06d0f3')"><el-select v-model="recipeForm.templateId" :disabled="!!recipeForm.id" data-test="recipe-template" @change="fillDefaults()"><el-option v-for="t in templates" :key="t.id" :value="t.id" :label="t.name" /></el-select></el-form-item>
      <el-form-item :label="$t('recipesView.k1be7ae')"><el-input v-model="recipeForm.name" maxlength="100" data-test="recipe-name" /></el-form-item>
      <el-form-item :label="$t('recipesView.k26670d')"><el-input v-model="recipeForm.description" maxlength="500" /></el-form-item>
      <el-form-item v-for="p in formTemplate?.parameters ?? []" :key="p.key" :label="p.name">
        <el-input-number v-model="recipeForm.values[p.key]" :min="p.min ?? undefined" :max="p.max ?? undefined" :data-test="`recipe-value-${p.key}`" />
        <span class="tiny muted" style="margin-left: 8px">{{ p.unit }} [{{ p.min ?? '-' }}, {{ p.max ?? '-' }}] · {{ tagName(p.tagId) }}</span>
      </el-form-item>
      <el-form-item :label="$t('recipesView.k8506f9')"><el-input v-model="recipeForm.note" maxlength="200" data-test="recipe-note" /></el-form-item>
    </el-form>
    <template #footer><el-button @click="recipeDialog = false">{{ $t('recipesView.k4d0b46') }}</el-button><el-button type="primary" data-test="recipe-save" @click="saveRecipe()">{{ $t('recipesView.kfadf24') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="downloadDialog" :title="$t('recipesView.k550ecc')" width="760px">
    <template v-if="checkResult">
      <p>{{ $t('recipesView.k85ad5d') }} <strong>{{ downloadRecipe?.name }}</strong> v{{ checkResult.version }}<el-tag v-if="checkResult.dualApproval" size="small" type="warning" style="margin-left: 8px">{{ $t('recipesView.k253ed0') }}</el-tag></p>
      <el-table :data="checkResult.parameters" size="small" data-test="recipe-check">
        <el-table-column prop="name" :label="$t('recipesView.k749d76')" width="120" />
        <el-table-column prop="tagName" :label="$t('recipesView.k8cff1c')" min-width="160" />
        <el-table-column :label="$t('recipesView.k6ac269')" width="170"><template #default="{ row }">{{ fmt(row.current) }} → <strong>{{ row.target }}</strong> {{ row.unit }}</template></el-table-column>
        <el-table-column :label="$t('recipesView.k40ffb3')" min-width="220"><template #default="{ row }"><span v-if="row.refusals.length" class="tiny danger">{{ row.refusals.join('；') }}</span><el-tag v-else size="small" type="success">{{ $t('recipesView.kdcc423') }}</el-tag></template></el-table-column>
      </el-table>
      <p v-if="!checkResult.ok" class="danger tiny">{{ $t('recipesView.kf75999') }}</p>
    </template>
    <template #footer><el-button @click="downloadDialog = false">{{ $t('recipesView.k4d0b46') }}</el-button><el-button type="primary" :disabled="!checkResult?.ok" data-test="recipe-download-confirm" @click="confirmDownload()">{{ checkResult?.dualApproval ? $t('recipesView.k61385c') : $t('recipesView.k00bb4a') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="versionsDialog" :title="$t('recipesView.kedfc09', [versionsRecipe?.name ?? ''])" width="760px">
    <el-table :data="versions" size="small" data-test="recipe-versions">
      <el-table-column :label="$t('recipesView.k989d1a')" width="70"><template #default="{ row }">v{{ row.version }}</template></el-table-column>
      <el-table-column :label="$t('recipesView.kc63f79')" width="80"><template #default="{ row }">{{ sourceLabels[row.source] ?? row.source }}</template></el-table-column>
      <el-table-column :label="$t('recipesView.k321b95')" min-width="260"><template #default="{ row }"><span class="tiny">{{ row.values.map((v: RecipeValue) => `${v.key}=${v.value}`).join('，') }}</span></template></el-table-column>
      <el-table-column :label="$t('recipesView.kc9c775')" min-width="160"><template #default="{ row }"><span class="tiny">{{ row.createdBy }} · {{ formatTime(row.createdTs) }}</span><div class="tiny muted">{{ row.note }}</div></template></el-table-column>
      <el-table-column label="" width="150"><template #default="{ row }">
        <el-button size="small" @click="compare(versionsRecipe!, row.version, String(versionsRecipe!.id), versionsRecipe!.version)">{{ $t('recipesView.k784dd3') }}</el-button>
      </template></el-table-column>
    </el-table>
  </el-dialog>

  <el-dialog v-model="compareDialog" :title="$t('recipesView.kc34f06')" width="640px">
    <template v-if="compareResult">
      <p class="tiny">{{ $t('recipesView.kee790c', [compareResult.leftLabel, compareResult.rightLabel, compareResult.differences]) }}</p>
      <el-table :data="compareResult.rows" size="small" data-test="recipe-compare" :row-class-name="diffClass">
        <el-table-column prop="name" :label="$t('recipesView.k749d76')" width="140" />
        <el-table-column :label="$t('recipesView.kdf3ee6')" width="140"><template #default="{ row }">{{ fmt(row.left) }} {{ row.unit }}</template></el-table-column>
        <el-table-column :label="$t('recipesView.k851cdd')" width="140"><template #default="{ row }">{{ fmt(row.right) }} {{ row.unit }}</template></el-table-column>
        <el-table-column label="" width="80"><template #default="{ row }"><el-tag v-if="row.different" size="small" type="warning">{{ $t('recipesView.kb3d980') }}</el-tag></template></el-table-column>
      </el-table>
    </template>
  </el-dialog>
</div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { areaApi, deviceApi, tagApi } from '../api'
import { recipeApi, type Recipe, type RecipeCheck, type RecipeCompare, type RecipeDownload, type RecipeParameter, type RecipeTemplate, type RecipeValue, type RecipeVersion } from '../api/recipes'
import { errorMessage } from '../api/http'
import { formatTime } from '../utils'
import { usePolling } from '../composables/usePolling'
import { tr } from '../i18n'

interface NamedTag { id: number; name: string; deviceId: number | null; writable: boolean }
const tab = ref('recipes')
const templates = ref<RecipeTemplate[]>([]), recipes = ref<Recipe[]>([]), downloads = ref<RecipeDownload[]>([])
const tags = ref<NamedTag[]>([]), devices = ref<{ id: number; name: string }[]>([]), areas = ref<{ id: number; name: string }[]>([])
const templateFilter = ref<number | null>(null)
const filteredRecipes = computed(() => recipes.value.filter(x => !templateFilter.value || x.templateId === templateFilter.value))
const writableTags = computed(() => tags.value.filter(x => x.writable && x.deviceId != null))
const downloadType: Record<string, string> = { Pending: 'warning', Running: 'warning', Completed: 'success', Failed: 'danger', PartiallyApplied: 'danger', Aborted: 'info', Rejected: 'info' }
const paramLabels: Record<string, string> = { pending: tr('recipesView.k25d23b'), done: tr('recipesView.k33246f'), failed: tr('recipesView.k3e3c80'), unknown: tr('recipesView.kd9c32a'), skipped: tr('recipesView.kafa3f3') }
const paramType: Record<string, string> = { done: 'success', failed: 'danger', unknown: 'danger' }
const sourceLabels: Record<string, string> = { manual: tr('recipesView.ka7f814'), upload: tr('recipesView.kb45fe4'), project: tr('recipesView.ke42503') }
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
  }, tr('recipesView.kf6b7a4'))
}

const templateDialog = ref(false)
const templateForm = reactive<{ id?: number; name: string; areaId: number; description: string | null; dualApproval: boolean; revision: number; parameters: RecipeParameter[] }>({ name: '', areaId: 1, description: null, dualApproval: false, revision: 0, parameters: [] })
function editTemplate(t?: RecipeTemplate): void {
  Object.assign(templateForm, t ? { ...t, parameters: t.parameters.map(p => ({ ...p })) } : { id: undefined, name: '', areaId: areas.value[0]?.id ?? 1, description: null, dualApproval: false, revision: 0, parameters: [] })
  templateDialog.value = true
}
async function saveTemplate(): Promise<void> {
  if (await guard(() => recipeApi.saveTemplate({ ...templateForm }), tr('recipesView.k40525a'))) { templateDialog.value = false; ElMessage.success(tr('recipesView.kcdfab9')); await refresh() }
}
async function removeTemplate(t: RecipeTemplate): Promise<void> {
  try { await ElMessageBox.confirm(tr('recipesView.kc2c3dd', [t.name]), tr('recipesView.k3755f5'), { type: 'warning' }) } catch { return }
  if (await guard(() => recipeApi.removeTemplate(t.id), tr('recipesView.k72250c'))) await refresh()
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
  if (await guard(async () => { const r = await recipeApi.save({ ...recipeForm, values }); if (r.warnings.length) ElMessage.warning(r.warnings.join('；')) }, tr('recipesView.k40525a'))) {
    recipeDialog.value = false; ElMessage.success(tr('recipesView.ke90f33')); await refresh()
  }
}
async function removeRecipe(r: Recipe): Promise<void> {
  try { await ElMessageBox.confirm(tr('recipesView.k243149', [r.name]), tr('recipesView.k3755f5'), { type: 'warning' }) } catch { return }
  if (await guard(() => recipeApi.remove(r.id), tr('recipesView.k72250c'))) await refresh()
}
async function upload(r: Recipe | null): Promise<void> {
  const templateId = r?.templateId ?? templateFilter.value; if (!templateId) return
  let input = ''
  try { input = (await ElMessageBox.prompt(r ? tr('recipesView.k6739cb', [r.name]) : tr('recipesView.kb4b7ad'), tr('recipesView.k66e0f9'), { inputValue: r ? tr('recipesView.k66e0f9') : '', inputPattern: r ? undefined : /\S/, inputErrorMessage: tr('recipesView.k1502ce') })).value ?? '' } catch { return }
  if (await guard(async () => { const res = await recipeApi.upload(templateId, r?.id ?? null, r ? null : input.trim(), r ? input : null); if (res.warnings.length) ElMessage.warning(res.warnings.join('；')) }, tr('recipesView.kf17ff8'))) {
    ElMessage.success(tr('recipesView.k4048be')); await refresh()
  }
}

const downloadDialog = ref(false), downloadRecipe = ref<Recipe | null>(null), checkResult = ref<RecipeCheck | null>(null)
async function prepareDownload(r: Recipe): Promise<void> {
  downloadRecipe.value = r; checkResult.value = null
  if (await guard(async () => { checkResult.value = await recipeApi.check(r.id, r.version) }, tr('recipesView.kd728cf'))) downloadDialog.value = true
}
async function confirmDownload(): Promise<void> {
  const r = downloadRecipe.value!; const version = checkResult.value!.version
  await guard(async () => {
    const d = await recipeApi.download(r.id, version)
    if (d.status === 'Pending') ElMessage.success(tr('recipesView.k33430c'))
    else if (d.status === 'Rejected') ElMessage.error(d.message ?? tr('recipesView.k5bf97e'))
    else ElMessage.success(tr('recipesView.kdfd357', [d.id]))
  }, tr('recipesView.k2d0918'))
  downloadDialog.value = false; await refresh()
}
async function abort(d: RecipeDownload): Promise<void> { if (await guard(() => recipeApi.abort(d.id), tr('recipesView.k50cf66'))) await refresh() }

const versionsDialog = ref(false), versionsRecipe = ref<Recipe | null>(null), versions = ref<RecipeVersion[]>([])
async function showVersions(r: Recipe): Promise<void> {
  versionsRecipe.value = r
  if (await guard(async () => { versions.value = await recipeApi.versions(r.id) }, tr('recipesView.kf6b7a4'))) versionsDialog.value = true
}
const diffClass = ({ row }: { row: { different: boolean } }) => row.different ? 'diff-row' : ''
const compareDialog = ref(false), compareResult = ref<RecipeCompare | null>(null)
async function compare(r: Recipe, leftVersion: number | null, right: string, rightVersion: number | null): Promise<void> {
  if (await guard(async () => { compareResult.value = await recipeApi.compare(r.id, leftVersion, right, rightVersion) }, tr('recipesView.k3a7c48'))) compareDialog.value = true
}

// 下载进行中要秒级进度；全部结束后每轮只判本地状态，不发请求。
usePolling(async () => {
  if (!downloads.value.some(x => x.status === 'Running')) return
  downloads.value = await recipeApi.downloads().catch(() => downloads.value)
}, 1000)
onMounted(async () => {
  await refresh()
})
</script>

<style scoped>
.danger { color: var(--el-color-danger); }
:deep(.diff-row) { background: var(--el-color-warning-light-9); }
</style>
