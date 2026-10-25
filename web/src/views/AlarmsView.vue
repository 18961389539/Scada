<template>
  <div class="page">
    <el-alert
      v-if="summary?.flood.inFlood"
      type="error"
      show-icon
      :closable="false"
      class="flood-banner"
      :title="$t('alarmsView.kff8eb2', [summary.flood.count, summary.flood.threshold])"
    />
    <div class="panel">
      <el-tabs v-model="tab" @tab-change="onTabChange">
        <el-tab-pane :label="$t('alarmsView.kc97e08')" name="live" />
        <el-tab-pane :label="$t('alarmsView.k894f96', [shelves.length])" name="shelved" />
        <el-tab-pane :label="$t('alarmsView.k8e0241')" name="history" />
      </el-tabs>
      <div class="toolbar">
        <template v-if="tab === 'live'">
          <el-tag type="danger" effect="light" data-test="count-unack">{{ $t('alarmsView.k3af88d', [summary?.unack ?? 0]) }}</el-tag>
          <el-tag type="warning" effect="light">{{ $t('alarmsView.k109812', [summary?.rtnUnack ?? 0]) }}</el-tag>
          <el-tag type="info" effect="light">{{ $t('alarmsView.kdcfa5c', [summary?.acked ?? 0]) }}</el-tag>
          <el-tag effect="plain">{{ $t('alarmsView.kef6a50', [summary?.shelved ?? 0]) }}</el-tag>
          <el-tag effect="plain">{{ $t('alarmsView.k80a67e', [summary?.suppressed ?? 0]) }}</el-tag>
          <el-tag effect="plain">{{ $t('alarmsView.k0e262f', [summary?.outOfServiceRules ?? 0]) }}</el-tag>
          <el-checkbox v-model="showHidden" style="margin-left: 8px">{{ $t('alarmsView.k73647c') }}</el-checkbox>
        </template>
        <template v-else-if="tab === 'history'">
          <el-date-picker v-model="historyRange" type="datetimerange" :start-placeholder="$t('alarmsView.ke8868a')" :end-placeholder="$t('alarmsView.ka0bb9f')" style="width: 360px" />
          <el-button :icon="Search" @click="loadHistory">{{ $t('alarmsView.k711363') }}</el-button>
          <el-input v-model="keyword" clearable :placeholder="$t('alarmsView.kd812c3')" style="width: 200px" />
        </template>
        <span class="spacer" />
        <el-button v-if="tab === 'live' && canAck" size="small" data-test="ack-visible" @click="ackVisible">{{ $t('alarmsView.ackVisible') }}</el-button>
        <el-button v-if="tab === 'live' && canAck" size="small" type="warning" data-test="silence-ann" @click="silenceAnnunciators">{{ $t('alarmsView.silenceAnn') }}</el-button>
        <el-button :icon="Refresh" size="small" @click="reload">{{ $t('alarmsView.k38108e') }}</el-button>
      </div>

      <el-table v-if="tab === 'shelved'" :data="shelves" size="small" :empty-text="$t('alarmsView.k0d51d2')">
        <el-table-column :label="$t('alarmsView.ked904c')" width="90"><template #default="{ row }">#{{ row.ruleId }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.k8cff1c')" width="90"><template #default="{ row }">#{{ row.tagId }}</template></el-table-column>
        <el-table-column prop="reason" :label="$t('alarmsView.k1ff9c3')" min-width="200" />
        <el-table-column prop="user" :label="$t('alarmsView.k06858d')" width="100" />
        <el-table-column :label="$t('alarmsView.k79cc87')" width="170"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.k8066e0')" width="170"><template #default="{ row }">{{ formatTime(row.untilTs) }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.kf3ea6d')" width="100">
          <template #default="{ row }">
            <el-button v-if="canAck" link type="primary" @click="unshelve(row.ruleId)">{{ $t('alarmsView.kcd308e') }}</el-button>
          </template>
        </el-table-column>
      </el-table>

      <el-table
        v-else
        v-loading="loading"
        :data="filteredAlarms"
        size="small"
        height="calc(100vh - 330px)"
        :row-class-name="rowClass"
        :empty-text="$t('alarmsView.k3d17a9')"
      >
        <el-table-column :label="$t('alarmsView.k254849')" width="80">
          <template #default="{ row }">
            <el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.k62e951')" width="130">
          <template #default="{ row }">
            <el-tag :type="alarmStateType(row.state)" size="small" data-test="alarm-state">{{ alarmStateLabels[row.state ?? 'Normal'] }}</el-tag>
            <div class="flags">
              <el-tag v-if="row.shelved" size="small" effect="plain">{{ $t('alarmsView.k06d561') }}</el-tag>
              <el-tooltip v-if="row.suppressed" :content="row.suppressReason || $t('alarmsView.kb37a4e')"><el-tag size="small" effect="plain" type="info">{{ $t('alarmsView.kaa2e2b') }}</el-tag></el-tooltip>
              <el-tag v-if="row.chattering" size="small" effect="plain" type="warning">{{ $t('alarmsView.ka60f92') }}</el-tag>
            </div>
          </template>
        </el-table-column>
        <el-table-column prop="message" :label="$t('alarmsView.kf3dd64')" min-width="220" show-overflow-tooltip />
        <el-table-column :label="$t('alarmsView.k5dfe3e')" width="100"><template #default="{ row }">{{ row.gatewayId ? $t('alarmsView.k38b4cc', [row.gatewayId]) : $t('alarmsView.k3963d7') }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.k521523')" width="90"><template #default="{ row }">{{ formatValue(row.value) }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.ke8868a')" width="165"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.ke93ea4')" width="190">
          <template #default="{ row }">
            {{ row.endTs ? formatTime(row.endTs) : $t('alarmsView.k559948') }}
            <div v-if="row.endReason" class="tiny muted" style="white-space: normal">{{ row.endReason }}</div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.k25722a')" width="100"><template #default="{ row }">{{ duration(row) }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.kb56d9a')" width="140">
          <template #default="{ row }">
            <span v-if="row.ackTs" class="tiny">{{ row.ackUser }}<br />{{ formatTime(row.ackTs) }}</span>
            <span v-else class="muted tiny">{{ $t('alarmsView.k0642fa') }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.kf3ea6d')" width="200" fixed="right">
          <template #default="{ row }">
            <el-button v-if="!row.ackTs && canAck" link type="primary" data-test="ack" @click="openAck(row)">{{ $t('alarmsView.kb56d9a') }}</el-button>
            <el-button v-if="canAck" link @click="openNote(row)">{{ $t('alarmsView.ke03614') }}</el-button>
            <el-button v-if="canAck && row.ruleId && !row.endTs && !row.shelved" link type="warning" @click="openShelve(row)">{{ $t('alarmsView.k06d561') }}</el-button>
            <el-button v-if="canAck && row.ruleId && row.shelved" link type="warning" @click="unshelve(row.ruleId)">{{ $t('alarmsView.kcd308e') }}</el-button>
            <el-button link data-test="timeline" @click="openTimeline(row)">{{ $t('alarmsView.k9058a1') }}</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="ackDialog.visible" :title="ackDialog.mode === 'ack' ? $t('alarmsView.kff8fce') : $t('alarmsView.kb82157')" width="460px">
      <div class="tiny muted" style="margin-bottom: 8px">{{ ackDialog.alarm?.message }}</div>
      <el-input v-model="ackDialog.note" type="textarea" :rows="3" maxlength="1000" show-word-limit :placeholder="ackDialog.mode === 'ack' ? $t('alarmsView.k7eb18b') : $t('alarmsView.k11abee')" data-test="note-input" />
      <template #footer>
        <el-button @click="ackDialog.visible = false">{{ $t('alarmsView.k4d0b46') }}</el-button>
        <el-button type="primary" :loading="ackDialog.saving" data-test="note-submit" @click="submitAck">{{ ackDialog.mode === 'ack' ? $t('alarmsView.kb56d9a') : $t('alarmsView.kfadf24') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="shelveDialog.visible" :title="$t('alarmsView.ke90a39')" width="460px">
      <el-form label-width="80px">
        <el-form-item :label="$t('alarmsView.k9f45b3')">{{ shelveDialog.alarm?.message }}</el-form-item>
        <el-form-item :label="$t('alarmsView.k29d055')">
          <el-select v-model="shelveDialog.minutes" style="width: 160px">
            <el-option v-for="m in [15, 30, 60, 120, 240, 480]" :key="m" :label="m >= 60 ? $t('alarmsView.k5247a4', [m / 60]) : $t('alarmsView.kc16d73', [m])" :value="m" />
          </el-select>
          <span class="tiny muted" style="margin-left: 8px">{{ $t('alarmsView.k96f9b0') }}</span>
        </el-form-item>
        <el-form-item :label="$t('alarmsView.k1ff9c3')" required>
          <el-input v-model="shelveDialog.reason" type="textarea" :rows="2" maxlength="500" :placeholder="$t('alarmsView.k7fc9ae')" data-test="shelve-reason" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="shelveDialog.visible = false">{{ $t('alarmsView.k4d0b46') }}</el-button>
        <el-button type="warning" :disabled="!shelveDialog.reason.trim()" data-test="shelve-submit" @click="submitShelve">{{ $t('alarmsView.k06d561') }}</el-button>
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
import { ElMessage } from 'element-plus'
import { Refresh, Search } from '@element-plus/icons-vue'
import * as signalR from '@microsoft/signalr'
import { annunciatorApi } from '../api/annunciators'
import { alarmApi, ruleApi } from '../api'
import { TOKEN_KEY, errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { AlarmEvent, AlarmShelve, AlarmSummary, AlarmTimeline } from '../types'
import { alarmActionLabels, alarmStateLabels, alarmStateType, hasAny, formatTime, formatValue, levelLabel, levelType } from '../utils'
import { tr } from '../i18n'

const auth = useAuthStore()
const canAck = computed(() => hasAny(auth.user, 'AckAlarm'))
const tab = ref<'live' | 'shelved' | 'history'>('live')
const alarms = ref<AlarmEvent[]>([])
const shelves = ref<AlarmShelve[]>([])
const summary = ref<AlarmSummary | null>(null)
const loading = ref(false)
const keyword = ref('')
const showHidden = ref(false)
const historyRange = ref<[Date, Date] | null>([new Date(Date.now() - 24 * 3600_000), new Date()])
const ackDialog = reactive({ visible: false, mode: 'ack' as 'ack' | 'note', alarm: null as AlarmEvent | null, note: '', saving: false })
const shelveDialog = reactive({ visible: false, alarm: null as AlarmEvent | null, minutes: 60, reason: '' })
const timeline = ref<AlarmTimeline | null>(null)
const timelineVisible = ref(false)

let connection: signalR.HubConnection | null = null
let timer: number | undefined
let audioContext: AudioContext | null = null

function playAlarmSound(level: string): void {
  try {
    audioContext ??= new AudioContext()
    void audioContext.resume()
    const now = audioContext.currentTime
    const oscillator = audioContext.createOscillator()
    const gain = audioContext.createGain()
    oscillator.type = 'square'
    oscillator.frequency.value = level === 'Critical' ? 880 : 660
    gain.gain.setValueAtTime(0.08, now)
    gain.gain.exponentialRampToValueAtTime(0.0001, now + 0.35)
    oscillator.connect(gain).connect(audioContext.destination)
    oscillator.start(now)
    oscillator.stop(now + 0.35)
  } catch {
    // 浏览器未授权音频播放时静默降级
  }
}

const filteredAlarms = computed(() => {
  let list = alarms.value
  if (tab.value === 'live' && !showHidden.value) list = list.filter((x) => !x.shelved && !x.suppressed)
  if (tab.value === 'history' && keyword.value.trim()) list = list.filter((x) => x.message.includes(keyword.value.trim()))
  return list
})

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

async function load(): Promise<void> {
  loading.value = true
  try {
    const [list, shelf] = await Promise.all([alarmApi.query({ live: true, limit: 1000 }), ruleApi.shelves()])
    alarms.value = list
    shelves.value = shelf
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmsView.k620b67')))
  } finally {
    loading.value = false
  }
  await loadSummary()
}

async function loadHistory(): Promise<void> {
  loading.value = true
  try {
    const [from, to] = historyRange.value ?? [new Date(Date.now() - 24 * 3600_000), new Date()]
    alarms.value = await alarmApi.query({ from: from.getTime(), to: to.getTime(), limit: 1000 })
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmsView.k620b67')))
  } finally {
    loading.value = false
  }
}

// 切到历史页时把默认区间刷新到“最近 24 小时至现在”，避免页面停留后漏掉新报警。
function onTabChange(): Promise<void> {
  if (tab.value === 'history') historyRange.value = [new Date(Date.now() - 24 * 3600_000), new Date()]
  return reload()
}

async function ackVisible() {
  try {
    const r = await annunciatorApi.ackVisible()
    ElMessage.success(tr('alarmsView.ackVisibleOk', [r.acked]))
    await reload()
  } catch (e: any) { ElMessage.error(e?.response?.data?.message ?? tr('alarmsView.ackVisibleFail')) }
}
async function silenceAnnunciators() {
  try {
    const list = await annunciatorApi.list()
    for (const p of list) await annunciatorApi.silence(p.id)
    ElMessage.success(tr('alarmsView.silenceAnnOk'))
  } catch (e: any) { ElMessage.error(e?.response?.data?.message ?? tr('alarmsView.silenceAnnFail')) }
}
function reload(): Promise<void> {
  return tab.value === 'history' ? loadHistory() : load()
}

function openAck(alarm: AlarmEvent): void {
  Object.assign(ackDialog, { visible: true, mode: 'ack', alarm, note: '', saving: false })
}

function openNote(alarm: AlarmEvent): void {
  Object.assign(ackDialog, { visible: true, mode: 'note', alarm, note: '', saving: false })
}

async function submitAck(): Promise<void> {
  if (!ackDialog.alarm) return
  if (ackDialog.mode === 'note' && !ackDialog.note.trim()) {
    ElMessage.warning(tr('alarmsView.kc31ec2'))
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
    ElMessage.error(errorMessage(error, tr('alarmsView.k09e424')))
  } finally {
    ackDialog.saving = false
  }
}

function openShelve(alarm: AlarmEvent): void {
  Object.assign(shelveDialog, { visible: true, alarm, minutes: 60, reason: '' })
}

async function submitShelve(): Promise<void> {
  if (!shelveDialog.alarm?.ruleId) return
  try {
    await ruleApi.shelve(shelveDialog.alarm.ruleId, shelveDialog.minutes, shelveDialog.reason.trim())
    ElMessage.success(tr('alarmsView.ka7f94c'))
    shelveDialog.visible = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmsView.k2fe047')))
  }
}

async function unshelve(ruleId: number): Promise<void> {
  try {
    await ruleApi.unshelve(ruleId)
    ElMessage.success(tr('alarmsView.kdaf18d'))
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmsView.k7286b4')))
  }
}

async function openTimeline(alarm: AlarmEvent): Promise<void> {
  try {
    timeline.value = await alarmApi.timeline(alarm.id)
    timelineVisible.value = true
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmsView.k760fd5')))
  }
}

async function startHub(): Promise<void> {
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/realtime', { accessTokenFactory: () => localStorage.getItem(TOKEN_KEY) ?? '' })
    .withAutomaticReconnect()
    .build()

  connection.on('alarm', (payload: { action: string; alarm: AlarmEvent }) => {
    const audible = !payload.alarm.shelved && !payload.alarm.suppressed && !payload.alarm.chattering
    if (audible && (payload.action === 'raise' || ((payload.action === 'unshelve' || payload.action === 'unsuppress') && !payload.alarm.ackTs && !payload.alarm.endTs)))
      playAlarmSound(payload.alarm.level)
    if (tab.value !== 'history') void load()
  })

  try {
    await connection.start()
  } catch {
    // fall back to manual refresh
  }
}

onMounted(async () => {
  await load()
  timer = window.setInterval(() => {
    if (tab.value !== 'history') void load()
  }, 30000)
  await startHub()
})

onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer)
  connection?.stop()
})
</script>

<style scoped>
.flood-banner {
  margin-bottom: 12px;
}
.flags {
  display: flex;
  gap: 2px;
  margin-top: 2px;
}
</style>
