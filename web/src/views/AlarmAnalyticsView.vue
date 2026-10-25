<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar wrap">
        <el-date-picker v-model="range" type="datetimerange" :start-placeholder="$t('alarmAnalytics.from')" :end-placeholder="$t('alarmAnalytics.to')" style="width: 340px" data-test="aa-range" />
        <el-button-group>
          <el-button v-for="q in quick" :key="q.hours" size="small" @click="setQuick(q.hours)">{{ q.label }}</el-button>
        </el-button-group>
        <el-select v-model="areaId" clearable :placeholder="$t('alarmAnalytics.allAreas')" style="width: 140px" data-test="aa-area">
          <el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" />
        </el-select>
        <el-select v-model="levels" multiple collapse-tags clearable :placeholder="$t('alarmAnalytics.allPriorities')" style="width: 160px" data-test="aa-levels">
          <el-option :value="0" :label="levelLabel('Info')" /><el-option :value="1" :label="levelLabel('Warning')" /><el-option :value="2" :label="levelLabel('Critical')" />
        </el-select>
        <el-select v-model="rules" multiple collapse-tags filterable clearable :placeholder="$t('alarmAnalytics.allRules')" style="width: 200px" data-test="aa-rules">
          <el-option v-for="r in ruleList" :key="r.id" :value="r.id" :label="ruleName(r.id)" />
        </el-select>
        <el-select v-model="top" style="width: 100px"><el-option v-for="n in [5, 10, 20, 50]" :key="n" :label="`Top ${n}`" :value="n" /></el-select>
        <el-button type="primary" :icon="Search" :loading="loading" data-test="aa-query" @click="load">{{ $t('alarmAnalytics.query') }}</el-button>
        <span class="spacer" />
        <el-button :disabled="!report" data-test="aa-export-xlsx" @click="exportAs('xlsx')">{{ $t('alarmAnalytics.exportXlsx') }}</el-button>
        <el-button :disabled="!report" data-test="aa-export-pdf" @click="exportAs('pdf')">{{ $t('alarmAnalytics.exportPdf') }}</el-button>
        <el-button v-if="canConfigure" data-test="aa-thresholds" @click="openThresholds">{{ $t('alarmAnalytics.thresholds') }}</el-button>
      </div>

      <el-alert v-if="report?.usedRollups" type="info" :closable="false" show-icon class="note" :title="$t('alarmAnalytics.rollupNote', [formatTime(report.detailFrom)])" />
      <el-alert v-if="report?.truncated" type="warning" :closable="false" show-icon class="note" :title="$t('alarmAnalytics.truncatedNote')" />

      <!-- 后端在无数据时仍返回全 0 的 kpi 对象，所以要按数据量判断，否则会显示一排 0 卡片 -->
      <template v-if="kpi && report && kpi.total > 0">
        <div class="cards">
          <div class="card" data-test="kpi-total"><div class="value">{{ kpi.annunciated }}</div><div class="label">{{ $t('alarmAnalytics.annunciated', [kpi.total]) }}</div></div>
          <el-tooltip :content="$t('alarmAnalytics.ratingHint', [th.acceptablePer10Min, th.manageablePer10Min])" placement="top">
            <div class="card" :class="ratingClass" data-test="kpi-rate10"><div class="value">{{ kpi.perOperator10Min }}</div><div class="label">{{ $t('alarmAnalytics.per10', [kpi.operatorPositions]) }} · {{ $t(`alarmAnalytics.rating.${kpi.rating}`) }}</div></div>
          </el-tooltip>
          <div class="card" :class="{ bad: kpi.perOperatorHour > hourLimits.manageable, warn: kpi.perOperatorHour > hourLimits.acceptable && kpi.perOperatorHour <= hourLimits.manageable }"><div class="value">{{ kpi.perOperatorHour }}</div><div class="label">{{ $t('alarmAnalytics.perHour') }}</div></div>
          <div class="card" :class="{ bad: kpi.perOperatorDay > dayLimits.manageable, warn: kpi.perOperatorDay > dayLimits.acceptable && kpi.perOperatorDay <= dayLimits.manageable }"><div class="value">{{ kpi.perOperatorDay }}</div><div class="label">{{ $t('alarmAnalytics.perDay') }}</div></div>
          <div class="card" :class="{ bad: kpi.peak10Min > th.floodStartPer10Min * kpi.operatorPositions }"><div class="value">{{ kpi.peak10Min }}</div><div class="label">{{ $t('alarmAnalytics.peak10') }}</div></div>
          <div class="card" :class="{ bad: kpi.percentTimeInFlood > 1 }" data-test="kpi-flood"><div class="value">{{ kpi.percentTimeInFlood }}%</div><div class="label">{{ $t('alarmAnalytics.floodPct', [kpi.floods.length]) }}</div></div>
          <div class="card"><div class="value">{{ sec(kpi.avgAckSec) }} / {{ sec(kpi.medianAckSec) }}</div><div class="label">{{ $t('alarmAnalytics.ackTime') }}</div></div>
          <div class="card"><div class="value">{{ sec(kpi.avgClearSec) }} / {{ sec(kpi.medianClearSec) }}</div><div class="label">{{ $t('alarmAnalytics.clearTime') }}</div></div>
          <div class="card" :class="{ warn: kpi.standing.length > 0 }"><div class="value">{{ kpi.standing.length }} / {{ kpi.stale.length }}</div><div class="label">{{ $t('alarmAnalytics.standingStale', [th.standingHours]) }}</div></div>
          <div class="card"><div class="value">{{ kpi.shelveCount }} · {{ kpi.shelvedHours }}h</div><div class="label">{{ $t('alarmAnalytics.shelved') }}</div></div>
          <div class="card"><div class="value">{{ kpi.suppressed }} · {{ kpi.suppressedHours }}h</div><div class="label">{{ $t('alarmAnalytics.suppressed') }}</div></div>
          <div class="card clickable" data-test="kpi-fleeting" @click="drill({ kind: 'fleeting' }, $t('alarmAnalytics.fleeting'))"><div class="value">{{ kpi.fleetingTotal }}</div><div class="label">{{ $t('alarmAnalytics.fleetingCard', [th.fleetingSec]) }}</div></div>
          <div class="card clickable" @click="drill({ kind: 'chattering' }, $t('alarmAnalytics.chattering'))"><div class="value">{{ kpi.chattering }}</div><div class="label">{{ $t('alarmAnalytics.chatteringCard') }}</div></div>
        </div>

        <el-row :gutter="16">
          <el-col :span="12"><h4>{{ $t('alarmAnalytics.rateChart') }}</h4><div ref="rateRef" class="chart" data-test="aa-rate-chart" /></el-col>
          <el-col :span="12"><h4>{{ $t('alarmAnalytics.trendChart') }}</h4><div ref="trendRef" class="chart" data-test="aa-trend-chart" /></el-col>
        </el-row>
        <el-row :gutter="16">
          <el-col :span="8"><h4>{{ $t('alarmAnalytics.priorityChart') }}</h4><div ref="priorityRef" class="chart" data-test="aa-priority-chart" /></el-col>
          <el-col :span="16"><h4>{{ $t('alarmAnalytics.paretoChart', [top]) }}</h4><div ref="paretoRef" class="chart" data-test="aa-pareto-chart" /></el-col>
        </el-row>
        <div class="tiny muted">{{ $t('alarmAnalytics.chartHint') }}</div>

        <el-tabs v-model="tab" class="tabs">
          <el-tab-pane :label="$t('alarmAnalytics.tabTop')" name="top">
            <el-table :data="kpi.top" size="small" :empty-text="$t('alarmAnalytics.none')" data-test="kpi-top" @row-click="(r: AlarmBadActor) => drill({ source: r.source }, r.message)">
              <el-table-column type="index" width="50" />
              <el-table-column prop="message" :label="$t('alarmAnalytics.alarm')" min-width="180" show-overflow-tooltip />
              <el-table-column :label="$t('alarmAnalytics.priority')" width="90"><template #default="{ row }"><el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag></template></el-table-column>
              <el-table-column prop="count" :label="$t('alarmAnalytics.count')" width="80" />
              <el-table-column prop="annunciated" :label="$t('alarmAnalytics.annunciatedCol')" width="90" />
              <el-table-column prop="chatterEpisodes" :label="$t('alarmAnalytics.chatterEpisodes')" width="90" />
              <el-table-column prop="fleeting" :label="$t('alarmAnalytics.fleeting')" width="80" />
              <el-table-column prop="percent" :label="$t('alarmAnalytics.percent')" width="80" />
              <el-table-column prop="cumulativePercent" :label="$t('alarmAnalytics.cumulative')" width="90" />
            </el-table>
          </el-tab-pane>
          <el-tab-pane :label="$t('alarmAnalytics.tabChattering', [kpi.chatteringSources.length])" name="chatter">
            <el-table :data="kpi.chatteringSources" size="small" :empty-text="$t('alarmAnalytics.none')" data-test="aa-chatter" @row-click="(r: AlarmChatterItem) => drill({ source: r.source }, r.message)">
              <el-table-column prop="message" :label="$t('alarmAnalytics.alarm')" min-width="180" show-overflow-tooltip />
              <el-table-column :label="$t('alarmAnalytics.priority')" width="90"><template #default="{ row }">{{ levelLabel(row.level) }}</template></el-table-column>
              <el-table-column prop="activations" :label="$t('alarmAnalytics.activations')" width="90" />
              <el-table-column prop="episodes" :label="$t('alarmAnalytics.chatterEpisodes')" width="90" />
              <el-table-column prop="maxInWindow" :label="$t('alarmAnalytics.maxInWindow', [th.chatterWindowSec])" width="140" />
            </el-table>
          </el-tab-pane>
          <el-tab-pane :label="$t('alarmAnalytics.tabFleeting', [kpi.fleeting.length])" name="fleeting">
            <el-table :data="kpi.fleeting" size="small" :empty-text="$t('alarmAnalytics.none')" data-test="aa-fleeting" @row-click="(r: AlarmFleetingItem) => drill({ source: r.source, kind: 'fleeting' }, r.message)">
              <el-table-column prop="message" :label="$t('alarmAnalytics.alarm')" min-width="180" show-overflow-tooltip />
              <el-table-column prop="count" :label="$t('alarmAnalytics.fleeting')" width="80" />
              <el-table-column prop="total" :label="$t('alarmAnalytics.count')" width="80" />
              <el-table-column :label="$t('alarmAnalytics.ratio')" width="80"><template #default="{ row }">{{ Math.round(row.ratio * 100) }}%</template></el-table-column>
              <el-table-column prop="p90DurationSec" :label="$t('alarmAnalytics.p90Duration')" width="120" />
            </el-table>
          </el-tab-pane>
          <el-tab-pane :label="$t('alarmAnalytics.tabConsequential', [kpi.consequential.length])" name="consequential">
            <el-table :data="kpi.consequential" size="small" :empty-text="$t('alarmAnalytics.none')" data-test="aa-consequential">
              <el-table-column prop="messageA" :label="$t('alarmAnalytics.alarmA')" min-width="160" show-overflow-tooltip />
              <el-table-column prop="countA" :label="$t('alarmAnalytics.count')" width="70" />
              <el-table-column prop="messageB" :label="$t('alarmAnalytics.alarmB')" min-width="160" show-overflow-tooltip />
              <el-table-column prop="countB" :label="$t('alarmAnalytics.count')" width="70" />
              <el-table-column prop="together" :label="$t('alarmAnalytics.together', [th.consequentialWindowSec])" width="130" />
              <el-table-column :label="$t('alarmAnalytics.ratio')" width="80"><template #default="{ row }">{{ Math.round(row.ratio * 100) }}%</template></el-table-column>
            </el-table>
          </el-tab-pane>
          <el-tab-pane :label="$t('alarmAnalytics.tabFloods', [kpi.floods.length])" name="floods">
            <el-table :data="kpi.floods" size="small" :empty-text="$t('alarmAnalytics.noFloods')" data-test="aa-floods" @row-click="(r: AlarmFloodPeriod) => drillFlood(r)">
              <el-table-column :label="$t('alarmAnalytics.start')" width="165"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
              <el-table-column :label="$t('alarmAnalytics.duration')" width="90"><template #default="{ row }">{{ Math.round((row.endTs - row.startTs) / 60000) }} min</template></el-table-column>
              <el-table-column prop="count" :label="$t('alarmAnalytics.count')" width="80" />
              <el-table-column prop="peak10Min" :label="$t('alarmAnalytics.peak10Short')" width="100" />
              <el-table-column :label="$t('alarmAnalytics.firstOut')" min-width="160" show-overflow-tooltip><template #default="{ row }">{{ row.firstOut?.message ?? '—' }}</template></el-table-column>
              <el-table-column :label="$t('alarmAnalytics.contributors')" min-width="220" show-overflow-tooltip><template #default="{ row }">{{ row.contributors.slice(0, 3).map((c: AlarmSourceCount) => `${c.message}×${c.count}`).join('; ') }}</template></el-table-column>
            </el-table>
          </el-tab-pane>
          <el-tab-pane :label="$t('alarmAnalytics.tabStanding', [kpi.standing.length])" name="standing">
            <el-table :data="kpi.standing" size="small" :empty-text="$t('alarmAnalytics.none')" data-test="aa-standing">
              <el-table-column prop="message" :label="$t('alarmAnalytics.alarm')" min-width="180" show-overflow-tooltip />
              <el-table-column :label="$t('alarmAnalytics.priority')" width="90"><template #default="{ row }">{{ levelLabel(row.level) }}</template></el-table-column>
              <el-table-column :label="$t('alarmAnalytics.start')" width="165"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
              <el-table-column prop="hours" :label="$t('alarmAnalytics.hours')" width="80" />
              <el-table-column :label="$t('alarmAnalytics.acked')" width="90"><template #default="{ row }">{{ row.acked ? $t('alarmAnalytics.yes') : $t('alarmAnalytics.staleTag') }}</template></el-table-column>
            </el-table>
          </el-tab-pane>
          <el-tab-pane :label="$t('alarmAnalytics.tabShelving')" name="shelving">
            <el-table :data="kpi.shelvedByRule" size="small" :empty-text="$t('alarmAnalytics.none')">
              <el-table-column :label="$t('alarmAnalytics.rule')" min-width="180"><template #default="{ row }">{{ ruleName(row.ruleId) }}</template></el-table-column>
              <el-table-column prop="count" :label="$t('alarmAnalytics.count')" width="80" />
              <el-table-column prop="hours" :label="$t('alarmAnalytics.hours')" width="80" />
            </el-table>
          </el-tab-pane>
          <el-tab-pane :label="$t('alarmAnalytics.tabSuggestions', [report.suggestions.length])" name="suggestions">
            <el-alert type="info" :closable="false" show-icon :title="$t('alarmAnalytics.suggestionsNote')" class="note" />
            <el-table :data="report.suggestions" size="small" :empty-text="$t('alarmAnalytics.none')" data-test="aa-suggestions">
              <el-table-column :label="$t('alarmAnalytics.kind')" width="110"><template #default="{ row }"><el-tag size="small" :type="row.severity >= 3 ? 'danger' : row.severity === 2 ? 'warning' : 'info'">{{ $t(`alarmAnalytics.kinds.${row.kind}`) }}</el-tag></template></el-table-column>
              <el-table-column prop="message" :label="$t('alarmAnalytics.alarm')" min-width="160" show-overflow-tooltip />
              <el-table-column :label="$t('alarmAnalytics.advice')" min-width="220"><template #default="{ row }">{{ $t(`alarmAnalytics.advices.${row.kind}`) }}</template></el-table-column>
              <el-table-column :label="$t('alarmAnalytics.evidence')" min-width="200"><template #default="{ row }">{{ kv(row.evidence) }}</template></el-table-column>
              <el-table-column :label="$t('alarmAnalytics.currentValue')" min-width="160"><template #default="{ row }">{{ kv(row.current) }}</template></el-table-column>
              <el-table-column :label="$t('alarmAnalytics.suggestedValue')" min-width="140"><template #default="{ row }"><b>{{ kv(row.suggested) }}</b></template></el-table-column>
            </el-table>
          </el-tab-pane>
        </el-tabs>
      </template>
      <!-- 查询前 / 无数据时给出提示：此前除工具栏外整页空白 -->
      <el-empty v-else :description="emptyHint" :image-size="96" data-test="aa-empty" />
    </div>

    <el-drawer v-model="drillOpen" :title="drillTitle" size="60%">
      <div class="tiny muted" data-test="aa-drill-total">{{ $t('alarmAnalytics.drillTotal', [drillTotal, drillRows.length]) }}</div>
      <el-table v-loading="drillLoading" :data="drillRows" size="small" height="calc(100vh - 140px)" data-test="aa-drill-table">
        <el-table-column :label="$t('alarmAnalytics.start')" width="165"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
        <el-table-column prop="message" :label="$t('alarmAnalytics.alarm')" min-width="180" show-overflow-tooltip />
        <el-table-column :label="$t('alarmAnalytics.priority')" width="80"><template #default="{ row }">{{ levelLabel(row.level) }}</template></el-table-column>
        <el-table-column :label="$t('alarmAnalytics.duration')" width="90"><template #default="{ row }">{{ row.endTs ? sec((row.endTs - row.startTs) / 1000) : $t('alarmAnalytics.active') }}</template></el-table-column>
        <el-table-column :label="$t('alarmAnalytics.ackBy')" width="140"><template #default="{ row }">{{ row.ackTs ? `${row.ackUser ?? ''} +${sec((row.ackTs - row.startTs) / 1000)}` : '—' }}</template></el-table-column>
        <el-table-column :label="$t('alarmAnalytics.flags')" width="140"><template #default="{ row }">{{ flags(row) }}</template></el-table-column>
      </el-table>
    </el-drawer>

    <el-dialog v-model="thOpen" :title="$t('alarmAnalytics.thresholds')" width="720px">
      <el-form v-if="thForm" label-width="260px" size="small">
        <el-form-item v-for="f in thFields" :key="f.key" :label="$t(`alarmAnalytics.th.${f.key}`)">
          <el-input-number v-model="(thForm as any)[f.key]" :min="f.min" :max="f.max" :step="f.step ?? 1" :precision="f.precision ?? 0" controls-position="right" :data-test="`th-${f.key}`" />
          <span class="tiny muted" style="margin-left: 8px">{{ $t('alarmAnalytics.defaultIs', [(thDefaults as any)?.[f.key]]) }}</span>
        </el-form-item>
      </el-form>
      <el-alert v-if="thError" type="error" :closable="false" :title="thError" />
      <template #footer>
        <el-button @click="restoreDefaults">{{ $t('alarmAnalytics.restoreDefaults') }}</el-button>
        <el-button @click="thOpen = false">{{ $t('alarmAnalytics.cancel') }}</el-button>
        <el-button type="primary" :loading="thSaving" data-test="th-save" @click="saveThresholds">{{ $t('alarmAnalytics.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Search } from '@element-plus/icons-vue'
import * as echarts from 'echarts'
import { areaApi, ruleApi } from '../api'
import { alarmAnalyticsApi, type AlarmAnalyticsFilter, type AlarmAnalyticsReport, type AlarmBadActor, type AlarmChatterItem, type AlarmDrillEvent, type AlarmFleetingItem, type AlarmFloodPeriod, type AlarmSourceCount, type AlarmThresholds, type AlarmTrendPoint } from '../api/alarmAnalytics'
import { errorMessage } from '../api/http'
import type { AlarmRule, Area } from '../types'
import { downloadBlob, formatTime, hasGlobal, levelLabel, levelType } from '../utils'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'

const auth = useAuthStore()
const canConfigure = computed(() => hasGlobal(auth.user, 'Configure'))
const H = 3600_000
const LEVELS = ['Info', 'Warning', 'Critical'] as const
const COLORS = ['#909399', '#e6a23c', '#d9534f']
const range = ref<[Date, Date] | null>([new Date(Date.now() - 24 * H), new Date()])
const quick = [{ hours: 24, label: tr('alarmAnalytics.q24h') }, { hours: 24 * 7, label: tr('alarmAnalytics.q7d') }, { hours: 24 * 30, label: tr('alarmAnalytics.q30d') }]
const areaId = ref<number | null>(null), levels = ref<number[]>([]), rules = ref<number[]>([]), top = ref(10)
const areas = ref<Area[]>([]), ruleList = ref<AlarmRule[]>([])
const report = ref<AlarmAnalyticsReport | null>(null), trend = ref<{ granularity: 'hour' | 'day'; points: AlarmTrendPoint[] } | null>(null)
const kpi = computed(() => report.value?.kpi ?? null)
const th = computed(() => report.value?.thresholds ?? ({ floodStartPer10Min: 10, acceptablePer10Min: 1, manageablePer10Min: 2, standingHours: 24, fleetingSec: 10, chatterWindowSec: 60, consequentialWindowSec: 5 } as AlarmThresholds))
const loading = ref(false), tab = ref('top')
// 区分「还没查」和「查了但没数据」，空状态文案不同
const queried = ref(false)
const emptyHint = computed(() => (queried.value ? tr('alarmAnalytics.emptyNoData') : tr('alarmAnalytics.emptyHint')))
const ratingClass = computed(() => {
  const r = kpi.value?.rating
  return r === 'acceptable' ? 'good' : r === 'manageable' ? '' : r === 'possiblyOverDemanding' ? 'warn' : 'bad'
})

// 小时/日阈值必须由可配置的 10 分钟阈值推导（×6 个十分钟 / ×144 个十分钟每天），
// 否则改了阈值配置只有「每 10 分钟」那张卡片变色，小时与日卡片仍按旧标准着色。
const hourLimits = computed(() => ({
  acceptable: th.value.acceptablePer10Min * 6,
  manageable: th.value.manageablePer10Min * 6
}))
const dayLimits = computed(() => ({
  acceptable: th.value.acceptablePer10Min * 144,
  manageable: th.value.manageablePer10Min * 144
}))
let lastFilter: AlarmAnalyticsFilter | null = null

const rateRef = ref<HTMLElement>(), trendRef = ref<HTMLElement>(), priorityRef = ref<HTMLElement>(), paretoRef = ref<HTMLElement>()
let charts: echarts.ECharts[] = []

function ruleName(id: number | null): string {
  const r = ruleList.value.find(x => x.id === id)
  return r?.message ? `#${r.id} ${r.message}` : `#${id}`
}
function sec(v: number | null | undefined): string {
  if (v == null) return '—'
  return v < 120 ? `${Math.round(v)}s` : v < 7200 ? `${Math.round(v / 60)}min` : `${(v / 3600).toFixed(1)}h`
}
function kv(d: Record<string, number>): string {
  return Object.entries(d).map(([k, v]) => `${tr(`alarmAnalytics.field.${k}`)}=${k === 'level' ? levelLabel(LEVELS[v]) : k === 'acked' ? (v ? tr('alarmAnalytics.yes') : tr('alarmAnalytics.no')) : v}`).join('; ') || '—'
}
function flags(e: AlarmDrillEvent): string {
  return [e.shelved && tr('alarmAnalytics.flagShelved'), e.suppressed && tr('alarmAnalytics.flagSuppressed'), e.chattering && tr('alarmAnalytics.flagChattering')].filter(Boolean).join(' ') || '—'
}
function setQuick(hours: number): void { range.value = [new Date(Date.now() - hours * H), new Date()]; void load() }
function filter(): AlarmAnalyticsFilter {
  const [from, to] = range.value ?? [new Date(Date.now() - 24 * H), new Date()]
  return { from: from.getTime(), to: to.getTime(), areaId: areaId.value, levels: [...levels.value], rules: [...rules.value], top: top.value }
}

async function load(): Promise<void> {
  loading.value = true
  try {
    const f = filter()
    const [r, t] = await Promise.all([alarmAnalyticsApi.analyze(f), alarmAnalyticsApi.trend(f)])
    report.value = r; trend.value = t; lastFilter = f
    await nextTick(); render()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmAnalytics.loadFailed')))
  } finally { loading.value = false; queried.value = true }
}

function chart(el: HTMLElement | undefined): echarts.ECharts | null {
  if (!el) return null
  const existing = echarts.getInstanceByDom(el)
  if (existing) { existing.off('click'); return existing }
  const c = echarts.init(el); charts.push(c); return c
}

/* eslint-disable @typescript-eslint/no-explicit-any */
function render(): void {
  const r = report.value, k = kpi.value
  if (!r || !k) return
  const positions = k.operatorPositions
  const rate = chart(rateRef.value)
  rate?.setOption({
    grid: { left: 40, right: 16, top: 24, bottom: 40 }, tooltip: { trigger: 'axis' },
    xAxis: { type: 'time' }, yAxis: { type: 'value', minInterval: 1 },
    dataZoom: [{ type: 'inside' }, { type: 'slider', height: 14, bottom: 4 }],
    series: [{
      type: 'bar', name: tr('alarmAnalytics.alarmsPer10'), data: r.rate10.map(b => [b.startTs, b.count]), barMaxWidth: 12,
      itemStyle: { color: (p: any) => p.value[1] > r.thresholds.floodStartPer10Min * positions ? '#d9534f' : p.value[1] > r.thresholds.manageablePer10Min * positions ? '#e6a23c' : '#5b8ff9' },
      markLine: { symbol: 'none', data: [
        { yAxis: r.thresholds.floodStartPer10Min * positions, label: { formatter: tr('alarmAnalytics.floodLine') }, lineStyle: { color: '#d9534f' } },
        { yAxis: r.thresholds.acceptablePer10Min * positions, label: { formatter: tr('alarmAnalytics.acceptableLine') }, lineStyle: { color: '#67c23a' } }
      ] },
      markArea: { itemStyle: { color: 'rgba(217,83,79,0.12)' }, data: k.floods.map(f => [{ xAxis: f.startTs }, { xAxis: f.endTs }]) }
    }]
  }, true)
  rate?.on('click', (p: any) => { const ts = p.value?.[0]; if (typeof ts === 'number') void drill({}, `${formatTime(ts)} +10min`, { from: ts, to: ts + 600_000 }) })

  const t = trend.value
  const tc = chart(trendRef.value)
  if (t && tc) {
    const step = t.granularity === 'hour' ? H : 24 * H
    tc.setOption({
      grid: { left: 40, right: 16, top: 30, bottom: 24 }, tooltip: { trigger: 'axis' }, legend: { top: 0 },
      xAxis: { type: 'time' }, yAxis: { type: 'value', minInterval: 1 },
      series: (['info', 'warning', 'critical'] as const).map((key, i) => ({
        type: 'bar', stack: 'p', name: levelLabel(LEVELS[i]), barMaxWidth: 18,
        itemStyle: { color: COLORS[i] }, data: t.points.map(p => [p.ts, p[key]])
      }))
    }, true)
    tc.on('click', (p: any) => { const ts = p.value?.[0]; if (typeof ts === 'number') void drill({}, formatTime(ts), { from: ts, to: ts + step }) })
  }

  const pc = chart(priorityRef.value)
  pc?.setOption({
    tooltip: { trigger: 'item' }, legend: { bottom: 0 },
    series: [
      { type: 'pie', name: tr('alarmAnalytics.actual'), radius: ['35%', '60%'], center: ['50%', '45%'], label: { formatter: '{b}\n{d}%' },
        data: k.priority.map((p, i) => ({ name: levelLabel(p.level), value: p.count, level: i, itemStyle: { color: COLORS[i] } })) },
      { type: 'pie', name: tr('alarmAnalytics.target'), radius: ['0%', '25%'], center: ['50%', '45%'], label: { show: false }, silent: true,
        data: k.priority.map((p, i) => ({ name: `${levelLabel(p.level)} ${tr('alarmAnalytics.target')}`, value: p.targetPercent, itemStyle: { color: ['#c0c4cc', '#f3d19e', '#f0a8a6'][i] } })) }
    ]
  }, true)
  pc?.on('click', (p: any) => { if (p.seriesIndex === 0) void drill({}, p.name, { levels: [p.data.level] }) })

  const pa = chart(paretoRef.value)
  pa?.setOption({
    grid: { left: 40, right: 40, top: 24, bottom: 60 }, tooltip: { trigger: 'axis' },
    xAxis: { type: 'category', data: k.top.map(x => x.message), axisLabel: { rotate: 25, width: 90, overflow: 'truncate' } },
    yAxis: [{ type: 'value', minInterval: 1 }, { type: 'value', max: 100, axisLabel: { formatter: '{value}%' } }],
    series: [
      { type: 'bar', name: tr('alarmAnalytics.count'), data: k.top.map(x => x.count), itemStyle: { color: '#5b8ff9' }, barMaxWidth: 28 },
      { type: 'line', name: tr('alarmAnalytics.cumulative'), yAxisIndex: 1, data: k.top.map(x => x.cumulativePercent), itemStyle: { color: '#e6a23c' } }
    ]
  }, true)
  pa?.on('click', (p: any) => { const item = k.top[p.dataIndex]; if (item) void drill({ source: item.source }, item.message) })
}

// 下钻：在当前筛选上叠加时段/等级/报警源/种类，打开明细抽屉。
const drillOpen = ref(false), drillTitle = ref(''), drillRows = ref<AlarmDrillEvent[]>([]), drillTotal = ref(0), drillLoading = ref(false)
async function drill(extra: { source?: string; kind?: string; ids?: number[] }, title: string, override?: Partial<AlarmAnalyticsFilter>): Promise<void> {
  const base = lastFilter ?? filter()
  drillTitle.value = title; drillOpen.value = true; drillLoading.value = true; drillRows.value = []
  try {
    const page = await alarmAnalyticsApi.events({ ...base, ...override }, extra)
    drillRows.value = page.items; drillTotal.value = page.total
  } catch (error) { ElMessage.error(errorMessage(error, tr('alarmAnalytics.loadFailed'))) }
  finally { drillLoading.value = false }
}
function drillFlood(f: AlarmFloodPeriod): void { void drill({ ids: f.eventIds }, `${tr('alarmAnalytics.flood')} ${formatTime(f.startTs)}`, { from: f.startTs, to: f.endTs }) }

async function exportAs(format: 'xlsx' | 'pdf'): Promise<void> {
  try {
    const f = lastFilter ?? filter()
    downloadBlob(await alarmAnalyticsApi.export(f, format), `alarm-analytics-${new Date(f.from).toISOString().slice(0, 10)}.${format}`)
  } catch (error) { ElMessage.error(errorMessage(error, tr('alarmAnalytics.exportFailed'))) }
}

// 阈值配置（全局配置权限）。
const thOpen = ref(false), thForm = ref<AlarmThresholds | null>(null), thDefaults = ref<AlarmThresholds | null>(null), thRevision = ref(0), thSaving = ref(false), thError = ref('')
const thFields: { key: keyof AlarmThresholds; min: number; max: number; step?: number; precision?: number }[] = [
  { key: 'operatorPositions', min: 0, max: 100 },
  { key: 'acceptablePer10Min', min: 0.1, max: 1000, step: 0.5, precision: 1 }, { key: 'manageablePer10Min', min: 0.1, max: 1000, step: 0.5, precision: 1 },
  { key: 'floodStartPer10Min', min: 1, max: 10000 }, { key: 'floodEndPer10Min', min: 1, max: 10000 },
  { key: 'chatterCount', min: 2, max: 1000 }, { key: 'chatterWindowSec', min: 1, max: 86400 }, { key: 'fleetingSec', min: 1, max: 3600 },
  { key: 'standingHours', min: 0.1, max: 8760, step: 1, precision: 1 }, { key: 'staleHours', min: 0.1, max: 8760, step: 1, precision: 1 },
  { key: 'consequentialWindowSec', min: 1, max: 3600 }, { key: 'consequentialMinCount', min: 2, max: 100000 }, { key: 'consequentialMinRatio', min: 0.05, max: 1, step: 0.05, precision: 2 },
  { key: 'frequentPerDay', min: 0.1, max: 100000, step: 1, precision: 1 },
  { key: 'targetLowPercent', min: 0, max: 100, precision: 1 }, { key: 'targetMediumPercent', min: 0, max: 100, precision: 1 }, { key: 'targetHighPercent', min: 0, max: 100, precision: 1 },
  { key: 'topN', min: 1, max: 100 }
]
function restoreDefaults(): void { if (thDefaults.value) thForm.value = { ...thDefaults.value } }
async function openThresholds(): Promise<void> {
  try {
    const r = await alarmAnalyticsApi.thresholds()
    thForm.value = { ...r.thresholds }; thDefaults.value = r.defaults; thRevision.value = r.revision; thError.value = ''; thOpen.value = true
  } catch (error) { ElMessage.error(errorMessage(error, tr('alarmAnalytics.loadFailed'))) }
}
async function saveThresholds(): Promise<void> {
  if (!thForm.value) return
  thSaving.value = true; thError.value = ''
  try {
    const r = await alarmAnalyticsApi.saveThresholds(thForm.value, thRevision.value)
    thRevision.value = r.revision; thOpen.value = false; ElMessage.success(tr('alarmAnalytics.saved')); await load()
  } catch (error) { thError.value = errorMessage(error, tr('alarmAnalytics.saveFailed')) }
  finally { thSaving.value = false }
}

function resize(): void { charts.forEach(c => c.resize()) }
onMounted(async () => {
  window.addEventListener('resize', resize)
  const [a, r] = await Promise.all([areaApi.list().catch(() => [] as Area[]), ruleApi.list().catch(() => [] as AlarmRule[])])
  areas.value = a; ruleList.value = r
  await load()
})
onBeforeUnmount(() => { window.removeEventListener('resize', resize); charts.forEach(c => c.dispose()); charts = [] })
</script>

<style scoped>
.toolbar.wrap { flex-wrap: wrap; gap: 8px; }
.note { margin: 8px 0; }
.cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(165px, 1fr)); gap: 10px; margin: 12px 0 16px; }
.card { border: 1px solid var(--el-border-color-lighter); border-radius: 6px; padding: 10px 12px; }
.card.clickable { cursor: pointer; }
.card .value { font-size: 20px; font-weight: 600; }
.card .label { font-size: 12px; color: var(--el-text-color-secondary); }
.card.bad .value { color: var(--el-color-danger); }
.card.warn .value { color: var(--el-color-warning); }
.card.good .value { color: var(--el-color-success); }
.chart { height: 260px; width: 100%; }
.tabs { margin-top: 12px; }
:deep(.el-table__row) { cursor: pointer; }
</style>
