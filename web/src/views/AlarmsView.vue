<template>
  <div class="page alarm-page">
    <el-alert
      v-if="floodAlert"
      :type="floodAlert.type"
      show-icon
      :closable="false"
      class="flood-banner"
      data-test="flood-banner"
      :title="floodAlert.title"
      :description="floodAlert.detail"
    />
    <div class="panel alarm-panel">
      <el-tabs v-model="tab" @tab-change="onTabChange">
        <el-tab-pane :label="$t('alarmsView.kc97e08')" name="live" />
        <el-tab-pane :label="$t('alarmsView.k894f96', [shelves.length])" name="shelved" />
        <el-tab-pane :label="$t('alarmsView.k8e0241')" name="history" />
      </el-tabs>
      <div class="toolbar">
        <template v-if="tab === 'live'">
          <el-check-tag :checked="stateFilter === 'active'" type="primary" data-test="filter-active" @change="stateFilter = 'active'">{{ $t('alarmsView.filterActive') }}</el-check-tag>
          <el-check-tag :checked="stateFilter === 'unack'" type="danger" data-test="filter-unack" @change="stateFilter = 'unack'">{{ $t('alarmsView.k3af88d', [summary?.unack ?? 0]) }}</el-check-tag>
          <el-check-tag :checked="stateFilter === 'rtnUnack'" type="warning" data-test="filter-rtn" @change="stateFilter = 'rtnUnack'">{{ $t('alarmsView.k109812', [summary?.rtnUnack ?? 0]) }}</el-check-tag>
          <el-check-tag :checked="stateFilter === 'acked'" type="info" data-test="filter-acked" @change="stateFilter = 'acked'">{{ $t('alarmsView.kdcfa5c', [summary?.acked ?? 0]) }}</el-check-tag>
          <el-check-tag :checked="stateFilter === 'shelved'" data-test="filter-shelved" @change="stateFilter = 'shelved'">{{ $t('alarmsView.kef6a50', [summary?.shelved ?? 0]) }}</el-check-tag>
          <el-check-tag :checked="stateFilter === 'suppressed'" data-test="filter-suppressed" @change="stateFilter = 'suppressed'">{{ $t('alarmsView.k80a67e', [summary?.suppressed ?? 0]) }}</el-check-tag>
          <el-tag effect="plain" type="info">{{ $t('alarmsView.k0e262f', [summary?.outOfServiceRules ?? 0]) }}</el-tag>
          <el-select v-model="levelFilter" size="small" clearable :placeholder="$t('alarmsView.allLevels')" style="width: 110px" data-test="level-filter">
            <el-option v-for="l in levelOptions" :key="l" :label="levelLabel(l)" :value="l" />
          </el-select>
          <el-input v-model="keyword" size="small" clearable :placeholder="$t('alarmsView.kd812c3')" style="width: 160px" data-test="alarm-keyword" />
        </template>
        <template v-else-if="tab === 'history'">
          <el-select v-model="rangePreset" size="small" clearable :placeholder="$t('alarmsView.range24h')" style="width: 130px" data-test="range-preset" @change="applyRangePreset">
            <el-option :label="$t('alarmsView.range1h')" :value="1" />
            <el-option :label="$t('alarmsView.range8h')" :value="8" />
            <el-option :label="$t('alarmsView.range24h')" :value="24" />
            <el-option :label="$t('alarmsView.range7d')" :value="168" />
          </el-select>
          <el-date-picker v-model="historyRange" type="datetimerange" :start-placeholder="$t('alarmsView.ke8868a')" :end-placeholder="$t('alarmsView.ka0bb9f')" style="width: 360px" />
          <el-button :icon="Search" @click="loadHistory">{{ $t('alarmsView.k711363') }}</el-button>
          <el-input v-model="keyword" clearable :placeholder="$t('alarmsView.kd812c3')" style="width: 200px" data-test="alarm-keyword" />
        </template>
        <span class="spacer" />
        <span v-if="batchProgress" class="tiny muted" data-test="batch-progress">{{ batchProgress }}</span>
        <el-button v-if="tab === 'live' && canAck" size="small" type="primary" data-test="batch-ack" :disabled="!selected.length" :loading="batchRunning" @click="batchAck">
          {{ $t('alarmsView.batchAck') }}<template v-if="selected.length"> ({{ selected.length }})</template>
        </el-button>
        <el-button v-if="tab === 'live' && canAck" size="small" data-test="ack-visible" @click="ackVisible">{{ $t('alarmsView.ackVisible') }}</el-button>
        <el-button v-if="tab === 'live' && canAck" size="small" type="warning" data-test="silence-ann" @click="silenceAnnunciators">{{ $t('alarmsView.silenceAnn') }}</el-button>
        <el-button :icon="Refresh" size="small" @click="reload">{{ $t('alarmsView.k38108e') }}</el-button>
      </div>

      <!-- 服务端按 limit 裁剪，超出部分会被静默丢弃；不说明的话用户会以为看到的就是全部 -->
      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="alarms-load-error" />
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="alarms-action-error" />

      <el-alert
        v-if="truncated && tab !== 'shelved'"
        :title="$t('alarmsView.truncated', [PAGE_LIMIT])"
        type="warning"
        :closable="false"
        show-icon
        style="margin-bottom: 8px"
        data-test="alarms-truncated"
      />

      <el-table v-if="tab === 'shelved'" :data="shelves" size="small" :empty-text="loadError && !shelves.length ? loadError : $t('alarmsView.k0d51d2')">
        <el-table-column :label="$t('alarmsView.ked904c')" width="90"><template #default="{ row }">#{{ row.ruleId }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.k8cff1c')" width="90"><template #default="{ row }">#{{ row.tagId }}</template></el-table-column>
        <el-table-column prop="reason" :label="$t('alarmsView.k1ff9c3')" min-width="200" />
        <el-table-column prop="user" :label="$t('alarmsView.k06858d')" width="100" />
        <el-table-column :label="$t('alarmsView.k79cc87')" width="170"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.k8066e0')" width="170"><template #default="{ row }">{{ formatTime(row.untilTs) }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.kf3ea6d')" width="100">
          <template #default="{ row }">
            <el-button v-if="canAckAlarm(row)" link type="primary" @click="unshelve(row.ruleId)">{{ $t('alarmsView.kcd308e') }}</el-button>
          </template>
        </el-table-column>
      </el-table>

      <div v-else class="alarm-table-wrap">
      <el-table
        ref="tableRef"
        v-loading="loading"
        :data="filteredAlarms"
        row-key="id"
        size="small"
        height="100%"
        :row-class-name="rowClass"
        :empty-text="loadError && !filteredAlarms.length ? loadError : $t('alarmsView.k3d17a9')"
        @selection-change="onSelectionChange"
      >
        <el-table-column v-if="tab === 'live' && canAck" type="selection" width="42" :reserve-selection="true" :selectable="canAckAlarm" />
        <el-table-column :label="`${$t('alarmsView.k62e951')} / ${$t('alarmsView.k254849')}`" width="150">
          <template #default="{ row }">
            <div class="state-cell">
              <el-tag :type="alarmStateType(row.state)" size="small" data-test="alarm-state">{{ alarmStateLabels[row.state ?? 'Normal'] }}</el-tag>
              <el-tag :type="levelType(row.level)" size="small" effect="plain">{{ levelLabel(row.level) }}</el-tag>
            </div>
            <div class="flags">
              <el-tooltip v-if="row.firstOut" :content="$t('alarmsView.firstOutHint')" placement="top">
                <el-tag size="small" effect="dark" type="danger" data-test="first-out">{{ $t('alarmsView.firstOut') }}</el-tag>
              </el-tooltip>
              <el-tag v-if="row.shelved" size="small" effect="plain">{{ $t('alarmsView.k06d561') }}</el-tag>
              <el-tooltip v-if="row.suppressed" :content="row.suppressReason || $t('alarmsView.kb37a4e')"><el-tag size="small" effect="plain" type="info">{{ $t('alarmsView.kaa2e2b') }}</el-tag></el-tooltip>
              <el-tag v-if="row.chattering" size="small" effect="plain" type="warning">{{ $t('alarmsView.ka60f92') }}</el-tag>
            </div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.kf3dd64')" min-width="200" show-overflow-tooltip>
          <template #default="{ row }">
            <el-tag v-if="row.gatewayId" size="small" effect="plain" class="gateway-tag">{{ $t('alarmsView.k38b4cc', [row.gatewayId]) }}</el-tag>
            <span>{{ row.message }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.k521523')" width="90"><template #default="{ row }">{{ formatValue(row.value) }}</template></el-table-column>
        <el-table-column :label="`${$t('alarmsView.k79cc87')} / ${$t('alarmsView.k25722a')}`" width="165">
          <template #default="{ row }">
            <span>{{ formatTime(row.startTs) }}</span>
            <div class="tiny muted">{{ duration(row) }}</div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.ke93ea4')" width="175">
          <template #default="{ row }">
            {{ row.endTs ? formatTime(row.endTs) : $t('alarmsView.k559948') }}
            <div v-if="row.endReason" class="tiny muted" style="white-space: normal">{{ row.endReason }}</div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.kb56d9a')" width="115">
          <template #default="{ row }">
            <span v-if="row.ackTs" class="tiny">{{ row.ackUser }}<br />{{ formatTime(row.ackTs) }}</span>
            <span v-else class="muted tiny">{{ $t('alarmsView.k0642fa') }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.kf3ea6d')" width="190" fixed="right">
          <template #default="{ row }">
            <el-button v-if="!row.ackTs && canAckAlarm(row)" link type="primary" data-test="ack" @click="openAck(row)">{{ $t('alarmsView.kb56d9a') }}</el-button>
            <el-button v-if="canAckAlarm(row)" link @click="openNote(row)">{{ $t('alarmsView.ke03614') }}</el-button>
            <el-button v-if="canAckAlarm(row) && row.ruleId && !row.endTs && !row.shelved" link type="warning" @click="openShelve(row)">{{ $t('alarmsView.k06d561') }}</el-button>
            <el-button v-if="canAckAlarm(row) && row.ruleId && row.shelved" link type="warning" @click="unshelve(row.ruleId)">{{ $t('alarmsView.kcd308e') }}</el-button>
            <el-button link data-test="timeline" @click="openTimeline(row)">{{ $t('alarmsView.k9058a1') }}</el-button>
          </template>
        </el-table-column>
      </el-table>
      </div>
    </div>

    <el-dialog v-model="ackDialog.visible" :title="ackDialog.mode === 'ack' ? $t('alarmsView.kff8fce') : $t('alarmsView.kb82157')" width="460px">
      <el-alert v-if="ackError" :title="ackError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="ack-error" />
      <div class="tiny muted" style="margin-bottom: 8px">{{ ackDialog.alarm?.message }}</div>
      <el-input v-model="ackDialog.note" type="textarea" :rows="3" maxlength="1000" show-word-limit :placeholder="ackDialog.mode === 'ack' ? $t('alarmsView.k7eb18b') : $t('alarmsView.k11abee')" data-test="note-input" />
      <template #footer>
        <el-button @click="ackDialog.visible = false">{{ $t('alarmsView.k4d0b46') }}</el-button>
        <el-button type="primary" :loading="ackDialog.saving" data-test="note-submit" @click="submitAck">{{ ackDialog.mode === 'ack' ? $t('alarmsView.kb56d9a') : $t('alarmsView.kfadf24') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="shelveDialog.visible" :title="$t('alarmsView.ke90a39')" width="460px">
      <el-form label-width="80px" @submit.prevent>
        <el-form-item :label="$t('alarmsView.k9f45b3')">{{ shelveDialog.alarm?.message }}</el-form-item>
        <el-form-item :label="$t('alarmsView.k29d055')">
          <el-select v-model="shelveDialog.minutes" style="width: 160px">
            <el-option v-for="m in [15, 30, 60, 120, 240, 480]" :key="m" :label="m >= 60 ? $t('alarmsView.k5247a4', [m / 60]) : $t('alarmsView.kc16d73', [m])" :value="m" />
          </el-select>
          <span class="tiny muted" style="margin-left: 8px">{{ $t('alarmsView.shelveHint') }}</span>
        </el-form-item>
        <el-form-item :label="$t('alarmsView.k1ff9c3')" required :error="shelveError">
          <el-input v-model="shelveDialog.reason" type="textarea" :rows="2" maxlength="500" :placeholder="$t('alarmsView.k7fc9ae')" data-test="shelve-reason" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="shelveDialog.visible = false">{{ $t('alarmsView.k4d0b46') }}</el-button>
        <el-button type="warning" :disabled="!shelveDialog.reason.trim()" :loading="shelveDialog.saving" data-test="shelve-submit" @click="submitShelve">{{ $t('alarmsView.k06d561') }}</el-button>
      </template>
    </el-dialog>

    <el-drawer v-model="timelineVisible" :title="$t('alarmsView.kb2da68')" size="440px">
      <template v-if="timeline">
        <div class="tiny" style="margin-bottom: 12px">
          <el-tag :type="alarmStateType(timeline.alarm.state)" size="small">{{ alarmStateLabels[timeline.alarm.state ?? 'Normal'] }}</el-tag>
          {{ timeline.alarm.message }}
        </div>
        <el-timeline>
          <el-timeline-item :timestamp="formatTime(timeline.alarm.startTs)" type="danger">{{ $t('alarmsView.k476f9e', [formatValue(timeline.alarm.value)]) }}</el-timeline-item>
          <el-timeline-item v-for="item in timeline.activities" :key="item.id" :timestamp="formatTime(item.ts)" data-test="timeline-item">
            <b>{{ alarmActionLabels[item.action] ?? item.action }}</b>
            <span v-if="item.user" class="muted"> · {{ item.user }}</span>
            <div v-if="item.note" class="tiny">{{ item.note }}</div>
          </el-timeline-item>
        </el-timeline>
        <div v-if="timeline.notifications.length" class="tiny muted">{{ $t('alarmsView.k9768ca', [timeline.notifications.length, timeline.notifications.filter((x) => !x.success).length]) }}</div>
      </template>
    </el-drawer>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type TableInstance } from 'element-plus'
import { Refresh, Search } from '@element-plus/icons-vue'
import { annunciatorApi } from '../api/annunciators'
import { alarmApi, deviceApi, ruleApi } from '../api'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { AlarmEvent, AlarmShelve, AlarmSummary, AlarmTimeline } from '../types'
import { alarmActionLabels, alarmStateLabels, alarmStateType, canAckAlarmEvent, hasAny, hasInArea, formatTime, formatValue, levelLabel, levelType } from '../utils'
import { runBatch, summarizeBatch } from '../composables/batchRunner'
import { serverFormErrors } from '../composables/formErrors'
import { usePolling } from '../composables/usePolling'
import { onHubEvent } from '../realtime/hub'
import { tr } from '../i18n'

const auth = useAuthStore()
const canAck = computed(() => hasAny(auth.user, 'AckAlarm'))
const deviceAreas = ref<Map<number, number>>(new Map())
function canAckAlarm(row: AlarmEvent): boolean {
  return canAckAlarmEvent(auth.user, row, deviceAreas.value)
}
function canSilencePanel(areaId: number | null): boolean {
  if (!canAck.value) return false
  return areaId == null || hasInArea(auth.user, 'AckAlarm', areaId)
}
const tab = ref<'live' | 'shelved' | 'history'>('live')
const alarms = ref<AlarmEvent[]>([])
// 服务端按 limit 裁剪结果；多取 1 条用来判断「是否还有更多」，避免把截断当成全部
const PAGE_LIMIT = 1000
const truncated = ref(false)
const shelves = ref<AlarmShelve[]>([])
const summary = ref<AlarmSummary | null>(null)
const loading = ref(false)
const loadError = ref('')
const actionError = ref('')
const ackError = ref('')
const keyword = ref('')
// 默认只看「全部活动」（未恢复、未暂挂/抑制）。已恢复未确认的存量最多，
// 全量默认展示会把首屏刷成几千行；需要时点对应状态筛选即可。
const stateFilter = ref<'active' | 'unack' | 'rtnUnack' | 'acked' | 'shelved' | 'suppressed'>('active')
const historyRange = ref<[Date, Date] | null>([new Date(Date.now() - 24 * 3600_000), new Date()])
const ackDialog = reactive({ visible: false, mode: 'ack' as 'ack' | 'note', alarm: null as AlarmEvent | null, note: '', saving: false })
const shelveDialog = reactive({ visible: false, alarm: null as AlarmEvent | null, minutes: 60, reason: '', saving: false })
const timeline = ref<AlarmTimeline | null>(null)
const timelineVisible = ref(false)
const shelveError = ref('')
// 批量确认：多选行 + 进度状态；等级与关键词过滤同时服务实时页和历史页
const selected = ref<AlarmEvent[]>([])
const batchRunning = ref(false)
const batchProgress = ref('')
const tableRef = ref<TableInstance>()
const levelOptions = ['Critical', 'Warning', 'Info'] as const
const levelFilter = ref('')
const rangePreset = ref<number | ''>('')

let pushTimer: number | undefined
let disposeAlarmHandler: (() => void) | undefined

// 报警声音统一由 AppLayout 经 handleAlarmPush 播放（走 alarmAudio 的静音与过滤）。
// 这里曾有一份自建 AudioContext 的发声实现：它绕过了静音开关，并且与 AppLayout 的订阅
// 重复消费同一条 alarm 事件，导致静音失效、报警页上双重发声。请勿再加回来。

// 运行期洪水治理（按区域、带滞回与自动抑制）优先展示；没有运行期洪水时退回按历史速率的检测。
const floodAlert = computed(() => {
  const guard = summary.value?.floodGuard
  const areas = (guard?.areas ?? []).filter((x) => x.inFlood)
  if (guard && areas.length) {
    const detail = [
      guard.autoSuppress
        ? tr('alarmsView.floodGuardSuppress', [areas.reduce((sum, x) => sum + x.suppressedCount, 0), levelLabel(guard.suppressMaxLevel)])
        : tr('alarmsView.floodGuardNoSuppress'),
      tr('alarmsView.floodGuardExit', [Math.min(...areas.map((x) => x.exitThreshold))])
    ]
    const firstOut = areas.find((x) => x.firstOutEventId !== null)?.firstOutEventId
    if (guard.firstOut && firstOut) detail.push(tr('alarmsView.floodGuardFirstOut', [firstOut]))
    return {
      type: 'error' as const,
      title: tr('alarmsView.floodGuardTitle', [areas.map((x) => tr('alarmsView.floodAreaStat', [x.areaName ?? tr('alarmsView.floodAreaUnnamed'), x.count, x.threshold])).join('；')]),
      detail: detail.join(' · ')
    }
  }
  const flood = summary.value?.flood
  if (flood?.inFlood) return { type: 'warning' as const, title: tr('alarmsView.kff8eb2', [flood.count, flood.threshold]), detail: '' }
  return null
})

const filteredAlarms = computed(() => {
  let list = alarms.value
  if (tab.value === 'live') {
    const byState: Record<typeof stateFilter.value, (a: AlarmEvent) => boolean> = {
      active: (a) => !a.endTs && !a.shelved && !a.suppressed,
      unack: (a) => !a.ackTs && !a.endTs && !a.shelved && !a.suppressed,
      rtnUnack: (a) => !a.ackTs && !!a.endTs,
      acked: (a) => !!a.ackTs,
      shelved: (a) => !!a.shelved,
      suppressed: (a) => !!a.suppressed
    }
    list = list.filter(byState[stateFilter.value])
    if (levelFilter.value) list = list.filter((x) => x.level === levelFilter.value)
  }
  // 关键词同时服务实时与历史：现场排查通常先按内容收敛
  const kw = keyword.value.trim()
  if (kw) list = list.filter((x) => x.message.includes(kw))
  return list
})

function onSelectionChange(rows: AlarmEvent[]): void {
  selected.value = rows
}

// 只确认未确认的选中项：已确认的跳过，避免整批因个别 400 失败。
// 用最新列表回填，防止选中后列表刷新留下过期对象。
async function batchAck(): Promise<void> {
  const picked = new Set(selected.value.map((x) => x.id))
  const rows = alarms.value.filter((x) => picked.has(x.id) && !x.ackTs)
  if (!rows.length) return
  try {
    await ElMessageBox.confirm(
      tr('alarmsView.batchAckConfirm', [rows.length]),
      tr('alarmsView.confirmTitle'),
      { type: 'warning', confirmButtonText: tr('alarmsView.batchAck'), cancelButtonText: tr('alarmsView.k4d0b46') }
    )
  } catch {
    return
  }
  batchRunning.value = true
  try {
    const report = await runBatch(
      rows,
      (a) => `#${a.id} ${a.message}`,
      async (a) => { await alarmApi.ack(a.id, '') },
      (p) => { batchProgress.value = tr('alarmsView.batchAckProgress', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : '']) }
    )
    if (report.failed.length === 0) ElMessage.success(tr('alarmsView.batchAckOk', [report.ok, report.total]))
    else await ElMessageBox.alert(summarizeBatch(report, ''), tr('alarmsView.confirmTitle'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('alarmsView.k4d0b46') })
    await reload()
  } finally {
    batchRunning.value = false
    batchProgress.value = ''
  }
}

function applyRangePreset(hours: number | ''): void {
  if (typeof hours !== 'number') return
  historyRange.value = [new Date(Date.now() - hours * 3600_000), new Date()]
  void loadHistory()
}

function rowClass({ row }: { row: AlarmEvent }): string {
  return row.level === 'Critical' && !row.endTs && !row.shelved ? 'alarm-row-critical' : ''
}

function duration(alarm: AlarmEvent): string {
  const end = alarm.endTs ?? Date.now()
  const seconds = Math.max(0, Math.floor((end - alarm.startTs) / 1000))
  if (seconds < 60) return tr('alarmsView.k99598b', [seconds])
  if (seconds < 3600) return tr('alarmsView.k76dfb6', [Math.floor(seconds / 60), seconds % 60])
  return tr('alarmsView.k1cf4ee', [Math.floor(seconds / 3600), Math.floor((seconds % 3600) / 60)])
}

async function loadSummary(): Promise<void> {
  try {
    summary.value = await alarmApi.summary()
  } catch {
    // 概况失败不影响列表
  }
}

// background=true 用于推送与定时刷新：不点亮全表遮罩（遮罩会挡住确认操作）。失败留在页面上，不再额外弹提示。
async function load(background = false): Promise<void> {
  if (!background) loading.value = true
  try {
    const [list, shelf] = await Promise.all([alarmApi.query({ live: true, limit: PAGE_LIMIT + 1 }), ruleApi.shelves()])
    truncated.value = list.length > PAGE_LIMIT
    alarms.value = truncated.value ? list.slice(0, PAGE_LIMIT) : list
    shelves.value = shelf
    loadError.value = ''
  } catch (error) {
    loadError.value = errorMessage(error, tr('alarmsView.k620b67'))
  } finally {
    if (!background) loading.value = false
  }
  await loadSummary()
}

// 报警洪峰时推送会连成一片，每条都触发一次全量重查会把列表刷成请求风暴。
// 这里做在途去重 + 400ms 合批：同一波事件只查一次，落下的那波用 queued 补上。
let backgroundBusy = false
let backgroundQueued = false

async function backgroundRefresh(): Promise<void> {
  if (tab.value === 'history') return
  if (backgroundBusy) {
    backgroundQueued = true
    return
  }
  backgroundBusy = true
  try {
    await load(true)
  } finally {
    backgroundBusy = false
  }
  if (backgroundQueued) {
    backgroundQueued = false
    await backgroundRefresh()
  }
}

function schedulePushRefresh(): void {
  if (tab.value === 'history') return
  if (pushTimer !== undefined) window.clearTimeout(pushTimer)
  pushTimer = window.setTimeout(() => {
    pushTimer = undefined
    void backgroundRefresh()
  }, 400)
}

async function loadHistory(): Promise<void> {
  loading.value = true
  try {
    const [from, to] = historyRange.value ?? [new Date(Date.now() - 24 * 3600_000), new Date()]
    const list = await alarmApi.query({ from: from.getTime(), to: to.getTime(), limit: PAGE_LIMIT + 1 })
    truncated.value = list.length > PAGE_LIMIT
    alarms.value = truncated.value ? list.slice(0, PAGE_LIMIT) : list
    loadError.value = ''
  } catch (error) {
    loadError.value = errorMessage(error, tr('alarmsView.k620b67'))
  } finally {
    loading.value = false
  }
}

// 切到历史页时把默认区间刷新到“最近 24 小时至现在”，避免页面停留后漏掉新报警。
function onTabChange(): Promise<void> {
  selected.value = []
  rangePreset.value = ''
  // 选中行按 row-key 保留在表格内部，切页时要显式清掉，避免跨页误批量
  tableRef.value?.clearSelection()
  if (tab.value === 'history') historyRange.value = [new Date(Date.now() - 24 * 3600_000), new Date()]
  return reload()
}

async function ackVisible(): Promise<void> {
  const count = summary.value?.ackableUnack ?? summary.value?.unack ?? 0
  if (!count) {
    ElMessage.info(tr('alarmsView.ackVisibleOk', [0]))
    return
  }
  // 一键确认不可撤销，先让操作人看到影响条数
  try {
    await ElMessageBox.confirm(tr('alarmsView.ackVisibleConfirm', [count]), tr('alarmsView.confirmTitle'), {
      type: 'warning',
      confirmButtonText: tr('alarmsView.ackVisible'),
      cancelButtonText: tr('alarmsView.k4d0b46')
    })
  } catch {
    return
  }
  actionError.value = ''
  try {
    const r = await annunciatorApi.ackVisible()
    ElMessage.success(tr('alarmsView.ackVisibleOk', [r.acked]))
    await reload()
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmsView.ackVisibleFail'))
  }
}

async function silenceAnnunciators(): Promise<void> {
  actionError.value = ''
  let list: Awaited<ReturnType<typeof annunciatorApi.list>>
  try {
    list = await annunciatorApi.list()
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmsView.silenceAnnFail'))
    return
  }
  const ackable = list.filter((p) => canSilencePanel(p.areaId))
  if (!ackable.length) {
    ElMessage.info(tr('alarmsView.silenceAnnNone'))
    return
  }
  // 静音会停掉现场声光，属安全相关操作，必须先确认。只包含当前用户有确认权的面板。
  try {
    await ElMessageBox.confirm(tr('alarmsView.silenceAnnConfirm', [ackable.length]), tr('alarmsView.confirmTitle'), {
      type: 'warning',
      confirmButtonText: tr('alarmsView.silenceAnn'),
      cancelButtonText: tr('alarmsView.k4d0b46')
    })
  } catch {
    return
  }
  // 逐个面板静音，部分失败时列出明细，不静默吞错
  const report = await runBatch(ackable, (p) => p.name, async (p) => { await annunciatorApi.silence(p.id) })
  if (report.failed.length === 0) ElMessage.success(tr('alarmsView.silenceAnnOk'))
  else await ElMessageBox.alert(summarizeBatch(report, ''), tr('alarmsView.confirmTitle'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('alarmsView.k4d0b46') })
}
function reload(): Promise<void> {
  return tab.value === 'history' ? loadHistory() : load()
}

function openAck(alarm: AlarmEvent): void {
  ackError.value = ''
  Object.assign(ackDialog, { visible: true, mode: 'ack', alarm, note: '', saving: false })
}

function openNote(alarm: AlarmEvent): void {
  ackError.value = ''
  Object.assign(ackDialog, { visible: true, mode: 'note', alarm, note: '', saving: false })
}

async function submitAck(): Promise<void> {
  if (!ackDialog.alarm) return
  ackError.value = ''
  if (ackDialog.mode === 'note' && !ackDialog.note.trim()) {
    ackError.value = tr('alarmsView.kc31ec2')
    return
  }
  ackDialog.saving = true
  try {
    if (ackDialog.mode === 'ack') await alarmApi.ack(ackDialog.alarm.id, ackDialog.note.trim())
    else await alarmApi.note(ackDialog.alarm.id, ackDialog.note.trim())
    ElMessage.success(ackDialog.mode === 'ack' ? tr('alarmsView.kd9fea6') : tr('alarmsView.kbad4a8'))
    ackDialog.visible = false
    await reload()
  } catch (error) {
    ackError.value = errorMessage(error, tr('alarmsView.k09e424'))
  } finally {
    ackDialog.saving = false
  }
}

function openShelve(alarm: AlarmEvent): void {
  shelveError.value = ''
  Object.assign(shelveDialog, { visible: true, alarm, minutes: 60, reason: '', saving: false })
}

async function submitShelve(): Promise<void> {
  if (!shelveDialog.alarm?.ruleId) return
  shelveError.value = ''
  if (!shelveDialog.reason.trim()) {
    shelveError.value = tr('alarmsView.shelveReasonRequired')
    return
  }
  shelveDialog.saving = true
  try {
    await ruleApi.shelve(shelveDialog.alarm.ruleId, shelveDialog.minutes, shelveDialog.reason.trim())
    ElMessage.success(tr('alarmsView.ka7f94c'))
    shelveDialog.visible = false
    await load()
  } catch (error) {
    // 服务端 400 尽量落到原因字段旁，避免只弹一条全局提示
    shelveError.value = serverFormErrors(error, tr('alarmsView.k2fe047')).message
  } finally {
    shelveDialog.saving = false
  }
}

async function unshelve(ruleId: number): Promise<void> {
  // 解除后该规则的新报警会重新通知，属易误触操作
  try {
    await ElMessageBox.confirm(tr('alarmsView.unshelveConfirm'), tr('alarmsView.confirmTitle'), {
      type: 'warning',
      confirmButtonText: tr('alarmsView.kcd308e'),
      cancelButtonText: tr('alarmsView.k4d0b46')
    })
  } catch {
    return
  }
  actionError.value = ''
  try {
    await ruleApi.unshelve(ruleId)
    ElMessage.success(tr('alarmsView.kdaf18d'))
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmsView.k7286b4'))
  }
}

async function openTimeline(alarm: AlarmEvent): Promise<void> {
  actionError.value = ''
  try {
    timeline.value = await alarmApi.timeline(alarm.id)
    timelineVisible.value = true
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmsView.k760fd5'))
  }
}

function registerAlarmHandler(): void {
  // 只负责刷新数据；发声由 AppLayout 统一处理，避免重复消费同一条 alarm 事件。
  disposeAlarmHandler = onHubEvent<{ action: string; alarm: AlarmEvent }>('alarm', () => {
    schedulePushRefresh()
  })
}

// 30s 兜底轮询：推送丢包时列表不至于长期停摆；隐藏页自动暂停，回到前台补一次。
usePolling(backgroundRefresh, 30000)

onMounted(async () => {
  void deviceApi.list().then(list => {
    const map = new Map<number, number>()
    for (const d of list) if (d.areaId != null) map.set(d.id, d.areaId)
    deviceAreas.value = map
  }).catch(() => {})
  await load()
  registerAlarmHandler()
})

onBeforeUnmount(() => {
  if (pushTimer !== undefined) window.clearTimeout(pushTimer)
  disposeAlarmHandler?.()
})
</script>

<style scoped>
/* 报警台是常驻工作页：过滤区固定、表格占满剩余高度，横向滚动条才会一直贴在表格底部可见。 */
.alarm-page {
  height: calc(100vh - var(--scada-header-height));
  display: flex;
  flex-direction: column;
}

.alarm-panel {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  margin-bottom: 0;
}

.alarm-panel :deep(.el-tabs__header) {
  flex: none;
  margin-bottom: var(--scada-space-3);
}

.alarm-panel :deep(.toolbar) {
  flex: none;
  gap: 8px;
}

/* 状态筛选片兼作统计：压缩内边距，1600 宽下工具栏保持单行。 */
.alarm-panel :deep(.el-check-tag) {
  padding: 4px 10px;
  font-size: var(--scada-fs-caption);
}

.alarm-table-wrap {
  flex: 1;
  min-height: 280px;
}

.state-cell {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}

.gateway-tag {
  margin-right: 6px;
}

.flood-banner {
  margin-bottom: 12px;
}
.flags {
  display: flex;
  gap: 2px;
  margin-top: 2px;
}
</style>
