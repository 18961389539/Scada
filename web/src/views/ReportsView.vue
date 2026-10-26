<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-radio-group v-model="reportType" @change="onTypeChange">
          <el-radio-button value="daily">{{ $t('reportsView.k4f91c6') }}</el-radio-button>
          <el-radio-button value="monthly">{{ $t('reportsView.kea9058') }}</el-radio-button>
        </el-radio-group>
        <el-date-picker
          v-model="range"
          :type="reportType === 'daily' ? 'daterange' : 'monthrange'"
          :start-placeholder="reportType === 'daily' ? $t('reportsView.k1f2919') : $t('reportsView.k78f495')"
          :end-placeholder="reportType === 'daily' ? $t('reportsView.kf4b9b2') : $t('reportsView.k465751')"
          style="width: 300px"
        />
        <el-select
          v-model="selectedTags"
          multiple
          filterable
          collapse-tags
          collapse-tags-tooltip
          :placeholder="$t('reportsView.kba1068')"
          style="min-width: 280px; max-width: 480px"
        >
          <el-option
            v-for="tag in tags"
            :key="tag.id"
            :label="`${deviceName(tag.deviceId)} / ${tag.name}`"
            :value="tag.id"
          />
        </el-select>
        <el-button type="primary" :icon="Search" :loading="loading" @click="query">{{ $t('reportsView.k3a52ae') }}</el-button>
      </div>

      <div class="report-summary">
        <span class="muted">{{ reportType === 'daily' ? $t('reportsView.k4f91c6') : $t('reportsView.kea9058') }} · {{ rangeLabel }} · {{ $t('reportsView.k732953', [rows.length]) }}</span>
        <span class="spacer" />
        <el-button :icon="Download" :disabled="rows.length === 0" @click="exportExcel">{{ $t('reportsView.kc7f85b') }}</el-button>
      </div>
      <el-alert v-if="queryError && rows.length" type="error" :closable="false" show-icon :title="queryError" data-test="reports-query-error" />
      <el-alert v-if="exportError" type="error" :closable="false" show-icon :title="exportError" data-test="reports-export-error" />

      <el-empty
        v-if="!loading && rows.length === 0"
        data-test="reports-empty"
        :description="queryError || catalogError || (queried ? $t('reportsView.k882a20') : $t('reportsView.notQueried'))"
      />
      <el-table v-else :data="rows" v-loading="loading" border stripe class="report-table" :height="rows.length ? 'calc(100vh - 300px)' : undefined">
        <el-table-column prop="time" :label="$t('reportsView.k89b4aa')" width="180" sortable>
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
import { computed, onMounted, ref } from 'vue'
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
const queried = ref(false)
const queryError = ref('')
const catalogError = ref('')
const exportError = ref('')
const rows = ref<ReportRow[]>([])

const rangeLabel = computed(() => {
  if (!range.value) return tr('reportsView.kf5fa06')
  const [start, end] = range.value
  return reportType.value === 'daily'
    ? `${formatTime(start.getTime()).slice(0, 10)} ~ ${formatTime(end.getTime()).slice(0, 10)}`
    : `${start.getFullYear()}-${String(start.getMonth() + 1).padStart(2, '0')} ~ ${end.getFullYear()}-${String(
        end.getMonth() + 1
      ).padStart(2, '0')}`
})

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
  if (!range.value) {
    queryError.value = tr('reportsView.k1dbd50')
    return null
  }

  const [start, end] = range.value
  if (reportType.value === 'daily') {
    const from = new Date(start.getFullYear(), start.getMonth(), start.getDate()).getTime()
    const to = new Date(end.getFullYear(), end.getMonth(), end.getDate() + 1).getTime() - 1
    return { from, to }
  }

  const from = new Date(start.getFullYear(), start.getMonth(), 1).getTime()
  const to = new Date(end.getFullYear(), end.getMonth() + 1, 1).getTime() - 1
  return { from, to }
}

function tagLabel(tagId: number): string {
  const tag = tags.value.find((x) => x.id === tagId)
  if (!tag) return `#${tagId}`
  return `${deviceName(tag.deviceId)} / ${tag.name}`
}

function onTypeChange(): void {
  range.value = defaultRange(reportType.value)
  rows.value = []
  queried.value = false
  queryError.value = ''
}

async function query(): Promise<void> {
  if (selectedTags.value.length === 0) {
    queryError.value = tr('reportsView.k24f3cf')
    return
  }

  const resolved = resolveRange()
  if (!resolved) return

  loading.value = true
  try {
    const result = await reportApi.query({
      type: reportType.value,
      from: resolved.from,
      to: resolved.to,
      tagIds: selectedTags.value
    })

    rows.value = result.series
      .flatMap((series) =>
        series.points.map((point) => ({
          ts: point.ts,
          tagId: series.tagId,
          tagName: tagLabel(series.tagId),
          avg: point.avg,
          min: point.min,
          max: point.max,
          count: point.count
        }))
      )
      .sort((a, b) => a.ts - b.ts || a.tagName.localeCompare(b.tagName))
    queryError.value = ''
    queried.value = true
  } catch (error) {
    queryError.value = errorMessage(error, tr('reportsView.kf5973f'))
  } finally {
    loading.value = false
  }
}

async function exportExcel(): Promise<void> {
  const resolved = resolveRange()
  if (!resolved || selectedTags.value.length === 0) return
  exportError.value = ''
  try {
    const blob = await reportApi.export({
      type: reportType.value,
      from: resolved.from,
      to: resolved.to,
      tagIds: selectedTags.value
    })
    const stamp = new Date().toISOString().slice(0, 10).replace(/-/g, '')
    downloadBlob(blob, `report_${reportType.value}_${stamp}.xlsx`)
  } catch (error) {
    exportError.value = await errorText(error, tr('reportsView.k675338'))
  }
}

onMounted(async () => {
  range.value = defaultRange(reportType.value)
  try {
    ;[devices.value, tags.value] = await Promise.all([deviceApi.list(), tagApi.list()])
    selectedTags.value = tags.value.slice(0, 3).map((x) => x.id)
    catalogError.value = ''
  } catch (error) {
    catalogError.value = errorMessage(error, tr('reportsView.kdf92e4'))
  }
})
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
.report-table :deep(.el-table__empty-block) {
  min-height: 180px;
}
</style>
