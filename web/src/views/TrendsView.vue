<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-select
          v-model="selectedTags"
          multiple
          filterable
          collapse-tags
          collapse-tags-tooltip
          placeholder="选择测点（最多 50 个）"
          style="min-width: 320px; max-width: 520px"
        >
          <el-option
            v-for="tag in tags"
            :key="tag.id"
            :label="`${deviceName(tag.deviceId)} / ${tag.name}`"
            :value="tag.id"
          />
        </el-select>
        <el-button-group>
          <el-button v-for="preset in presets" :key="preset.hours" size="small" @click="applyPreset(preset.hours)">
            {{ preset.label }}
          </el-button>
        </el-button-group>
        <el-date-picker
          v-model="customRange"
          type="datetimerange"
          start-placeholder="开始时间"
          end-placeholder="结束时间"
          style="width: 360px"
        />
        <el-button type="primary" :icon="Search" :loading="loading" @click="query">查询</el-button>
        <span class="spacer" />
        <el-tag v-if="resolution" type="info" size="small">数据源：{{ resolutionLabel }}</el-tag>
        <el-button :icon="Download" :disabled="!series.length" @click="exportData">导出 CSV</el-button>
      </div>
      <div ref="chartRef" class="chart-box" />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Download, Search } from '@element-plus/icons-vue'
import * as echarts from 'echarts'
import { deviceApi, historyApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import type { Device, HistorySeries, Tag } from '../types'
import { exportCsv, formatTime } from '../utils'

const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const selectedTags = ref<number[]>([])
const customRange = ref<[Date, Date] | null>(null)
const from = ref(Date.now() - 3600_000)
const to = ref(Date.now())
const loading = ref(false)
const resolution = ref('')
const series = ref<HistorySeries[]>([])
const chartRef = ref<HTMLDivElement>()
let chart: echarts.ECharts | null = null

const presets = [
  { label: '1 小时', hours: 1 },
  { label: '6 小时', hours: 6 },
  { label: '24 小时', hours: 24 },
  { label: '7 天', hours: 24 * 7 },
  { label: '30 天', hours: 24 * 30 }
]

const resolutionLabel = computed(() => {
  if (resolution.value === 'Raw') return '原始数据'
  if (resolution.value === 'FiveMinute') return '5 分钟聚合'
  if (resolution.value === 'Hour') return '小时聚合'
  return resolution.value
})

function deviceName(id: number): string {
  return devices.value.find((x) => x.id === id)?.name ?? `#${id}`
}

function tagName(id: number): string {
  const tag = tags.value.find((x) => x.id === id)
  return tag ? tag.name : `#${id}`
}

function applyPreset(hours: number): void {
  customRange.value = null
  to.value = Date.now()
  from.value = to.value - hours * 3600_000
  void query()
}

async function query(): Promise<void> {
  if (selectedTags.value.length === 0) {
    ElMessage.warning('请先选择测点')
    return
  }

  if (customRange.value) {
    from.value = customRange.value[0].getTime()
    to.value = customRange.value[1].getTime()
  }

  loading.value = true
  try {
    const result = await historyApi.query(selectedTags.value, from.value, to.value)
    resolution.value = result.resolution
    series.value = result.series
    render()
  } catch (error) {
    ElMessage.error(errorMessage(error, '查询失败'))
  } finally {
    loading.value = false
  }
}

function render(): void {
  if (!chart) return
  const colors = ['#1668dc', '#1e9e6a', '#d48806', '#d4380d', '#722ed1', '#13c2c2', '#eb2f96', '#fa8c16']
  chart.setOption(
    {
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'cross' },
        valueFormatter: (value: unknown) => (typeof value === 'number' ? value.toFixed(3) : String(value))
      },
      legend: { type: 'scroll', top: 4 },
      grid: { left: 56, right: 24, top: 44, bottom: 78 },
      xAxis: { type: 'time', axisLabel: { hideOverlap: true } },
      yAxis: { type: 'value', scale: true, splitLine: { lineStyle: { color: '#edf1f7' } } },
      dataZoom: [
        { type: 'inside', start: 0, end: 100 },
        { type: 'slider', height: 22, bottom: 24 }
      ],
      animation: false,
      series: series.value.map((item, index) => ({
        name: tagName(item.tagId),
        type: 'line',
        showSymbol: false,
        sampling: 'lttb',
        lineStyle: { width: 1.6 },
        itemStyle: { color: colors[index % colors.length] },
        data: item.points.map((point) => [point.ts, point.value])
      }))
    },
    true
  )
}

function exportData(): void {
  const rows: (string | number)[][] = []
  for (const item of series.value) {
    for (const point of item.points) {
      rows.push([formatTime(point.ts), tagName(item.tagId), point.value, point.quality])
    }
  }
  exportCsv(`trend_${new Date().toISOString().slice(0, 10)}.csv`, ['时间', '测点', '数值', '质量'], rows)
}

function resize(): void {
  chart?.resize()
}

onMounted(async () => {
  try {
    ;[devices.value, tags.value] = await Promise.all([deviceApi.list(), tagApi.list()])
    selectedTags.value = tags.value.slice(0, 3).map((x) => x.id)
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载测点失败'))
  }

  await nextTick()
  if (chartRef.value) {
    chart = echarts.init(chartRef.value)
    window.addEventListener('resize', resize)
  }

  if (selectedTags.value.length > 0) await query()
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', resize)
  chart?.dispose()
})
</script>
