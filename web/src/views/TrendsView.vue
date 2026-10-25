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
          data-test="tag-picker"
        >
          <el-option
            v-for="tag in tags"
            :key="tag.id"
            :label="`${deviceName(tag.deviceId)} / ${tag.name}`"
            :value="tag.id"
          />
        </el-select>
        <span class="tiny muted" data-test="selected-count">{{ $t('trendsView.selectedCount', [selectedTags.length]) }}</span>
        <el-button v-if="selectedTags.length" text data-test="clear-tags" @click="clearTags">{{ $t('trendsView.clearTags') }}</el-button>
        <el-button-group>
          <el-button
            v-for="preset in presets"
            :key="preset.hours"
            :type="activePreset === preset.hours ? 'primary' : 'default'"
            @click="applyPreset(preset.hours)"
          >
            {{ preset.label }}
          </el-button>
        </el-button-group>
        <el-date-picker
          v-model="customRange"
          type="datetimerange"
          :start-placeholder="$t('trendsView.ke8868a')"
          :end-placeholder="$t('trendsView.ka0bb9f')"
          style="width: 360px"
          @change="onRangeChange"
        />
        <el-button type="primary" :icon="Search" :loading="loading" @click="query">{{ $t('trendsView.k711363') }}</el-button>
        <span class="spacer" />
        <el-switch v-model="autoRefresh" :active-text="$t('trendsView.autoRefresh')" data-test="auto-refresh" />
        <el-tag v-if="resolution" type="info" size="small">{{ $t('trendsView.kebf028', [resolutionLabel]) }}</el-tag>
        <el-tag v-if="totalPoints" type="info" size="small" data-test="point-count">{{ $t('trendsView.pointCount', [totalPoints, series.length]) }}</el-tag>
        <el-button :icon="Download" :disabled="!series.length" @click="exportData">{{ $t('trendsView.k7e9cc8') }}</el-button>
      </div>
      <div ref="chartRef" class="chart-box" />
      <div v-if="emptyHint" class="chart-empty" data-test="chart-empty">
        <el-empty :description="emptyHint" :image-size="80" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
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
// 预置范围高亮：点了哪个按钮一眼可见；手动改日期后取消高亮
const activePreset = ref<number | null>(1)
const autoRefresh = ref(false)
// 是否已执行过查询：区分「还没查」和「查了但没数据」，空状态文案不同
const queried = ref(false)
let refreshTimer: number | undefined

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

const totalPoints = computed(() => series.value.reduce((sum, item) => sum + item.points.length, 0))

// 查询前 / 无数据时给出明确提示，避免图表区只是一片空白
const emptyHint = computed(() => {
  if (loading.value) return ''
  if (selectedTags.value.length === 0) return tr('trendsView.k24f3cf')
  if (queried.value && totalPoints.value === 0) return tr('trendsView.emptyNoData')
  return ''
})

function deviceName(id: number | null): string {
  if (id === null) return tr('trendsView.k4e0387')
  return devices.value.find((x) => x.id === id)?.name ?? `#${id}`
}

// 图例和导出必须带设备名：不同设备下常有同名测点（如「温度 1」），只显示测点名无法区分
function tagName(id: number): string {
  const tag = tags.value.find((x) => x.id === id)
  return tag ? `${deviceName(tag.deviceId)} / ${tag.name}` : `#${id}`
}

function applyPreset(hours: number): void {
  activePreset.value = hours
  customRange.value = null
  to.value = Date.now()
  from.value = to.value - hours * 3600_000
  void query()
}

// 手动改日期后脱离预置；与预置按钮保持一致：选完即查
function onRangeChange(): void {
  activePreset.value = null
  if (customRange.value) void query()
}

function clearTags(): void {
  selectedTags.value = []
  series.value = []
  queried.value = false
  chart?.clear()
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
    queried.value = true
  }
}

// 自动刷新：默认关闭，避免后台页面持续查询；间隔 30s，与实时页轮询节奏一致
watch(autoRefresh, (on) => {
  if (refreshTimer !== undefined) {
    window.clearInterval(refreshTimer)
    refreshTimer = undefined
  }
  if (!on) return
  refreshTimer = window.setInterval(() => {
    if (!loading.value && selectedTags.value.length) void query()
  }, 30000)
})

function render(): void {
  if (!chart) return
  if (!series.value.some((item) => item.points.length)) {
    chart.clear()
    return
  }
  const colors = ['#1668dc', '#1e9e6a', '#d48806', '#d4380d', '#722ed1', '#13c2c2', '#eb2f96', '#fa8c16']
  chart.setOption(
    {
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'cross' },
        valueFormatter: (value: unknown) => (typeof value === 'number' ? value.toFixed(3) : String(value))
      },
      legend: { type: 'scroll', top: 4 },
      grid: { left: 56, right: 24, top: 44, bottom: 66 },
      xAxis: { type: 'time', axisLabel: { hideOverlap: true } },
      yAxis: { type: 'value', scale: true, splitLine: { lineStyle: { color: '#edf1f7' } } },
      dataZoom: [
        { type: 'inside', start: 0, end: 100 },
        {
          type: 'slider',
          height: 18,
          bottom: 16,
          showDetail: false,
          borderColor: '#e3e9f2',
          backgroundColor: '#f7fafd',
          fillerColor: 'rgba(22, 104, 220, 0.10)',
          dataBackground: {
            lineStyle: { color: '#cdd9e8' },
            areaStyle: { color: '#eaf0f8' }
          },
          selectedDataBackground: {
            lineStyle: { color: '#8fb4e8' },
            areaStyle: { color: '#dbe8f8' }
          },
          handleSize: '80%',
          handleStyle: { color: '#fff', borderColor: '#1668dc' },
          moveHandleSize: 3,
          moveHandleStyle: { color: '#cbd8e8' }
        }
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
  if (refreshTimer !== undefined) window.clearInterval(refreshTimer)
  chart?.dispose()
})
</script>

<style scoped>
.panel {
  position: relative;
}
/* 查询前 / 无数据时覆盖图表区，避免只看到空白 */
.chart-empty {
  position: absolute;
  left: 0;
  right: 0;
  bottom: var(--scada-space-5);
  height: 440px;
  display: flex;
  align-items: center;
  justify-content: center;
  pointer-events: none;
}
</style>
