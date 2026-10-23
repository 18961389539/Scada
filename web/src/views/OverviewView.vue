<template>
  <div class="page">
    <div class="card-grid">
      <div class="stat-card">
        <div class="label">设备在线</div>
        <div class="value">{{ health?.onlineDevices ?? 0 }} / {{ health?.totalDevices ?? 0 }}</div>
        <div class="sub">采集任务运行状态</div>
      </div>
      <div class="stat-card">
        <div class="label">未确认报警</div>
        <div class="value" :class="unackCount > 0 ? 'tag-fail' : ''">{{ unackCount }}</div>
        <div class="sub">当前活动报警 {{ activeAlarms.length }} 条</div>
      </div>
      <div class="stat-card">
        <div class="label">配置测点</div>
        <div class="value">{{ health?.configuredTags ?? 0 }}</div>
        <div class="sub">通道积压 {{ health?.busBacklog ?? 0 }}</div>
      </div>
      <div class="stat-card">
        <div class="label">历史存储</div>
        <div class="value">{{ formatBytes(health?.storage.historyBytes ?? 0) }}</div>
        <div class="sub">磁盘剩余 {{ health?.disk.freeGb ?? '-' }} GB</div>
      </div>
      <div class="stat-card">
        <div class="label">运行时长</div>
        <div class="value">{{ uptime }}</div>
        <div class="sub">{{ health?.environment ?? '-' }}</div>
      </div>
    </div>

    <div class="panel">
      <div class="toolbar">
        <strong>设备通信状态</strong>
        <span class="spacer" />
        <span class="muted tiny">每 5 秒刷新</span>
        <el-button size="small" :icon="Refresh" @click="load">刷新</el-button>
      </div>
      <el-table :data="health?.devices ?? []" size="small" empty-text="暂无设备">
        <el-table-column prop="name" label="设备" min-width="140" />
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag :type="row.online ? 'success' : 'danger'" effect="light" size="small">
              {{ row.online ? '在线' : '离线' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="最近采集" min-width="150">
          <template #default="{ row }">{{ row.lastPollTs ? formatTime(row.lastPollTs) : '-' }}</template>
        </el-table-column>
        <el-table-column label="周期" width="100">
          <template #default="{ row }">{{ row.lastCycleMs ? row.lastCycleMs.toFixed(2) + ' ms' : '-' }}</template>
        </el-table-column>
        <el-table-column prop="tagCount" label="测点数" width="90" />
        <el-table-column prop="commFailures" label="通信失败" width="100" />
        <el-table-column label="错误信息" min-width="200">
          <template #default="{ row }"><span class="tiny muted">{{ row.error ?? '-' }}</span></template>
        </el-table-column>
      </el-table>
    </div>

    <div class="panel">
      <div class="toolbar">
        <strong>当前活动报警</strong>
        <span class="spacer" />
        <el-button size="small" @click="router.push('/alarms')">查看全部</el-button>
      </div>
      <el-table :data="activeAlarms" size="small" empty-text="当前没有活动报警">
        <el-table-column label="级别" width="90">
          <template #default="{ row }">
            <el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="message" label="报警内容" min-width="220" />
        <el-table-column label="数值" width="110">
          <template #default="{ row }">{{ formatValue(row.value) }}</template>
        </el-table-column>
        <el-table-column label="开始时间" width="170">
          <template #default="{ row }">{{ formatTime(row.startTs) }}</template>
        </el-table-column>
        <el-table-column label="确认" width="120">
          <template #default="{ row }">
            <el-tag v-if="row.ackTs" type="info" size="small">{{ row.ackUser }}</el-tag>
            <span v-else class="muted tiny">未确认</span>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Refresh } from '@element-plus/icons-vue'
import { alarmApi, healthApi } from '../api'
import type { AlarmEvent, Health } from '../types'
import { formatBytes, formatTime, formatValue, levelLabel, levelType } from '../utils'

const router = useRouter()
const health = ref<Health | null>(null)
const alarms = ref<AlarmEvent[]>([])
let timer: number | undefined

const activeAlarms = computed(() => alarms.value.filter((x) => !x.endTs).slice(0, 10))
const unackCount = computed(() => activeAlarms.value.filter((x) => !x.ackTs).length)
const uptime = computed(() => {
  const seconds = health.value?.uptimeSeconds ?? 0
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  return days > 0 ? `${days}天 ${hours}小时` : `${hours}小时 ${minutes}分`
})

async function load(): Promise<void> {
  try {
    const [h, a] = await Promise.all([
      healthApi.get(),
      alarmApi.query({ activeOnly: true, limit: 200 })
    ])
    health.value = h
    alarms.value = a
  } catch {
    // next refresh will retry
  }
}

onMounted(async () => {
  await load()
  timer = window.setInterval(load, 5000)
})

onBeforeUnmount(() => {
  if (timer) window.clearInterval(timer)
})
</script>
