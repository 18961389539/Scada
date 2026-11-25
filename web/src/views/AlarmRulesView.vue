<template>
  <div class="page page--fill">
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterTag" clearable filterable remote :remote-method="searchFilterTags" :loading="filterTagLoading" :placeholder="$t('alarmRulesView.kb8ceed')" :aria-label="$t('alarmRulesView.kb8ceed')" style="width: 220px" data-test="rule-filter-tag" @visible-change="onFilterTagsOpen">
          <el-option v-for="tag in filterTagOptions" :key="tag.id" :label="alarmRuleTagLabel(tag)" :value="tag.id" />
        </el-select>
        <span class="spacer" />
        <template v-if="selected.length">
          <el-text data-test="rule-batch-count">{{ $t('alarmRulesView.k805f4f', [selected.length]) }}</el-text>
          <el-button data-test="rule-batch-enable" :loading="batching" @click="batchSetEnabled(true)">{{ $t('alarmRulesView.kf5e360') }}</el-button>
          <el-button data-test="rule-batch-disable" :loading="batching" @click="batchSetEnabled(false)">{{ $t('alarmRulesView.k45f528') }}</el-button>
          <el-button type="danger" plain data-test="rule-batch-delete" :loading="batching" @click="batchDelete">{{ $t('alarmRulesView.k362aed') }}</el-button>
          <el-button v-if="batching" type="warning" plain :disabled="batchCancel" data-test="rule-batch-stop" @click="batchCancel = true">{{ $t('batchUx.stopBatch') }}</el-button>
        </template>
        <el-button v-if="can(auth.user, 'Configure')" data-test="rule-quad-create" @click="openQuad">{{ $t('alarmRulesView.k7a0a8e') }}</el-button>
        <el-button :icon="Download" :loading="exporting" data-test="rule-export-xlsx" @click="exportRules('xlsx')">{{ $t('alarmRulesView.exportXlsx') }}</el-button>
        <el-button :icon="Download" :loading="exporting" data-test="rule-export-csv" @click="exportRules('csv')">{{ $t('alarmRulesView.exportCsv') }}</el-button>
        <el-button v-if="canImport" :icon="Upload" :loading="importing" data-test="rule-import" @click="pickImport">{{ $t('alarmRulesView.importRules') }}</el-button>
        <input ref="importInput" type="file" accept=".xlsx,.csv" hidden data-test="rule-import-file" @change="onImportFile" />
        <el-button type="primary" :icon="Plus" data-test="rule-create" @click="openCreate">{{ $t('alarmRulesView.k945c08') }}</el-button>
      </div>
      <div class="catalog-filters">
        <el-input v-model="search" clearable :prefix-icon="Search" :placeholder="$t('alarmRulesView.searchPlaceholder')" :aria-label="$t('alarmRulesView.searchPlaceholder')" class="catalog-search" />
        <el-select v-model="filterState" :aria-label="$t('listFilters.status')" class="catalog-select" data-test="rule-filter-state">
          <el-option :label="$t('listFilters.allStates')" value="all" />
          <el-option :label="$t('alarmRulesView.kd4e9ca')" value="enabled" />
          <el-option :label="$t('alarmRulesView.kbe70be')" value="disabled" />
          <el-option :label="$t('alarmRulesView.ka3f7d9')" value="outOfService" />
        </el-select>
        <el-select v-model="filterCategory" :aria-label="$t('alarmRulesView.k31f7ba')" class="catalog-select" data-test="rule-filter-category">
          <el-option :label="$t('alarmRulesView.k8b5074')" value="all" />
          <el-option v-for="(label, value) in alarmCategoryLabels" :key="value" :label="label" :value="value" />
        </el-select>
        <el-button v-if="hasFilters" link type="primary" data-test="rule-reset-filters" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
        <span v-if="!loading || ruleTotal > 0 || rules.length" class="catalog-count muted" role="status" data-test="rule-result-count">{{ $t('listFilters.resultCount', [ruleTotal, scopeTotal]) }}</span>
        <el-button :icon="Refresh" :loading="loading" data-test="rule-refresh" @click="load">{{ $t('listFilters.refresh') }}</el-button>
      </div>
      <el-alert v-if="batchProgress" :title="batchCancel ? $t('batchUx.stopping') : batchProgress" type="info" :closable="false" show-icon style="margin-bottom: 8px" data-test="rule-batch-progress" />
      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="rules-load-error" />
      <el-alert v-if="catalogError" :title="catalogError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="rules-catalog-error" />
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="rules-action-error" />

      <el-table ref="tableRef" v-loading="loading" :data="rules" size="small" height="100%" row-key="id" @selection-change="onSelect" @sort-change="onSortChange">
        <template #empty>
          <PageState v-if="loadError" state="error" :description="loadError" :retry-text="$t('common.retry')" compact @retry="load()" />
          <PageState v-else-if="hasFilters" state="empty" :description="$t('listFilters.noMatches')" compact>
            <el-button link type="primary" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
          </PageState>
          <PageState v-else state="empty" :description="$t('alarmRulesView.ka0230f')" compact />
        </template>
        <el-table-column type="selection" width="42" :selectable="canSelectRule" />
        <el-table-column prop="tagName" :label="$t('alarmRulesView.k8cff1c')" width="125" sortable="custom" show-overflow-tooltip>
          <template #default="{ row }">{{ tagName(row.tagId) }}</template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.ke4e46c')" width="90" show-overflow-tooltip>
          <template #default="{ row }">{{ alarmTypeLabels[row.type] }}</template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.k31f7ba')" width="90" show-overflow-tooltip>
          <template #default="{ row }">{{ alarmCategoryLabels[row.category] ?? row.category }}</template>
        </el-table-column>
        <el-table-column :label="`${$t('alarmRulesView.ka05320')} / ${$t('alarmRulesView.k254849')}`" width="150" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="isLevelRule(row)" class="mono">{{ row.threshold }}</span>
            <span v-else-if="row.type === 'Discrete'" class="mono">{{ activeWhenLabels[row.activeWhen ?? 'On'] }}<template v-if="row.bitIndex != null"> · bit {{ row.bitIndex }}</template></span>
            <span v-else-if="row.type === 'Expression'" class="mono" :title="row.expression ?? ''">{{ row.expression ? 'f(x)' : '—' }}</span>
            <span v-else class="muted">—</span>
            <el-tag :type="levelType(row.level)" size="small" style="margin-left: 6px">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="`${$t('alarmRulesView.k27cd56')} / ${$t('alarmRulesView.k3b86db')}`" width="170" show-overflow-tooltip>
          <template #default="{ row }">
            <template v-if="isLevelRule(row)">
              <span class="mono">{{ row.delaySec }}s / {{ row.offDelaySec ?? 0 }}s</span>
              <span class="muted tiny">· {{ row.recoverDeadband }}</span>
            </template>
            <span v-else-if="showsTiming(row.type, row.activeWhen)" class="mono">{{ row.delaySec }}s / {{ row.offDelaySec ?? 0 }}s</span>
            <span v-else class="muted tiny">—</span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.kb37a4e')" width="120" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="row.suppressMode && row.suppressMode !== 'None'" class="tiny">{{ $t('alarmRulesView.k045924', [tagName(row.suppressTagId), row.suppressMode === 'WhenOn' ? 1 : 0]) }}</span>
            <span v-else class="muted tiny">-</span>
          </template>
        </el-table-column>
        <el-table-column prop="message" :label="$t('alarmRulesView.kb62f14')" min-width="150" show-overflow-tooltip sortable="custom">
          <template #default="{ row }">{{ localizedText(row.message, row.messageEn) || '—' }}</template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.kd4e9ca')" width="78">
          <template #default="{ row }">
            <el-tag :class="{ 'tag-quiet': row.enabled }" :type="row.enabled ? undefined : 'warning'" size="small" :data-test="`rule-enabled-${row.id}`">{{ row.enabled ? $t('alarmRulesView.kd4e9ca') : $t('alarmRulesView.kbe70be') }}</el-tag>
            <el-tooltip v-if="row.outOfService" :content="row.outOfServiceReason || ''"><el-tag type="warning" size="small" data-test="oos-tag">{{ $t('alarmRulesView.ka3f7d9') }}</el-tag></el-tooltip>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.kf3ea6d')" width="240" fixed="right">
          <template #default="{ row }">
            <AlarmRuleRowActions :rule="row" :can-configure="canConfigureRule(row)" @edit="openEdit" @out-of-service="setOutOfService" @remove="remove" @simulate="openSimulate" @copy="openCopy" />
          </template>
        </el-table-column>
      </el-table>
      <el-pagination :current-page="page" :page-size="PAGE_SIZE" :total="ruleTotal" :pager-count="5" layout="total, prev, pager, next" hide-on-single-page data-test="rule-pagination" @update:current-page="page = $event" @current-change="onPageChange" />
    </div>

    <AlarmRuleEditDialog ref="editDialogRef" :resolve-tag="pinTag" :ensure-devices="ensureDevices" :can-configure-tag="canConfigureTag" @saved="load" />
    <AlarmRuleQuadDialog ref="quadDialog" :resolve-tag="pinTag" :ensure-devices="ensureDevices" :can-configure-tag="canConfigureTag" @saved="load" />

    <AlarmRuleSimulateDialog ref="simulateDialog" :tag-name="tagName" />
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, toRefs, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, type TableInstance } from 'element-plus'
import { Plus, Refresh, Search, Download, Upload } from '@element-plus/icons-vue'
import { ruleApi, deviceApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import type { AlarmRule, Tag } from '../types'
import { activeWhenLabels, alarmCategoryLabels, alarmTypeLabels, downloadBlob, expressionTagIds, levelLabel, levelType, localizedText } from '../utils'
import { alarmRuleTagLabel, isLevelRule, showsTiming, toAlarmRuleInput } from './alarmRuleModel'
import AlarmRuleEditDialog from '../components/AlarmRuleEditDialog.vue'
import PageState from '../components/PageState.vue'
import AlarmRuleQuadDialog from '../components/AlarmRuleQuadDialog.vue'
import AlarmRuleRowActions from '../components/AlarmRuleRowActions.vue'
import AlarmRuleSimulateDialog from '../components/AlarmRuleSimulateDialog.vue'
import { can, canGlobal } from '../access/policies'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'
import { runBatch, summarizeBatch } from '../composables/batchRunner'
import { readLastNumber } from '../composables/lastChoice'
import { positiveQueryId } from '../nav/query'
import { useCatalogQuery } from '../composables/useCatalogQuery'
import { useAlarmRuleTagPicker, withPinnedTags } from '../composables/useAlarmRuleTagPicker'

const auth = useAuthStore()
const route = useRoute(), router = useRouter()
const canImport = computed(() => canGlobal(auth.user, 'Configure'))
const importInput = ref<HTMLInputElement>()
const exporting = ref(false), importing = ref(false)
const rules = ref<AlarmRule[]>([])
const ruleTotal = ref(0)
const scopeTotal = ref(0)
const tags = ref<Tag[]>([])
const devices = ref<{ id: number; areaId?: number | null }[]>([])
let devicesPromise: Promise<void> | null = null
let devicesLoaded = false
function canConfigureTag(tag: Tag): boolean {
  if (tag.dataType === 'String') return false
  if (tag.kind !== 'Physical') return tag.areaId != null ? can(auth.user, 'Configure', tag.areaId) : can(auth.user, 'Configure', undefined)
  return can(auth.user, 'Configure', devices.value.find(d => d.id === tag.deviceId)?.areaId)
}
function canConfigureRule(rule: AlarmRule): boolean {
  const tag = tags.value.find(t => t.id === rule.tagId)
  return !!tag && canConfigureTag(tag)
}
function canSelectRule(rule: AlarmRule): boolean { return canConfigureRule(rule) }
const filterTag = computed({
  get: () => positiveQueryId(route.query.tagId),
  set: (value: number | undefined) => {
    const query = { ...route.query }
    if (value) query.tagId = String(value)
    else delete query.tagId
    delete query.page
    void router.replace({ query })
  }
})
const catalog = useCatalogQuery({ search: '', state: 'all', category: 'all' })
const { search, state: filterState, category: filterCategory } = toRefs(catalog.draft)
const hasFilters = computed(() => !!catalog.applied.value.search || catalog.applied.value.state !== 'all' || catalog.applied.value.category !== 'all' || !!catalog.draft.search.trim() || catalog.draft.state !== 'all' || catalog.draft.category !== 'all' || filterTag.value !== undefined)
const tableRef = ref<TableInstance>()
const PAGE_SIZE = 100
const page = computed({
  get: () => catalog.page.value,
  set: value => { catalog.page.value = value }
})
const sortKey = ref('')
const sortOrder = ref<'ascending' | 'descending' | null>(null)
function onPageChange(): void { selected.value = []; tableRef.value?.clearSelection() }
function onSortChange({ prop, order }: { prop: string; order: 'ascending' | 'descending' | null }): void {
  sortKey.value = order && (prop === 'tagName' || prop === 'message') ? prop : ''
  sortOrder.value = order
  if (catalog.page.value !== 1) catalog.page.value = 1
  onPageChange()
}
let searchTimer = 0
watch(() => catalog.draft.search, () => {
  window.clearTimeout(searchTimer)
  searchTimer = window.setTimeout(() => { void catalog.apply() }, 250)
})
watch(() => catalog.draft.state, () => { void catalog.apply() })
watch(() => catalog.draft.category, () => { void catalog.apply() })
watch([() => JSON.stringify(catalog.applied.value), () => catalog.page.value, filterTag, sortKey, sortOrder], () => {
  onPageChange()
  void load()
})
const loading = ref(false)
const loadError = ref('')
const catalogError = ref('')
const actionError = ref('')
const batching = ref(false)
const batchCancel = ref(false)
const batchProgress = ref('')
const selected = ref<AlarmRule[]>([])

async function ensureDevices(): Promise<void> {
  if (devicesLoaded) return
  if (!devicesPromise) {
    devicesPromise = deviceApi.list()
      .then(list => { devices.value = list; devicesLoaded = true; catalogError.value = '' })
      .catch(error => {
        devicesPromise = null
        catalogError.value = errorMessage(error, tr('alarmRulesView.kdf92e4'))
        throw error
      })
  }
  await devicesPromise
}

function rememberTags(list: Tag[]): void {
  if (!list.length) return
  const map = new Map(tags.value.map(tag => [tag.id, tag]))
  for (const tag of list) map.set(tag.id, tag)
  tags.value = [...map.values()]
}

async function pinTag(id?: number | null): Promise<Tag | undefined> {
  if (!id) return
  const known = tags.value.find(tag => tag.id === id)
  if (known) return known
  try {
    const [tag] = await tagApi.list({ ids: [id] })
    if (tag) rememberTags([tag])
    return tag
  } catch {
    return undefined
  }
}

const filterPicker = useAlarmRuleTagPicker({
  resolveTag: pinTag,
  ensureDevices,
  onLoaded: rememberTags,
  pinnedId: () => filterTag.value,
  forcePin: () => true
})
const { items: filterTagOptions, loading: filterTagLoading, search: searchFilterTags, open: openFilterTags } = filterPicker
function onFilterTagsOpen(open: boolean): void { openFilterTags(open) }

const editDialogRef = ref<InstanceType<typeof AlarmRuleEditDialog>>()
const quadDialog = ref<InstanceType<typeof AlarmRuleQuadDialog>>()
function preferredRuleTag(): number | undefined { return filterTag.value ?? readLastNumber('rule-tag') ?? undefined }
function openCreate(): void { void editDialogRef.value?.openCreate(preferredRuleTag()) }
function openEdit(rule: AlarmRule): void { void editDialogRef.value?.openEdit(rule) }
function openCopy(rule: AlarmRule): void { void editDialogRef.value?.openCopy(rule) }
function openQuad(): void { void quadDialog.value?.open(preferredRuleTag()) }

function onSelect(rows: AlarmRule[]): void { selected.value = rows }


async function setOutOfService(rule: AlarmRule, outOfService: boolean): Promise<void> {
  let reason: string | undefined
  if (outOfService) {
    try {
      const result = await ElMessageBox.prompt(tr('alarmRulesView.k3db650'), tr('alarmRulesView.kbba488'), {
        inputValidator: (value: string) => (value && value.trim() ? true : tr('alarmRulesView.k75ebf5')),
        confirmButtonText: tr('alarmRulesView.kd989e5'),
        cancelButtonText: tr('alarmRulesView.k4d0b46')
      })
      reason = result.value.trim()
    } catch {
      return
    }
  }
  actionError.value = ''
  try {
    await ruleApi.outOfService(rule.id, outOfService, reason)
    ElMessage.success(outOfService ? tr('alarmRulesView.k6c7dcb') : tr('alarmRulesView.k6aa9ad'))
    actionError.value = ''
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmRulesView.k09e424'))
  }
}

function tagName(id?: number | null): string {
  return tags.value.find((x) => x.id === id)?.name ?? `#${id}`
}

let loadVersion = 0, disposed = false
async function load(): Promise<void> {
  if (disposed) return
  const version = ++loadVersion
  loading.value = true
  selected.value = []
  try {
    const applied = catalog.applied.value
    const extra = !!applied.search.trim() || applied.state !== 'all' || applied.category !== 'all'
    const pageRequest = ruleApi.page({
      tagId: filterTag.value,
      search: applied.search,
      state: applied.state,
      category: applied.category,
      skip: Math.max(0, (catalog.page.value - 1) * PAGE_SIZE),
      take: PAGE_SIZE,
      sort: sortKey.value,
      order: sortOrder.value
    })
    const scopeRequest = extra
      ? ruleApi.page({ tagId: filterTag.value, skip: 0, take: 1 })
      : null
    const result = await pageRequest
    if (version !== loadVersion || disposed) return
    const maxPage = Math.max(1, Math.ceil(result.total / PAGE_SIZE))
    if (catalog.page.value > maxPage) {
      catalog.page.value = maxPage
      return
    }
    const ids = new Set<number>()
    for (const rule of result.items) {
      ids.add(rule.tagId)
      if (rule.suppressTagId) ids.add(rule.suppressTagId)
      for (const id of expressionTagIds(rule.expression)) ids.add(id)
    }
    const idList = [...ids].filter(id => id > 0).slice(0, 500)
    const [pageTags] = await Promise.all([
      idList.length ? tagApi.list({ ids: idList }) : Promise.resolve([] as Tag[]),
      ensureDevices().catch(() => undefined)
    ])
    if (version !== loadVersion || disposed) return
    rememberTags(pageTags)
    const scope = scopeRequest ? await scopeRequest : null
    if (version !== loadVersion || disposed) return
    rules.value = result.items
    ruleTotal.value = result.total
    scopeTotal.value = scope?.total ?? result.total
    loadError.value = ''
  } catch (error) {
    if (version !== loadVersion) return
    loadError.value = errorMessage(error, tr('alarmRulesView.k9174d4'))
  } finally {
    if (version === loadVersion) loading.value = false
  }
}

async function exportRules(format: 'xlsx' | 'csv'): Promise<void> {
  if (disposed || exporting.value) return
  exporting.value = true
  actionError.value = ''
  try {
    const blob = await ruleApi.export(format, filterTag.value)
    const stamp = new Date().toISOString().slice(0, 19).replace(/[-:T]/g, '')
    downloadBlob(blob, `alarm-rules-${stamp}.${format}`)
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmRulesView.exportFailed'))
  } finally {
    exporting.value = false
  }
}

function pickImport(): void {
  importInput.value?.click()
}

async function onImportFile(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (disposed || !file || importing.value) return
  importing.value = true
  actionError.value = ''
  try {
    const preview = await ruleApi.importPreview(file)
    if (!await confirmExcelImportPreview(preview, tr('importPreview.titleRules'))) return
    const result = await ruleApi.importApply(preview.id)
    const summary = tr('importPreview.done', [result.created, result.updated, result.skipped, result.conflicts ?? 0])
    if (result.errors.length) await ElMessageBox.alert(`${summary}\n${result.errors.join('\n')}`, tr('importPreview.titleRules'), { type: 'warning', confirmButtonText: tr('alarmRulesView.kcb63c6') })
    else ElMessage.success(summary)
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('importPreview.applyFailed'))
  } finally {
    importing.value = false
  }
}

const simulateDialog = ref<InstanceType<typeof AlarmRuleSimulateDialog>>()
function openSimulate(rule: AlarmRule): void { simulateDialog.value?.open(rule) }

onBeforeUnmount(() => { disposed = true; loadVersion++; window.clearTimeout(searchTimer) })
async function resetFilters(): Promise<void> {
  window.clearTimeout(searchTimer)
  await catalog.apply(true)
  if (filterTag.value !== undefined) filterTag.value = undefined
}


async function remove(rule: AlarmRule): Promise<void> {
  actionError.value = ''
  try {
    await ruleApi.remove(rule.id)
    actionError.value = ''
    ElMessage.success(tr('alarmRulesView.kfb5fe1'))
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmRulesView.k72250c'))
  }
}

async function reportBatch(report: Awaited<ReturnType<typeof runBatch>>, okText: string): Promise<void> {
  batchProgress.value = ''
  batchCancel.value = false
  const pending = report.total - report.ok - report.failed.length
  if (report.failed.length === 0) {
    if (report.canceled) ElMessage.info(tr('batchUx.stoppedOnly', [report.ok, report.total, pending]))
    else ElMessage.success(okText)
  } else {
    const summary = summarizeBatch(report, tr('alarmRulesView.kbce2ef'))
    const body = report.canceled ? `${summary}\n\n${tr('batchUx.stoppedSuffix', [pending])}` : summary
    await ElMessageBox.alert(body, tr('alarmRulesView.kcfc1db'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('alarmRulesView.kcb63c6') })
  }
  await load()
}

async function batchSetEnabled(enabled: boolean): Promise<void> {
  const rows = selected.value.slice()
  if (!rows.length) return
  batching.value = true
  batchCancel.value = false
  const report = await runBatch(rows, (r) => `#${r.id} ${r.message || tagName(r.tagId)}`, async (r) => { await ruleApi.update(r.id, toAlarmRuleInput(r, { enabled })) }, (p) => {
    batchProgress.value = tr('alarmRulesView.k4c5062', [enabled ? tr('alarmRulesView.kd4e9ca') : tr('alarmRulesView.kd989e5'), p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  }, () => batchCancel.value)
  batching.value = false
  await reportBatch(report, tr('alarmRulesView.k611dcc', [enabled ? tr('alarmRulesView.kd4e9ca') : tr('alarmRulesView.kd989e5'), report.ok]))
}

async function batchDelete(): Promise<void> {
  const rows = selected.value.slice()
  if (!rows.length) return
  try {
    await ElMessageBox.confirm(tr('alarmRulesView.k6db0fb', [rows.length]), tr('alarmRulesView.k362aed'), { type: 'warning', confirmButtonText: tr('alarmRulesView.k3755f5'), cancelButtonText: tr('alarmRulesView.k4d0b46') })
  } catch { return }
  batching.value = true
  batchCancel.value = false
  const report = await runBatch(rows, (r) => `#${r.id} ${r.message || tagName(r.tagId)}`, async (r) => { await ruleApi.remove(r.id) }, (p) => {
    batchProgress.value = tr('alarmRulesView.kee9792', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  }, () => batchCancel.value)
  batching.value = false
  await reportBatch(report, tr('alarmRulesView.kad6890', [report.ok]))
}

onMounted(async () => {
  await load()
  if (filterTag.value) {
    const tag = await pinTag(filterTag.value)
    if (tag) filterTagOptions.value = withPinnedTags(filterTagOptions.value, tag, undefined, true)
  }
  // 从测点页“配置报警”进入时直接打开新建对话框；消费掉 create 参数避免重复弹出。
  if (route.query.create === '1' && filterTag.value) {
    await ensureDevices().catch(() => undefined)
    const tag = tags.value.find(item => item.id === filterTag.value)
    if (tag && canConfigureTag(tag)) void openCreate()
    const query = { ...route.query }
    delete query.create
    void router.replace({ query })
  }
})
</script>
