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
          :placeholder="$t('trendsView.kb202dd')"
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
          :start-placeholder="$t('trendsView.ke8868a')"
          :end-placeholder="$t('trendsView.ka0bb9f')"
          style="width: 360px"
        />
        <el-button type="primary" :icon="Search" :loading="loading" @click="query">{{ $t('trendsView.k711363') }}</el-button>
        <span class="spacer" />
        <el-tag v-if="resolution" type="info" size="small">{{ $t('trendsView.kebf028', [resolutionLabel]) }}</el-tag>
        <el-button :icon="Download" :disabled="!series.length" @click="exportData">{{ $t('trendsView.k7e9cc8') }}</el-button>
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
import { tr } from '../i18n'

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
  { label: tr('trendsView.kc8fb1c'), hours: 1 },
  { label: tr('trendsView.k43499b'), hours: 6 },
  { label: tr('trendsView.k284b07'), hours: 24 },
  { label: tr('trendsView.ke45a81'), hours: 24 * 7 },
  { label: tr('trendsView.k9401ee'), hours: 24 * 30 }
]

const resolutionLabel = computed(() => {
  if (resolution.value === 'Raw') return tr('trendsView.k77a6fb')
  if (resolution.value === 'FiveMinute') return tr('trendsView.kfbf1e3')
  if (resolution.value === 'Hour') return tr('trendsView.kc9d329')
  return resolution.value
})

function deviceName(id: number | null): string {
  if (id === null) return tr('trendsView.k4e0387')
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
    ElMessage.warning(tr('trendsView.k24f3cf'))
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
    ElMessage.error(errorMessage(error, tr('trendsView.k381ea3')))
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
  exportCsv(`trend_${new Date().toISOString().slice(0, 10)}.csv`, [tr('trendsView.k89b4aa'), tr('trendsView.k8cff1c'), tr('trendsView.k521523'), tr('trendsView.k82f516')], rows)
}

function resize(): void {
  chart?.resize()
}

onMounted(async () => {
  try {
    ;[devices.value, tags.value] = await Promise.all([deviceApi.list(), tagApi.list()])
    selectedTags.value = tags.value.slice(0, 3).map((x) => x.id)
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('trendsView.kdf92e4')))
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
