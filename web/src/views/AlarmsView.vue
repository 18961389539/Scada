<template>
  <div class="page">
    <div class="panel">
      <el-tabs v-model="tab" @tab-change="load">
        <el-tab-pane label="实时报警" name="active">
          <div class="toolbar">
            <el-tag type="danger" effect="light">未确认 {{ unackCount }}</el-tag>
            <el-tag type="warning" effect="light">活动 {{ alarms.length }}</el-tag>
            <span class="spacer" />
            <el-button :icon="Refresh" size="small" @click="load">刷新</el-button>
          </div>
        </el-tab-pane>
        <el-tab-pane label="历史报警" name="history">
          <div class="toolbar">
            <el-date-picker
              v-model="historyRange"
              type="datetimerange"
              start-placeholder="开始时间"
              end-placeholder="结束时间"
              style="width: 360px"
            />
            <el-button :icon="Search" @click="loadHistory">查询</el-button>
            <el-input v-model="keyword" clearable placeholder="按内容过滤" style="width: 200px" />
          </div>
        </el-tab-pane>
      </el-tabs>

      <el-table
        v-loading="loading"
        :data="filteredAlarms"
        size="small"
        height="calc(100vh - 300px)"
        :row-class-name="rowClass"
        empty-text="暂无报警"
      >
        <el-table-column label="级别" width="90">
          <template #default="{ row }">
            <el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="message" label="报警内容" min-width="240" show-overflow-tooltip />
        <el-table-column label="采集位置" width="110"><template #default="{ row }">{{ row.gatewayId ? `网关 #${row.gatewayId}` : '中心本机' }}</template></el-table-column>
        <el-table-column label="数值" width="110">
          <template #default="{ row }">{{ formatValue(row.value) }}</template>
        </el-table-column>
        <el-table-column label="开始时间" width="170">
          <template #default="{ row }">{{ formatTime(row.startTs) }}</template>
        </el-table-column>
        <el-table-column label="结束时间 / 原因" width="205">
          <template #default="{ row }">
            {{ row.endTs ? formatTime(row.endTs) : '持续中' }}
            <div v-if="row.endReason" class="tiny muted" style="white-space: normal">{{ row.endReason }}</div>
          </template>
        </el-table-column>
        <el-table-column label="持续时间" width="110">
          <template #default="{ row }">{{ duration(row) }}</template>
        </el-table-column>
        <el-table-column label="确认" width="150">
          <template #default="{ row }">
            <span v-if="row.ackTs" class="tiny">{{ row.ackUser }}<br />{{ formatTime(row.ackTs) }}</span>
            <span v-else class="muted tiny">未确认</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="90" fixed="right">
          <template #default="{ row }">
            <el-button
              v-if="!row.ackTs && canOperate(auth.user?.role)"
              link
              type="primary"
              @click="ack(row)"
            >
              确认
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Refresh, Search } from '@element-plus/icons-vue'
import * as signalR from '@microsoft/signalr'
import { alarmApi } from '../api'
import { TOKEN_KEY, errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { AlarmEvent } from '../types'
import { canOperate, formatTime, formatValue, levelLabel, levelType } from '../utils'

const auth = useAuthStore()
const tab = ref<'active' | 'history'>('active')
const alarms = ref<AlarmEvent[]>([])
const loading = ref(false)
const keyword = ref('')
const historyRange = ref<[Date, Date] | null>([
  new Date(Date.now() - 24 * 3600_000),
  new Date()
])

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

const unackCount = computed(() => alarms.value.filter((x) => !x.ackTs).length)
const filteredAlarms = computed(() => {
  if (tab.value === 'active' || !keyword.value.trim()) return alarms.value
  const text = keyword.value.trim()
  return alarms.value.filter((x) => x.message.includes(text))
})

function rowClass({ row }: { row: AlarmEvent }): string {
  return row.level === 'Critical' && !row.endTs ? 'alarm-row-critical' : ''
}

function duration(alarm: AlarmEvent): string {
  const end = alarm.endTs ?? Date.now()
  const seconds = Math.max(0, Math.floor((end - alarm.startTs) / 1000))
  if (seconds < 60) return `${seconds} 秒`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分 ${seconds % 60} 秒`
  return `${Math.floor(seconds / 3600)} 小时 ${Math.floor((seconds % 3600) / 60)} 分`
}

async function load(): Promise<void> {
  loading.value = true
  try {
    alarms.value = await alarmApi.query({
      activeOnly: tab.value === 'active',
      limit: 500
    })
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载报警失败'))
  } finally {
    loading.value = false
  }
}

async function loadHistory(): Promise<void> {
  loading.value = true
  try {
    const [from, to] = historyRange.value ?? [new Date(Date.now() - 24 * 3600_000), new Date()]
    alarms.value = await alarmApi.query({ from: from.getTime(), to: to.getTime(), limit: 500 })
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载报警失败'))
  } finally {
    loading.value = false
  }
}

async function ack(alarm: AlarmEvent): Promise<void> {
  try {
    await alarmApi.ack(alarm.id)
    ElMessage.success('已确认')
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '确认失败'))
  }
}

async function startHub(): Promise<void> {
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/realtime', { accessTokenFactory: () => localStorage.getItem(TOKEN_KEY) ?? '' })
    .withAutomaticReconnect()
    .build()

  connection.on('alarm', (payload: { action: string; alarm: AlarmEvent }) => {
    if (payload.action === 'raise') playAlarmSound(payload.alarm.level)
    if (tab.value === 'active') void load()
    else void loadHistory()
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
    if (tab.value === 'active') void load()
  }, 30000)
  await startHub()
})

onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer)
  connection?.stop()
})
</script>
