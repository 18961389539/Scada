<template>
  <div class="page trends-page">
    <div class="panel">
      <div class="toolbar">
        <!-- 上千点位时 el-select 为每个点位渲染 DOM，展开即卡；改用虚拟列表，只绘制可见行 -->
        <el-select-v2
          v-model="selectedTags"
          :options="tagOptions"
          multiple
          filterable
          collapse-tags
          collapse-tags-tooltip
          :multiple-limit="50"
          :aria-label="$t('trendsView.kb202dd')"
          :placeholder="$t('trendsView.kb202dd')"
          class="trend-tag-picker"
          data-test="tag-picker"
        />
        <span class="tiny muted" data-test="selected-count">{{ $t('trendsView.selectedCount', [selectedTags.length]) }}</span>
        <span v-if="selectedTags.length >= 50" class="tiny trend-limit-hint" data-test="tag-limit-hint">{{ $t('trendsUx.limitReached') }}</span>
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
          class="trend-date-range"
          popper-class="trend-date-popper"
          @change="onRangeChange"
        />
        <el-button type="primary" :icon="Search" :loading="loading" @click="query()">{{ $t('trendsView.k711363') }}</el-button>
        <span class="spacer" />
        <el-switch v-model="autoRefresh" :active-text="$t('trendsView.autoRefresh')" data-test="auto-refresh" />
        <el-tag v-if="resolution" class="tag-quiet" :type="undefined" size="small">{{ $t('trendsView.kebf028', [resolutionLabel]) }}</el-tag>
        <el-tag v-if="totalPoints" class="tag-quiet" :type="undefined" size="small" data-test="point-count">{{ $t('trendsView.pointCount', [totalPoints, series.length]) }}</el-tag>
        <el-button :icon="Download" :disabled="!hasData || pendingConditions || loading" data-test="trend-export" @click="exportData">{{ $t('trendsView.k7e9cc8') }}</el-button>
      </div>
      <PageStatusBar :issues="pageIssues" @action="onStatusAction" />
      <p v-if="displayedQuery" id="trend-result-context" class="trend-result-context muted tiny" role="status" data-test="trend-result-context">{{ $t('trendsView.resultContext', [formatTime(displayedQuery.from), formatTime(displayedQuery.to), displayedQuery.tagIds.length]) }}</p>
      <p v-if="hasData" class="tiny muted" data-test="trend-axis-hint">{{ $t('trendsView.axisGroupingHint') }}</p>
      <p v-if="alarmMarks.length" class="tiny muted" data-test="trend-alarm-marks">{{ $t('trendsUx.alarmMarks') }}</p>

      <div class="chart-wrap">
        <div ref="chartRef" class="chart-box" :style="{ height: `${chartModel.height}px` }" role="img" :aria-describedby="displayedQuery ? 'trend-result-context' : undefined" :aria-label="chartDescription" data-test="trend-chart" />
        <div v-if="emptyHint" class="chart-empty" data-test="chart-empty">
          <el-empty :description="emptyHint" :image-size="80" />
        </div>
      </div>

      <TrendPlaybackBar
        v-if="hasData"
        :playing="playing"
        :cursor-ts="cursorTs"
        :display-from="displayFrom"
        :display-to="displayTo"
        :play-step="playStep"
        :speed="speed"
        :cursor-values="cursorValues"
        @toggle="togglePlay"
        @reset="seek(displayFrom)"
        @seek="onSeek"
        @update:speed="speed = $event"
      />

      <TrendAnalysisPanel
        :show="!!displayedQuery && series.length > 0"
        :loading="analysisLoading"
        :error="analysisError"
        :can-analyze="canAnalyze"
        :coverage-summary="coverageSummary"
        :qualities="qualities"
        :rows="analysisRows"
        :display-from="displayFrom"
        :display-to="displayTo"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Download, Search } from '@element-plus/icons-vue'
import * as echarts from '../charts'
import { alarmApi, deviceApi, historyApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import type { AlarmEvent, Device, HistoryAnalysisResult, HistoryPoint, HistorySeries, Tag, TagQuality } from '../types'
import { exportCsv, formatTime } from '../utils'
import { formatValue } from '../utils'
import { usePolling } from '../composables/usePolling'
import { tr } from '../i18n'
import { chartPalette } from '../theme'
import { positiveQueryId } from '../nav/query'
import { visibleNavItems } from '../nav/catalog'
import { useAuthStore } from '../stores/auth'
import { readLastText, rememberText } from '../composables/lastChoice'
import { createQuerySync } from '../composables/routeQuery'
import TrendPlaybackBar from '../components/TrendPlaybackBar.vue'
import TrendAnalysisPanel from '../components/TrendAnalysisPanel.vue'
import PageStatusBar, { type PageIssue } from '../components/PageStatusBar.vue'
import { buildTrendChart, formatTrendValue, type TrendAlarmMark, type TrendChartLabels, type TrendTrace } from '../components/trendChart'

const route = useRoute()
const router = useRouter()
const querySync = createQuerySync(() => route.query, ['tagIds', 'tagId', 'preset', 'from', 'to'])
const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const selectedTags = ref<number[]>([])
const customRange = ref<[Date, Date] | null>(null)
const from = ref(Date.now() - 3600_000)
const to = ref(Date.now())
const loading = ref(false)
const resolution = ref('')
const downsampled = ref(false)
const series = ref<HistorySeries[]>([])
const chartRef = ref<HTMLDivElement>()
let chart: echarts.ECharts | null = null
// 预置范围高亮：点了哪个按钮一眼可见；手动改日期后取消高亮
const activePreset = ref<number | null>(1)
const autoRefresh = ref(false)
// 是否已执行过查询：区分「还没查」和「查了但没数据」，空状态文案不同
const queried = ref(false)
const queryError = ref('')
const catalogError = ref('')
const catalogLoading = ref(false)
const contextError = ref('')
interface DisplayedQuery { tagIds: number[]; from: number; to: number; key: string }
const displayedQuery = ref<DisplayedQuery | null>(null)
const conditionKey = computed(() => JSON.stringify({
  tagIds: selectedTags.value,
  preset: activePreset.value,
  range: customRange.value?.map(date => date.getTime()) ?? (activePreset.value === null ? [from.value, to.value] : null)
}))
const pendingConditions = computed(() => !!displayedQuery.value && displayedQuery.value.key !== conditionKey.value)
const displayFrom = computed(() => displayedQuery.value?.from ?? from.value)
const displayTo = computed(() => displayedQuery.value?.to ?? to.value)

const analysis = ref<HistoryAnalysisResult | null>(null)
const analysisLoading = ref(false)
const analysisError = ref('')
const cursorTs = ref(from.value)
const playing = ref(false)
// 1× 为实时速度；大倍速用于快速浏览长区间，300× 即 1 秒回放 5 分钟数据。

const speed = ref(60)
let playTimer: number | undefined
let querySeq = 0
let disposed = false, catalogLoaded = false

const seriesColors = ['#1668dc', '#1e9e6a', '#d48806', '#d4380d', '#722ed1', '#13c2c2', '#eb2f96', '#fa8c16']
const qualities: TagQuality[] = ['Good', 'Uncertain', 'Bad', 'CommFailure']
const valueLabels: TrendChartLabels = {
  unitless: tr('trendsView.unitlessAxis'), boolean: tr('trendsView.booleanAxis'),
  aggregateBoolean: tr('trendsView.aggregateBooleanAxis'),
  zero: tr('realtimeMonitor.bitZero'), one: tr('realtimeMonitor.bitOne'),
  formatNumber: value => formatValue(value, 3)
}
const chartTraces = computed<TrendTrace[]>(() => series.value.map(item => {
  const tag = tags.value.find(tag => tag.id === item.tagId)
  return { ...item, name: tagName(item.tagId), unit: tag?.unit?.trim() ?? '', isBoolean: tag?.dataType === 'Bool', color: colorFor(item.tagId) }
}))
const auth = useAuthStore()
const alarmMarkLimit = 500
const alarmMarks = ref<TrendAlarmMark[]>([])
const alarmMarksCapped = ref(false)
const alarmMarksError = ref('')
const trendAlarms = ref<AlarmEvent[]>([])
const canViewAlarms = computed(() => visibleNavItems(auth.user).some(item => item.path === '/alarms'))
const chartModel = computed(() => buildTrendChart(chartTraces.value, {
  from: displayFrom.value, to: displayTo.value, resolution: resolution.value, palette: chartPalette(), labels: valueLabels,
  marks: alarmMarks.value
}))
const hasBooleanTraces = computed(() => chartTraces.value.some(trace => trace.isBoolean && trace.points.length))
const hasAggregatedBooleans = computed(() => hasBooleanTraces.value && resolution.value !== 'Raw')
// 页面级错误/提示统一走状态条：单条时原位显示，多条时折叠，避免把图表推到首屏之外。
const pageIssues = computed<PageIssue[]>(() => {
  const issues: PageIssue[] = []
  if (catalogError.value) issues.push({ id: 'catalog', test: 'trend-catalog-error', level: 'error', message: catalogError.value, action: tr('common.retry'), actionTest: 'trend-catalog-retry' })
  if (queryError.value) issues.push({ id: 'query', test: 'trend-query-error', level: 'error', message: queryError.value })
  if (contextError.value) issues.push({ id: 'context', test: 'trend-context-error', level: 'warning', message: contextError.value })
  if (pendingConditions.value) issues.push({ id: 'pending', test: 'trend-pending-conditions', level: 'warning', message: tr('trendsView.conditionsChanged') })
  if (hasAggregatedBooleans.value) issues.push({ id: 'boolean', test: 'trend-boolean-aggregate', level: 'warning', message: tr('trendsView.aggregateBooleanHint') })
  else if (hasBooleanTraces.value && downsampled.value) issues.push({ id: 'sampled', test: 'trend-boolean-sampled', level: 'warning', message: tr('trendsView.sampledBooleanHint') })
  if (alarmMarksCapped.value) issues.push({ id: 'marks-capped', test: 'trend-alarm-marks-capped', level: 'warning', message: tr('trendsUx.alarmMarksCapped', [alarmMarkLimit]) })
  if (alarmMarksError.value) issues.push({ id: 'marks-error', test: 'trend-alarm-marks-error', level: 'warning', message: alarmMarksError.value })
  return issues
})
function onStatusAction(id: string): void {
  if (id === 'catalog') void loadCatalog()
}
const chartDescription = computed(() => [tr('model.lineChart'), ...chartModel.value.tracks.map(track => track.label)].join(' · '))

// 最近使用测点：上千点位的站点里，靠全量下拉翻找太慢；按用户记在本机，不进服务端配置
const RECENT_TAG_KEY = 'trend_recent_tags'
const RECENT_TAG_LIMIT = 8
function readRecentTagIds(): number[] {
  return readLastText(RECENT_TAG_KEY)
    .split(',')
    .map(part => positiveQueryId(part.trim()))
    .filter((id): id is number => id !== undefined)
    .slice(0, RECENT_TAG_LIMIT)
}
const recentTagIds = ref<number[]>(readRecentTagIds())
const recentTags = computed(() => recentTagIds.value
  .map(id => tags.value.find(tag => tag.id === id))
  .filter((tag): tag is Tag => !!tag))
// 已出现在「最近使用」里的测点不再重复列进全量组，避免同一测点出现两次
const otherTags = computed(() => {
  const recent = new Set(recentTags.value.map(tag => tag.id))
  return tags.value.filter(tag => !recent.has(tag.id))
})
// select-v2 的 options：分组结构与原来的 el-option-group 一致，标签在这里一次算好，
// 避免每次展开下拉都逐条 find 设备名
const deviceNames = computed(() => new Map(devices.value.map(device => [device.id, device.name])))
const tagOptions = computed(() => {
  if (!tags.value.length) return []
  const toOption = (tag: Tag) => ({
    value: tag.id,
    label: `${deviceNames.value.get(tag.deviceId ?? -1) ?? (tag.deviceId ? `#${tag.deviceId}` : tr('trendsView.k4e0387'))} / ${tag.name}`
  })
  const groups: { label: string; options: { value: number; label: string }[] }[] = []
  if (recentTags.value.length) groups.push({ label: tr('trendsUx.recentTags'), options: recentTags.value.map(toOption) })
  groups.push({ label: tr('trendsUx.allTags'), options: otherTags.value.map(toOption) })
  return groups
})
function rememberRecentTags(ids: number[]): void {
  if (!ids.length) return
  const next = [...new Set([...ids, ...recentTagIds.value])].slice(0, RECENT_TAG_LIMIT)
  recentTagIds.value = next
  rememberText(RECENT_TAG_KEY, next.join(','))
}

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
const canAnalyze = computed(() => !!displayedQuery.value && displayedQuery.value.tagIds.length <= 20 && displayTo.value - displayFrom.value <= 24 * 3600_000)
const playStep = computed(() => Math.max(1, Math.round((displayTo.value - displayFrom.value) / 2000)))

// 查询前 / 无数据时给出明确提示，避免图表区只是一片空白
const emptyHint = computed(() => {
  if (loading.value) return ''
  if (catalogError.value && !catalogLoaded) return catalogError.value
  if (queryError.value && totalPoints.value === 0) return queryError.value
  if (contextError.value && selectedTags.value.length === 0) return contextError.value
  if (selectedTags.value.length === 0) return tr('trendsView.k24f3cf')
  if (queried.value && totalPoints.value === 0) return tr('trendsView.emptyNoData')
  if (!queried.value) return tr('trendsView.readyToQuery')
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
    name: tagLabel(item.tagId),
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
  displayValue: string
}

const cursorValues = computed<CursorValue[]>(() =>
  series.value.map((item) => {
    const point = pointAtOrBefore(item.points, cursorTs.value)
    const tag = tags.value.find(tag => tag.id === item.tagId)
    return {
      tagId: item.tagId, name: tagName(item.tagId), color: colorFor(item.tagId),
      value: point?.value ?? null, quality: point?.quality,
      displayValue: point ? formatTrendValue(point.value, { unit: tag?.unit ?? '', isBoolean: tag?.dataType === 'Bool' }, resolution.value !== 'Raw', valueLabels) : '-'
    }
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

function tagLabel(id: number): string {
  const unit = tags.value.find(tag => tag.id === id)?.unit?.trim()
  return unit ? `${tagName(id)} [${unit}]` : tagName(id)
}

function colorFor(tagId: number): string {
  const index = series.value.findIndex(item => item.tagId === tagId)
  return seriesColors[(index < 0 ? 0 : index) % seriesColors.length]
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
  if (!customRange.value) { applyPreset(1); return }
  activePreset.value = null
  void query()
}

function invalidateQuery(): void {
  querySeq++
  loading.value = false
  analysisLoading.value = false
  playing.value = false
}

function clearResults(): void {
  invalidateQuery()
  series.value = []
  analysis.value = null
  analysisError.value = ''
  alarmMarks.value = []
  alarmMarksCapped.value = false
  alarmMarksError.value = ''
  trendAlarms.value = []
  resolution.value = ''
  downsampled.value = false
  displayedQuery.value = null
  queried.value = false
  queryError.value = ''
  chart?.clear()
}

function clearTags(): void {
  selectedTags.value = []
  contextError.value = ''
  clearResults()
  // 空选择也要写回（tagIds 为空串），否则刷新会恢复上一次的测点
  syncUrl()
}

watch(selectedTags, () => {
  contextError.value = ''
  if (!selectedTags.value.length) clearResults()
  else invalidateQuery()
}, { deep: true, flush: 'sync' })

async function query(resetPlayback = true, background = false): Promise<void> {
  if (disposed || selectedTags.value.length === 0) return
  if (background && pendingConditions.value) return

  if (customRange.value) {
    from.value = customRange.value[0].getTime()
    to.value = customRange.value[1].getTime()
  } else if (activePreset.value !== null) {
    to.value = Date.now()
    from.value = to.value - activePreset.value * 3600_000
  }

  const requestFrom = from.value
  const requestTo = to.value
  const requestTags = [...selectedTags.value]
  if (!Number.isFinite(requestFrom) || !Number.isFinite(requestTo) || requestFrom >= requestTo) {
    invalidateQuery()
    queryError.value = tr('trendsView.invalidRange')
    return
  }
  const requestKey = conditionKey.value
  const seq = ++querySeq
  loading.value = true
  analysisLoading.value = false
  if (resetPlayback) playing.value = false
  if (!background) queryError.value = ''
  try {
    const result = await historyApi.query(requestTags, requestFrom, requestTo)
    if (seq !== querySeq) return
    queryError.value = ''
    analysis.value = null
    analysisError.value = ''
    queried.value = true
    resolution.value = result.resolution
    downsampled.value = result.downsampled === true
    series.value = result.series
    displayedQuery.value = { tagIds: requestTags, from: requestFrom, to: requestTo, key: requestKey }
    syncUrl()
    rememberRecentTags(requestTags)
    if (resetPlayback) {
      playing.value = false
      cursorTs.value = requestFrom
    } else {
      cursorTs.value = Math.min(requestTo, Math.max(requestFrom, cursorTs.value))
    }
    render()
    void loadAlarmMarks(seq, requestTags, requestFrom, requestTo)
    if (requestTags.length <= 20 && requestTo - requestFrom <= 24 * 3600_000) void loadAnalysis(seq, requestTags, requestFrom, requestTo)
  } catch (error) {
    if (seq === querySeq) {
      queryError.value = errorMessage(error, tr('trendsView.k381ea3'))
    }
  } finally {
    if (seq === querySeq) loading.value = false
  }
}

function valueNear(points: HistoryPoint[], ts: number, fallback: number): number {
  let best = fallback
  let distance = Number.POSITIVE_INFINITY
  for (const point of points) {
    const gap = Math.abs(point.ts - ts)
    if (gap < distance) { best = point.value; distance = gap }
  }
  return best
}

function alarmPageQuery(alarm: AlarmEvent): { path: string; query: Record<string, string> } {
  const query: Record<string, string> = { focus: String(alarm.id) }
  if (alarm.tagId) query.tagId = String(alarm.tagId)
  // 已恢复且仍搁置或抑制的报警不在“恢复未确认”里，实时筛选会把它滤掉。
  const ended = alarm.endTs != null
  if (ended && (alarm.ackTs != null || alarm.shelved || alarm.suppressed)) {
    const endTs = alarm.endTs ?? alarm.startTs
    const ackTs = alarm.ackTs ?? alarm.startTs
    query.tab = 'history'
    query.from = String(Math.min(alarm.startTs, ackTs, endTs) - 1000)
    query.to = String(Math.max(alarm.startTs, ackTs, endTs) + 1000)
    return { path: '/alarms', query }
  }
  query.state = alarm.shelved && !alarm.endTs ? 'shelved'
    : alarm.suppressed && !alarm.endTs ? 'suppressed'
      : alarm.endTs && !alarm.ackTs ? 'rtnUnack'
        : alarm.ackTs ? 'acked' : 'unack'
  return { path: '/alarms', query }
}

async function loadAlarmMarks(seq: number, tagIds: number[], requestFrom: number, requestTo: number): Promise<void> {
  alarmMarksError.value = ''
  alarmMarksCapped.value = false
  if (!canViewAlarms.value) { alarmMarks.value = []; trendAlarms.value = []; return }
  try {
    const alarms = await alarmApi.query({ from: requestFrom, to: requestTo, tagIds, touch: true, limit: alarmMarkLimit, includeShelved: true })
    if (seq !== querySeq) return
    trendAlarms.value = alarms
    alarmMarksCapped.value = alarms.length >= alarmMarkLimit
    const byTag = new Map(series.value.map(item => [item.tagId, item.points]))
    const marks: TrendAlarmMark[] = []
    for (const alarm of alarms) {
      if (!alarm.tagId) continue
      const points = byTag.get(alarm.tagId) ?? []
      const add = (ts: number | null, kind: 'start' | 'ack' | 'return') => {
        if (ts == null || ts < requestFrom || ts > requestTo) return
        marks.push({
          tagId: alarm.tagId!,
          alarmId: alarm.id,
          ts,
          y: valueNear(points, ts, alarm.value),
          label: `${tr(kind === 'start' ? 'trendsUx.alarmMarkStart' : kind === 'ack' ? 'trendsUx.alarmMarkAck' : 'trendsUx.alarmMarkReturn')} · ${alarm.message}`,
          color: kind === 'start' ? '#d4380d' : kind === 'ack' ? '#d48806' : '#389e0d'
        })
      }
      add(alarm.startTs, 'start')
      add(alarm.ackTs, 'ack')
      if (alarm.endTs !== alarm.startTs) add(alarm.endTs, 'return')
    }
    alarmMarks.value = marks
    render()
  } catch (error) {
    if (seq !== querySeq) return
    alarmMarks.value = []
    trendAlarms.value = []
    alarmMarksError.value = errorMessage(error, tr('trendsUx.alarmMarksFailed'))
  }
}

function onMarkClick(params: unknown): void {
  const click = params as { componentType?: string; data?: { alarmId?: number } }
  const id = click.componentType === 'markPoint' ? click.data?.alarmId : undefined
  const alarm = id == null ? undefined : trendAlarms.value.find(item => item.id === id)
  if (!alarm) return
  void router.push(alarmPageQuery(alarm))
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

// 自动刷新：默认关闭，避免后台页面持续查询；间隔 30s（实时页为 10s，两者不同）
const autoPolling = usePolling(() => query(false, true), 30000, {
  enabled: () => autoRefresh.value && !loading.value && !pendingConditions.value && selectedTags.value.length > 0
})
// 刚打开开关时立刻补一次，不必等满 30s
watch(autoRefresh, (on) => { if (on) void autoPolling.run() })

function markLine(showLabel: boolean): Record<string, unknown> | undefined {
  if (!hasData.value) return undefined
  const p = chartPalette()
  return {
    symbol: 'none',
    silent: true,
    animation: false,
    label: {
      show: showLabel,
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
  chart.resize({ height: chartModel.value.height })
  chart.setOption(chartModel.value.option, true)
  drawCursor()
}

function drawCursor(): void {
  if (!chart || !hasData.value) return
  const series = chartModel.value.option.series
  const list = Array.isArray(series) ? series : []
  const marks = new Map(list.map(item => [String(item.id), item.markPoint]))
  chart.setOption({ series: chartModel.value.tracks.map((track, index) => {
    const id = String(track.traces[0].tagId)
    return { id, markLine: markLine(index === 0), markPoint: marks.get(id) }
  }) }, { lazyUpdate: true })
}

function togglePlay(): void {
  if (playing.value) {
    playing.value = false
    return
  }
  if (cursorTs.value >= displayTo.value) cursorTs.value = displayFrom.value
  playing.value = true
}

function seek(ts: number): void {
  playing.value = false
  cursorTs.value = Math.min(displayTo.value, Math.max(displayFrom.value, ts))
}

function onSeek(value: number): void {
  playing.value = false
  seek(value)
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
    cursorTs.value = Math.min(displayTo.value, cursorTs.value + delta * speed.value)
    if (cursorTs.value >= displayTo.value) playing.value = false
  }, 100)
})

watch(cursorTs, () => drawCursor(), { flush: 'post' })

function isAlarmMarkTarget(target: unknown): boolean {
  let node = target as { ecData?: { dataModel?: { mainType?: string; type?: string } }; parent?: unknown } | null
  for (let depth = 0; node && depth < 8; depth += 1) {
    const kind = node.ecData?.dataModel?.mainType ?? node.ecData?.dataModel?.type
    if (kind === 'markPoint') return true
    node = (node.parent ?? null) as typeof node
  }
  return false
}

function onChartClick(event: { offsetX: number; target?: unknown }): void {
  // 图表点击在画布点击之后才发出，不能靠标志位挡住同一次定位。
  if (isAlarmMarkTarget(event.target)) return
  if (!chart) return
  // finder 只带 xAxisIndex 时 ECharts 走单轴换算，参数必须是标量像素；传 [x, y] 会算出 NaN
  const ts = Number(chart.convertFromPixel({ xAxisIndex: 0 }, event.offsetX))
  if (!Number.isFinite(ts)) return
  seek(ts)
}

function exportData(): void {
  if (!hasData.value || pendingConditions.value || loading.value) return
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

function parseTagIds(value: unknown): number[] {
  if (typeof value !== 'string' || !value) return []
  const ids = value.split(',').map(part => positiveQueryId(part.trim())).filter((id): id is number => id !== undefined)
  // 过滤已删除的测点，避免地址栏残留导致空图
  return [...new Set(ids)].filter(id => tags.value.some(tag => tag.id === id))
}

// 测点与时间范围写回地址栏：多测点视图才能被复制、被交接班复用。
// tagId 保持不动——它由报警页等入口写入，改动它会触发下面的上下文重建。
function syncUrl(): void {
  if (disposed) return
  const query: Record<string, string> = { tagIds: selectedTags.value.join(',') }
  if (typeof route.query.tagId === 'string') query.tagId = route.query.tagId
  if (activePreset.value !== null) query.preset = String(activePreset.value)
  else {
    query.from = String(from.value)
    query.to = String(to.value)
  }
  const current = route.query
  const unchanged = Object.keys(query).length === Object.keys(current).length &&
    Object.entries(query).every(([key, value]) => current[key] === value)
  if (unchanged) return
  // note the exact query written below so our own replace never re-applies it
  querySync.note({}, query)
  void router.replace({ query })
}

function applyTagContext(): void {
  clearResults()
  const id = positiveQueryId(route.query.tagId)
  const multi = parseTagIds(route.query.tagIds)
  // 空字符串代表用户主动清空过选择，不再回落到默认测点
  const explicitList = route.query.tagIds !== undefined
  if (multi.length) {
    selectedTags.value = multi
    contextError.value = ''
  } else if (explicitList) {
    selectedTags.value = []
    contextError.value = ''
  } else if (route.query.tagId === undefined) {
    selectedTags.value = tags.value.slice(0, 3).map(tag => tag.id)
    contextError.value = ''
  } else if (id && tags.value.some(tag => tag.id === id)) {
    selectedTags.value = [id]
    contextError.value = ''
  } else {
    selectedTags.value = []
    contextError.value = tr('trendsView.unavailableTag')
  }
}

// 时间范围同样从地址栏恢复：preset 优先于显式区间
function applyRangeContext(): void {
  const preset = Number(route.query.preset)
  if (Number.isFinite(preset) && preset > 0) {
    activePreset.value = preset
    customRange.value = null
    return
  }
  const fromTs = Number(route.query.from)
  const toTs = Number(route.query.to)
  if (Number.isFinite(fromTs) && Number.isFinite(toTs) && fromTs > 0 && toTs > fromTs) {
    activePreset.value = null
    customRange.value = [new Date(fromTs), new Date(toTs)]
    from.value = fromTs
    to.value = toTs
  }
}

watch(() => route.fullPath, () => {
  if (!querySync.changed()) return
  if (!catalogLoaded || disposed) return
  applyRangeContext()
  applyTagContext()
  if (selectedTags.value.length) void query()
})

async function loadCatalog(): Promise<void> {
  if (disposed || catalogLoading.value) return
  catalogLoading.value = true
  try {
    const [deviceList, tagList] = await Promise.all([deviceApi.list(), tagApi.list()])
    if (disposed) return
    devices.value = deviceList
    tags.value = tagList
    catalogLoaded = true
    applyTagContext()
    catalogError.value = ''
    if (selectedTags.value.length > 0) await query()
  } catch (error) {
    if (disposed) return
    catalogError.value = errorMessage(error, tr('trendsView.kdf92e4'))
  } finally {
    if (!disposed) catalogLoading.value = false
  }
}

onMounted(async () => {
  await nextTick()
  if (disposed) return
  if (chartRef.value) {
    chart = echarts.init(chartRef.value)
    chart.on('click', onMarkClick)
    chart.getZr().on('click', onChartClick)
    window.addEventListener('resize', resize)
  }

  applyRangeContext()
  await loadCatalog()
})

onBeforeUnmount(() => {
  disposed = true
  invalidateQuery()
  window.removeEventListener('resize', resize)
  if (playTimer !== undefined) window.clearInterval(playTimer)
  chart?.off('click', onMarkClick)
  chart?.getZr().off('click', onChartClick)
  chart?.dispose()
})
</script>

<style scoped>
.trend-tag-picker { min-width: 320px; max-width: 520px; }
.trend-limit-hint { color: var(--scada-warning); white-space: nowrap; }
/* 与报警页一致：小屏可收缩，不在工具栏里撑出横向滚动 */
.trend-date-range { flex: 0 1 360px; width: 360px; min-width: 240px; max-width: 100%; }
@media (max-width: 600px) {
  .trend-tag-picker { min-width: 0; width: 100%; max-width: 100%; }
  .trend-date-range { flex-basis: 100%; width: 100%; min-width: 0; }
  :global(.trend-date-popper .el-date-range-picker) { width: calc(100vw - 24px); max-height: calc(100vh - 24px); overflow: auto; }
  :global(.trend-date-popper .el-picker-panel__body) { min-width: 0; }
  :global(.trend-date-popper .el-date-range-picker__content) { width: 100%; padding: 12px; }
}
.trends-page .chart-box {
  min-height: 300px;
}
.trend-result-context { margin: 10px 0; }

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

</style>
