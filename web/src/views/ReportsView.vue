<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-radio-group v-model="reportType" :disabled="exporting" :aria-label="$t('reportsView.reportType')" @change="onTypeChange">
          <el-radio-button value="daily">{{ $t('reportsView.k4f91c6') }}</el-radio-button>
          <el-radio-button value="monthly">{{ $t('reportsView.kea9058') }}</el-radio-button>
        </el-radio-group>
        <el-date-picker
          v-model="range"
          :type="reportType === 'daily' ? 'daterange' : 'monthrange'"
          :start-placeholder="reportType === 'daily' ? $t('reportsView.k1f2919') : $t('reportsView.k78f495')"
          :end-placeholder="reportType === 'daily' ? $t('reportsView.kf4b9b2') : $t('reportsView.k465751')"
          :disabled="exporting"
          :shortcuts="rangeShortcuts"
          :aria-label="$t('reportsView.dateRange')"
          data-test="reports-range"
          style="width: 300px"
        />
        <el-select
          v-model="selectedTags"
          multiple
          clearable
          filterable
          collapse-tags
          collapse-tags-tooltip
          :placeholder="$t('reportsView.kba1068')"
          :aria-label="$t('reportsView.kba1068')"
          :disabled="catalogLoading || !!catalogError || exporting"
          :loading="catalogLoading"
          data-test="reports-tags"
          style="min-width: 280px; max-width: 480px"
        >
          <el-option
            v-for="tag in tags"
            :key="tag.id"
            :label="tagLabel(tag.id)"
            :value="tag.id"
          />
        </el-select>
        <el-button type="primary" :icon="Search" :loading="loading" :disabled="!canQuery || exporting" data-test="reports-query" @click="query">{{ $t('reportsView.k3a52ae') }}</el-button>
      </div>
      <el-alert v-if="catalogError" :title="catalogError" type="error" :closable="false" show-icon data-test="reports-catalog-error">
        <el-button link type="primary" :loading="catalogLoading" data-test="reports-catalog-retry" @click="loadCatalogs">{{ $t('reportsView.retryCatalog') }}</el-button>
      </el-alert>
      <p class="muted report-hint" role="status" data-test="reports-selection-status">{{ catalogLoading ? $t('reportsView.loadingCatalog') : validationMessage || $t('reportsView.selectionCount', [selectedTags.length, tags.length]) }}</p>

      <div class="report-summary">
        <div v-if="displayedQuery" class="muted result-context" role="status" data-test="reports-result-context">
          <span>{{ displayedQuery.type === 'daily' ? $t('reportsView.k4f91c6') : $t('reportsView.kea9058') }} · {{ rangeLabel }} · {{ $t('reportsView.k732953', [rows.length]) }}</span>
          <small>{{ $t('reportsView.resultContext', [displayedQuery.tagIds.length, formatTime(displayedQuery.generatedAt)]) }}</small>
        </div>
        <span class="spacer" />
        <el-button :icon="Download" :disabled="!canExport" :loading="exporting" data-test="reports-export" @click="exportExcel">{{ $t('reportsView.kc7f85b') }}</el-button>
      </div>
      <el-alert v-if="pendingConditions" :title="$t('reportsView.conditionsChanged')" type="warning" :closable="false" show-icon data-test="reports-pending-conditions" />
      <el-alert v-if="queryError && rows.length" type="error" :closable="false" show-icon :title="queryError" data-test="reports-query-error" />
      <el-alert v-if="exportError" type="error" :closable="false" show-icon :title="exportError" data-test="reports-export-error" />

      <el-empty
        v-if="!loading && rows.length === 0"
        data-test="reports-empty"
        :description="queryError || catalogError || (!catalogLoading && !tags.length ? $t('reportsView.noTags') : displayedQuery ? $t('reportsView.k882a20') : $t('reportsView.notQueried'))"
      />
      <el-table v-else :data="rows" v-loading="loading" border stripe class="report-table" data-test="reports-table" :height="rows.length ? 'calc(100vh - 380px)' : undefined">
        <el-table-column prop="ts" :label="$t('reportsView.k89b4aa')" width="180" sortable>
          <template #default="{ row }">{{ formatTime(row.ts) }}</template>
        </el-table-column>
        <el-table-column prop="tagName" :label="$t('reportsView.k8cff1c')" min-width="220" show-overflow-tooltip />
        <el-table-column :label="$t('reportsView.k0d47be')" width="140" align="right">
          <template #default="{ row }">{{ formatValue(row.avg, 3) }}</template>
        </el-table-column>
        <el-table-column :label="$t('reportsView.kc3d641')" width="140" align="right">
          <template #default="{ row }">{{ formatValue(row.min, 3) }}</template>
        </el-table-column>
        <el-table-column :label="$t('reportsView.k018219')" width="140" align="right">
          <template #default="{ row }">{{ formatValue(row.max, 3) }}</template>
        </el-table-column>
        <el-table-column prop="count" :label="$t('reportsView.k52168f')" width="110" align="right" />
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Download, Search } from '@element-plus/icons-vue'
import { deviceApi, reportApi, tagApi } from '../api'
import { errorMessage, errorText } from '../api/http'
import type { Device, Tag } from '../types'
import { downloadBlob, formatTime, formatValue } from '../utils'
import { tr } from '../i18n'

interface ReportRow {
  ts: number
  tagId: number
  tagName: string
  avg: number
  min: number
  max: number
  count: number
}

const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const selectedTags = ref<number[]>([])
const reportType = ref<'daily' | 'monthly'>('daily')
const range = ref<[Date, Date] | null>(null)
const loading = ref(false)
const catalogLoading = ref(true), catalogReady = ref(false), exporting = ref(false)
const queryError = ref('')
const catalogError = ref('')
const exportError = ref('')
const rows = ref<ReportRow[]>([])
interface ReportQuery { type: 'daily' | 'monthly'; from: number; to: number; tagIds: number[] }
interface DisplayedQuery extends ReportQuery { key: string; generatedAt: number }
const displayedQuery = ref<DisplayedQuery | null>(null)
let querySeq = 0, catalogSeq = 0, disposed = false
const resolvedRange = computed(resolveRange)
const conditionKey = computed(() => JSON.stringify({ type: reportType.value, ...resolvedRange.value, tagIds: [...selectedTags.value].sort((a, b) => a - b) }))
const pendingConditions = computed(() => !!displayedQuery.value && displayedQuery.value.key !== conditionKey.value)
const validationMessage = computed(() => {
  if (!catalogReady.value) return ''
  if (!tags.value.length) return tr('reportsView.noTags')
  if (!selectedTags.value.length) return tr('reportsView.k24f3cf')
  if (!range.value) return tr('reportsView.k1dbd50')
  return resolvedRange.value ? '' : tr('reportsView.invalidRange')
})
const canQuery = computed(() => catalogReady.value && !catalogLoading.value && !catalogError.value && selectedTags.value.length > 0 && !!resolvedRange.value)
const canExport = computed(() => !!displayedQuery.value && rows.value.length > 0 && !pendingConditions.value && !loading.value && !exporting.value && !queryError.value)

const rangeLabel = computed(() => {
  if (!displayedQuery.value) return ''
  const { type, from, to } = displayedQuery.value
  const length = type === 'daily' ? 10 : 7
  return `${formatTime(from).slice(0, length)} ~ ${formatTime(to).slice(0, length)}`
})
const rangeShortcuts = computed(() => reportType.value === 'daily' ? [
  { text: tr('reportsView.today'), value: () => defaultRange('daily') },
  { text: tr('reportsView.yesterday'), value: () => { const day = new Date(); day.setDate(day.getDate() - 1); return [day, day] } },
  { text: tr('reportsView.lastSevenDays'), value: () => { const end = new Date(), start = new Date(); start.setDate(start.getDate() - 6); return [start, end] } }
] : [
  { text: tr('reportsView.thisMonth'), value: () => defaultRange('monthly') },
  { text: tr('reportsView.lastMonth'), value: () => { const now = new Date(), month = new Date(now.getFullYear(), now.getMonth() - 1, 1); return [month, month] } },
  { text: tr('reportsView.lastThreeMonths'), value: () => { const now = new Date(); return [new Date(now.getFullYear(), now.getMonth() - 2, 1), new Date(now.getFullYear(), now.getMonth(), 1)] } }
])

function deviceName(id: number | null): string {
  if (id === null) return tr('reportsView.k4e0387')
  return devices.value.find((x) => x.id === id)?.name ?? `#${id}`
}

function defaultRange(type: 'daily' | 'monthly'): [Date, Date] {
  const now = new Date()
  if (type === 'daily') {
    return [now, now]
  }
  return [new Date(now.getFullYear(), now.getMonth(), 1), new Date(now.getFullYear(), now.getMonth(), 1)]
}

function resolveRange(): { from: number; to: number } | null {
  if (!range.value || range.value.length !== 2 || !range.value.every(d => d instanceof Date && Number.isFinite(d.getTime()))) return null

  const [start, end] = range.value
  const daily = reportType.value === 'daily'
  const from = new Date(start.getFullYear(), start.getMonth(), daily ? start.getDate() : 1).getTime()
  const to = new Date(end.getFullYear(), end.getMonth() + (daily ? 0 : 1), daily ? end.getDate() + 1 : 1).getTime() - 1
  return Number.isFinite(from) && Number.isFinite(to) && from < to ? { from, to } : null
}

function tagLabel(tagId: number): string {
  const tag = tags.value.find((x) => x.id === tagId)
  if (!tag) return `#${tagId}`
  return `#${tag.id} · ${deviceName(tag.deviceId)} / ${tag.name}`
}

function onTypeChange(): void {
  range.value = defaultRange(reportType.value)
}
watch(conditionKey, () => {
  querySeq++; loading.value = false; queryError.value = ''; exportError.value = ''
}, { flush: 'sync' })

async function query(): Promise<void> {
  if (disposed || loading.value || exporting.value || !canQuery.value || !resolvedRange.value) return
  const params: ReportQuery = { type: reportType.value, ...resolvedRange.value, tagIds: [...selectedTags.value] }
  const key = conditionKey.value, seq = ++querySeq
  const names = new Map(params.tagIds.map(id => [id, tagLabel(id)]))
  loading.value = true; queryError.value = ''; exportError.value = ''
  try {
    const result = await reportApi.query(params)
    if (disposed || seq !== querySeq) return

    rows.value = result.series
      .flatMap((series) =>
        series.points.map((point) => ({
          ts: point.ts,
          tagId: series.tagId,
          tagName: names.get(series.tagId) ?? `#${series.tagId}`,
          avg: point.avg,
          min: point.min,
          max: point.max,
          count: point.count
        }))
      )
      .sort((a, b) => a.ts - b.ts || a.tagName.localeCompare(b.tagName))
    queryError.value = ''
    displayedQuery.value = { ...params, key, generatedAt: Date.now() }
  } catch (error) {
    if (!disposed && seq === querySeq) queryError.value = errorMessage(error, tr('reportsView.kf5973f'))
  } finally {
    if (!disposed && seq === querySeq) loading.value = false
  }
}

async function exportExcel(): Promise<void> {
  if (disposed || !canExport.value || !displayedQuery.value) return
  const { type, from, to, tagIds } = displayedQuery.value
  exporting.value = true; exportError.value = ''
  try {
    const blob = await reportApi.export({ type, from, to, tagIds: [...tagIds] })
    if (disposed) return
    const date = (ts: number) => formatTime(ts).slice(0, type === 'daily' ? 10 : 7).replace(/-/g, '')
    downloadBlob(blob, `report_${type}_${date(from)}_${date(to)}.xlsx`)
  } catch (error) {
    const message = await errorText(error, tr('reportsView.k675338'))
    if (!disposed) exportError.value = message
  } finally {
    if (!disposed) exporting.value = false
  }
}

async function loadCatalogs(): Promise<void> {
  if (disposed || (catalogLoading.value && catalogSeq > 0)) return
  const seq = ++catalogSeq
  catalogLoading.value = true
  try {
    const [allDevices, allTags] = await Promise.all([deviceApi.list(), tagApi.list()])
    if (disposed || seq !== catalogSeq) return
    devices.value = allDevices; tags.value = allTags
    if (!catalogReady.value) selectedTags.value = allTags.slice(0, 3).map(t => t.id)
    catalogReady.value = true; catalogError.value = ''
  } catch (error) {
    if (!disposed && seq === catalogSeq) catalogError.value = errorMessage(error, tr('reportsView.kdf92e4'))
  } finally {
    if (!disposed && seq === catalogSeq) catalogLoading.value = false
  }
}
onMounted(() => {
  range.value = defaultRange(reportType.value)
  void loadCatalogs()
})
onBeforeUnmount(() => { disposed = true; querySeq++; catalogSeq++ })
</script>

<style scoped>
.report-summary {
  display: flex;
  align-items: center;
  gap: var(--scada-space-3);
  flex-wrap: wrap;
  margin-bottom: var(--scada-space-3);
}
.report-summary .spacer {
  flex: 1;
}
.report-hint { margin: 0 0 12px; font-size: 13px; }
.result-context { display: flex; flex-direction: column; gap: 5px; line-height: 1.6; }
.report-table { min-height: 220px; }
.report-table :deep(.el-table__empty-block) {
  min-height: 180px;
}
</style>
