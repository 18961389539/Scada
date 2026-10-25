<template>
  <div class="page trends-page">
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
          <el-option v-for="tag in tags" :key="tag.id" :label="`${deviceName(tag.deviceId)} / ${tag.name}`" :value="tag.id" />
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
        <el-button type="primary" :icon="Search" :loading="loading" @click="query()">{{ $t('trendsView.k711363') }}</el-button>
        <span class="spacer" />
        <el-switch v-model="autoRefresh" :active-text="$t('trendsView.autoRefresh')" data-test="auto-refresh" />
        <el-tag v-if="resolution" type="info" size="small">{{ $t('trendsView.kebf028', [resolutionLabel]) }}</el-tag>
        <el-tag v-if="totalPoints" type="info" size="small" data-test="point-count">{{ $t('trendsView.pointCount', [totalPoints, series.length]) }}</el-tag>
        <el-button :icon="Download" :disabled="!series.length" @click="exportData">{{ $t('trendsView.k7e9cc8') }}</el-button>
      </div>

      <div class="chart-wrap">
        <div ref="chartRef" class="chart-box" data-test="trend-chart" />
        <div v-if="emptyHint" class="chart-empty" data-test="chart-empty">
          <el-empty :description="emptyHint" :image-size="80" />
        </div>
      </div>

      <div v-if="hasData" class="playback-bar" data-test="playback-bar">
        <el-button
          circle
          size="small"
          :icon="playing ? VideoPause : VideoPlay"
          :title="playing ? $t('trendsView.pause') : $t('trendsView.play')"
          :aria-label="playing ? $t('trendsView.pause') : $t('trendsView.play')"
          data-test="playback-toggle"
          @click="togglePlay"
        />
        <el-button text size="small" :icon="RefreshLeft" :title="$t('trendsView.playbackReset')" data-test="playback-reset" @click="seek(from)" />
        <el-slider
          v-model="cursorTs"
          class="playback-slider"
          :min="from"
          :max="to"
          :step="playStep"
          :format-tooltip="formatTime"
          data-test="playback-slider"
          @input="onSeek"
        />
        <span class="mono tiny cursor-time" data-test="cursor-time">{{ formatTime(cursorTs) }}</span>
        <el-select v-model="speed" size="small" style="width: 92px" data-test="playback-speed">
          <el-option v-for="item in speeds" :key="item" :label="`${item}×`" :value="item" />
        </el-select>
      </div>

      <div v-if="hasData" class="playback-values" data-test="playback-values">
        <div v-for="item in cursorValues" :key="item.tagId" class="pv-item">
          <span class="pv-dot" :style="{ background: item.color }" />
          <span class="pv-name">{{ item.name }}</span>
          <span class="pv-value mono">{{ item.value === null ? '-' : formatValue(item.value, 3) }}</span>
          <el-tag v-if="item.quality" :type="qualityType(item.quality)" size="small" effect="plain">{{ qualityLabels[item.quality] }}</el-tag>
          <span v-else class="tiny muted">{{ $t('trendsView.noPointBefore') }}</span>
        </div>
      </div>

      <div v-if="selectedTags.length && series.length" v-loading="analysisLoading" class="analysis" data-test="history-analysis">
        <div class="analysis-head">
          <span class="analysis-title">{{ $t('trendsView.analysisTitle') }}</span>
          <span class="tiny muted">{{ $t('trendsView.analysisHint') }}</span>
          <span class="spacer" />
          <el-tag v-if="analysisRows.length" size="small" type="info">{{ $t('trendsView.coverageSummary', [coverageSummary]) }}</el-tag>
        </div>

        <el-alert v-if="!canAnalyze" :title="$t('trendsView.analysisLimit')" type="info" :closable="false" show-icon />
        <template v-else>
          <div v-if="analysisError" class="tiny muted">{{ analysisError }}</div>
          <el-table v-if="analysisRows.length" :data="analysisRows" size="small" class="analysis-table" data-test="analysis-table">
            <el-table-column :label="$t('trendsView.tag')" min-width="210" show-overflow-tooltip>
              <template #default="{ row }">
                <span class="pv-dot" :style="{ background: row.color }" />
                {{ row.name }}
              </template>
            </el-table-column>
            <el-table-column :label="$t('trendsView.weightedAvg')" width="130" align="right">
              <template #default="{ row }">
                <span class="mono">{{ row.timeWeightedAvg === null ? '-' : formatValue(row.timeWeightedAvg, 3) }}</span>
              </template>
            </el-table-column>
            <el-table-column :label="$t('trendsView.min')" width="100" align="right">
              <template #default="{ row }"><span class="mono">{{ formatValue(row.min, 3) }}</span></template>
            </el-table-column>
            <el-table-column :label="$t('trendsView.max')" width="100" align="right">
              <template #default="{ row }"><span class="mono">{{ formatValue(row.max, 3) }}</span></template>
            </el-table-column>
            <el-table-column :label="$t('trendsView.integral')" width="140" align="right">
              <template #default="{ row }"><span class="mono">{{ formatValue(row.integralHours, 4) }}</span></template>
            </el-table-column>
            <el-table-column :label="$t('trendsView.coverage')" width="100" align="right">
              <template #default="{ row }">{{ row.coverage }}%</template>
            </el-table-column>
            <el-table-column :label="$t('trendsView.samples')" width="110" align="right">
              <template #default="{ row }">{{ row.goodSamples }} / {{ row.samples }}</template>
            </el-table-column>
          </el-table>

          <template v-if="analysisRows.length">
            <div class="quality-legend-line">
              <span v-for="item in qualities" :key="item"><i class="quality-swatch" :class="`q-${item}`" />{{ qualityLabels[item] }}</span>
              <span class="spacer" />
              <span>{{ $t('trendsView.qualityHint') }}</span>
            </div>
            <div v-for="row in analysisRows" :key="row.tagId" class="quality-row" :data-test="`quality-${row.tagId}`">
              <span class="quality-name" :title="row.name">{{ row.name }}</span>
              <div class="quality-strip">
                <span
                  v-for="(interval, index) in row.quality"
                  :key="index"
                  class="quality-seg"
                  :class="`q-${interval.quality}`"
                  :style="{ left: percent(interval.from), width: percent(interval.to - interval.from) }"
                  :title="`${qualityLabels[interval.quality]} · ${formatTime(interval.from)} ~ ${formatTime(interval.to)}`"
                />
              </div>
              <span v-if="row.qualityTruncated" class="tiny muted">{{ $t('trendsView.qualityTruncated') }}</span>
            </div>
          </template>
        </template>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { Download, RefreshLeft, Search, VideoPause, VideoPlay } from '@element-plus/icons-vue'
import * as echarts from 'echarts'
import { deviceApi, historyApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import type { Device, HistoryAnalysisResult, HistoryPoint, HistorySeries, Tag, TagQuality } from '../types'
import { exportCsv, formatTime, formatValue, qualityLabels, qualityType } from '../utils'
import { usePolling } from '../composables/usePolling'
import { tr } from '../i18n'
import { chartPalette } from '../theme'

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

const analysis = ref<HistoryAnalysisResult | null>(null)
const analysisLoading = ref(false)
const analysisError = ref('')
const cursorTs = ref(from.value)
const playing = ref(false)
// 1× 为实时速度；大倍速用于快速浏览长区间，300× 即 1 秒回放 5 分钟数据。
const speeds = [1, 10, 60, 300]
const speed = ref(60)
let playTimer: number | undefined
let querySeq = 0

const seriesColors = ['#1668dc', '#1e9e6a', '#d48806', '#d4380d', '#722ed1', '#13c2c2', '#eb2f96', '#fa8c16']
const qualities: TagQuality[] = ['Good', 'Uncertain', 'Bad', 'CommFailure']

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
const hasData = computed(() => totalPoints.value > 0)
const canAnalyze = computed(() => selectedTags.value.length <= 20 && to.value - from.value <= 24 * 3600_000)
const playStep = computed(() => Math.max(1, Math.round((to.value - from.value) / 2000)))

// 查询前 / 无数据时给出明确提示，避免图表区只是一片空白
const emptyHint = computed(() => {
  if (loading.value) return ''
  if (selectedTags.value.length === 0) return tr('trendsView.k24f3cf')
  if (queried.value && totalPoints.value === 0) return tr('trendsView.emptyNoData')
  return ''
})

interface AnalysisRow {
  tagId: number
  name: string
  color: string
  timeWeightedAvg: number | null
  min: number | null
  max: number | null
  integralHours: number
  coverage: number
  goodSamples: number
  samples: number
  quality: { quality: TagQuality; from: number; to: number }[]
  qualityTruncated: boolean
}

const analysisRows = computed<AnalysisRow[]>(() => {
  if (!analysis.value) return []
  return analysis.value.tags.map((item) => ({
    tagId: item.tagId,
    name: tagName(item.tagId),
    color: colorFor(item.tagId),
    timeWeightedAvg: item.timeWeightedAvg,
    min: item.min,
    max: item.max,
    integralHours: item.integralHours,
    coverage: item.coverage,
    goodSamples: item.goodSamples,
    samples: item.samples,
    quality: item.quality,
    qualityTruncated: item.qualityTruncated
  }))
})

const coverageSummary = computed(() => {
  if (!analysisRows.value.length) return '0'
  return (analysisRows.value.reduce((sum, item) => sum + item.coverage, 0) / analysisRows.value.length).toFixed(2)
})

interface CursorValue {
  tagId: number
  name: string
  color: string
  value: number | null
  quality?: TagQuality
}

const cursorValues = computed<CursorValue[]>(() =>
  series.value.map((item) => {
    const point = pointAtOrBefore(item.points, cursorTs.value)
    return { tagId: item.tagId, name: tagName(item.tagId), color: colorFor(item.tagId), value: point?.value ?? null, quality: point?.quality }
  })
)

function pointAtOrBefore(points: HistoryPoint[], ts: number): HistoryPoint | null {
  let low = 0
  let high = points.length - 1
  let found = -1
  while (low <= high) {
    const mid = (low + high) >> 1
    if (points[mid].ts <= ts) {
      found = mid
      low = mid + 1
    } else {
      high = mid - 1
    }
  }
  return found >= 0 ? points[found] : null
}

function deviceName(id: number | null): string {
  if (id === null) return tr('trendsView.k4e0387')
  return devices.value.find((x) => x.id === id)?.name ?? `#${id}`
}

// 图例和导出必须带设备名：不同设备下常有同名测点（如「温度 1」），只显示测点名无法区分
function tagName(id: number): string {
  const tag = tags.value.find((x) => x.id === id)
  return tag ? `${deviceName(tag.deviceId)} / ${tag.name}` : `#${id}`
}

function colorFor(tagId: number): string {
  const index = selectedTags.value.indexOf(tagId)
  return seriesColors[(index < 0 ? 0 : index) % seriesColors.length]
}

function percent(ts: number): string {
  if (to.value <= from.value) return '0%'
  return `${Math.min(100, Math.max(0, ((ts - from.value) / (to.value - from.value)) * 100))}%`
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
  analysis.value = null
  queried.value = false
  playing.value = false
  chart?.clear()
}

async function query(resetPlayback = true): Promise<void> {
  if (selectedTags.value.length === 0) {
    ElMessage.warning(tr('trendsView.k24f3cf'))
    return
  }

  if (customRange.value) {
    from.value = customRange.value[0].getTime()
    to.value = customRange.value[1].getTime()
  }

  const requestFrom = from.value
  const requestTo = to.value
  const requestTags = [...selectedTags.value]
  const seq = ++querySeq
  loading.value = true
  analysis.value = null
  analysisError.value = ''
  try {
    const result = await historyApi.query(requestTags, requestFrom, requestTo)
    if (seq !== querySeq) return
    resolution.value = result.resolution
    series.value = result.series
    if (resetPlayback) {
      playing.value = false
      cursorTs.value = requestFrom
    } else {
      cursorTs.value = Math.min(requestTo, Math.max(requestFrom, cursorTs.value))
    }
    render()
    if (requestTags.length <= 20 && requestTo - requestFrom <= 24 * 3600_000) void loadAnalysis(seq, requestTags, requestFrom, requestTo)
  } catch (error) {
    if (seq === querySeq) ElMessage.error(errorMessage(error, tr('trendsView.k381ea3')))
  } finally {
    if (seq === querySeq) {
      loading.value = false
      queried.value = true
    }
  }
}

async function loadAnalysis(seq: number, tagIds: number[], requestFrom: number, requestTo: number): Promise<void> {
  analysisLoading.value = true
  try {
    const result = await historyApi.analyze(tagIds, requestFrom, requestTo)
    if (seq === querySeq) analysis.value = result
  } catch (error) {
    if (seq === querySeq) analysisError.value = errorMessage(error, tr('trendsView.analysisFailed'))
  } finally {
    if (seq === querySeq) analysisLoading.value = false
  }
}

// 自动刷新：默认关闭，避免后台页面持续查询；间隔 30s，与实时页轮询节奏一致
const autoPolling = usePolling(() => query(false), 30000, {
  enabled: () => autoRefresh.value && !loading.value && selectedTags.value.length > 0
})
// 刚打开开关时立刻补一次，不必等满 30s
watch(autoRefresh, (on) => { if (on) void autoPolling.run() })

function markLine(): Record<string, unknown> | undefined {
  if (!hasData.value) return undefined
  const p = chartPalette()
  return {
    symbol: 'none',
    silent: true,
    animation: false,
    label: {
      formatter: formatTime(cursorTs.value),
      position: 'insideEndTop',
      color: p.annotationText,
      backgroundColor: p.annotationBg,
      padding: [2, 4],
      borderRadius: 3
    },
    lineStyle: { color: '#d4380d', width: 1, type: 'dashed' },
    data: [{ xAxis: cursorTs.value }]
  }
}

function render(): void {
  if (!chart) return
  if (!series.value.some((item) => item.points.length)) {
    chart.clear()
    return
  }
  const p = chartPalette()
  chart.setOption(
    {
      textStyle: { color: p.text },
      tooltip: {
        trigger: 'axis',
        axisPointer: { type: 'cross' },
        valueFormatter: (value: unknown) => (typeof value === 'number' ? value.toFixed(3) : String(value))
      },
      legend: { type: 'scroll', top: 4, textStyle: { color: p.text } },
      grid: { left: 56, right: 24, top: 44, bottom: 66 },
      xAxis: { type: 'time', axisLabel: { hideOverlap: true, color: p.muted } },
      yAxis: { type: 'value', scale: true, axisLabel: { color: p.muted }, splitLine: { lineStyle: { color: p.split } } },
      dataZoom: [
        { type: 'inside', start: 0, end: 100 },
        {
          type: 'slider',
          height: 18,
          bottom: 16,
          showDetail: false,
          ...p.zoom,
          handleSize: '80%',
          moveHandleSize: 3
        }
      ],
      animation: false,
      series: series.value.map((item, index) => ({
        name: tagName(item.tagId),
        type: 'line',
        showSymbol: false,
        sampling: 'lttb',
        lineStyle: { width: 1.6 },
        itemStyle: { color: seriesColors[index % seriesColors.length] },
        markLine: index === 0 ? markLine() : undefined,
        data: item.points.map((point) => [point.ts, point.value])
      }))
    },
    true
  )
}

function drawCursor(): void {
  if (!chart || !hasData.value) return
  // 只更新第一条曲线上的游标线，避免每帧重绘整张图。
  chart.setOption({ series: [{ markLine: markLine() }] }, { lazyUpdate: true })
}

function togglePlay(): void {
  if (playing.value) {
    playing.value = false
    return
  }
  if (cursorTs.value >= to.value) cursorTs.value = from.value
  playing.value = true
}

function seek(ts: number): void {
  playing.value = false
  cursorTs.value = Math.min(to.value, Math.max(from.value, ts))
}

function onSeek(): void {
  playing.value = false
}

watch(playing, (on) => {
  if (playTimer !== undefined) {
    window.clearInterval(playTimer)
    playTimer = undefined
  }
  if (!on) return
  let last = performance.now()
  playTimer = window.setInterval(() => {
    const now = performance.now()
    const delta = now - last
    last = now
    cursorTs.value = Math.min(to.value, cursorTs.value + delta * speed.value)
    if (cursorTs.value >= to.value) playing.value = false
  }, 100)
})

watch(cursorTs, () => drawCursor(), { flush: 'post' })

function onChartClick(event: { offsetX: number }): void {
  if (!chart) return
  // finder 只带 xAxisIndex 时 ECharts 走单轴换算，参数必须是标量像素；传 [x, y] 会算出 NaN
  const ts = Number(chart.convertFromPixel({ xAxisIndex: 0 }, event.offsetX))
  if (!Number.isFinite(ts)) return
  seek(ts)
}

function exportData(): void {
  const rows: (string | number)[][] = []
  for (const item of series.value) {
    for (const point of item.points) {
      rows.push([formatTime(point.ts), tagName(item.tagId), point.value, point.quality])
    }
  }
  exportCsv(
    `trend_${new Date().toISOString().slice(0, 10)}.csv`,
    [tr('trendsView.k89b4aa'), tr('trendsView.k8cff1c'), tr('trendsView.k521523'), tr('trendsView.k82f516')],
    rows
  )
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
    chart.getZr().on('click', onChartClick)
    window.addEventListener('resize', resize)
  }

  if (selectedTags.value.length > 0) await query()
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', resize)
  if (playTimer !== undefined) window.clearInterval(playTimer)
  chart?.getZr().off('click', onChartClick)
  chart?.dispose()
})
</script>

<style scoped>
.trends-page .chart-box {
  height: clamp(300px, calc(100vh - 540px), 560px);
  min-height: 300px;
}

/* 查询前 / 无数据时覆盖图表区，避免只看到空白 */
.chart-wrap {
  position: relative;
}
.chart-empty {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  pointer-events: none;
}

.playback-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
  padding: 4px 10px;
  border: 1px solid var(--scada-border);
  border-radius: var(--scada-radius);
  background: var(--scada-panel-muted);
}
.playback-slider {
  flex: 1;
  margin: 0 10px;
}
.cursor-time {
  min-width: 150px;
  text-align: right;
}

.playback-values {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 18px;
  padding: 8px 4px 2px;
}
.pv-item {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 220px;
}
.pv-dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex: none;
}
.pv-name {
  color: var(--scada-text-secondary);
  font-size: var(--scada-fs-caption);
}
.pv-value {
  font-weight: 600;
  min-width: 64px;
}

.analysis {
  margin-top: 12px;
  padding-top: 10px;
  border-top: 1px solid var(--scada-border);
}
.analysis-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
}
.analysis-title {
  font-weight: 600;
}
.analysis-table {
  margin-bottom: 8px;
}
.quality-legend-line {
  display: flex;
  align-items: center;
  gap: 14px;
  margin: 2px 0 6px;
  color: var(--scada-text-muted);
  font-size: var(--scada-fs-caption);
}
.quality-swatch {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 2px;
  margin-right: 4px;
  vertical-align: -1px;
}
.quality-row {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 3px 0;
}
.quality-name {
  width: 210px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--scada-fs-caption);
}
.quality-strip {
  position: relative;
  flex: 1;
  height: 12px;
  border-radius: 2px;
  overflow: hidden;
  background: var(--scada-panel-muted);
}
.quality-seg {
  position: absolute;
  top: 0;
  bottom: 0;
  min-width: 1px;
}
.q-Good {
  background: rgba(82, 196, 26, 0.55);
}
.q-Bad {
  background: #d4380d;
}
.q-CommFailure {
  background: #722ed1;
}
.q-Uncertain {
  background: #d48806;
}
</style>
