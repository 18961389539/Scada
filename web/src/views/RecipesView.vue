<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><span class="spacer" /><el-button :loading="loading" @click="refresh()">{{ $t('recipesView.k38108e') }}</el-button></div>
    <p class="page-desc">{{ $t('recipesView.k654ddd') }}</p>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="recipes-load-error" />
    <el-alert v-if="areasError" :title="areasError" type="error" :closable="false" show-icon data-test="recipes-areas-error" />
    <el-alert v-if="downloadsError" :title="downloadsError" type="error" :closable="false" show-icon data-test="recipe-downloads-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="recipes-action-error" />
    <el-tabs v-model="tab">
      <el-tab-pane :label="$t('recipesView.k85ad5d')" name="recipes">
        <div class="toolbar">
          <el-input v-model="recipeSearch" clearable :aria-label="$t('recipesView.searchRecipes')" :placeholder="$t('recipesView.searchRecipes')" class="catalog-search" />
          <el-select v-model="templateFilter" clearable filterable :aria-label="$t('recipesView.k06d0f3')" :placeholder="$t('recipesView.k146966')" style="width: 220px" data-test="recipe-template-filter"><el-option v-for="t in templates" :key="t.id" :value="t.id" :label="t.name" /></el-select>
          <el-button v-if="recipeSearch || templateFilter" data-test="recipe-clear-filters" @click="recipeSearch = ''; templateFilter = null">{{ $t('listFilters.clear') }}</el-button>
          <el-button type="primary" :disabled="!configurableTemplates.length" data-test="recipe-new" @click="editRecipe()">{{ $t('recipesView.k91fabd') }}</el-button>
          <el-button :disabled="!canConfigureTemplate(templateFilter)" data-test="recipe-upload" @click="upload(null)">{{ $t('recipesView.k1680aa') }}</el-button>
        </div>
        <p class="catalog-result muted" role="status" data-test="recipe-result-count">{{ $t('listFilters.resultCount', [filteredRecipes.length, recipes.length]) }}</p>
        <el-table :data="filteredRecipes" v-loading="loading" size="small" data-test="recipes" :empty-text="loadError || (recipes.length ? $t('listFilters.noMatches') : $t('recipesView.kd7a48c'))">
          <el-table-column :label="$t('recipesView.k85ad5d')" min-width="180"><template #default="{ row }"><strong>{{ row.name }}</strong> <span class="tiny muted">{{ row.templateName }} · v{{ row.version }}</span><div class="tiny muted">{{ row.description }}</div><el-button link type="primary" :data-test="`recipe-records-${row.id}`" @click="showDownloads(row.id)">{{ $t('recipesView.viewDownloads') }}</el-button></template></el-table-column>
          <el-table-column :label="$t('recipesView.k749d76')" min-width="300"><template #default="{ row }"><span class="tiny">{{ valuesText(row) }}</span></template></el-table-column>
          <el-table-column :label="$t('recipesView.k97bfba')" min-width="150"><template #default="{ row }"><span class="tiny">{{ row.updatedBy }} · {{ formatTime(row.updatedTs) }}</span><div class="tiny muted">{{ row.note }}</div></template></el-table-column>
          <el-table-column :label="$t('recipesView.kf3ea6d')" width="440"><template #default="{ row }">
            <el-button v-if="canControlArea(row.areaId)" size="small" type="primary" :loading="checking && downloadRecipe?.id === row.id" :disabled="submitting" :data-test="`recipe-download-${row.id}`" @click="prepareDownload(row)">{{ $t('recipesView.ka1468f') }}</el-button>
            <el-button v-if="canConfigureArea(row.areaId)" size="small" @click="editRecipe(row)">{{ $t('recipesView.ka7f814') }}</el-button>
            <el-button size="small" @click="showVersions(row)">{{ $t('recipesView.k989d1a') }}</el-button>
            <el-button size="small" :data-test="`recipe-compare-live-${row.id}`" @click="compare(row, null, 'live', null)">{{ $t('recipesView.kaf8b2e') }}</el-button>
            <el-button v-if="canConfigureArea(row.areaId)" size="small" @click="upload(row)">{{ $t('recipesView.kb45fe4') }}</el-button>
            <el-button v-if="canConfigureArea(row.areaId)" size="small" type="danger" @click="removeRecipe(row)">{{ $t('recipesView.k3755f5') }}</el-button>
          </template></el-table-column>
        </el-table>
        <div ref="downloadSection" class="download-section">
        <h4>{{ $t('recipesView.kde9501') }}</h4>
        <el-alert v-if="latestDownload" :type="latestDownload.status === 'Completed' ? 'success' : ['Pending', 'Running'].includes(latestDownload.status) ? 'warning' : ['Failed', 'PartiallyApplied', 'Rejected'].includes(latestDownload.status) ? 'error' : 'info'" :title="$t('recipesView.latestDownload', [latestDownload.id, latestDownload.recipeName, latestDownload.version, latestDownload.statusLabel])" :closable="false" show-icon data-test="recipe-latest-download" />
        <p class="tiny muted">{{ $t('recipesView.downloadsHint') }}</p>
        <div class="catalog-filters">
          <el-input v-model="downloadSearch" clearable :aria-label="$t('recipesView.searchDownloads')" :placeholder="$t('recipesView.searchDownloads')" class="catalog-search" />
          <el-select v-model="downloadRecipeId" clearable filterable :placeholder="$t('recipesView.allRecipes')" :aria-label="$t('recipesView.filterRecipe')" class="catalog-select" data-test="download-recipe-filter"><el-option v-for="r in downloadRecipeOptions" :key="r.id" :value="r.id" :label="`#${r.id} · ${r.name}`" /></el-select>
          <el-select v-model="downloadState" :aria-label="$t('recipesView.k62e951')" class="catalog-select" data-test="download-state-filter"><el-option value="all" :label="$t('recipesView.allStates')" /><el-option value="active" :label="$t('recipesView.activeStates')" /><el-option value="attention" :label="$t('recipesView.attentionStates')" /><el-option value="completed" :label="$t('recipesView.completedState')" /></el-select>
          <el-button v-if="hasDownloadFilters" data-test="download-clear-filters" @click="clearDownloadFilters">{{ $t('listFilters.clear') }}</el-button>
          <span class="catalog-count muted" role="status" data-test="download-result-count">{{ $t('recipesView.downloadCount', [filteredDownloads.length, downloads.length, activeDownloads]) }}</span>
        </div>
        <el-table :data="filteredDownloads" size="small" data-test="recipe-downloads" :empty-text="downloadsError || loadError || (downloads.length ? $t('listFilters.noMatches') : $t('recipesView.k099342'))">
          <el-table-column :label="$t('recipesView.ka1468f')" min-width="150"><template #default="{ row }">#{{ row.id }} {{ row.recipeName }} v{{ row.version }}</template></el-table-column>
          <el-table-column :label="$t('recipesView.k62e951')" width="100"><template #default="{ row }"><el-tag size="small" :type="downloadType[row.status] ?? 'info'" :data-test="`download-status-${row.id}`">{{ row.statusLabel }}</el-tag></template></el-table-column>
          <el-table-column :label="$t('recipesView.k749d76')" min-width="320"><template #default="{ row }">
            <div v-for="r in row.results" :key="r.key" class="tiny"><el-tag size="small" :type="paramType[r.status] ?? 'info'">{{ paramLabels[r.status] ?? r.status }}</el-tag> {{ r.name }}：{{ fmt(r.before) }} → <strong>{{ r.target }}</strong><span v-if="r.readBack != null">{{ $t('recipesView.k08b3a6', [r.readBack]) }}</span> <span class="muted">{{ r.status === 'done' ? '' : r.message }}</span></div>
          </template></el-table-column>
          <el-table-column :label="$t('recipesView.k28febb')" min-width="220"><template #default="{ row }"><span class="tiny">{{ row.requestedBy }}{{ row.approvedBy ? $t('recipesView.kabef81', [row.approvedBy]) : '' }} · {{ formatTime(row.createdTs) }}</span><div class="tiny" :class="row.status === 'PartiallyApplied' ? 'danger' : 'muted'">{{ row.message }}</div></template></el-table-column>
          <el-table-column label="" width="80"><template #default="{ row }"><el-button v-if="row.status === 'Running' && canControlArea(row.areaId)" size="small" type="danger" :data-test="`recipe-abort-${row.id}`" @click="abort(row)">{{ $t('recipesView.k67847e') }}</el-button></template></el-table-column>
        </el-table>
        </div>
      </el-tab-pane>

      <el-tab-pane :label="$t('recipesView.k06d0f3')" name="templates">
        <div class="toolbar"><el-input v-model="templateSearch" clearable :aria-label="$t('recipesView.searchTemplates')" :placeholder="$t('recipesView.searchTemplates')" class="catalog-search" /><el-button v-if="configurableAreas.length" type="primary" data-test="template-new" @click="editTemplate()">{{ $t('recipesView.ka38a51') }}</el-button><span class="catalog-count muted" role="status" data-test="recipe-template-count">{{ $t('listFilters.resultCount', [filteredTemplates.length, templates.length]) }}</span></div>
        <el-table :data="filteredTemplates" v-loading="loading" size="small" data-test="recipe-templates" :empty-text="loadError || (templates.length ? $t('listFilters.noMatches') : $t('recipesView.kdf2250'))">
          <el-table-column prop="name" :label="$t('recipesView.k06d0f3')" min-width="160" />
          <el-table-column :label="$t('recipesView.k17fc93')" width="120"><template #default="{ row }">{{ areaName(row.areaId) }}</template></el-table-column>
          <el-table-column :label="$t('recipesView.k749d76')" min-width="320"><template #default="{ row }"><span class="tiny">{{ row.parameters.map((p: RecipeParameter) => `${p.name}(${tagName(p.tagId)}${p.unit ? ' ' + p.unit : ''} [${p.min ?? '-'}, ${p.max ?? '-'}])`).join('；') }}</span></template></el-table-column>
          <el-table-column :label="$t('recipesView.kb38c92')" width="80"><template #default="{ row }">{{ row.dualApproval ? $t('recipesView.k1b5fdf') : '-' }}</template></el-table-column>
          <el-table-column :label="$t('recipesView.kf3ea6d')" width="150"><template #default="{ row }"><el-button v-if="canConfigureArea(row.areaId)" size="small" @click="editTemplate(row)">{{ $t('recipesView.ka7f814') }}</el-button><el-button v-if="canConfigureArea(row.areaId)" size="small" type="danger" @click="removeTemplate(row)">{{ $t('recipesView.k3755f5') }}</el-button></template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
  </div>

  <el-dialog v-model="templateDialog" :title="templateForm.id ? $t('recipesView.k472812') : $t('recipesView.ka50f21')" width="900px" :close-on-click-modal="!savingTemplate" :close-on-press-escape="!savingTemplate" :show-close="!savingTemplate" data-test="recipe-template-dialog">
    <el-alert v-if="templateError" :title="templateError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="recipe-template-error" />
    <el-form label-width="90px" :disabled="savingTemplate">
      <el-form-item :label="$t('recipesView.k1be7ae')"><el-input v-model="templateForm.name" maxlength="100" data-test="template-name" /></el-form-item>
      <el-form-item :label="$t('recipesView.k17fc93')"><el-select v-model="templateForm.areaId" data-test="recipe-template-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
      <el-form-item :label="$t('recipesView.k26670d')"><el-input v-model="templateForm.description" maxlength="500" /></el-form-item>
      <el-form-item :label="$t('recipesView.k15f0c7')"><el-checkbox v-model="templateForm.dualApproval">{{ $t('recipesView.kbca2dc') }}</el-checkbox></el-form-item>
    </el-form>
    <el-table :data="templateForm.parameters" :inert="savingTemplate" size="small" data-test="template-params">
      <el-table-column :label="$t('recipesView.k5a8c89')" width="120"><template #default="{ row }"><el-input v-model="row.key" size="small" placeholder="temp" /></template></el-table-column>
      <el-table-column :label="$t('recipesView.k1be7ae')" width="130"><template #default="{ row }"><el-input v-model="row.name" size="small" /></template></el-table-column>
      <el-table-column :label="$t('recipesView.k8cff1c')" min-width="200"><template #default="{ row }"><el-select v-model="row.tagId" filterable size="small"><el-option v-for="t in configurableTags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select></template></el-table-column>
      <el-table-column :label="$t('recipesView.k8ec8c8')" width="80"><template #default="{ row }"><el-input v-model="row.unit" size="small" /></template></el-table-column>
      <el-table-column :label="$t('recipesView.kaf3af1')" width="100"><template #default="{ row }"><el-input-number v-model="row.min" size="small" :controls="false" style="width: 80px" /></template></el-table-column>
      <el-table-column :label="$t('recipesView.k0517c1')" width="100"><template #default="{ row }"><el-input-number v-model="row.max" size="small" :controls="false" style="width: 80px" /></template></el-table-column>
      <el-table-column label="" width="60"><template #default="{ $index }"><el-button size="small" link type="danger" @click="templateForm.parameters.splice($index, 1)">{{ $t('recipesView.k38cadf') }}</el-button></template></el-table-column>
    </el-table>
    <el-button size="small" :disabled="savingTemplate" style="margin-top: 8px" data-test="template-add-param" @click="templateForm.parameters.push({ key: `p${templateForm.parameters.length + 1}`, name: '', tagId: configurableTags[0]?.id ?? 0, unit: null, min: null, max: null, defaultValue: null })">{{ $t('recipesView.k4be702') }}</el-button>
    <template #footer><el-button :disabled="savingTemplate" @click="templateDialog = false">{{ $t('recipesView.k4d0b46') }}</el-button><el-button type="primary" :loading="savingTemplate" data-test="template-save" @click="saveTemplate()">{{ $t('recipesView.kfadf24') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="recipeDialog" :title="recipeForm.id ? $t('recipesView.k3d3567', [recipeForm.version + 1]) : $t('recipesView.k91fabd')" width="680px" :close-on-click-modal="!savingRecipe" :close-on-press-escape="!savingRecipe" :show-close="!savingRecipe" data-test="recipe-edit-dialog">
    <el-alert v-if="recipeError" :title="recipeError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="recipe-save-error" />
    <el-form label-width="90px" :disabled="savingRecipe">
      <el-form-item :label="$t('recipesView.k06d0f3')"><el-select v-model="recipeForm.templateId" :disabled="!!recipeForm.id" data-test="recipe-template" @change="fillDefaults()"><el-option v-for="t in (recipeForm.id ? templates : configurableTemplates)" :key="t.id" :value="t.id" :label="t.name" /></el-select></el-form-item>
      <el-form-item :label="$t('recipesView.k1be7ae')"><el-input v-model="recipeForm.name" maxlength="100" data-test="recipe-name" /></el-form-item>
      <el-form-item :label="$t('recipesView.k26670d')"><el-input v-model="recipeForm.description" maxlength="500" /></el-form-item>
      <el-form-item v-for="p in formTemplate?.parameters ?? []" :key="p.key" :label="p.name">
        <el-input-number v-model="recipeForm.values[p.key]" :min="p.min ?? undefined" :max="p.max ?? undefined" :data-test="`recipe-value-${p.key}`" />
        <span class="tiny muted" style="margin-left: 8px">{{ p.unit }} [{{ p.min ?? '-' }}, {{ p.max ?? '-' }}] · {{ tagName(p.tagId) }}</span>
      </el-form-item>
      <el-form-item :label="$t('recipesView.k8506f9')"><el-input v-model="recipeForm.note" maxlength="200" data-test="recipe-note" /></el-form-item>
    </el-form>
    <template #footer><el-button :disabled="savingRecipe" @click="recipeDialog = false">{{ $t('recipesView.k4d0b46') }}</el-button><el-button type="primary" :loading="savingRecipe" data-test="recipe-save" @click="saveRecipe()">{{ $t('recipesView.kfadf24') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="downloadDialog" :title="$t('recipesView.k550ecc')" width="760px" :close-on-click-modal="!submitting" :close-on-press-escape="!submitting" :show-close="!submitting" :before-close="closeDownload" data-test="recipe-download-dialog">
    <el-alert v-if="downloadError" :title="downloadError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="recipe-download-error" />
    <p v-if="downloadRecipe">{{ $t('recipesView.k85ad5d') }} <strong>{{ downloadRecipe.name }}</strong> v{{ downloadRecipe.version }}</p>
    <p v-if="checking" role="status" data-test="recipe-checking">{{ $t('recipesView.checking') }}</p>
    <el-button v-else-if="downloadError" data-test="recipe-check-retry" :disabled="submitting" @click="prepareDownload(downloadRecipe!)">{{ $t('recipesView.retryCheck') }}</el-button>
    <template v-if="checkResult">
      <el-tag v-if="checkResult.dualApproval" size="small" type="warning">{{ $t('recipesView.k253ed0') }}</el-tag>
      <el-table :data="checkResult.parameters" size="small" data-test="recipe-check">
        <el-table-column prop="name" :label="$t('recipesView.k749d76')" width="120" />
        <el-table-column prop="tagName" :label="$t('recipesView.k8cff1c')" min-width="160" />
        <el-table-column :label="$t('recipesView.k6ac269')" width="170"><template #default="{ row }">{{ fmt(row.current) }} → <strong>{{ row.target }}</strong> {{ row.unit }}</template></el-table-column>
        <el-table-column :label="$t('recipesView.k40ffb3')" min-width="220"><template #default="{ row }"><span v-if="row.refusals.length" class="tiny danger">{{ row.refusals.join('；') }}</span><el-tag v-else size="small" type="success">{{ $t('recipesView.kdcc423') }}</el-tag></template></el-table-column>
      </el-table>
      <p v-if="!checkResult.ok" class="danger tiny">{{ $t('recipesView.kf75999') }}</p>
      <p v-for="reason in checkResult.refusals" :key="reason" class="danger tiny">{{ reason }}</p>
    </template>
    <template #footer><el-button :disabled="submitting" @click="downloadDialog = false">{{ $t('recipesView.k4d0b46') }}</el-button><el-button type="primary" :loading="submitting" :disabled="!canConfirmDownload" data-test="recipe-download-confirm" @click="confirmDownload()">{{ checkResult?.dualApproval ? $t('recipesView.k61385c') : $t('recipesView.k00bb4a') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="versionsDialog" :title="$t('recipesView.kedfc09', [versionsRecipe?.name ?? ''])" width="760px">
    <el-alert v-if="versionsError" :title="versionsError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="recipe-versions-error" />
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
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { areaApi, deviceApi, tagApi } from '../api'
import { recipeApi, type Recipe, type RecipeCheck, type RecipeCompare, type RecipeDownload, type RecipeParameter, type RecipeTemplate, type RecipeValue, type RecipeVersion } from '../api/recipes'
import { errorMessage } from '../api/http'
import { useActionError } from '../composables/useActionError'
import { formatTime } from '../utils'
import { can } from '../access/policies'
import { useAuthStore } from '../stores/auth'
import { usePolling } from '../composables/usePolling'
import { tr } from '../i18n'

interface NamedTag { id: number; name: string; deviceId: number | null; writable: boolean }
const tab = ref('recipes')
const loading = ref(false), downloadSection = ref<HTMLElement>(), lastSubmitted = ref<RecipeDownload | null>(null)
let disposed = false, refreshSeq = 0, downloadsSeq = 0, checkSeq = 0
const templates = ref<RecipeTemplate[]>([]), recipes = ref<Recipe[]>([]), downloads = ref<RecipeDownload[]>([]), loadError = ref(''), areasError = ref(''), downloadsError = ref('')
const { actionError, guard } = useActionError()
const templateError = ref(''), recipeError = ref(''), downloadError = ref(''), versionsError = ref('')
const auth = useAuthStore()
const canConfigureArea = (areaId?: number | null) => can(auth.user, 'Configure', areaId)
const canControlArea = (areaId?: number | null) => can(auth.user, 'Control', areaId)
const tags = ref<NamedTag[]>([]), devices = ref<{ id: number; name: string; areaId?: number | null }[]>([]), areas = ref<{ id: number; name: string }[]>([])
const configurableAreas = computed(() => areas.value.filter(a => canConfigureArea(a.id)))
const configurableTemplates = computed(() => templates.value.filter(t => canConfigureArea(t.areaId)))
function canConfigureTemplate(id: number | null): boolean {
  if (id == null) return false
  const template = templates.value.find(x => x.id === id)
  return !!template && canConfigureArea(template.areaId)
}
const templateFilter = ref<number | null>(null)
const recipeSearch = ref(''), templateSearch = ref(''), downloadSearch = ref(''), downloadState = ref('all'), downloadRecipeId = ref<number | null>(null)
function matches(query: string, ...values: unknown[]): boolean {
  const text = values.filter(v => v != null).join(' ').toLocaleLowerCase()
  return query.trim().toLocaleLowerCase().split(/\s+/).every(term => text.includes(term))
}
const filteredRecipes = computed(() => recipes.value.filter(r => (!templateFilter.value || r.templateId === templateFilter.value) && matches(recipeSearch.value, r.name, r.templateName, r.description, r.note, r.updatedBy, `#${r.id}`, valuesText(r))))
const filteredTemplates = computed(() => templates.value.filter(t => matches(templateSearch.value, t.name, t.description, areaName(t.areaId), `#${t.id}`, ...t.parameters.map(p => `${p.key} ${p.name} ${tagName(p.tagId)}`))))
const activeDownloads = computed(() => downloads.value.filter(d => ['Pending', 'Running'].includes(d.status)).length)
const hasDownloadFilters = computed(() => !!downloadSearch.value || !!downloadRecipeId.value || downloadState.value !== 'all')
const filteredDownloads = computed(() => downloads.value.filter(d => {
  if (downloadRecipeId.value && d.recipeId !== downloadRecipeId.value) return false
  if (downloadState.value === 'active' && !['Pending', 'Running'].includes(d.status)) return false
  if (downloadState.value === 'attention' && !['Failed', 'PartiallyApplied', 'Aborted', 'Rejected'].includes(d.status)) return false
  if (downloadState.value === 'completed' && d.status !== 'Completed') return false
  return matches(downloadSearch.value, `#${d.id}`, d.recipeName, d.requestedBy, d.approvedBy, d.message, ...d.results.map(r => `${r.name} ${r.key} ${r.message ?? ''}`))
}))
const downloadRecipeOptions = computed(() => {
  const options = new Map(recipes.value.map(r => [r.id, { id: r.id, name: r.name }]))
  for (const d of downloads.value) if (!options.has(d.recipeId)) options.set(d.recipeId, { id: d.recipeId, name: d.recipeName })
  return [...options.values()].sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }))
})
const latestDownload = computed(() => lastSubmitted.value ? downloads.value.find(d => d.id === lastSubmitted.value!.id) ?? lastSubmitted.value : null)
function clearDownloadFilters(): void { downloadSearch.value = ''; downloadRecipeId.value = null; downloadState.value = 'all' }
async function showDownloads(recipeId: number): Promise<void> {
  clearDownloadFilters(); downloadRecipeId.value = recipeId; tab.value = 'recipes'
  await nextTick(); if (!disposed) downloadSection.value?.scrollIntoView({ block: 'start' })
}
const writableTags = computed(() => tags.value.filter(x => x.writable && x.deviceId != null))
const configurableTags = computed(() => writableTags.value.filter(t => canConfigureArea(devices.value.find(d => d.id === t.deviceId)?.areaId)))
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

async function refresh(): Promise<void> {
  if (disposed) return
  const seq = ++refreshSeq; loading.value = true
  await Promise.all([loadDownloads(), (async () => {
    try {
      const [d, t, a, allTemplates, allRecipes] = await Promise.all([
        deviceApi.list(), tagApi.list(),
        areaApi.list().then(list => ({ list, error: '' }), error => ({ list: areas.value, error: errorMessage(error, tr('recipesView.areasLoadFailed')) })),
        recipeApi.templates(), recipeApi.list()
      ])
      if (disposed || seq !== refreshSeq) return
      devices.value = d; tags.value = t; areas.value = a.list; areasError.value = a.error
      templates.value = allTemplates; recipes.value = allRecipes; loadError.value = ''
    } catch (e) {
      if (!disposed && seq === refreshSeq) loadError.value = errorMessage(e, tr('recipesView.kf6b7a4'))
    }
  })()])
  if (!disposed && seq === refreshSeq) loading.value = false
}
async function loadDownloads(): Promise<void> {
  const seq = ++downloadsSeq
  try {
    const result = await recipeApi.downloads()
    if (!disposed && seq === downloadsSeq) { downloads.value = result; downloadsError.value = '' }
  } catch (e) {
    if (!disposed && seq === downloadsSeq) downloadsError.value = errorMessage(e, tr('recipesView.downloadsLoadFailed'))
  }
}

const templateDialog = ref(false), savingTemplate = ref(false)
const templateForm = reactive<{ id?: number; name: string; areaId: number; description: string | null; dualApproval: boolean; revision: number; parameters: RecipeParameter[] }>({ name: '', areaId: 1, description: null, dualApproval: false, revision: 0, parameters: [] })
function editTemplate(t?: RecipeTemplate): void {
  if (savingTemplate.value) return
  const areaId = configurableAreas.value[0]?.id
  if (!t && areaId == null) return
  templateError.value = ''
  Object.assign(templateForm, t ? { ...t, parameters: t.parameters.map(p => ({ ...p })) } : { id: undefined, name: '', areaId, description: null, dualApproval: false, revision: 0, parameters: [] })
  templateDialog.value = true
}
async function saveTemplate(): Promise<void> {
  if (savingTemplate.value || disposed) return
  savingTemplate.value = true
  const payload = JSON.parse(JSON.stringify(templateForm))
  try {
    if (await guard(() => recipeApi.saveTemplate(payload), tr('recipesView.k40525a'), templateError) && !disposed) { templateDialog.value = false; ElMessage.success(tr('recipesView.kcdfab9')); await refresh() }
  } finally { savingTemplate.value = false }
}
async function removeTemplate(t: RecipeTemplate): Promise<void> {
  try { await ElMessageBox.confirm(tr('recipesView.kc2c3dd', [t.name]), tr('recipesView.k3755f5'), { type: 'warning' }) } catch { return }
  if (await guard(() => recipeApi.removeTemplate(t.id), tr('recipesView.k72250c'), actionError)) await refresh()
}

const recipeDialog = ref(false), savingRecipe = ref(false)
const recipeForm = reactive<{ id?: number; templateId: number; name: string; description: string | null; note: string | null; version: number; values: Record<string, number> }>({ templateId: 0, name: '', description: null, note: null, version: 0, values: {} })
const formTemplate = computed(() => templates.value.find(x => x.id === recipeForm.templateId))
function fillDefaults(): void {
  const values: Record<string, number> = {}
  for (const p of formTemplate.value?.parameters ?? []) values[p.key] = recipeForm.values[p.key] ?? p.defaultValue ?? p.min ?? 0
  recipeForm.values = values
}
function editRecipe(r?: Recipe): void {
  if (savingRecipe.value) return
  const templateId = templateFilter.value && canConfigureTemplate(templateFilter.value) ? templateFilter.value : configurableTemplates.value[0]?.id ?? 0
  recipeError.value = ''
  Object.assign(recipeForm, r ? { id: r.id, templateId: r.templateId, name: r.name, description: r.description, note: null, version: r.version, values: Object.fromEntries(r.values.map(v => [v.key, v.value])) }
    : { id: undefined, templateId, name: '', description: null, note: null, version: 0, values: {} })
  fillDefaults(); recipeDialog.value = true
}
async function saveRecipe(): Promise<void> {
  if (savingRecipe.value || disposed) return
  const values = Object.entries(recipeForm.values).map(([key, value]) => ({ key, value: Number(value) }))
  const payload = { ...recipeForm, values }
  savingRecipe.value = true
  try {
    if (await guard(async () => { const r = await recipeApi.save(payload); if (!disposed && r.warnings.length) ElMessage.warning(r.warnings.join('；')) }, tr('recipesView.k40525a'), recipeError) && !disposed) {
      recipeDialog.value = false; ElMessage.success(tr('recipesView.ke90f33')); await refresh()
    }
  } finally { savingRecipe.value = false }
}
async function removeRecipe(r: Recipe): Promise<void> {
  try { await ElMessageBox.confirm(tr('recipesView.k243149', [r.name]), tr('recipesView.k3755f5'), { type: 'warning' }) } catch { return }
  if (await guard(() => recipeApi.remove(r.id), tr('recipesView.k72250c'), actionError)) await refresh()
}
async function upload(r: Recipe | null): Promise<void> {
  const templateId = r?.templateId ?? templateFilter.value; if (!templateId) return
  let input = ''
  try { input = (await ElMessageBox.prompt(r ? tr('recipesView.k6739cb', [r.name]) : tr('recipesView.kb4b7ad'), tr('recipesView.k66e0f9'), { inputValue: r ? tr('recipesView.k66e0f9') : '', inputPattern: r ? undefined : /\S/, inputErrorMessage: tr('recipesView.k1502ce') })).value ?? '' } catch { return }
  if (await guard(async () => { const res = await recipeApi.upload(templateId, r?.id ?? null, r ? null : input.trim(), r ? input : null); if (res.warnings.length) ElMessage.warning(res.warnings.join('；')) }, tr('recipesView.kf17ff8'), actionError)) {
    ElMessage.success(tr('recipesView.k4048be')); await refresh()
  }
}

const downloadDialog = ref(false), downloadRecipe = ref<Recipe | null>(null), checkResult = ref<RecipeCheck | null>(null), checking = ref(false), submitting = ref(false)
const canConfirmDownload = computed(() => downloadDialog.value && !checking.value && !submitting.value && !!checkResult.value?.ok && checkResult.value.recipeId === downloadRecipe.value?.id && checkResult.value.version === downloadRecipe.value?.version)
watch(downloadDialog, visible => { if (!visible) { checkSeq++; checking.value = false; checkResult.value = null } }, { flush: 'sync' })
function closeDownload(done: () => void): void { if (!submitting.value) done() }
async function prepareDownload(r: Recipe): Promise<void> {
  if (disposed || submitting.value || (checking.value && downloadRecipe.value?.id === r.id && downloadRecipe.value.version === r.version)) return
  const target: Recipe = JSON.parse(JSON.stringify(r)), seq = ++checkSeq
  downloadRecipe.value = target; checkResult.value = null; downloadError.value = ''; checking.value = true; downloadDialog.value = true
  try {
    const result = await recipeApi.check(target.id, target.version)
    if (disposed || seq !== checkSeq || !downloadDialog.value) return
    if (result.recipeId !== target.id || result.version !== target.version) { downloadError.value = tr('recipesView.checkMismatch'); return }
    checkResult.value = result
  } catch (e) {
    if (!disposed && seq === checkSeq && downloadDialog.value) downloadError.value = errorMessage(e, tr('recipesView.kd728cf'))
  } finally {
    if (!disposed && seq === checkSeq) checking.value = false
  }
}
async function confirmDownload(): Promise<void> {
  if (disposed || !canConfirmDownload.value || !downloadRecipe.value || !checkResult.value) return
  const { id } = downloadRecipe.value, version = checkResult.value.version
  submitting.value = true; downloadError.value = ''
  try {
    const d = await recipeApi.download(id, version)
    if (disposed) return
    lastSubmitted.value = d; downloads.value = [d, ...downloads.value.filter(row => row.id !== d.id)]
    if (d.status === 'Rejected') { downloadError.value = d.message || tr('recipesView.k5bf97e'); checkResult.value = null; return }
    if (d.status === 'Pending') ElMessage.success(tr('recipesView.k33430c'))
    else if (['Running', 'Completed'].includes(d.status)) ElMessage.success(tr('recipesView.kdfd357', [d.id]))
    downloadDialog.value = false
    await refresh(); if (!disposed) await showDownloads(id)
  } catch (e) {
    if (!disposed) { downloadError.value = errorMessage(e, tr('recipesView.k2d0918')); checkResult.value = null }
  } finally { if (!disposed) submitting.value = false }
}
async function abort(d: RecipeDownload): Promise<void> {
  try { await ElMessageBox.confirm(tr('recipesView.abortConfirm', [d.recipeName, d.id]), tr('recipesView.k67847e'), { type: 'warning', confirmButtonText: tr('recipesView.k67847e') }) } catch { return }
  if (await guard(() => recipeApi.abort(d.id), tr('recipesView.k50cf66'), actionError)) await refresh()
}

const versionsDialog = ref(false), versionsRecipe = ref<Recipe | null>(null), versions = ref<RecipeVersion[]>([])
async function showVersions(r: Recipe): Promise<void> {
  versionsRecipe.value = r
  versionsError.value = ''
  if (await guard(async () => { versions.value = await recipeApi.versions(r.id) }, tr('recipesView.kf6b7a4'), actionError)) versionsDialog.value = true
}
const diffClass = ({ row }: { row: { different: boolean } }) => row.different ? 'diff-row' : ''
const compareDialog = ref(false), compareResult = ref<RecipeCompare | null>(null)
async function compare(r: Recipe, leftVersion: number | null, right: string, rightVersion: number | null): Promise<void> {
  const target = versionsDialog.value ? versionsError : actionError
  if (await guard(async () => { compareResult.value = await recipeApi.compare(r.id, leftVersion, right, rightVersion) }, tr('recipesView.k3a7c48'), target)) compareDialog.value = true
}

// Pending approvals and running downloads both need progress updates, even when filters hide them.
usePolling(loadDownloads, 1000, { enabled: () => !loading.value && activeDownloads.value > 0 })
onMounted(async () => {
  await refresh()
})
onBeforeUnmount(() => { disposed = true; refreshSeq++; downloadsSeq++; checkSeq++ })
</script>

<style scoped>
.danger { color: var(--el-color-danger); }
.download-section { scroll-margin-top: 70px; }.download-section .catalog-filters { margin-top: 12px; }
:deep(.diff-row) { background: var(--el-color-warning-light-9); }
</style>
