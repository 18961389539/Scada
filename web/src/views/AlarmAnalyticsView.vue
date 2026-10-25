<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-date-picker v-model="range" type="datetimerange" :start-placeholder="$t('alarmAnalyticsView.ke8868a')" :end-placeholder="$t('alarmAnalyticsView.ka0bb9f')" style="width: 360px" />
        <el-select v-model="top" style="width: 120px">
          <el-option v-for="n in [5, 10, 20, 50]" :key="n" :label="`Top ${n}`" :value="n" />
        </el-select>
        <el-button type="primary" :icon="Search" :loading="loading" @click="load">{{ $t('alarmAnalyticsView.kca8ffc') }}</el-button>
        <span class="spacer" />
        <span class="tiny muted">{{ $t('alarmAnalyticsView.k7f244c', [kpi?.floodThreshold ?? 10]) }}</span>
      </div>

      <div v-if="kpi" class="cards">
        <div class="card" data-test="kpi-total"><div class="value">{{ kpi.annunciated }}</div><div class="label">{{ $t('alarmAnalyticsView.ka4dc34') }}</div></div>
        <div class="card"><div class="value">{{ kpi.perHour }}</div><div class="label">{{ $t('alarmAnalyticsView.k0097ea') }}</div></div>
        <div class="card" :class="{ bad: kpi.perOperatorHour > 6 }"><div class="value">{{ kpi.perOperatorHour }}</div><div class="label">{{ $t('alarmAnalyticsView.kc96274', [kpi.operatorPositions]) }}</div></div>
        <div class="card"><div class="value">{{ kpi.peak10Min }}</div><div class="label">{{ $t('alarmAnalyticsView.kf0a6e9') }}</div></div>
        <div class="card" :class="{ bad: kpi.percentTimeInFlood > 1 }"><div class="value">{{ kpi.percentTimeInFlood }}%</div><div class="label">{{ $t('alarmAnalyticsView.k4c4c9b', [kpi.floodBuckets]) }}</div></div>
        <div class="card"><div class="value">{{ kpi.standing.length }}</div><div class="label">{{ $t('alarmAnalyticsView.k06dfab') }}</div></div>
        <div class="card"><div class="value">{{ kpi.chattering }}</div><div class="label">{{ $t('alarmAnalyticsView.k19e848') }}</div></div>
        <div class="card"><div class="value">{{ kpi.shelved }} / {{ kpi.suppressed }}</div><div class="label">{{ $t('alarmAnalyticsView.kc8d9d0') }}</div></div>
      </div>

      <el-row v-if="kpi" :gutter="16">
        <el-col :span="12">
          <h4>{{ $t('alarmAnalyticsView.kddb4d1', [top]) }}</h4>
          <el-table :data="kpi.top" size="small" :empty-text="$t('alarmAnalyticsView.ka3e187')" data-test="kpi-top">
            <el-table-column type="index" width="50" />
            <el-table-column prop="message" :label="$t('alarmAnalyticsView.k9f45b3')" min-width="180" show-overflow-tooltip />
            <el-table-column :label="$t('alarmAnalyticsView.k254849')" width="80"><template #default="{ row }"><el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag></template></el-table-column>
            <el-table-column prop="count" :label="$t('alarmAnalyticsView.kd4a97e')" width="70" />
            <el-table-column prop="chattering" :label="$t('alarmAnalyticsView.ka60f92')" width="70" />
          </el-table>
        </el-col>
        <el-col :span="12">
          <h4>{{ $t('alarmAnalyticsView.k4a048b') }}</h4>
          <el-table :data="kpi.operators" size="small" :empty-text="$t('alarmAnalyticsView.k8d3ef0')">
            <el-table-column prop="user" :label="$t('alarmAnalyticsView.k0c7c09')" />
            <el-table-column prop="acks" :label="$t('alarmAnalyticsView.kcfea28')" width="80" />
            <el-table-column prop="acksPerHour" :label="$t('alarmAnalyticsView.k63a1f9')" width="100" />
            <el-table-column :label="$t('alarmAnalyticsView.kde55f1')" width="100"><template #default="{ row }">{{ $t('alarmAnalyticsView.k99598b', [row.avgAckSeconds]) }}</template></el-table-column>
            <el-table-column prop="notes" :label="$t('alarmAnalyticsView.ke03614')" width="70" />
          </el-table>
        </el-col>
      </el-row>

      <el-row v-if="kpi" :gutter="16" style="margin-top: 16px">
        <el-col :span="12">
          <h4>{{ $t('alarmAnalyticsView.k6a8b7e', [standingHours]) }}</h4>
          <el-table :data="kpi.standing" size="small" :empty-text="$t('alarmAnalyticsView.k720777')">
            <el-table-column prop="message" :label="$t('alarmAnalyticsView.k9f45b3')" min-width="180" show-overflow-tooltip />
            <el-table-column :label="$t('alarmAnalyticsView.k79cc87')" width="165"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
            <el-table-column prop="hours" :label="$t('alarmAnalyticsView.k99f690')" width="80" />
            <el-table-column :label="$t('alarmAnalyticsView.kb56d9a')" width="70"><template #default="{ row }">{{ row.acked ? $t('alarmAnalyticsView.k30160a') : $t('alarmAnalyticsView.k8bf5c1') }}</template></el-table-column>
          </el-table>
        </el-col>
        <el-col :span="12">
          <h4>{{ $t('alarmAnalyticsView.k79c55f') }}</h4>
          <el-table :data="kpi.floods" size="small" :empty-text="$t('alarmAnalyticsView.kff84a4')">
            <el-table-column :label="$t('alarmAnalyticsView.k79cc87')" min-width="165"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
            <el-table-column prop="count" :label="$t('alarmAnalyticsView.ka84a76')" width="100" />
          </el-table>
        </el-col>
      </el-row>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Search } from '@element-plus/icons-vue'
import { alarmApi } from '../api'
import { errorMessage } from '../api/http'
import type { AlarmKpi } from '../types'
import { formatTime, levelLabel, levelType } from '../utils'
import { tr } from '../i18n'

const range = ref<[Date, Date] | null>([new Date(Date.now() - 24 * 3600_000), new Date()])
const top = ref(10)
const standingHours = 24
const kpi = ref<AlarmKpi | null>(null)
const loading = ref(false)

async function load(): Promise<void> {
  loading.value = true
  try {
    const [from, to] = range.value ?? [new Date(Date.now() - 24 * 3600_000), new Date()]
    kpi.value = await alarmApi.kpi({ from: from.getTime(), to: to.getTime(), top: top.value, standingHours })
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmAnalyticsView.k957323')))
  } finally {
    loading.value = false
  }
}

onMounted(load)
</script>

<style scoped>
.cards {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(170px, 1fr));
  gap: 12px;
  margin: 12px 0 16px;
}
.card {
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  padding: 12px;
}
.card .value {
  font-size: 22px;
  font-weight: 600;
}
.card .label {
  font-size: 12px;
  color: var(--el-text-color-secondary);
}
.card.bad .value {
  color: var(--el-color-danger);
}
</style>
