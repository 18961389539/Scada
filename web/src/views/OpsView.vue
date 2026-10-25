<template>
  <div class="page">
    <div class="panel" data-test="ha-panel">
      <div class="toolbar">
        <strong>{{ $t('opsView.k64ad17') }}</strong>
        <el-tag v-if="ha" :type="roleType" size="small" data-test="ha-role">{{ roleLabel }}</el-tag>
        <span v-if="ha?.enabled" class="tiny muted">{{ $t('opsView.kad493f', [ha.nodeId, ha.term]) }}</span>
        <span class="spacer" />
        <el-button @click="refresh()">{{ $t('opsView.k38108e') }}</el-button>
        <el-button v-if="ha?.enabled && ha.role === 'Primary'" type="warning" data-test="ha-switchover" @click="act('switchover')">{{ $t('opsView.k353b2a') }}</el-button>
        <el-button v-if="ha?.enabled && ha.role === 'Standby'" type="danger" data-test="ha-promote" @click="act('promote')">{{ $t('opsView.k10d585') }}</el-button>
      </div>
      <el-alert v-if="ha && !ha.enabled" type="info" :closable="false" show-icon data-test="ha-standalone"
        :title="$t('opsView.kb62818')" />
      <template v-if="ha?.enabled">
        <el-descriptions :column="3" size="small" border>
          <el-descriptions-item :label="$t('opsView.k58e5db')">{{ ha.peer ? $t('opsView.k5130ef', [ha.peer.nodeId, roleText(ha.peer.role), ha.peer.term]) : $t('opsView.k358801') }}</el-descriptions-item>
          <el-descriptions-item :label="$t('opsView.k66211a')">{{ ha.peerUrl }}</el-descriptions-item>
          <el-descriptions-item :label="$t('opsView.k534682')">{{ ha.peerSeenTs ? formatTime(ha.peerSeenTs) : '-' }}</el-descriptions-item>
          <el-descriptions-item :label="$t('opsView.k797c58')">{{ ha.witness ?? $t('opsView.k397eb7') }}</el-descriptions-item>
          <el-descriptions-item :label="$t('opsView.k308cac')">{{ ha.lease ? $t('opsView.kc3c163', [ha.lease.holder, ha.lease.term, formatTime(ha.lease.expiresTs), ha.lease.handoverTo ? $t('opsView.k3c2e40', [ha.lease.handoverTo]) : '']) : '-' }}</el-descriptions-item>
          <el-descriptions-item :label="$t('opsView.kc79d88')">{{ ha.autoFailover ? $t('opsView.k256783') : $t('opsView.k6c14bd') }}</el-descriptions-item>
          <el-descriptions-item :label="$t('opsView.kaafdc7')">{{ $t('opsView.kd7b6ad', [ha.configSyncTs ? formatTime(ha.configSyncTs) : '-', ha.configApplied]) }}</el-descriptions-item>
          <el-descriptions-item :label="$t('opsView.kb51418')">{{ $t('opsView.k81d109', [ha.historySyncTs ? formatTime(ha.historySyncTs) : '-', ha.historyRowsBehind]) }}</el-descriptions-item>
          <el-descriptions-item :label="$t('opsView.k5387b4')">{{ ha.switchovers }}</el-descriptions-item>
        </el-descriptions>
        <el-alert v-if="ha.witnessError" type="error" :closable="false" :title="$t('opsView.k98e6fc', [ha.witnessError])" style="margin-top: 8px" />
        <el-alert v-if="ha.replicationError" type="warning" :closable="false" :title="$t('opsView.k9f2469', [ha.replicationError])" style="margin-top: 8px" />
        <el-table :data="ha.events" size="small" style="margin-top: 8px" max-height="240" :empty-text="$t('opsView.kfad64b')" data-test="ha-events">
          <el-table-column :label="$t('opsView.k89b4aa')" width="170"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
          <el-table-column prop="kind" :label="$t('opsView.ke4e46c')" width="100" />
          <el-table-column prop="message" :label="$t('opsView.k163aec')" min-width="300" />
          <el-table-column prop="term" :label="$t('opsView.k287da7')" width="70" />
          <el-table-column :label="$t('opsView.k06858d')" width="120"><template #default="{ row }">{{ row.actor ?? 'system' }}</template></el-table-column>
        </el-table>
      </template>
    </div>

    <div class="panel" data-test="backup-panel">
      <div class="toolbar">
        <strong>{{ $t('opsView.k3199f6') }}</strong>
        <span class="tiny muted">{{ $t('opsView.ka93a8d', [targets.length ? targets.map(t => $t('opsView.k86a85f', [t.name, t.kind, t.keepLatest])).join('、') : $t('opsView.k63595e')]) }}</span>
        <span class="spacer" />
        <el-button type="primary" :loading="running" data-test="backup-run" @click="runBackup()">{{ $t('opsView.k982e93') }}</el-button>
      </div>
      <el-table :data="runs" size="small" :empty-text="$t('opsView.ke8e754')" data-test="backup-runs">
        <el-table-column :label="$t('opsView.k89b4aa')" width="170"><template #default="{ row }">{{ formatTime(Date.parse(row.finishedAt)) }}</template></el-table-column>
        <el-table-column prop="trigger" :label="$t('opsView.k27eccf')" width="70" />
        <el-table-column :label="$t('opsView.k9ce1ae')" min-width="240"><template #default="{ row }">{{ row.fileName ?? '—' }} <span class="tiny muted">{{ row.bytes ? formatBytes(row.bytes) : '' }}</span></template></el-table-column>
        <el-table-column :label="$t('opsView.k971ee3')" width="130"><template #default="{ row }">
          <el-tag v-if="row.drill" size="small" :type="row.drill.ok ? 'success' : 'danger'">{{ row.drill.ok ? $t('opsView.kcb29e9', [row.drill.durationMs]) : $t('opsView.k3e3c80') }}</el-tag><span v-else>{{ $t('opsView.kfb37bd') }}</span>
        </template></el-table-column>
        <el-table-column :label="$t('opsView.ka4168e')" min-width="200"><template #default="{ row }">
          <span v-if="!row.targets.length" class="muted">—</span>
          <el-tag v-for="t in row.targets" :key="t.target" size="small" :type="t.ok ? 'success' : 'danger'" style="margin-right: 4px" :title="t.error ?? ''">{{ t.target }}</el-tag>
        </template></el-table-column>
        <el-table-column :label="$t('opsView.k26670d')" min-width="220"><template #default="{ row }"><span class="tiny">{{ problems(row) }}</span></template></el-table-column>
      </el-table>
      <div class="toolbar" style="margin-top: 8px"><strong class="tiny">{{ $t('opsView.k82e94d') }}</strong></div>
      <el-table :data="backups" size="small" max-height="240" :empty-text="$t('opsView.k488e58')">
        <el-table-column prop="fileName" :label="$t('opsView.k49deaf')" min-width="260" />
        <el-table-column :label="$t('opsView.kfd2070')" width="110"><template #default="{ row }">{{ formatBytes(row.bytes) }}</template></el-table-column>
        <el-table-column :label="$t('opsView.kf3ea6d')" width="120"><template #default="{ row }"><el-button size="small" @click="drill(row.fileName)">{{ $t('opsView.k971ee3') }}</el-button></template></el-table-column>
      </el-table>
      <p class="tiny muted">{{ $t('opsView.k13006b') }} <code>/metrics</code>{{ $t('opsView.k84669a') }} <code>Scada:MetricsToken</code> {{ $t('opsView.k0d630f') }}</p>
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
import { tr } from '../i18n'

const ha = ref<HaStatus | null>(null)
const runs = ref<BackupRun[]>([])
const backups = ref<BackupSummary[]>([])
const targets = ref<{ name: string; kind: string; keepLatest: number }[]>([])
const running = ref(false)
let timer: number | undefined

const roleText = (role: string) => ({ Primary: tr('opsView.k2e8a0c'), Standby: tr('opsView.k77c29d'), Standalone: tr('opsView.k59ff88') } as Record<string, string>)[role] ?? role
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
  const title = kind === 'switchover' ? tr('opsView.k353b2a') : tr('opsView.k10d585')
  let reason: string
  try {
    const result = await ElMessageBox.prompt(kind === 'switchover'
      ? tr('opsView.k76648b')
      : tr('opsView.k9771fd'), title,
      { inputValidator: (v: string) => (!!v && v.trim().length > 0 && v.length <= 200) || tr('opsView.k18169c'), confirmButtonText: tr('opsView.kf526c8'), cancelButtonText: tr('opsView.k4d0b46') })
    reason = result.value.trim()
  } catch { return }
  try {
    if (kind === 'switchover') await opsApi.switchover(reason)
    else {
      try { await opsApi.promote(reason, false) }
      catch (e) {
        if (ha.value?.witness) throw e
        await ElMessageBox.confirm(tr('opsView.k3cbaa9', [errorMessage(e)]), tr('opsView.kc24622'), { type: 'warning', confirmButtonText: tr('opsView.kc24622'), cancelButtonText: tr('opsView.k4d0b46') })
        await opsApi.promote(reason, true)
      }
    }
    ElMessage.success(tr('opsView.ke99b48'))
  } catch (e) { if (e !== 'cancel') ElMessage.error(errorMessage(e)) }
  await refresh()
}

async function runBackup(): Promise<void> {
  running.value = true
  try {
    const run = await opsApi.run()
    if (run.ok) ElMessage.success(tr('opsView.k726cf0', [run.fileName]))
    else ElMessage.warning(tr('opsView.k9364ce', [problems(run)]))
  } catch (e) { ElMessage.error(errorMessage(e)) }
  finally { running.value = false; await refresh() }
}

async function drill(fileName: string): Promise<void> {
  try {
    const result = await opsApi.drill(fileName)
    if (result.ok) ElMessage.success(tr('opsView.kcc6c81', [result.durationMs, result.tags]))
    else ElMessage.error(tr('opsView.k4b10df', [result.problems.join('；')]))
  } catch (e) { ElMessage.error(errorMessage(e)) }
}

onMounted(() => { void refresh(); timer = window.setInterval(() => { void opsApi.status().then(s => { ha.value = s }).catch(() => {}) }, 5000) })
onBeforeUnmount(() => window.clearInterval(timer))
</script>
