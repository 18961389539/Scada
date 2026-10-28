<template>
  <div class="page">
    <el-alert v-if="metaError" :title="metaError" type="error" :closable="false" show-icon data-test="oee-meta-error" />
    <el-button v-if="metaError" data-test="oee-meta-retry" @click="refreshMeta">{{ $t('common.retry') }}</el-button>
    <el-alert v-if="queryError" :title="queryError" type="error" :closable="false" show-icon data-test="oee-query-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="oee-action-error" />
    <div class="panel oee-panel" v-loading="loading">
      <div class="oee-actions">
        <router-link v-if="can(auth.user, 'Control')" :to="productionEventsLocation"><el-button data-test="oee-production-events">{{ $t('ux.events.title') }}</el-button></router-link>
        <el-button size="small" :icon="Download" :disabled="!canExport" :loading="exporting" data-test="oee-export-xlsx" @click="exportAs('xlsx')">{{ $t('oee.exportExcel') }}</el-button>
        <el-button size="small" :icon="Download" :disabled="!canExport" :loading="exporting" data-test="oee-export-pdf" @click="exportAs('pdf')">{{ $t('oee.exportPdf') }}</el-button>
      </div>
          <div class="toolbar wrap">
            <el-date-picker v-model="range" type="datetimerange" style="width: 340px" :aria-label="$t('common.timeRange')" data-test="oee-range" @change="onRangeChange" />
            <el-button-group>
              <el-button :type="activeQuick === 24 ? 'primary' : 'default'" @click="setQuick(24)">{{ $t('oee.range24h') }}</el-button>
              <el-button :type="activeQuick === 24 * 7 ? 'primary' : 'default'" @click="setQuick(24 * 7)">{{ $t('oee.range7d') }}</el-button>
              <el-button :type="activeQuick === 24 * 30 ? 'primary' : 'default'" @click="setQuick(24 * 30)">{{ $t('oee.range30d') }}</el-button>
            </el-button-group>
            <el-select v-model="areaId" clearable :aria-label="$t('oee.area')" :placeholder="$t('oee.allAreas')" style="width: 140px" data-test="oee-area">
              <el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" />
            </el-select>
            <el-select v-model="lineId" clearable filterable :aria-label="$t('oee.line')" :placeholder="$t('oee.allLines')" style="width: 160px" data-test="oee-line">
              <el-option v-for="l in filteredLines" :key="l.id" :value="l.id" :label="l.name" />
            </el-select>
            <el-select v-model="productId" clearable filterable :aria-label="$t('oee.product')" :placeholder="$t('oee.allProducts')" style="width: 140px">
              <el-option v-for="p in products" :key="p.id" :value="p.id" :label="p.name" />
            </el-select>
            <el-select v-model="shiftId" clearable :aria-label="$t('oee.shift')" :placeholder="$t('oee.allShifts')" style="width: 140px">
              <el-option v-for="s in shifts" :key="s.id" :value="s.id" :label="s.name" />
            </el-select>
            <el-button type="primary" :loading="loading" data-test="oee-query" @click="load">{{ $t('oee.query') }}</el-button>
          </div>
          <el-button v-if="drillOrigin" :disabled="loading || exporting" class="drill-back" data-test="oee-back-to-range" @click="restoreRange">{{ $t('oeeHierarchy.backToRange') }}</el-button>
          <el-alert v-if="pendingConditions" type="warning" :closable="false" show-icon class="note" data-test="oee-pending-conditions" :title="$t('oeeReview.conditionsChanged')" />
          <p v-if="lastFilter" class="tiny muted" role="status" data-test="oee-result-context">{{ $t('oeeReview.resultContext', [formatTime(lastFilter.from), formatTime(lastFilter.to), areas.find(area => area.id === lastFilter?.areaId)?.name ?? (lastFilter.areaId == null ? $t('oee.allAreas') : `#${lastFilter.areaId}`), lines.find(line => line.id === lastFilter?.lineId)?.name ?? (lastFilter.lineId == null ? $t('oee.allLines') : `#${lastFilter.lineId}`)]) }}</p>
          <el-alert v-if="report?.usedRollups" type="info" :closable="false" show-icon class="note" :title="$t('oee.rollupNote')" />
          <!-- 没有任何计划生产时间与产量时，A/P/Q/OEE 全是「—」。不解释的话会被当成系统故障。 -->
          <el-alert v-if="kpi && !kpi.plannedMs && !kpi.total" type="warning" :closable="false" show-icon class="note" data-test="oee-empty" :title="$t('oee.noData')" />
          <template v-if="kpi">
            <section aria-labelledby="oee-core-title" class="kpi-section">
              <h2 id="oee-core-title" class="kpi-section-title">{{ $t('oeeHierarchy.coreMetrics') }}</h2>
              <div class="core-cards" data-test="oee-core-metrics">
                <article v-for="metric in coreMetrics" :key="metric.id" class="card core-card" :class="[pctClass(metric.value, metric.target), { 'oee-primary': metric.id === 'oee' }]" :data-test="`kpi-${metric.id}`">
                  <h3 class="label">{{ metric.label }}</h3>
                  <div class="value">{{ pct(metric.value) }}</div>
                  <div class="metric-target">{{ $t('oeeHierarchy.target', [targetPct(metric.target)]) }}</div>
                  <span class="metric-status" data-test="oee-target-status">{{ metricStatus(metric.value, metric.target) }}</span>
                </article>
              </div>
            </section>
            <section aria-labelledby="oee-supporting-title" class="kpi-section supporting-section">
              <h2 id="oee-supporting-title" class="kpi-section-title">{{ $t('oeeHierarchy.supportingMetrics') }}</h2>
              <dl class="supporting-metrics" data-test="oee-supporting-metrics">
                <div data-test="kpi-good"><dt>{{ $t('oee.good') }}</dt><dd class="value">{{ num(kpi.good) }}</dd></div>
                <div><dt>{{ $t('oee.scrapRate') }}</dt><dd class="value">{{ pct(kpi.scrapRate) }}</dd></div>
                <div><dt>{{ $t('oee.runPlanned') }}</dt><dd class="value">{{ hours(kpi.runMs) }} / {{ hours(kpi.plannedMs) }}</dd></div>
                <div><dt>MTBF / MTTR (h)</dt><dd class="value">{{ num(kpi.mtbfHours) }} / {{ num(kpi.mttrHours) }}</dd></div>
                <div><dt>{{ $t('oee.faults') }}</dt><dd class="value">{{ count(kpi.faultCount) }}</dd></div>
                <div><dt>{{ $t('oee.vsTarget') }}</dt><dd class="value">{{ ratioPct(kpi.productionVsTargetPercent) }}</dd></div>
              </dl>
            </section>
            <el-row :gutter="16">
              <el-col :span="12"><h4 id="oee-trend-title">{{ $t('oee.trend') }}</h4>
                <div class="chart-wrap">
                  <div ref="trendRef" class="chart" role="img" aria-labelledby="oee-trend-title" data-test="oee-trend-chart" />
                  <div v-if="!hasProduction" class="chart-empty">{{ $t('oee.chartEmpty') }}</div>
                </div>
              </el-col>
              <el-col :span="12"><h4 id="oee-pareto-title">{{ $t('oee.pareto') }}</h4>
                <div class="chart-wrap">
                  <div ref="paretoRef" class="chart" role="img" aria-labelledby="oee-pareto-title" data-test="oee-pareto-chart" />
                  <div v-if="!hasProduction" class="chart-empty">{{ $t('oee.chartEmpty') }}</div>
                </div>
              </el-col>
            </el-row>
            <el-tabs v-model="tab">
              <el-tab-pane :label="$t('oee.tabPareto')" name="pareto">
                <el-table :data="kpi.downtimePareto" size="small" data-test="oee-pareto-table">
                  <el-table-column prop="code" :label="$t('oee.code')" width="100" />
                  <el-table-column prop="name" :label="$t('oee.name')" min-width="140" />
                  <el-table-column :label="$t('oee.category')" width="110"><template #default="{ row }">{{ labelOf(categoryLabels, row.category) }}</template></el-table-column>
                  <el-table-column prop="hours" :label="$t('oee.hours')" width="90" />
                  <el-table-column prop="count" :label="$t('oee.count')" width="80" />
                  <el-table-column prop="percent" label="%" width="80" />
                  <el-table-column prop="cumulativePercent" :label="$t('oee.cumulative')" width="90" />
                </el-table>
              </el-tab-pane>
              <el-tab-pane :label="$t('oee.tabShift')" name="shift">
                <el-table :data="kpi.byShift" size="small" data-test="oee-by-shift">
                  <el-table-column prop="shiftName" :label="$t('oee.shift')" min-width="120" />
                  <el-table-column label="OEE" width="90"><template #default="{ row }">{{ pct(row.oee) }}</template></el-table-column>
                  <el-table-column label="A" width="90"><template #default="{ row }">{{ pct(row.availability) }}</template></el-table-column>
                  <el-table-column label="P" width="90"><template #default="{ row }">{{ pct(row.performance) }}</template></el-table-column>
                  <el-table-column label="Q" width="90"><template #default="{ row }">{{ pct(row.quality) }}</template></el-table-column>
                  <el-table-column prop="good" :label="$t('oee.good')" width="90" />
                </el-table>
              </el-tab-pane>
              <el-tab-pane :label="$t('oee.tabProduct')" name="product">
                <el-table :data="kpi.byProduct" size="small">
                  <el-table-column prop="productName" :label="$t('oee.product')" min-width="120" />
                  <el-table-column label="OEE" width="90"><template #default="{ row }">{{ pct(row.oee) }}</template></el-table-column>
                  <el-table-column prop="good" :label="$t('oee.good')" width="90" />
                  <el-table-column prop="total" :label="$t('oee.total')" width="90" />
                </el-table>
              </el-tab-pane>
              <el-tab-pane :label="$t('oee.tabDay')" name="day">
                <el-table :data="kpi.byDay" size="small" data-test="oee-by-day">
                  <el-table-column prop="label" :label="$t('oee.date')" width="120" />
                  <el-table-column label="OEE" width="90"><template #default="{ row }">{{ pct(row.oee) }}</template></el-table-column>
                  <el-table-column label="A" width="90"><template #default="{ row }">{{ pct(row.availability) }}</template></el-table-column>
                  <el-table-column label="P" width="90"><template #default="{ row }">{{ pct(row.performance) }}</template></el-table-column>
                  <el-table-column label="Q" width="90"><template #default="{ row }">{{ pct(row.quality) }}</template></el-table-column>
                  <el-table-column prop="good" :label="$t('oee.good')" width="90" />
                  <el-table-column :label="$t('oeeHierarchy.viewDay')" min-width="120"><template #default="{ row }"><el-button link type="primary" :disabled="loading || exporting || pendingConditions" data-test="oee-view-day" @click="drillDay(row)">{{ $t('oeeHierarchy.viewDay') }}</el-button></template></el-table-column>
                </el-table>
              </el-tab-pane>
            </el-tabs>
          </template>
    </div>

  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Download } from '@element-plus/icons-vue'
import * as echarts from '../charts'
import { oeeApi, type OeeReport, type ProductionLine, type ProductionProduct, type ProductionShift } from '../api/oee'
import { areaApi } from '../api'
import { useAuthStore } from '../stores/auth'
import { errorMessage } from '../api/http'
import { t } from '../i18n'
import { chartPalette } from '../theme'
import { downloadBlob, formatTime } from '../utils'
import { can } from '../access/policies'

const auth = useAuthStore()
const categoryLabels: Record<string, string> = { Availability: t('oee.availability'), Performance: t('oee.performance'), Quality: t('oee.quality') }
function labelOf(map: Record<string, string>, value: string): string { return map[value] ?? value }
const tab = ref('pareto'), loading = ref(false)
const actionError = ref('')
const range = ref<[Date, Date] | null>([new Date(Date.now() - 86400000), new Date()])
const activeQuick = ref<number | null>(24)
const drillOrigin = ref<{ range: [number, number]; quick: number | null } | null>(null)
const areaId = ref<number>(), lineId = ref<number>(), productId = ref<number>(), shiftId = ref<number>()
const areas = ref<{ id: number; name: string }[]>([]), lines = ref<ProductionLine[]>([]), products = ref<ProductionProduct[]>([])
const shifts = ref<ProductionShift[]>([])
const report = ref<OeeReport | null>(null)
const metaError = ref('')
const queryError = ref('')
const kpi = computed(() => report.value?.kpi ?? null)
const th = computed(() => report.value?.thresholds ?? { targetOee: 0.85, targetAvailability: 0.9, targetPerformance: 0.95, targetQuality: 0.99, clampPerformance: true, topN: 10 })
const filteredLines = computed(() => areaId.value ? lines.value.filter(l => l.areaId === areaId.value) : lines.value)
watch(filteredLines, available => { if (lineId.value != null && !available.some(line => line.id === lineId.value)) lineId.value = undefined }, { flush: 'sync' })
const trendRef = ref<HTMLDivElement>(), paretoRef = ref<HTMLDivElement>()
// 没有计划时间与产量时后端仍返回全 0 的 kpi；图表只剩坐标轴，需要盖一层说明
const hasProduction = computed(() => !!kpi.value && (kpi.value.plannedMs > 0 || kpi.value.total > 0))
let trendChart: echarts.ECharts | null = null, paretoChart: echarts.ECharts | null = null
let querySeq = 0, metaSeq = 0, disposed = false
const lastFilter = ref<ReturnType<typeof filter>>(null), displayedKey = ref(''), exporting = ref(false)
const conditionKey = computed(() => JSON.stringify(filter()))
const pendingConditions = computed(() => !!lastFilter.value && displayedKey.value !== conditionKey.value)
const canExport = computed(() => !!report.value && !!lastFilter.value && !pendingConditions.value && !loading.value && !exporting.value && !queryError.value)
watch(conditionKey, () => { querySeq++; loading.value = false; queryError.value = '' }, { flush: 'sync' })

function filter() {
  if (!range.value) return null
  return { from: range.value[0].getTime(), to: range.value[1].getTime(), areaId: areaId.value, lineId: lineId.value, productId: productId.value, shiftId: shiftId.value }
}
const productionEventsLocation = computed(() => {
  const params = lastFilter.value ?? filter()
  return { path: '/production/events', query: { from: params?.from, to: params?.to, lineId: params?.lineId } }
})

// 无计划时间/产量时服务端仍返回全 0：0% 的 OEE 会被误读成「真的停了」，统一显示 —。
const pct = (v?: number | null) => !hasProduction.value || v == null ? '—' : (v * 100).toFixed(1) + '%'
const targetPct = (v: number) => (v * 100).toFixed(1) + '%'
const coreMetrics = computed(() => [
  { id: 'oee', label: 'OEE', value: kpi.value?.oee, target: th.value.targetOee },
  { id: 'a', label: t('oee.availability'), value: kpi.value?.availability, target: th.value.targetAvailability },
  { id: 'p', label: t('oee.performance') + (kpi.value?.performanceClamped ? ' *' : ''), value: kpi.value?.performance, target: th.value.targetPerformance },
  { id: 'q', label: t('oee.quality'), value: kpi.value?.quality, target: th.value.targetQuality }
])
const metricStatus = (v: number | null | undefined, target: number) => t(!hasProduction.value || v == null ? 'oeeHierarchy.noData' : v >= target ? 'oeeHierarchy.onTarget' : 'oeeHierarchy.belowTarget')
const num = (v?: number | null) => !hasProduction.value || v == null ? '—' : Number(v.toFixed(2))
const count = (v: number) => hasProduction.value ? String(v) : '—'
const ratioPct = (v?: number | null) => !hasProduction.value || v == null ? '—' : `${Number(v.toFixed(2))}%`
const hours = (ms: number) => !hasProduction.value ? '—' : (ms / 3600000).toFixed(2)
const pctClass = (v: number | null | undefined, target: number) => !hasProduction.value || v == null ? '' : v >= target ? 'ok' : v >= target * 0.85 ? 'warn' : 'bad'

function onRangeChange() { activeQuick.value = null; drillOrigin.value = null }
function setQuick(h: number) { drillOrigin.value = null; activeQuick.value = h; range.value = [new Date(Date.now() - h * 3600000), new Date()] }
async function refreshMeta() {
  if (disposed) return
  const seq = ++metaSeq
  try {
    const [a, l, p, s] = await Promise.all([areaApi.list(), oeeApi.lines(), oeeApi.products(), oeeApi.shifts()])
    if (disposed || seq !== metaSeq) return
    areas.value = a; lines.value = l; products.value = p; shifts.value = s
    metaError.value = ''
  } catch (error) {
    if (!disposed && seq === metaSeq) metaError.value = errorMessage(error, t('oee.loadFailed'))
  }
}
async function load() {
  if (disposed || loading.value || exporting.value) return
  const params = filter(), key = conditionKey.value, seq = ++querySeq
  if (!params || !Number.isFinite(params.from) || !Number.isFinite(params.to) || params.from >= params.to) { queryError.value = t('trendsView.invalidRange'); return }
  loading.value = true
  queryError.value = ''
  try {
    const result = await oeeApi.analytics(params)
    if (disposed || seq !== querySeq) return
    report.value = result; lastFilter.value = params; displayedKey.value = key
    await nextTick(); if (!disposed) renderCharts()
    queryError.value = ''
  } catch (error) {
    if (!disposed && seq === querySeq) queryError.value = errorMessage(error, t('oee.loadFailed'))
  } finally { if (!disposed && seq === querySeq) loading.value = false }
}
function renderCharts() {
  if (!report.value || !trendRef.value || !paretoRef.value) return
  trendChart ??= echarts.init(trendRef.value); paretoChart ??= echarts.init(paretoRef.value)
  const p = chartPalette()
  const trend = report.value.trend
  trendChart.setOption({
    textStyle: { color: p.text },
    grid: { left: 8, right: 12, top: 60, bottom: 24, containLabel: true },
    tooltip: { trigger: 'axis' },
    legend: { data: ['OEE', 'A', 'P', 'Q'], textStyle: { color: p.text } },
    xAxis: { type: 'category', data: trend.map(x => x.label), axisLabel: { color: p.muted } },
    yAxis: { type: 'value', max: 100, axisLabel: { formatter: '{value}%', color: p.muted }, splitLine: { lineStyle: { color: p.split } } },
    series: [
      { name: 'OEE', type: 'line', data: trend.map(x => x.oee == null ? null : +(x.oee * 100).toFixed(2)) },
      { name: 'A', type: 'line', data: trend.map(x => x.availability == null ? null : +(x.availability * 100).toFixed(2)) },
      { name: 'P', type: 'line', data: trend.map(x => x.performance == null ? null : +(x.performance * 100).toFixed(2)) },
      { name: 'Q', type: 'line', data: trend.map(x => x.quality == null ? null : +(x.quality * 100).toFixed(2)) }
    ]
  })
  const pareto = report.value.kpi.downtimePareto
  paretoChart.setOption({
    textStyle: { color: p.text },
    grid: { left: 8, right: 8, top: 40, bottom: 24, containLabel: true },
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'category', data: pareto.map(x => x.code), axisLabel: { color: p.muted } },
    yAxis: [
      { type: 'value', name: 'h', axisLabel: { color: p.muted }, splitLine: { lineStyle: { color: p.split } } },
      { type: 'value', max: 100, axisLabel: { formatter: '{value}%', color: p.muted }, splitLine: { lineStyle: { color: p.split } } }
    ],
    series: [
      { type: 'bar', data: pareto.map(x => +x.hours.toFixed(2)) },
      { type: 'line', yAxisIndex: 1, data: pareto.map(x => x.cumulativePercent) }
    ]
  })
}
function drillDay(row: { from: number; to: number }) {
  if (loading.value || exporting.value || pendingConditions.value || !range.value) return
  drillOrigin.value ??= { range: [range.value[0].getTime(), range.value[1].getTime()], quick: activeQuick.value }
  activeQuick.value = null
  range.value = [new Date(row.from), new Date(row.to)]; void load()
}
function restoreRange() {
  if (loading.value || exporting.value || !drillOrigin.value) return
  const origin = drillOrigin.value
  range.value = [new Date(origin.range[0]), new Date(origin.range[1])]
  activeQuick.value = origin.quick
  drillOrigin.value = null
  void load()
}
async function exportAs(format: 'xlsx' | 'pdf') {
  if (disposed || !canExport.value || !lastFilter.value) return
  const url = oeeApi.exportUrl({ ...lastFilter.value, format })
  const token = auth.token
  exporting.value = true
  actionError.value = ''
  try {
    const r = await fetch(url, { headers: token ? { Authorization: `Bearer ${token}` } : {} })
    if (!r.ok) {
      const body = await r.text().catch(() => '')
      let detail = body
      try { detail = (JSON.parse(body) as { message?: string }).message ?? body } catch { /* 非 JSON 就按原文 */ }
      if (!disposed) actionError.value = detail.slice(0, 200) || t('oee.loadFailed')
      return
    }
    const blob = await r.blob()
    if (!disposed) downloadBlob(blob, `oee.${format}`)
  } catch (error) {
    if (!disposed) actionError.value = errorMessage(error, t('oee.loadFailed'))
  } finally { if (!disposed) exporting.value = false }
}

function resize() { trendChart?.resize(); paretoChart?.resize() }
onMounted(async () => { window.addEventListener('resize', resize); await refreshMeta(); if (!disposed) await load() })
onBeforeUnmount(() => { disposed = true; querySeq++; window.removeEventListener('resize', resize); trendChart?.dispose(); paretoChart?.dispose() })
</script>

<style scoped>
.oee-panel { position: relative; }
.oee-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-bottom: 8px;
}
.toolbar { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; }
.wrap { flex-wrap: wrap; }
.spacer { flex: 1; }
.kpi-section { margin: 20px 0; }
.kpi-section-title { margin: 0 0 10px; font-size: var(--scada-fs-section); font-weight: 600; }
.core-cards { display: grid; grid-template-columns: minmax(210px, 1.3fr) repeat(3, minmax(0, 1fr)); gap: 12px; }
.card {
  background: var(--scada-panel);
  border: 1px solid var(--scada-border);
  border-left: 3px solid var(--scada-border-strong);
  border-radius: var(--scada-radius);
  padding: 18px;
  box-shadow: var(--scada-shadow-sm);
}
.card .value {
  font-family: var(--scada-font-mono);
  font-variant-numeric: tabular-nums;
  font-size: 28px;
  font-weight: 600;
  color: var(--scada-text);
}
.card .label { font-size: 14px; color: var(--scada-text-secondary); margin: 0 0 10px; font-weight: 500; }
.oee-primary { background: var(--scada-primary-soft); }
.oee-primary .label { font-weight: 600; }
.oee-primary .value { font-size: 38px; line-height: 1.25; }
.metric-target { margin-top: 10px; color: var(--scada-text-secondary); font-size: 13px; }
.metric-status { display: inline-block; margin-top: 8px; font-size: 13px; font-weight: 600; color: var(--scada-text-secondary); }
.supporting-section { padding: 16px; background: var(--scada-panel-muted); border-radius: var(--scada-radius); }
.supporting-metrics { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 18px 24px; margin: 0; }
.supporting-metrics > div { min-width: 0; }
.supporting-metrics dt { color: var(--scada-text-secondary); font-size: 13px; }
.supporting-metrics dd { margin: 6px 0 0; font-size: 18px; font-family: var(--scada-font-mono); font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.drill-back { margin-bottom: 8px; }
.card.ok { border-left-color: var(--scada-success); }
.card.warn { border-left-color: var(--scada-warning); }
.card.bad { border-left-color: var(--scada-danger); }
.chart { height: 280px; }
.chart-wrap { position: relative; }
.chart-empty {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--scada-space-6);
  text-align: center;
  color: var(--scada-text-muted);
  font-size: 13px;
  background: var(--scada-panel);
  border: 1px dashed var(--scada-border);
  border-radius: var(--scada-radius-sm);
}
.note { margin-bottom: 8px; }
.mt { margin-top: 8px; }

@media (max-width: 1100px) {
  .core-cards { grid-template-columns: repeat(2, minmax(0, 1fr)); }
}
@media (max-width: 768px) {
  .supporting-metrics { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
  .card { padding: 14px; }
  .oee-primary { grid-column: 1 / -1; }
  .core-card:last-child { grid-column: 1 / -1; }
  :deep(.el-row .el-col) { flex: 0 0 100%; max-width: 100%; }
  :deep(.toolbar .el-date-editor) { display: grid; grid-template-columns: 16px minmax(0, 1fr) 16px; gap: 4px; height: auto; width: 100% !important; flex: 0 0 100%; }
  :deep(.toolbar .el-range-input) { grid-column: 2; width: 100%; height: 28px; text-align: left; }
  :deep(.toolbar .el-range-separator) { display: none; }
  :deep(.toolbar .el-range__icon) { grid-area: 1 / 1 / 3 / 2; margin: 0; align-self: center; }
  :deep(.toolbar .el-range__close-icon) { grid-area: 1 / 3 / 3 / 4; margin: 0; align-self: center; }
}
</style>
