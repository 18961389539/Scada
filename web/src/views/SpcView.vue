<template>
  <div class="page spc-page">
    <div class="panel">
      <div class="toolbar filter-row">
        <span class="hint">{{ $t('spc.subtitle') }}</span>
        <span class="spacer" />
        <el-select v-model="selectedId" :placeholder="$t('spc.selectChart')" style="width: 260px" data-test="spc-chart-select" @change="loadSeries">
          <el-option v-for="chart in charts" :key="chart.id" :label="chart.name" :value="chart.id" />
        </el-select>
        <el-select v-model="rangeHours" style="width: 130px" data-test="spc-range" @change="loadSeries">
          <el-option v-for="hours in rangeOptions" :key="hours" :label="hours >= 24 ? $t('spc.days', [hours / 24]) : $t('spc.hours', [hours])" :value="hours" />
        </el-select>
        <el-button data-test="spc-refresh" @click="loadAll">{{ $t('spc.refresh') }}</el-button>
        <el-button v-if="canConfigureAny" type="primary" data-test="spc-add" @click="openChart()">{{ $t('spc.addChart') }}</el-button>
        <el-button v-if="selectedChart && canConfigureArea(selectedChart.areaId)" data-test="spc-edit" @click="openChart(selectedChart)">{{ $t('spc.editChart') }}</el-button>
        <el-button v-if="selectedChart && canConfigureArea(selectedChart.areaId)" data-test="spc-reset" @click="resetLimits">{{ $t('spc.resetLimits') }}</el-button>
        <el-popconfirm v-if="selectedChart && canConfigureArea(selectedChart.areaId)" :title="$t('spc.confirmDelete', [selectedChart.name])" @confirm="removeChart">
          <template #reference><el-button type="danger" plain data-test="spc-delete">{{ $t('spc.delete') }}</el-button></template>
        </el-popconfirm>
      </div>
      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="spc-error" />
      <el-empty v-if="!charts.length && !loadError" :description="$t('spc.noCharts')" />

      <template v-if="selectedChart">
        <div class="spc-summary">
          <span>{{ selectedChart.tagName }}<span v-if="selectedChart.unit" class="muted"> · {{ selectedChart.unit }}</span></span>
          <span class="muted">{{ selectedChart.chartType === 'Individuals' ? $t('spc.typeIndividuals') : $t('spc.typeXbarR') }}</span>
          <span class="muted">{{ $t('spc.samplesCount', [samples.length]) }}</span>
          <el-tag v-for="rule in selectedChart.activeRules" :key="rule" type="danger" size="small">{{ ruleLabel(rule) }}</el-tag>
          <span v-if="!selectedChart.activeRules.length" class="muted">{{ $t('spc.noActiveRules') }}</span>
          <span class="spacer" />
          <template v-if="selectedChart.latestSample">
            <span class="muted">{{ $t('spc.mean') }} {{ formatQty(selectedChart.latestSample.mean) }}</span>
            <span class="muted">{{ $t('spc.rangeValue') }} {{ formatQty(selectedChart.latestSample.range) }}</span>
            <span class="muted">Cp {{ selectedChart.latestSample.cp == null ? '-' : formatQty(selectedChart.latestSample.cp) }}</span>
            <span class="muted">Cpk {{ selectedChart.latestSample.cpk == null ? '-' : formatQty(selectedChart.latestSample.cpk) }}</span>
          </template>
        </div>
        <el-alert v-if="limitsHint" :title="limitsHint" type="warning" :closable="false" show-icon data-test="spc-limits-hint" />
        <el-alert v-if="selectedChart.activeRules.length" :title="$t('spc.activeRulesHint')" type="error" :closable="false" show-icon data-test="spc-active-hint" />

        <div ref="meanChartRef" class="spc-chart" data-test="spc-mean-chart" />
        <div ref="rangeChartRef" class="spc-chart" data-test="spc-range-chart" />

        <div class="panel-subtitle">{{ $t('spc.violations') }}</div>
        <el-table :data="violations" size="small" :empty-text="$t('spc.noViolations')" data-test="spc-violations">
          <el-table-column :label="$t('spc.violationTime')" width="160">
            <template #default="{ row }">{{ formatTime(row.ts) }}</template>
          </el-table-column>
          <el-table-column :label="$t('spc.violationRule')" width="220">
            <template #default="{ row }">{{ ruleLabel(row.rule) }}</template>
          </el-table-column>
          <el-table-column prop="message" :label="$t('spc.violationMessage')" min-width="220" show-overflow-tooltip />
          <el-table-column :label="$t('spc.violationStatus')" width="100">
            <template #default="{ row }">
              <el-tag :type="row.recoveredTs ? 'info' : 'danger'" size="small">{{ row.recoveredTs ? $t('spc.recovered') : $t('spc.active') }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column :label="$t('spc.alarm')" width="100">
            <template #default="{ row }">
              <router-link v-if="row.alarmEventId" :to="`/alarms?focus=${row.alarmEventId}`" class="link">#{{ row.alarmEventId }}</router-link>
              <span v-else class="muted">-</span>
            </template>
          </el-table-column>
        </el-table>
      </template>
    </div>

    <el-dialog v-model="chartDialog" :title="chartForm.id ? $t('spc.editChart') : $t('spc.addChart')" width="760px" destroy-on-close>
      <el-alert v-if="dialogError" :title="dialogError" type="error" :closable="false" show-icon data-test="spc-save-error" />
      <el-form label-width="120px" size="small">
        <el-row :gutter="12">
          <el-col :span="12"><el-form-item :label="$t('spc.name')"><el-input v-model="chartForm.name" data-test="spc-name" /></el-form-item></el-col>
          <el-col :span="12">
            <el-form-item :label="$t('spc.area')">
              <el-select v-model="chartForm.areaId" style="width: 100%" data-test="spc-area" @change="onAreaChange">
                <el-option v-for="area in configurableAreas" :key="area.id" :label="area.name" :value="area.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('spc.tag')">
              <el-select v-model="chartForm.tagId" filterable style="width: 100%" data-test="spc-tag" :placeholder="$t('spc.tag')">
                <el-option v-for="tag in tagOptions" :key="tag.id" :label="`#${tag.id} ${tag.name}${tag.unit ? ' (' + tag.unit + ')' : ''}`" :value="tag.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('spc.chartType')">
              <el-select v-model="chartForm.chartType" style="width: 100%" @change="onChartTypeChange">
                <el-option :label="$t('spc.typeIndividuals')" value="Individuals" />
                <el-option :label="$t('spc.typeXbarR')" value="XbarR" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('spc.subgroupSize')">
              <el-input-number v-model="chartForm.subgroupSize" :min="1" :max="10" :disabled="chartForm.chartType === 'Individuals'" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('spc.samplingInterval')">
              <el-input-number v-model="chartForm.samplingIntervalSec" :min="5" :max="86400" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('spc.baselineSubgroups')">
              <el-input-number v-model="chartForm.baselineSubgroups" :min="5" :max="100" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="12"><el-form-item :label="$t('spc.autoLimits')"><el-switch v-model="chartForm.autoLimits" /></el-form-item></el-col>
          <template v-if="!chartForm.autoLimits">
            <el-col :span="8"><el-form-item :label="$t('spc.ucl')"><el-input-number v-model="chartForm.upperControlLimit" style="width: 100%" /></el-form-item></el-col>
            <el-col :span="8"><el-form-item :label="$t('spc.centerLine')"><el-input-number v-model="chartForm.centerLine" style="width: 100%" /></el-form-item></el-col>
            <el-col :span="8"><el-form-item :label="$t('spc.lcl')"><el-input-number v-model="chartForm.lowerControlLimit" style="width: 100%" /></el-form-item></el-col>
          </template>
          <el-col :span="12"><el-form-item :label="$t('spc.specUpper')"><el-input-number v-model="chartForm.specUpperLimit" style="width: 100%" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('spc.specLower')"><el-input-number v-model="chartForm.specLowerLimit" style="width: 100%" /></el-form-item></el-col>
          <el-col :span="24">
            <el-form-item :label="$t('spc.rules')">
              <el-checkbox v-for="option in ruleOptions" :key="option.rule" v-model="chartForm[option.key]">{{ ruleLabel(option.rule) }}</el-checkbox>
            </el-form-item>
          </el-col>
          <el-col :span="24">
            <el-form-item :label="$t('spc.description')"><el-input v-model="chartForm.description" type="textarea" :rows="2" /></el-form-item>
          </el-col>
        </el-row>
      </el-form>
      <template #footer>
        <el-button @click="chartDialog = false">{{ $t('spc.cancel') }}</el-button>
        <el-button type="primary" :loading="saving" data-test="spc-save" @click="saveChart">{{ $t('spc.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import * as echarts from '../charts'
import { areaApi, deviceApi, spcApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import { can } from '../access/policies'
import { tr } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { formatTime } from '../utils'
import { chartPalette, onThemeChanged } from '../theme'
import type { Area, Device, SpcChart, SpcChartInput, SpcChartType, SpcSample, SpcViolation, SpcViolationRule, Tag } from '../types'

const auth = useAuthStore()
const charts = ref<SpcChart[]>([])
const areas = ref<Area[]>([])
const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const samples = ref<SpcSample[]>([])
const violations = ref<SpcViolation[]>([])
const selectedId = ref<number>()
const rangeHours = ref(8)
const rangeOptions = [2, 8, 24, 72, 168]
const loading = ref(false), saving = ref(false)
const loadError = ref(''), dialogError = ref('')
const chartDialog = ref(false)
const meanChartRef = ref<HTMLDivElement>()
const rangeChartRef = ref<HTMLDivElement>()
let meanChart: echarts.ECharts | null = null
let rangeChart: echarts.ECharts | null = null
let timer: number | null = null

const canConfigureAny = computed(() => can(auth.user, 'Configure'))
const configurableAreas = computed(() => areas.value.filter(area => can(auth.user, 'Configure', area.id)))
const canConfigureArea = (areaId: number) => can(auth.user, 'Configure', areaId)
const selectedChart = computed(() => charts.value.find(chart => chart.id === selectedId.value) ?? null)
const limitsHint = computed(() => {
  const chart = selectedChart.value
  if (!chart || !chart.autoLimits || !chart.limitsProvisional) return ''
  return tr('spc.limitsProvisional', [samples.value.length, chart.baselineSubgroups])
})

const ruleOptions: { key: 'ruleBeyond3Sigma' | 'ruleTwoOfThreeBeyond2Sigma' | 'ruleFourOfFiveBeyond1Sigma' | 'ruleEightOnOneSide' | 'ruleRangeBeyondLimit'; rule: SpcViolationRule }[] = [
  { key: 'ruleBeyond3Sigma', rule: 'Beyond3Sigma' },
  { key: 'ruleTwoOfThreeBeyond2Sigma', rule: 'TwoOfThreeBeyond2Sigma' },
  { key: 'ruleFourOfFiveBeyond1Sigma', rule: 'FourOfFiveBeyond1Sigma' },
  { key: 'ruleEightOnOneSide', rule: 'EightOnOneSide' },
  { key: 'ruleRangeBeyondLimit', rule: 'RangeBeyondLimit' }
]
const ruleLabels: Record<SpcViolationRule, string> = {
  Beyond3Sigma: tr('spc.rule.Beyond3Sigma'),
  TwoOfThreeBeyond2Sigma: tr('spc.rule.TwoOfThreeBeyond2Sigma'),
  FourOfFiveBeyond1Sigma: tr('spc.rule.FourOfFiveBeyond1Sigma'),
  EightOnOneSide: tr('spc.rule.EightOnOneSide'),
  RangeBeyondLimit: tr('spc.rule.RangeBeyondLimit')
}
function ruleLabel(rule: SpcViolationRule): string { return ruleLabels[rule] }

const chartForm = reactive({
  id: 0,
  revision: 0,
  name: '',
  enabled: true,
  areaId: null as number | null,
  tagId: null as number | null,
  chartType: 'Individuals' as SpcChartType,
  subgroupSize: 1,
  samplingIntervalSec: 60,
  baselineSubgroups: 25,
  autoLimits: true,
  centerLine: null as number | null,
  upperControlLimit: null as number | null,
  lowerControlLimit: null as number | null,
  specUpperLimit: null as number | null,
  specLowerLimit: null as number | null,
  ruleBeyond3Sigma: true,
  ruleTwoOfThreeBeyond2Sigma: true,
  ruleFourOfFiveBeyond1Sigma: true,
  ruleEightOnOneSide: true,
  ruleRangeBeyondLimit: true,
  description: ''
})

function tagAreaId(tag: Tag): number | null {
  if (tag.areaId != null) return tag.areaId
  if (tag.deviceId != null) return devices.value.find(device => device.id === tag.deviceId)?.areaId ?? null
  return null
}
const tagOptions = computed(() => tags.value.filter(tag =>
  tag.dataType !== 'Bool' && tag.dataType !== 'String' && (!chartForm.areaId || tagAreaId(tag) === chartForm.areaId)))
function formatQty(value: number | null | undefined): string {
  if (value == null || !Number.isFinite(value)) return '-'
  return value.toLocaleString(undefined, { maximumFractionDigits: 4 })
}

async function loadCatalogs() {
  try {
    const [areaList, deviceList, tagList] = await Promise.all([areaApi.list(), deviceApi.list(), tagApi.list()])
    areas.value = areaList
    devices.value = deviceList
    tags.value = tagList
  } catch (e) {
    loadError.value = errorMessage(e, tr('spc.loadFailed'))
  }
}
async function loadCharts() {
  try {
    charts.value = await spcApi.charts()
    if (!selectedId.value || !charts.value.some(chart => chart.id === selectedId.value)) {
      selectedId.value = charts.value[0]?.id
    }
    loadError.value = ''
  } catch (e) {
    loadError.value = errorMessage(e, tr('spc.loadFailed'))
  }
}
async function loadAll() {
  await loadCharts()
  await loadSeries()
}
let seriesSeq = 0
async function loadSeries() {
  const seq = ++seriesSeq
  const chart = selectedChart.value
  if (!chart) {
    samples.value = []
    violations.value = []
    renderCharts()
    return
  }
  loading.value = true
  try {
    const to = Date.now()
    const from = to - rangeHours.value * 3600_000
    const [sampleRows, violationRows, chartView] = await Promise.all([
      spcApi.samples(chart.id, { from, to, take: 2000 }),
      spcApi.violations(chart.id, { from, to, take: 200 }),
      spcApi.chart(chart.id)
    ])
    if (seq !== seriesSeq || chart.id !== selectedId.value) return
    samples.value = sampleRows
    violations.value = violationRows
    const index = charts.value.findIndex(item => item.id === chart.id)
    if (index >= 0) charts.value[index] = chartView
    loadError.value = ''
    await nextTick()
    if (seq !== seriesSeq || chart.id !== selectedId.value) return
    renderCharts()
  } catch (e) {
    if (seq !== seriesSeq || chart.id !== selectedId.value) return
    loadError.value = errorMessage(e, tr('spc.loadFailed'))
  } finally {
    if (seq === seriesSeq) loading.value = false
  }
}

function chartLimits() {
  const chart = selectedChart.value
  const latest = chart?.latestSample
  return {
    center: latest?.centerLine ?? chart?.centerLine ?? null,
    ucl: latest?.upperControlLimit ?? chart?.upperControlLimit ?? null,
    lcl: latest?.lowerControlLimit ?? chart?.lowerControlLimit ?? null,
    rangeCenter: latest?.rangeCenter ?? chart?.rangeCenter ?? null,
    rangeUcl: latest?.rangeUpperControlLimit ?? chart?.rangeUpperControlLimit ?? null,
    rangeLcl: latest?.rangeLowerControlLimit ?? chart?.rangeLowerControlLimit ?? null
  }
}
function renderCharts() {
  if (!meanChartRef.value || !rangeChartRef.value) return
  if (!meanChart) meanChart = echarts.init(meanChartRef.value)
  if (!rangeChart) rangeChart = echarts.init(rangeChartRef.value)
  const p = chartPalette()
  const chart = selectedChart.value
  const limits = chartLimits()
  const meanData = samples.value.map(sample => [sample.ts, sample.mean])
  const rangeData = samples.value.map(sample => [sample.ts, sample.range])
  const violatingMeans = samples.value.filter(sample => sample.ruleCodes.length > 0).map(sample => ({ coord: [sample.ts, sample.mean], value: tr('spc.violationMark') }))
  const violatingRanges = samples.value.filter(sample => sample.ruleCodes.includes('RangeBeyondLimit')).map(sample => ({ coord: [sample.ts, sample.range], value: tr('spc.violationMark') }))
  const markLines = (center: number | null, ucl: number | null, lcl: number | null) => ({
    silent: true, symbol: 'none', animation: false,
    data: [
      ...(ucl == null ? [] : [{ yAxis: ucl, name: 'UCL', lineStyle: { color: '#e03131', type: 'dashed' as const } }]),
      ...(center == null ? [] : [{ yAxis: center, name: 'CL', lineStyle: { color: '#2f9e44', type: 'dashed' as const } }]),
      ...(lcl == null ? [] : [{ yAxis: lcl, name: 'LCL', lineStyle: { color: '#e03131', type: 'dashed' as const } }])
    ],
    label: { formatter: '{b}', position: 'end' as const, fontSize: 10, color: p.text, backgroundColor: p.annotationBg, padding: [2, 4], borderRadius: 3 }
  })
  const axisStyle = { axisLabel: { color: p.muted }, splitLine: { lineStyle: { color: p.split } }, nameTextStyle: { color: p.muted } }
  const base = {
    textStyle: { color: p.text },
    tooltip: { trigger: 'axis' as const, backgroundColor: p.annotationBg, textStyle: { color: p.annotationText } },
    grid: { left: 58, right: 18, top: 28, bottom: 48 },
    xAxis: { type: 'time' as const, ...axisStyle },
    dataZoom: [{ type: 'inside' as const }, { type: 'slider' as const, height: 16, bottom: 8, ...p.zoom }],
    animation: false
  }
  meanChart.setOption({
    ...base,
    title: { text: tr('spc.meanChart'), left: 10, top: 2, textStyle: { fontSize: 13, fontWeight: 'normal', color: p.text } },
    yAxis: { type: 'value' as const, name: chart?.unit ?? '', scale: true, ...axisStyle },
    series: [{
      name: tr('spc.mean'), type: 'line' as const, showSymbol: true, symbolSize: 5, data: meanData,
      lineStyle: { width: 1.5 }, markLine: markLines(limits.center, limits.ucl, limits.lcl),
      markPoint: { symbolSize: 34, data: violatingMeans, itemStyle: { color: '#e03131' }, label: { fontSize: 9, color: '#fff' } }
    }]
  }, true)
  rangeChart.setOption({
    ...base,
    title: { text: tr('spc.rangeChart'), left: 10, top: 2, textStyle: { fontSize: 13, fontWeight: 'normal', color: p.text } },
    yAxis: { type: 'value' as const, name: chart?.unit ?? '', scale: true, ...axisStyle },
    series: [{
      name: tr('spc.rangeValue'), type: 'line' as const, showSymbol: true, symbolSize: 5, data: rangeData,
      lineStyle: { width: 1.5 }, markLine: markLines(limits.rangeCenter, limits.rangeUcl, limits.rangeLcl),
      markPoint: { symbolSize: 34, data: violatingRanges, itemStyle: { color: '#e03131' }, label: { fontSize: 9, color: '#fff' } }
    }]
  }, true)
}

function onAreaChange() {
  const tag = tags.value.find(item => item.id === chartForm.tagId)
  if (tag && tagAreaId(tag) !== chartForm.areaId) chartForm.tagId = null
}
function onChartTypeChange() {
  if (chartForm.chartType === 'Individuals') chartForm.subgroupSize = 1
  else if (chartForm.subgroupSize < 2) chartForm.subgroupSize = 5
}
function openChart(chart?: SpcChart) {
  chartForm.id = chart?.id ?? 0
  chartForm.revision = chart?.revision ?? 0
  chartForm.name = chart?.name ?? ''
  chartForm.enabled = chart?.enabled ?? true
  chartForm.areaId = chart?.areaId ?? configurableAreas.value[0]?.id ?? null
  chartForm.tagId = chart?.tagId ?? null
  chartForm.chartType = chart?.chartType ?? 'Individuals'
  chartForm.subgroupSize = chart?.subgroupSize ?? 1
  chartForm.samplingIntervalSec = chart?.samplingIntervalSec ?? 60
  chartForm.baselineSubgroups = chart?.baselineSubgroups ?? 25
  chartForm.autoLimits = chart?.autoLimits ?? true
  chartForm.centerLine = chart?.centerLine ?? null
  chartForm.upperControlLimit = chart?.upperControlLimit ?? null
  chartForm.lowerControlLimit = chart?.lowerControlLimit ?? null
  chartForm.specUpperLimit = chart?.specUpperLimit ?? null
  chartForm.specLowerLimit = chart?.specLowerLimit ?? null
  chartForm.ruleBeyond3Sigma = chart?.ruleBeyond3Sigma ?? true
  chartForm.ruleTwoOfThreeBeyond2Sigma = chart?.ruleTwoOfThreeBeyond2Sigma ?? true
  chartForm.ruleFourOfFiveBeyond1Sigma = chart?.ruleFourOfFiveBeyond1Sigma ?? true
  chartForm.ruleEightOnOneSide = chart?.ruleEightOnOneSide ?? true
  chartForm.ruleRangeBeyondLimit = chart?.ruleRangeBeyondLimit ?? true
  chartForm.description = chart?.description ?? ''
  dialogError.value = ''
  chartDialog.value = true
}
async function saveChart() {
  if (chartForm.areaId == null || chartForm.tagId == null) {
    dialogError.value = tr('spc.required')
    return
  }
  saving.value = true
  dialogError.value = ''
  try {
    const input: SpcChartInput = {
      name: chartForm.name.trim(), enabled: chartForm.enabled, areaId: chartForm.areaId, tagId: chartForm.tagId,
      chartType: chartForm.chartType, subgroupSize: chartForm.subgroupSize,
      samplingIntervalSec: chartForm.samplingIntervalSec, baselineSubgroups: chartForm.baselineSubgroups,
      autoLimits: chartForm.autoLimits, centerLine: chartForm.autoLimits ? null : chartForm.centerLine,
      upperControlLimit: chartForm.autoLimits ? null : chartForm.upperControlLimit,
      lowerControlLimit: chartForm.autoLimits ? null : chartForm.lowerControlLimit,
      specUpperLimit: chartForm.specUpperLimit, specLowerLimit: chartForm.specLowerLimit,
      ruleBeyond3Sigma: chartForm.ruleBeyond3Sigma, ruleTwoOfThreeBeyond2Sigma: chartForm.ruleTwoOfThreeBeyond2Sigma,
      ruleFourOfFiveBeyond1Sigma: chartForm.ruleFourOfFiveBeyond1Sigma,
      ruleEightOnOneSide: chartForm.ruleEightOnOneSide, ruleRangeBeyondLimit: chartForm.ruleRangeBeyondLimit,
      description: chartForm.description.trim(), revision: chartForm.revision
    }
    const saved = chartForm.id ? await spcApi.update(chartForm.id, input) : await spcApi.create(input)
    chartDialog.value = false
    selectedId.value = saved.id
    await loadCharts()
    await loadSeries()
    ElMessage.success(tr('spc.saved'))
  } catch (e) {
    dialogError.value = errorMessage(e, tr('spc.saveFailed'))
  } finally {
    saving.value = false
  }
}
async function resetLimits() {
  const chart = selectedChart.value
  if (!chart) return
  try {
    await ElMessageBox.confirm(tr('spc.confirmReset', [chart.name]), tr('spc.resetLimits'), { type: 'warning' })
  } catch { return }
  try {
    const updated = await spcApi.resetLimits(chart.id)
    selectedId.value = updated.id
    await loadCharts()
    await loadSeries()
    ElMessage.success(tr('spc.saved'))
  } catch (e) {
    loadError.value = errorMessage(e, tr('spc.actionFailed'))
  }
}
async function removeChart() {
  const chart = selectedChart.value
  if (!chart) return
  try {
    await spcApi.remove(chart.id)
    selectedId.value = undefined
    await loadCharts()
    await loadSeries()
    ElMessage.success(tr('spc.deleted'))
  } catch (e) {
    loadError.value = errorMessage(e, tr('spc.deleteFailed'))
  }
}

watch(selectedId, () => { void loadSeries() })

function resize() {
  meanChart?.resize()
  rangeChart?.resize()
}
let disposeTheme: (() => void) | undefined
onMounted(async () => {
  await loadCatalogs()
  await loadAll()
  window.addEventListener('resize', resize)
  disposeTheme = onThemeChanged(() => renderCharts())
  timer = window.setInterval(() => { if (!chartDialog.value) void loadSeries() }, 30000)
})
onUnmounted(() => {
  disposeTheme?.()
  if (timer != null) window.clearInterval(timer)
  window.removeEventListener('resize', resize)
  meanChart?.dispose()
  rangeChart?.dispose()
  meanChart = null
  rangeChart = null
})
</script>

<style scoped>
.filter-row { flex-wrap: wrap; }
.spc-summary { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin: 4px 0 10px; }
.panel-subtitle { margin: 14px 0 6px; font-weight: 600; }
.spc-chart { width: 100%; height: 300px; margin-top: 6px; }
.link { color: var(--el-color-primary); text-decoration: none; }
</style>
