<template>
  <div class="page">
    <div class="card-grid">
      <div class="stat-card">
        <div class="label">{{ $t('overviewView.k99828c') }}</div>
        <div class="value">{{ health?.onlineDevices ?? 0 }} / {{ health?.totalDevices ?? 0 }}</div>
        <div class="sub">{{ $t('overviewView.kc64267') }}</div>
      </div>
      <div class="stat-card">
        <div class="label">{{ $t('overviewView.k9710b5') }}</div>
        <div class="value" :class="unackCount > 0 ? 'tag-fail' : ''">{{ unackCount }}</div>
        <div class="sub">{{ $t('overviewView.ka84899', [activeAlarms.length]) }}</div>
      </div>
      <div class="stat-card">
        <div class="label">{{ $t('overviewView.k674e52') }}</div>
        <div class="value">{{ health?.configuredTags ?? 0 }}</div>
        <div class="sub">{{ $t('overviewView.k8e8615', [health?.busBacklog ?? 0]) }}</div>
      </div>
      <div class="stat-card">
        <div class="label">{{ $t('overviewView.k4d5c56') }}</div>
        <div class="value">{{ formatBytes(health?.storage.historyBytes ?? 0) }}</div>
        <div class="sub">{{ $t('overviewView.kcc8821', [health?.disk.freeGb ?? '-']) }}</div>
      </div>
      <div class="stat-card">
        <div class="label">{{ $t('overviewView.k66836c') }}</div>
        <div class="value">{{ uptime }}</div>
        <div class="sub">{{ health?.environment ?? '-' }}</div>
      </div>
    </div>

    <div class="panel">
      <div class="toolbar">
        <strong>{{ $t('overviewView.kd97d61') }}</strong>
        <span class="spacer" />
        <span class="muted tiny">{{ $t('overviewView.kc2d817') }}</span>
        <el-button size="small" :icon="Refresh" @click="load">{{ $t('overviewView.k38108e') }}</el-button>
      </div>
      <el-table :data="health?.devices ?? []" size="small" :empty-text="$t('overviewView.k2b9379')">
        <el-table-column prop="name" :label="$t('overviewView.k01f2c1')" min-width="140" />
        <el-table-column :label="$t('overviewView.k62e951')" width="110">
          <template #default="{ row }">
            <el-tag :type="row.online ? 'success' : 'danger'" effect="light" size="small">
              {{ row.online ? $t('overviewView.k0373ff') : $t('overviewView.k211357') }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.k014935')" min-width="150">
          <template #default="{ row }">{{ row.lastPollTs ? formatTime(row.lastPollTs) : '-' }}</template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.k8a12b1')" width="100">
          <template #default="{ row }">{{ row.lastCycleMs ? row.lastCycleMs.toFixed(2) + ' ms' : '-' }}</template>
        </el-table-column>
        <el-table-column prop="tagCount" :label="$t('overviewView.k217dc2')" width="90" />
        <el-table-column prop="commFailures" :label="$t('overviewView.k6ac251')" width="100" />
        <el-table-column :label="$t('overviewView.ka38a81')" min-width="200">
          <template #default="{ row }"><span class="tiny muted">{{ row.error ?? '-' }}</span></template>
        </el-table-column>
      </el-table>
    </div>

    <div class="panel">
      <div class="toolbar">
        <strong>{{ $t('overviewView.k855fec') }}</strong>
        <span class="spacer" />
        <el-button size="small" @click="router.push('/alarms')">{{ $t('overviewView.ked2172') }}</el-button>
      </div>
      <el-table :data="activeAlarms" size="small" :empty-text="$t('overviewView.k996b9d')">
        <el-table-column :label="$t('overviewView.k254849')" width="90">
          <template #default="{ row }">
            <el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="message" :label="$t('overviewView.kf3dd64')" min-width="220" />
        <el-table-column :label="$t('overviewView.k521523')" width="110">
          <template #default="{ row }">{{ formatValue(row.value) }}</template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.ke8868a')" width="170">
          <template #default="{ row }">{{ formatTime(row.startTs) }}</template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.kb56d9a')" width="120">
          <template #default="{ row }">
            <el-tag v-if="row.ackTs" type="info" size="small">{{ row.ackUser }}</el-tag>
            <span v-else class="muted tiny">{{ $t('overviewView.k0642fa') }}</span>
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
import { tr } from '../i18n'

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
  return days > 0 ? tr('overviewView.k4ed72e', [days, hours]) : tr('overviewView.k165ffa', [hours, minutes])
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
