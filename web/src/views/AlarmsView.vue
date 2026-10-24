<template>
  <div class="page">
    <el-alert
      v-if="summary?.flood.inFlood"
      type="error"
      show-icon
      :closable="false"
      class="flood-banner"
      :title="`报警洪水：最近 10 分钟 ${summary.flood.count} 条，超过阈值 ${summary.flood.threshold} 条。请优先处理严重报警，必要时暂挂干扰报警。`"
    />
    <div class="panel">
      <el-tabs v-model="tab" @tab-change="onTabChange">
        <el-tab-pane label="实时报警" name="live" />
        <el-tab-pane :label="`暂挂 (${shelves.length})`" name="shelved" />
        <el-tab-pane label="历史报警" name="history" />
      </el-tabs>
      <div class="toolbar">
        <template v-if="tab === 'live'">
          <el-tag type="danger" effect="light" data-test="count-unack">未确认 {{ summary?.unack ?? 0 }}</el-tag>
          <el-tag type="warning" effect="light">已恢复未确认 {{ summary?.rtnUnack ?? 0 }}</el-tag>
          <el-tag type="info" effect="light">已确认 {{ summary?.acked ?? 0 }}</el-tag>
          <el-tag effect="plain">暂挂 {{ summary?.shelved ?? 0 }}</el-tag>
          <el-tag effect="plain">抑制 {{ summary?.suppressed ?? 0 }}</el-tag>
          <el-tag effect="plain">停用规则 {{ summary?.outOfServiceRules ?? 0 }}</el-tag>
          <el-checkbox v-model="showHidden" style="margin-left: 8px">显示暂挂/抑制</el-checkbox>
        </template>
        <template v-else-if="tab === 'history'">
          <el-date-picker v-model="historyRange" type="datetimerange" start-placeholder="开始时间" end-placeholder="结束时间" style="width: 360px" />
          <el-button :icon="Search" @click="loadHistory">查询</el-button>
          <el-input v-model="keyword" clearable placeholder="按内容过滤" style="width: 200px" />
        </template>
        <span class="spacer" />
        <el-button :icon="Refresh" size="small" @click="reload">刷新</el-button>
      </div>

      <el-table v-if="tab === 'shelved'" :data="shelves" size="small" empty-text="没有暂挂中的报警">
        <el-table-column label="规则" width="90"><template #default="{ row }">#{{ row.ruleId }}</template></el-table-column>
        <el-table-column label="测点" width="90"><template #default="{ row }">#{{ row.tagId }}</template></el-table-column>
        <el-table-column prop="reason" label="原因" min-width="200" />
        <el-table-column prop="user" label="操作人" width="100" />
        <el-table-column label="开始" width="170"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
        <el-table-column label="到期" width="170"><template #default="{ row }">{{ formatTime(row.untilTs) }}</template></el-table-column>
        <el-table-column label="操作" width="100">
          <template #default="{ row }">
            <el-button v-if="canAck" link type="primary" @click="unshelve(row.ruleId)">解除暂挂</el-button>
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
        empty-text="暂无报警"
      >
        <el-table-column label="级别" width="80">
          <template #default="{ row }">
            <el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="130">
          <template #default="{ row }">
            <el-tag :type="alarmStateType(row.state)" size="small" data-test="alarm-state">{{ alarmStateLabels[row.state ?? 'Normal'] }}</el-tag>
            <div class="flags">
              <el-tag v-if="row.shelved" size="small" effect="plain">暂挂</el-tag>
              <el-tooltip v-if="row.suppressed" :content="row.suppressReason || '条件抑制'"><el-tag size="small" effect="plain" type="info">抑制</el-tag></el-tooltip>
              <el-tag v-if="row.chattering" size="small" effect="plain" type="warning">抖动</el-tag>
            </div>
          </template>
        </el-table-column>
        <el-table-column prop="message" label="报警内容" min-width="220" show-overflow-tooltip />
        <el-table-column label="采集位置" width="100"><template #default="{ row }">{{ row.gatewayId ? `网关 #${row.gatewayId}` : '中心本机' }}</template></el-table-column>
        <el-table-column label="数值" width="90"><template #default="{ row }">{{ formatValue(row.value) }}</template></el-table-column>
        <el-table-column label="开始时间" width="165"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
        <el-table-column label="结束时间 / 原因" width="190">
          <template #default="{ row }">
            {{ row.endTs ? formatTime(row.endTs) : '持续中' }}
            <div v-if="row.endReason" class="tiny muted" style="white-space: normal">{{ row.endReason }}</div>
          </template>
        </el-table-column>
        <el-table-column label="持续" width="100"><template #default="{ row }">{{ duration(row) }}</template></el-table-column>
        <el-table-column label="确认" width="140">
          <template #default="{ row }">
            <span v-if="row.ackTs" class="tiny">{{ row.ackUser }}<br />{{ formatTime(row.ackTs) }}</span>
            <span v-else class="muted tiny">未确认</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="200" fixed="right">
          <template #default="{ row }">
            <el-button v-if="!row.ackTs && canAck" link type="primary" data-test="ack" @click="openAck(row)">确认</el-button>
            <el-button v-if="canAck" link @click="openNote(row)">备注</el-button>
            <el-button v-if="canAck && row.ruleId && !row.endTs && !row.shelved" link type="warning" @click="openShelve(row)">暂挂</el-button>
            <el-button v-if="canAck && row.ruleId && row.shelved" link type="warning" @click="unshelve(row.ruleId)">解除暂挂</el-button>
            <el-button link data-test="timeline" @click="openTimeline(row)">记录</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="ackDialog.visible" :title="ackDialog.mode === 'ack' ? '确认报警' : '添加备注'" width="460px">
      <div class="tiny muted" style="margin-bottom: 8px">{{ ackDialog.alarm?.message }}</div>
      <el-input v-model="ackDialog.note" type="textarea" :rows="3" maxlength="1000" show-word-limit :placeholder="ackDialog.mode === 'ack' ? '处理说明（可选）' : '备注内容'" data-test="note-input" />
      <template #footer>
        <el-button @click="ackDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="ackDialog.saving" data-test="note-submit" @click="submitAck">{{ ackDialog.mode === 'ack' ? '确认' : '保存' }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="shelveDialog.visible" title="暂挂报警" width="460px">
      <el-form label-width="80px">
        <el-form-item label="报警">{{ shelveDialog.alarm?.message }}</el-form-item>
        <el-form-item label="时长">
          <el-select v-model="shelveDialog.minutes" style="width: 160px">
            <el-option v-for="m in [15, 30, 60, 120, 240, 480]" :key="m" :label="m >= 60 ? `${m / 60} 小时` : `${m} 分钟`" :value="m" />
          </el-select>
          <span class="tiny muted" style="margin-left: 8px">到期自动恢复</span>
        </el-form-item>
        <el-form-item label="原因" required>
          <el-input v-model="shelveDialog.reason" type="textarea" :rows="2" maxlength="500" placeholder="必填，例如：仪表校验中" data-test="shelve-reason" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="shelveDialog.visible = false">取消</el-button>
        <el-button type="warning" :disabled="!shelveDialog.reason.trim()" data-test="shelve-submit" @click="submitShelve">暂挂</el-button>
      </template>
    </el-dialog>

    <el-drawer v-model="timelineVisible" title="报警处理记录" size="440px">
      <template v-if="timeline">
        <div class="tiny" style="margin-bottom: 12px">
          <el-tag :type="alarmStateType(timeline.alarm.state)" size="small">{{ alarmStateLabels[timeline.alarm.state ?? 'Normal'] }}</el-tag>
          {{ timeline.alarm.message }}
        </div>
        <el-timeline>
          <el-timeline-item :timestamp="formatTime(timeline.alarm.startTs)" type="danger">报警开始，值 {{ formatValue(timeline.alarm.value) }}</el-timeline-item>
          <el-timeline-item v-for="item in timeline.activities" :key="item.id" :timestamp="formatTime(item.ts)" data-test="timeline-item">
            <b>{{ alarmActionLabels[item.action] ?? item.action }}</b>
            <span v-if="item.user" class="muted"> · {{ item.user }}</span>
            <div v-if="item.note" class="tiny">{{ item.note }}</div>
          </el-timeline-item>
        </el-timeline>
        <div v-if="timeline.notifications.length" class="tiny muted">外发 {{ timeline.notifications.length }} 次，失败 {{ timeline.notifications.filter((x) => !x.success).length }} 次</div>
      </template>
    </el-drawer>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Refresh, Search } from '@element-plus/icons-vue'
import * as signalR from '@microsoft/signalr'
import { alarmApi, ruleApi } from '../api'
import { TOKEN_KEY, errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { AlarmEvent, AlarmShelve, AlarmSummary, AlarmTimeline } from '../types'
import { alarmActionLabels, alarmStateLabels, alarmStateType, hasAny, formatTime, formatValue, levelLabel, levelType } from '../utils'

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
  if (seconds < 60) return `${seconds} 秒`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分 ${seconds % 60} 秒`
  return `${Math.floor(seconds / 3600)} 小时 ${Math.floor((seconds % 3600) / 60)} 分`
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
    ElMessage.error(errorMessage(error, '加载报警失败'))
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
    ElMessage.error(errorMessage(error, '加载报警失败'))
  } finally {
    loading.value = false
  }
}

// 切到历史页时把默认区间刷新到“最近 24 小时至现在”，避免页面停留后漏掉新报警。
function onTabChange(): Promise<void> {
  if (tab.value === 'history') historyRange.value = [new Date(Date.now() - 24 * 3600_000), new Date()]
  return reload()
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
    ElMessage.warning('请填写备注')
    return
  }
  ackDialog.saving = true
  try {
    if (ackDialog.mode === 'ack') await alarmApi.ack(ackDialog.alarm.id, ackDialog.note.trim())
    else await alarmApi.note(ackDialog.alarm.id, ackDialog.note.trim())
    ElMessage.success(ackDialog.mode === 'ack' ? '已确认' : '备注已保存')
    ackDialog.visible = false
    await reload()
  } catch (error) {
    ElMessage.error(errorMessage(error, '操作失败'))
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
    ElMessage.success('已暂挂')
    shelveDialog.visible = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '暂挂失败'))
  }
}

async function unshelve(ruleId: number): Promise<void> {
  try {
    await ruleApi.unshelve(ruleId)
    ElMessage.success('已解除暂挂')
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '解除失败'))
  }
}

async function openTimeline(alarm: AlarmEvent): Promise<void> {
  try {
    timeline.value = await alarmApi.timeline(alarm.id)
    timelineVisible.value = true
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载处理记录失败'))
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
