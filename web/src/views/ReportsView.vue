<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-radio-group v-model="reportType" @change="onTypeChange">
          <el-radio-button value="daily">日报表</el-radio-button>
          <el-radio-button value="monthly">月报表</el-radio-button>
        </el-radio-group>
        <el-date-picker
          v-model="range"
          :type="reportType === 'daily' ? 'daterange' : 'monthrange'"
          :start-placeholder="reportType === 'daily' ? '开始日期' : '开始月份'"
          :end-placeholder="reportType === 'daily' ? '结束日期' : '结束月份'"
          style="width: 300px"
        />
        <el-select
          v-model="selectedTags"
          multiple
          filterable
          collapse-tags
          collapse-tags-tooltip
          placeholder="选择测点"
          style="min-width: 280px; max-width: 480px"
        >
          <el-option
            v-for="tag in tags"
            :key="tag.id"
            :label="`${deviceName(tag.deviceId)} / ${tag.name}`"
            :value="tag.id"
          />
        </el-select>
        <el-button type="primary" :icon="Search" :loading="loading" @click="query">生成报表</el-button>
        <span class="spacer" />
        <el-button :icon="Download" :disabled="rows.length === 0" @click="exportExcel">导出 Excel</el-button>
      </div>

      <div class="report-summary">
        <el-tag type="info">{{ reportType === 'daily' ? '日报表' : '月报表' }}</el-tag>
        <span class="muted">{{ rangeLabel }}</span>
        <span class="muted">共 {{ rows.length }} 条聚合数据</span>
      </div>

      <el-table :data="rows" v-loading="loading" border stripe height="calc(100vh - 300px)">
        <el-table-column prop="time" label="时间" width="180" sortable>
          <template #default="{ row }">{{ formatTime(row.ts) }}</template>
        </el-table-column>
        <el-table-column prop="tagName" label="测点" min-width="200" show-overflow-tooltip />
        <el-table-column label="平均值" width="140" align="right">
          <template #default="{ row }">{{ formatValue(row.avg, 3) }}</template>
        </el-table-column>
        <el-table-column label="最小值" width="140" align="right">
          <template #default="{ row }">{{ formatValue(row.min, 3) }}</template>
        </el-table-column>
        <el-table-column label="最大值" width="140" align="right">
          <template #default="{ row }">{{ formatValue(row.max, 3) }}</template>
        </el-table-column>
        <el-table-column prop="count" label="样本数" width="110" align="right" />
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Download, Search } from '@element-plus/icons-vue'
import { deviceApi, reportApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import type { Device, Tag } from '../types'
import { downloadBlob, formatTime, formatValue } from '../utils'

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
const rows = ref<ReportRow[]>([])

const rangeLabel = computed(() => {
  if (!range.value) return '未选择时间范围'
  const [start, end] = range.value
  return reportType.value === 'daily'
    ? `${formatTime(start.getTime()).slice(0, 10)} ~ ${formatTime(end.getTime()).slice(0, 10)}`
    : `${start.getFullYear()}-${String(start.getMonth() + 1).padStart(2, '0')} ~ ${end.getFullYear()}-${String(
        end.getMonth() + 1
      ).padStart(2, '0')}`
})

function deviceName(id: number | null): string {
  if (id === null) return '计算测点'
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
    ElMessage.warning('请选择时间范围')
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

function onTypeChange(): void {
  range.value = defaultRange(reportType.value)
  rows.value = []
}

async function query(): Promise<void> {
  if (selectedTags.value.length === 0) {
    ElMessage.warning('请先选择测点')
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

    const nameMap = new Map(tags.value.map((x) => [x.id, x.name]))
    rows.value = result.series
      .flatMap((series) =>
        series.points.map((point) => ({
          ts: point.ts,
          tagId: series.tagId,
          tagName: nameMap.get(series.tagId) ?? `#${series.tagId}`,
          avg: point.avg,
          min: point.min,
          max: point.max,
          count: point.count
        }))
      )
      .sort((a, b) => a.ts - b.ts || a.tagName.localeCompare(b.tagName))

    if (rows.value.length === 0) {
      ElMessage.info('所选范围内暂无归档数据')
    }
  } catch (error) {
    ElMessage.error(errorMessage(error, '报表查询失败'))
  } finally {
    loading.value = false
  }
}

async function exportExcel(): Promise<void> {
  const resolved = resolveRange()
  if (!resolved || selectedTags.value.length === 0) return

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
    ElMessage.error(errorMessage(error, '导出失败'))
  }
}

onMounted(async () => {
  range.value = defaultRange(reportType.value)
  try {
    ;[devices.value, tags.value] = await Promise.all([deviceApi.list(), tagApi.list()])
    selectedTags.value = tags.value.slice(0, 3).map((x) => x.id)
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载测点失败'))
  }
})
</script>
