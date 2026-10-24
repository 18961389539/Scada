<template>
  <div class="page">
    <div class="panel" data-test="ha-panel">
      <div class="toolbar">
        <strong>主备状态</strong>
        <el-tag v-if="ha" :type="roleType" size="small" data-test="ha-role">{{ roleLabel }}</el-tag>
        <span v-if="ha?.enabled" class="tiny muted">节点 {{ ha.nodeId }} · 任期 {{ ha.term }}</span>
        <span class="spacer" />
        <el-button @click="refresh()">刷新</el-button>
        <el-button v-if="ha?.enabled && ha.role === 'Primary'" type="warning" data-test="ha-switchover" @click="act('switchover')">切换到备机</el-button>
        <el-button v-if="ha?.enabled && ha.role === 'Standby'" type="danger" data-test="ha-promote" @click="act('promote')">在本机接管</el-button>
      </div>
      <el-alert v-if="ha && !ha.enabled" type="info" :closable="false" show-icon data-test="ha-standalone"
        title="单机部署：未启用主备。启用方法与仲裁、复制的说明见 docs/high-availability.md。" />
      <template v-if="ha?.enabled">
        <el-descriptions :column="3" size="small" border>
          <el-descriptions-item label="对端">{{ ha.peer ? `${ha.peer.nodeId}（${roleText(ha.peer.role)}，任期 ${ha.peer.term}）` : '不可达' }}</el-descriptions-item>
          <el-descriptions-item label="对端地址">{{ ha.peerUrl }}</el-descriptions-item>
          <el-descriptions-item label="最近心跳">{{ ha.peerSeenTs ? formatTime(ha.peerSeenTs) : '-' }}</el-descriptions-item>
          <el-descriptions-item label="仲裁">{{ ha.witness ?? '无（仅手动切换）' }}</el-descriptions-item>
          <el-descriptions-item label="租约">{{ ha.lease ? `${ha.lease.holder} · 任期 ${ha.lease.term} · 至 ${formatTime(ha.lease.expiresTs)}${ha.lease.handoverTo ? ` · 交接给 ${ha.lease.handoverTo}` : ''}` : '-' }}</el-descriptions-item>
          <el-descriptions-item label="自动切换">{{ ha.autoFailover ? '开启' : '关闭' }}</el-descriptions-item>
          <el-descriptions-item label="配置复制">{{ ha.configSyncTs ? formatTime(ha.configSyncTs) : '-' }}（已应用 {{ ha.configApplied }} 次）</el-descriptions-item>
          <el-descriptions-item label="历史复制">{{ ha.historySyncTs ? formatTime(ha.historySyncTs) : '-' }}，落后 {{ ha.historyRowsBehind }} 行</el-descriptions-item>
          <el-descriptions-item label="切换次数">{{ ha.switchovers }}</el-descriptions-item>
        </el-descriptions>
        <el-alert v-if="ha.witnessError" type="error" :closable="false" :title="`仲裁不可用：${ha.witnessError}`" style="margin-top: 8px" />
        <el-alert v-if="ha.replicationError" type="warning" :closable="false" :title="`复制失败：${ha.replicationError}`" style="margin-top: 8px" />
        <el-table :data="ha.events" size="small" style="margin-top: 8px" max-height="240" empty-text="暂无事件" data-test="ha-events">
          <el-table-column label="时间" width="170"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
          <el-table-column prop="kind" label="类型" width="100" />
          <el-table-column prop="message" label="内容" min-width="300" />
          <el-table-column prop="term" label="任期" width="70" />
          <el-table-column label="操作人" width="120"><template #default="{ row }">{{ row.actor ?? 'system' }}</template></el-table-column>
        </el-table>
      </template>
    </div>

    <div class="panel" data-test="backup-panel">
      <div class="toolbar">
        <strong>备份与恢复演练</strong>
        <span class="tiny muted">异地目标：{{ targets.length ? targets.map(t => `${t.name}（${t.kind}，保留 ${t.keepLatest}）`).join('、') : '未配置' }}</span>
        <span class="spacer" />
        <el-button type="primary" :loading="running" data-test="backup-run" @click="runBackup()">立即备份并演练</el-button>
      </div>
      <el-table :data="runs" size="small" empty-text="暂无备份记录" data-test="backup-runs">
        <el-table-column label="时间" width="170"><template #default="{ row }">{{ formatTime(Date.parse(row.finishedAt)) }}</template></el-table-column>
        <el-table-column prop="trigger" label="触发" width="70" />
        <el-table-column label="备份包" min-width="240"><template #default="{ row }">{{ row.fileName ?? '—' }} <span class="tiny muted">{{ row.bytes ? formatBytes(row.bytes) : '' }}</span></template></el-table-column>
        <el-table-column label="恢复演练" width="130"><template #default="{ row }">
          <el-tag v-if="row.drill" size="small" :type="row.drill.ok ? 'success' : 'danger'">{{ row.drill.ok ? `通过 ${row.drill.durationMs} ms` : '失败' }}</el-tag><span v-else>未演练</span>
        </template></el-table-column>
        <el-table-column label="异地" min-width="200"><template #default="{ row }">
          <span v-if="!row.targets.length" class="muted">—</span>
          <el-tag v-for="t in row.targets" :key="t.target" size="small" :type="t.ok ? 'success' : 'danger'" style="margin-right: 4px" :title="t.error ?? ''">{{ t.target }}</el-tag>
        </template></el-table-column>
        <el-table-column label="说明" min-width="220"><template #default="{ row }"><span class="tiny">{{ problems(row) }}</span></template></el-table-column>
      </el-table>
      <div class="toolbar" style="margin-top: 8px"><strong class="tiny">本地备份包</strong></div>
      <el-table :data="backups" size="small" max-height="240" empty-text="暂无备份包">
        <el-table-column prop="fileName" label="文件" min-width="260" />
        <el-table-column label="大小" width="110"><template #default="{ row }">{{ formatBytes(row.bytes) }}</template></el-table-column>
        <el-table-column label="操作" width="120"><template #default="{ row }"><el-button size="small" @click="drill(row.fileName)">恢复演练</el-button></template></el-table-column>
      </el-table>
      <p class="tiny muted">监控：Prometheus 抓取 <code>/metrics</code>（配置 <code>Scada:MetricsToken</code> 后以 Bearer 令牌访问），指标说明见 docs/operations.md。</p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { opsApi } from '../api/ops'
import type { BackupRun, BackupSummary, HaStatus } from '../api/ops'
import { errorMessage } from '../api/http'
import { formatBytes, formatTime } from '../utils'

const ha = ref<HaStatus | null>(null)
const runs = ref<BackupRun[]>([])
const backups = ref<BackupSummary[]>([])
const targets = ref<{ name: string; kind: string; keepLatest: number }[]>([])
const running = ref(false)
let timer: number | undefined

const roleText = (role: string) => ({ Primary: '主机', Standby: '备机', Standalone: '单机' } as Record<string, string>)[role] ?? role
const roleLabel = computed(() => (ha.value ? roleText(ha.value.role) : ''))
const roleType = computed(() => (ha.value?.role === 'Standby' ? 'warning' : 'success'))
const problems = (run: BackupRun) => [run.error, ...(run.drill?.problems ?? []), ...run.targets.filter(t => !t.ok).map(t => `${t.target}：${t.error}`)].filter(Boolean).join('；')

async function refresh(): Promise<void> {
  try {
    const [s, r, b, t] = await Promise.all([opsApi.status(), opsApi.runs(), opsApi.backups(), opsApi.targets()])
    ha.value = s; runs.value = r; backups.value = b; targets.value = t
  } catch (e) { ElMessage.error(errorMessage(e)) }
}

async function act(kind: 'switchover' | 'promote'): Promise<void> {
  const title = kind === 'switchover' ? '切换到备机' : '在本机接管'
  let reason: string
  try {
    const result = await ElMessageBox.prompt(kind === 'switchover'
      ? '本机将停止采集与控制，备机完成最终同步后接管。请填写原因（写入审计）。'
      : '若主机在线将转交主机执行正常切换；主机不在线时需仲裁确认其租约已过期。请填写原因（写入审计）。', title,
      { inputValidator: (v: string) => (!!v && v.trim().length > 0 && v.length <= 200) || '请填写原因', confirmButtonText: '确定', cancelButtonText: '取消' })
    reason = result.value.trim()
  } catch { return }
  try {
    if (kind === 'switchover') await opsApi.switchover(reason)
    else {
      try { await opsApi.promote(reason, false) }
      catch (e) {
        if (ha.value?.witness) throw e
        await ElMessageBox.confirm(`${errorMessage(e)}。确认原主机已停机后可强制接管，否则可能出现双主。`, '强制接管', { type: 'warning', confirmButtonText: '强制接管', cancelButtonText: '取消' })
        await opsApi.promote(reason, true)
      }
    }
    ElMessage.success('已完成')
  } catch (e) { if (e !== 'cancel') ElMessage.error(errorMessage(e)) }
  await refresh()
}

async function runBackup(): Promise<void> {
  running.value = true
  try {
    const run = await opsApi.run()
    if (run.ok) ElMessage.success(`备份 ${run.fileName} 已完成并通过恢复演练`)
    else ElMessage.warning(`备份完成但有问题：${problems(run)}`)
  } catch (e) { ElMessage.error(errorMessage(e)) }
  finally { running.value = false; await refresh() }
}

async function drill(fileName: string): Promise<void> {
  try {
    const result = await opsApi.drill(fileName)
    if (result.ok) ElMessage.success(`恢复演练通过（${result.durationMs} ms，测点 ${result.tags}）`)
    else ElMessage.error(`恢复演练失败：${result.problems.join('；')}`)
  } catch (e) { ElMessage.error(errorMessage(e)) }
}

onMounted(() => { void refresh(); timer = window.setInterval(() => { void opsApi.status().then(s => { ha.value = s }).catch(() => {}) }, 5000) })
onBeforeUnmount(() => window.clearInterval(timer))
</script>
