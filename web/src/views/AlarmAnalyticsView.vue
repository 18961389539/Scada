<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-date-picker v-model="range" type="datetimerange" start-placeholder="开始时间" end-placeholder="结束时间" style="width: 360px" />
        <el-select v-model="top" style="width: 120px">
          <el-option v-for="n in [5, 10, 20, 50]" :key="n" :label="`Top ${n}`" :value="n" />
        </el-select>
        <el-button type="primary" :icon="Search" :loading="loading" @click="load">统计</el-button>
        <span class="spacer" />
        <span class="tiny muted">参照 ISA-18.2 / EEMUA 191：每操作员每小时 ≤6 条为可接受，每 10 分钟 >{{ kpi?.floodThreshold ?? 10 }} 条为洪水</span>
      </div>

      <div v-if="kpi" class="cards">
        <div class="card" data-test="kpi-total"><div class="value">{{ kpi.annunciated }}</div><div class="label">报警数（不含暂挂/抑制/抖动）</div></div>
        <div class="card"><div class="value">{{ kpi.perHour }}</div><div class="label">每小时报警数</div></div>
        <div class="card" :class="{ bad: kpi.perOperatorHour > 6 }"><div class="value">{{ kpi.perOperatorHour }}</div><div class="label">每操作员每小时（{{ kpi.operatorPositions }} 人）</div></div>
        <div class="card"><div class="value">{{ kpi.peak10Min }}</div><div class="label">10 分钟峰值</div></div>
        <div class="card" :class="{ bad: kpi.percentTimeInFlood > 1 }"><div class="value">{{ kpi.percentTimeInFlood }}%</div><div class="label">洪水时间占比（{{ kpi.floodBuckets }} 个区间）</div></div>
        <div class="card"><div class="value">{{ kpi.standing.length }}</div><div class="label">长期驻留报警</div></div>
        <div class="card"><div class="value">{{ kpi.chattering }}</div><div class="label">抖动报警</div></div>
        <div class="card"><div class="value">{{ kpi.shelved }} / {{ kpi.suppressed }}</div><div class="label">暂挂 / 抑制</div></div>
      </div>

      <el-row v-if="kpi" :gutter="16">
        <el-col :span="12">
          <h4>最频繁报警 Top {{ top }}</h4>
          <el-table :data="kpi.top" size="small" empty-text="无报警" data-test="kpi-top">
            <el-table-column type="index" width="50" />
            <el-table-column prop="message" label="报警" min-width="180" show-overflow-tooltip />
            <el-table-column label="级别" width="80"><template #default="{ row }"><el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag></template></el-table-column>
            <el-table-column prop="count" label="次数" width="70" />
            <el-table-column prop="chattering" label="抖动" width="70" />
          </el-table>
        </el-col>
        <el-col :span="12">
          <h4>操作员负荷</h4>
          <el-table :data="kpi.operators" size="small" empty-text="无确认记录">
            <el-table-column prop="user" label="操作员" />
            <el-table-column prop="acks" label="确认数" width="80" />
            <el-table-column prop="acksPerHour" label="每小时确认" width="100" />
            <el-table-column label="平均响应" width="100"><template #default="{ row }">{{ row.avgAckSeconds }} 秒</template></el-table-column>
            <el-table-column prop="notes" label="备注" width="70" />
          </el-table>
        </el-col>
      </el-row>

      <el-row v-if="kpi" :gutter="16" style="margin-top: 16px">
        <el-col :span="12">
          <h4>长期驻留报警（持续 ≥ {{ standingHours }} 小时，仍活动）</h4>
          <el-table :data="kpi.standing" size="small" empty-text="无">
            <el-table-column prop="message" label="报警" min-width="180" show-overflow-tooltip />
            <el-table-column label="开始" width="165"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
            <el-table-column prop="hours" label="小时" width="80" />
            <el-table-column label="确认" width="70"><template #default="{ row }">{{ row.acked ? '是' : '否' }}</template></el-table-column>
          </el-table>
        </el-col>
        <el-col :span="12">
          <h4>洪水区间（每 10 分钟）</h4>
          <el-table :data="kpi.floods" size="small" empty-text="统计期内无洪水">
            <el-table-column label="开始" min-width="165"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
            <el-table-column prop="count" label="报警数" width="100" />
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
    ElMessage.error(errorMessage(error, '统计失败'))
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
