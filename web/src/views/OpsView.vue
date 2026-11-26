<template>
  <div class="page">
    <el-alert v-if="readError" :title="readError" type="error" :closable="false" show-icon data-test="ops-load-error" />
    <el-alert v-if="statusError && statusError !== readError" :title="statusError" type="error" :closable="false" show-icon data-test="ops-status-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="ops-action-error" />
    <div class="panel" data-test="ha-panel">
      <div class="toolbar">
        <h3>{{ $t('opsView.k64ad17') }}</h3>
        <el-tag v-if="ha" :class="{ 'tag-quiet': ha.role !== 'Standby' }" :type="roleType" size="small" data-test="ha-role">{{ roleLabel }}</el-tag>
        <span v-if="ha?.enabled" class="tiny muted">{{ $t('opsView.kad493f', [ha.nodeId, ha.term]) }}</span>
        <span class="spacer" />
        <el-button @click="refresh()">{{ $t('opsView.k38108e') }}</el-button>
        <el-button v-if="ha?.enabled && ha.role === 'Primary'" type="warning" :loading="haBusy" data-test="ha-switchover" @click="act('switchover')">{{ $t('opsView.k353b2a') }}</el-button>
        <el-button v-if="ha?.enabled && ha.role === 'Standby'" type="danger" :loading="haBusy" data-test="ha-promote" @click="act('promote')">{{ $t('opsView.k10d585') }}</el-button>
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
          <el-descriptions-item :label="$t('haOps.runtimeBehind')">{{ $t('haOps.runtimeBehindValue', [ha.runtimeRowsBehind ?? 0]) }}</el-descriptions-item>
          <el-descriptions-item :label="$t('haOps.spoolBehind')">{{ $t('haOps.spoolBehindValue', [ha.peerSpoolApproxCount ?? 0, ha.peerSpoolBytes ?? 0]) }}</el-descriptions-item>
          <el-descriptions-item :label="$t('opsView.k5387b4')">{{ ha.switchovers }}</el-descriptions-item>
        </el-descriptions>
        <el-alert type="info" :closable="false" show-icon data-test="ha-peer-transport"
          :title="$t('opsView.peerTransportHint')" style="margin-top: 8px" />
        <el-alert v-if="ha.witnessError" type="error" :closable="false" :title="$t('opsView.k98e6fc', [ha.witnessError])" style="margin-top: 8px" />
        <el-alert v-if="ha.replicationError" type="warning" :closable="false" :title="$t('opsView.k9f2469', [ha.replicationError])" style="margin-top: 8px" />
        <el-alert v-for="warning in certWarnings" :key="warning.code" type="warning" :closable="false" show-icon :title="warning.message" data-test="ops-cert-warning" style="margin-top: 8px" />
        <el-table :data="ha.events" size="small" style="margin-top: 8px" max-height="240" :empty-text="readError || $t('opsView.kfad64b')" data-test="ha-events">
          <el-table-column :label="$t('opsView.k89b4aa')" width="170" show-overflow-tooltip><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
          <el-table-column :label="$t('opsView.ke4e46c')" width="100" show-overflow-tooltip><template #default="{ row }">{{ eventKind(row.kind) }}</template></el-table-column>
          <el-table-column prop="message" :label="$t('opsView.k163aec')" min-width="300" show-overflow-tooltip />
          <el-table-column prop="term" :label="$t('opsView.k287da7')" width="70" show-overflow-tooltip />
          <el-table-column :label="$t('opsView.k06858d')" width="120" show-overflow-tooltip><template #default="{ row }">{{ row.actor ?? $t('opsView.actorSystem') }}</template></el-table-column>
        </el-table>
      </template>
    </div>

    <div class="panel" data-test="desktop-clients-panel">
      <div class="toolbar">
        <h3>{{ $t('opsView.desktopClients.title') }}</h3>
        <el-tag v-if="desktop" size="small" :type="desktop.haRole === 'Standby' ? 'warning' : undefined">{{ desktop.node }} · {{ desktop.haRole }}</el-tag>
        <span v-if="desktop" class="tiny muted">{{ $t('opsView.desktopClients.onlineSummary', [desktop.online, desktop.total]) }}</span>
        <span v-if="desktop" class="tiny muted" data-test="desktop-clients-policy">{{ policyText }}</span>
        <el-tag v-if="desktop && desktop.blocked > 0" size="small" type="danger" data-test="desktop-clients-blocked">{{ $t('opsView.desktopClients.blockedSummary', [desktop.blocked]) }}</el-tag>
        <el-tag v-if="desktop && desktop.outdated > 0" size="small" type="warning" data-test="desktop-clients-outdated">{{ $t('opsView.desktopClients.outdatedSummary', [desktop.outdated]) }}</el-tag>
        <span class="spacer" />
        <el-button :loading="desktopBusy" data-test="desktop-clients-refresh" @click="refreshDesktop()">{{ $t('opsView.desktopClients.refresh') }}</el-button>
      </div>
      <el-alert v-if="desktopError" :title="desktopError" type="error" :closable="false" show-icon data-test="desktop-clients-error" />
      <el-alert v-else-if="desktop?.haRole === 'Standby'" :title="$t('opsView.desktopClients.standbyNote')" type="info" :closable="false" show-icon data-test="desktop-clients-standby" />
      <el-alert v-else-if="desktop && desktop.total > 0 && desktop.online === 0" :title="$t('opsView.desktopClients.allOffline')" type="warning" :closable="false" show-icon data-test="desktop-clients-all-offline" />
      <el-table :data="desktopRows" size="small" :empty-text="desktopError ? $t('opsView.desktopClients.loadFailed') : $t('opsView.desktopClients.empty')" data-test="desktop-clients-table">
        <el-table-column prop="machineName" :label="$t('opsView.desktopClients.machine')" min-width="130" show-overflow-tooltip />
        <el-table-column prop="windowsUser" :label="$t('opsView.desktopClients.windowsUser')" width="120" show-overflow-tooltip />
        <el-table-column :label="$t('opsView.desktopClients.account')" width="120" show-overflow-tooltip><template #default="{ row }">{{ row.userName ?? '—' }}</template></el-table-column>
        <el-table-column :label="$t('opsView.desktopClients.target')" min-width="170" show-overflow-tooltip><template #default="{ row }">{{ targetText(row) }}</template></el-table-column>
        <el-table-column prop="appVersion" :label="$t('opsView.desktopClients.version')" width="100" show-overflow-tooltip />
        <el-table-column prop="webView2Version" :label="$t('opsView.desktopClients.webview')" width="130" show-overflow-tooltip />
        <el-table-column :label="$t('opsView.desktopClients.source')" width="100"><template #default="{ row }">{{ sourceText(row.configScope) }}</template></el-table-column>
        <el-table-column :label="$t('opsView.desktopClients.status')" width="150">
          <template #default="{ row }">
            <el-tag size="small" :type="row.online ? 'success' : 'info'">{{ row.online ? $t('opsView.desktopClients.online') : $t('opsView.desktopClients.offline') }}</el-tag>
            <el-tag v-if="row.conflict" size="small" type="warning" style="margin-left: 4px">{{ $t('opsView.desktopClients.conflict') }}</el-tag>
            <el-tag v-if="row.blockedReason" size="small" type="danger" style="margin-left: 4px">{{ $t('opsView.desktopClients.blocked') }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('opsView.desktopClients.lastSeen')" width="170" show-overflow-tooltip><template #default="{ row }">{{ formatTime(row.lastSeenTs) }}</template></el-table-column>
      </el-table>
      <!-- 服务端最多返回 500 条；一次渲染全部行会让 15 秒轮询明显卡顿，分页后每页只挂 20 行。 -->
      <el-pagination
        v-if="desktopPageCount > 1"
        :current-page="Math.min(desktopPage, desktopPageCount)"
        :page-size="DESKTOP_PAGE_SIZE"
        :total="desktop?.clients.length ?? 0"
        :pager-count="5"
        layout="total, prev, pager, next"
        data-test="desktop-clients-pagination"
        @update:current-page="desktopPage = $event"
      />
      <p v-if="desktop?.truncated" class="tiny muted" data-test="desktop-clients-truncated">
        {{ $t('opsView.desktopClients.truncatedHint', [desktop.clients.length, desktop.total]) }}
      </p>
    </div>

    <div id="backup" class="panel" data-test="backup-panel">
      <el-alert type="info" show-icon :closable="false" :title="$t('opsView.fullBackupHint')" data-test="backup-kind-hint" style="margin-bottom: 8px" />
      <el-alert v-if="backupSecurity?.plaintextRisk" type="warning" show-icon :closable="false" :title="$t('opsView.plaintextRisk')" data-test="backup-plaintext-risk" style="margin-bottom: 8px" />
      <div class="toolbar">
        <h3>{{ $t('opsView.k3199f6') }}</h3>
        <span class="hint">{{ $t('opsView.ka93a8d', [targets.length ? targets.map(t => $t('opsView.k86a85f', [t.name, targetKind(t.kind), t.keepLatest])).join('、') : $t('opsView.k63595e')]) }}</span>
        <span class="spacer" />
        <el-button :loading="diagnosticsBusy" data-test="diagnostics-download" @click="downloadDiagnostics()">{{ $t('opsView.downloadDiagnostics') }}</el-button>
        <el-button type="primary" :loading="running" data-test="backup-run" @click="runBackup()">{{ $t('opsView.k982e93') }}</el-button>
      </div>
      <el-table :data="runs" size="small" :empty-text="readError && !runs.length ? readError : $t('opsView.ke8e754')" data-test="backup-runs">
        <el-table-column :label="$t('opsView.k89b4aa')" width="170" show-overflow-tooltip><template #default="{ row }">{{ formatTime(Date.parse(row.finishedAt)) }}</template></el-table-column>
        <el-table-column prop="trigger" :label="$t('opsView.k27eccf')" width="70" show-overflow-tooltip />
        <el-table-column :label="$t('opsView.k9ce1ae')" min-width="240" show-overflow-tooltip><template #default="{ row }">{{ row.fileName ?? '—' }} <span class="tiny muted">{{ row.bytes ? formatBytes(row.bytes) : '' }}</span></template></el-table-column>
        <el-table-column :label="$t('opsView.k971ee3')" width="130" show-overflow-tooltip><template #default="{ row }">
          <el-tag v-if="row.drill" size="small" :type="row.drill.ok ? 'success' : 'danger'">{{ row.drill.ok ? $t('opsView.kcb29e9', [row.drill.durationMs]) : $t('opsView.k3e3c80') }}</el-tag><span v-else>{{ $t('opsView.kfb37bd') }}</span>
        </template></el-table-column>
        <el-table-column :label="$t('opsView.ka4168e')" min-width="200" show-overflow-tooltip><template #default="{ row }">
          <span v-if="!row.targets.length" class="muted">—</span>
          <el-tag v-for="t in row.targets" :key="t.target" size="small" :type="t.ok ? 'success' : 'danger'" style="margin-right: 4px" :title="t.error ?? ''">{{ t.target }}</el-tag>
        </template></el-table-column>
        <el-table-column :label="$t('opsView.k26670d')" min-width="220" show-overflow-tooltip><template #default="{ row }"><span class="tiny">{{ problems(row) }}</span></template></el-table-column>
      </el-table>
      <div class="toolbar" style="margin-top: 8px"><strong class="tiny">{{ $t('opsView.k82e94d') }}</strong></div>
      <el-table :data="backups" size="small" max-height="320" :empty-text="readError && !backups.length ? readError : $t('opsView.k488e58')" data-test="backup-list">
        <el-table-column prop="fileName" :label="$t('opsView.k49deaf')" min-width="240" show-overflow-tooltip />
        <el-table-column :label="$t('opsView.createdAt')" width="170" show-overflow-tooltip>
          <template #default="{ row }">{{ row.createdAt ? formatTime(Date.parse(row.createdAt)) : '—' }}</template>
        </el-table-column>
        <el-table-column :label="$t('opsView.kfd2070')" width="110" show-overflow-tooltip><template #default="{ row }">{{ formatBytes(row.bytes) }}</template></el-table-column>
        <el-table-column :label="$t('opsView.encryption')" width="90">
          <template #default="{ row }">
            <el-tag size="small" :type="row.encrypted ? 'success' : 'warning'" :data-test="row.encrypted ? 'backup-encrypted' : 'backup-plaintext'">
              {{ row.encrypted ? $t('opsView.encrypted') : $t('opsView.plaintext') }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('opsView.kf3ea6d')" width="260" show-overflow-tooltip>
          <template #default="{ row }">
            <el-button size="small" :loading="drilling === row.fileName" :disabled="!!drilling && drilling !== row.fileName" data-test="backup-drill" @click="drill(row.fileName)">{{ $t('opsView.k971ee3') }}</el-button>
            <el-button size="small" :loading="verifying === row.fileName" :disabled="!!verifying && verifying !== row.fileName" data-test="backup-verify" @click="verify(row.fileName)">{{ $t('opsView.verify') }}</el-button>
            <el-button size="small" :loading="downloading === row.fileName" :disabled="!!downloading && downloading !== row.fileName" data-test="backup-download" @click="downloadBackup(row.fileName)">{{ $t('opsView.download') }}</el-button>
          </template>
        </el-table-column>
      </el-table>
      <p class="tiny muted">{{ $t('opsView.k13006b') }} <code>/metrics</code>{{ $t('opsView.k84669a') }} <code>Scada:MetricsToken</code> {{ $t('opsView.k0d630f') }}</p>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { opsApi } from '../api/ops'
import { healthApi } from '../api'
import type { Health } from '../types'
import type { BackupRun, BackupSecurity, BackupSummary, DesktopClient, DesktopClientList, HaStatus } from '../api/ops'
import { errorMessage } from '../api/http'
import { downloadBlob, formatBytes, formatTime } from '../utils'
import { usePolling } from '../composables/usePolling'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'

const auth = useAuthStore()

const ha = ref<HaStatus | null>(null)
const desktop = ref<DesktopClientList | null>(null)
const desktopBusy = ref(false)
const desktopError = ref('')
const DESKTOP_PAGE_SIZE = 20
const desktopPage = ref(1)
const desktopPageCount = computed(() => Math.max(1, Math.ceil((desktop.value?.clients.length ?? 0) / DESKTOP_PAGE_SIZE)))
const desktopRows = computed(() => {
  const rows = desktop.value?.clients ?? []
  const page = Math.min(desktopPage.value, desktopPageCount.value)
  const start = (page - 1) * DESKTOP_PAGE_SIZE
  return rows.slice(start, start + DESKTOP_PAGE_SIZE)
})
const policyText = computed(() => {
  const policy = desktop.value?.policy
  if (!policy || (!policy.minimumVersion && !policy.latestVersion)) return tr('opsView.desktopClients.policyNone')
  return tr('opsView.desktopClients.policySummary', [policy.minimumVersion ?? '—', policy.latestVersion ?? '—'])
})
const runs = ref<BackupRun[]>([])
const backups = ref<BackupSummary[]>([])
const targets = ref<{ name: string; kind: string; keepLatest: number }[]>([])
const backupSecurity = ref<BackupSecurity | null>(null)
const running = ref(false)
const verifying = ref('')
const downloading = ref('')
const diagnosticsBusy = ref(false)
// 交接要等对端同步，期间再点一次会再发一次切换/接管（备机上还可能走到强制接管）
const haBusy = ref(false)
const drilling = ref('')
const readError = ref(''), statusError = ref(''), actionError = ref('')

const roleText = (role: string) => ({ Primary: tr('opsView.k2e8a0c'), Standby: tr('opsView.k77c29d'), Standalone: tr('opsView.k59ff88') } as Record<string, string>)[role] ?? role
const eventKinds: Record<string, string> = { start: tr('opsView.eventStart'), stop: tr('opsView.eventStop'), promote: tr('opsView.eventPromote'), demote: tr('opsView.eventDemote'), handover: tr('opsView.eventHandover') }
function eventKind(kind: string): string { return eventKinds[kind] ?? kind }
function targetKind(kind: string): string {
  const key = kind.trim().toLowerCase()
  if (key === 'directory') return tr('opsView.targetDirectory')
  if (key === 's3') return tr('opsView.targetS3')
  return kind
}
const targetKinds: Record<string, string> = {
  screen: tr('opsView.desktopClients.kind.screen'),
  layout: tr('opsView.desktopClients.kind.layout'),
  workstation: tr('opsView.desktopClients.kind.workstation'),
  workspace: tr('opsView.desktopClients.kind.workspace')
}
function targetText(row: DesktopClient): string {
  const kind = row.targetKind ? targetKinds[row.targetKind] ?? row.targetKind : ''
  const name = row.targetName || (row.targetId ? `#${row.targetId}` : '')
  return [kind, name].filter(Boolean).join(' · ') || '—'
}
function sourceText(scope: string | null): string {
  if (scope === 'machine') return tr('opsView.desktopClients.sourceMachine')
  if (scope === 'user') return tr('opsView.desktopClients.sourceUser')
  return tr('opsView.desktopClients.sourceLocal')
}
async function refreshDesktop(): Promise<void> {
  desktopBusy.value = true
  try {
    desktop.value = await opsApi.desktopClients()
    desktopError.value = ''
  } catch (e) {
    desktopError.value = errorMessage(e, tr('opsView.desktopClients.loadFailed'))
  } finally {
    desktopBusy.value = false
  }
}
const roleLabel = computed(() => (ha.value ? roleText(ha.value.role) : ''))
const roleType = computed(() => (ha.value?.role === 'Standby' ? 'warning' : undefined))
const problems = (run: BackupRun) => [run.error, ...(run.drill?.problems ?? []), ...run.targets.filter(t => !t.ok).map(t => `${t.target}：${t.error}`)].filter(Boolean).join('；')

async function authorizedDownload(url: string, method: 'GET' | 'POST', fallbackName: string): Promise<string> {
  const token = auth.token
  const r = await fetch(url, { method, headers: token ? { Authorization: `Bearer ${token}` } : {} })
  if (!r.ok) {
    const body = await r.text().catch(() => '')
    let detail = body
    try { detail = (JSON.parse(body) as { message?: string }).message ?? body } catch { /* 非 JSON 就按原文 */ }
    throw new Error(detail.slice(0, 200) || tr('opsView.actionFailed'))
  }
  const blob = await r.blob()
  const disposition = r.headers.get('content-disposition') ?? ''
  const match = /filename\*?=(?:UTF-8'')?["']?([^";]+)/i.exec(disposition)
  downloadBlob(blob, match ? decodeURIComponent(match[1]) : fallbackName)
  return match ? decodeURIComponent(match[1]) : fallbackName
}

async function refresh(): Promise<void> {
  try {
    const [s, r, b, t, security, health] = await Promise.all([
      opsApi.status(), opsApi.runs(), opsApi.backups(), opsApi.targets(),
      opsApi.backupSecurity().catch(() => null),
      healthApi.get().catch(() => null)
    ])
    ha.value = s; runs.value = r; backups.value = b; targets.value = t
    backupSecurity.value = security
    certWarnings.value = (health?.warnings ?? []).filter(w =>
      w.code.startsWith('report_signing_cert') || w.code.startsWith('external_tcp_tls_cert'))
    readError.value = ''; statusError.value = ''
  } catch (e) {
    readError.value = errorMessage(e, tr('opsView.actionFailed'))
  }
}

function haLagCounts() {
  return {
    history: ha.value?.historyRowsBehind ?? 0,
    runtime: ha.value?.runtimeRowsBehind ?? 0,
    spool: ha.value?.peerSpoolApproxCount ?? 0,
    spoolBytes: ha.value?.peerSpoolBytes ?? 0
  }
}
function haIsBehind() {
  const lag = haLagCounts()
  return lag.history > 0 || lag.runtime > 0 || lag.spool > 0 || lag.spoolBytes > 0
}

async function act(kind: 'switchover' | 'promote'): Promise<void> {
  if (haBusy.value) return
  const title = kind === 'switchover' ? tr('opsView.k353b2a') : tr('opsView.k10d585')
  const lag = haLagCounts()
  const lagText = tr('haOps.lagSummary', [lag.history, lag.runtime, lag.spool])
  let reason: string
  try {
    const result = await ElMessageBox.prompt((kind === 'switchover'
      ? tr('opsView.k76648b')
      : tr('opsView.k9771fd')) + '\n' + lagText, title,
      { inputValidator: (v: string) => (!!v && v.trim().length > 0 && v.length <= 200) || tr('opsView.k18169c'), confirmButtonText: tr('opsView.kf526c8'), cancelButtonText: tr('opsView.k4d0b46') })
    reason = result.value.trim()
    if (haIsBehind()) {
      await ElMessageBox.confirm(tr('haOps.lagConfirm', [lag.history, lag.runtime, lag.spool]), tr('haOps.lagConfirmTitle'), { type: 'warning', confirmButtonText: tr('haOps.lagConfirmOk'), cancelButtonText: tr('opsView.k4d0b46') })
    }
  } catch { return }
  if (haBusy.value) return
  haBusy.value = true
  actionError.value = ''
  try {
    if (kind === 'switchover') await opsApi.switchover(reason)
    else {
      try { await opsApi.promote(reason, false) }
      catch (e) {
        if (ha.value?.witness) throw e
        const forceLag = haLagCounts()
        await ElMessageBox.confirm(tr('opsView.k3cbaa9', [errorMessage(e, tr('opsView.actionFailed'))]) + '\n' + tr('haOps.lagSummary', [forceLag.history, forceLag.runtime, forceLag.spool]), tr('opsView.kc24622'), { type: 'warning', confirmButtonText: tr('opsView.kc24622'), cancelButtonText: tr('opsView.k4d0b46') })
        await opsApi.promote(reason, true)
      }
    }
    actionError.value = ''
    ElMessage.success(tr('opsView.ke99b48'))
  } catch (e) { if (e !== 'cancel' && e !== 'close') actionError.value = errorMessage(e, tr('opsView.actionFailed')) }
  finally { haBusy.value = false }
  await refresh()
}

async function runBackup(): Promise<void> {
  running.value = true
  actionError.value = ''
  try {
    const run = await opsApi.run()
    if (run.ok) { actionError.value = ''; ElMessage.success(tr('opsView.k726cf0', [run.fileName])) }
    else actionError.value = tr('opsView.k9364ce', [problems(run)])
  } catch (e) { actionError.value = errorMessage(e, tr('opsView.actionFailed')) }
  finally { running.value = false; await refresh() }
}

async function drill(fileName: string): Promise<void> {
  if (drilling.value) return
  drilling.value = fileName
  actionError.value = ''
  try {
    const result = await opsApi.drill(fileName)
    if (result.ok) { actionError.value = ''; ElMessage.success(tr('opsView.kcc6c81', [result.durationMs, result.tags])) }
    else actionError.value = tr('opsView.k4b10df', [result.problems.join('；')])
  } catch (e) { actionError.value = errorMessage(e, tr('opsView.actionFailed')) }
  finally { drilling.value = '' }
}

async function verify(fileName: string): Promise<void> {
  if (verifying.value) return
  verifying.value = fileName
  actionError.value = ''
  try {
    const result = await opsApi.verify(fileName)
    if (result.valid) {
      actionError.value = ''
      ElMessage.success(tr('opsView.verifyOk', [fileName]))
    } else actionError.value = tr('opsView.verifyFailed', [result.problems.join('；') || tr('opsView.verifyUnknown')])
  } catch (e) { actionError.value = errorMessage(e, tr('opsView.actionFailed')) }
  finally { verifying.value = '' }
}

async function downloadBackup(fileName: string): Promise<void> {
  if (downloading.value) return
  downloading.value = fileName
  actionError.value = ''
  try {
    await authorizedDownload(opsApi.downloadUrl(fileName), 'GET', fileName)
    actionError.value = ''
  } catch (e) { actionError.value = errorMessage(e, tr('opsView.actionFailed')) }
  finally { downloading.value = '' }
}

async function downloadDiagnostics(): Promise<void> {
  if (diagnosticsBusy.value) return
  diagnosticsBusy.value = true
  actionError.value = ''
  try {
    await authorizedDownload(opsApi.diagnosticsUrl(), 'POST', `diag_${Date.now()}.zip`)
    actionError.value = ''
    ElMessage.success(tr('opsView.diagnosticsReady'))
  } catch (e) { actionError.value = errorMessage(e, tr('opsView.actionFailed')) }
  finally { diagnosticsBusy.value = false }
}

// HA 状态 5s 兜底轮询：主备切换要在页面上看得出来
// Status polling must not clear a failed backup, run, or target load.
usePolling(async () => {
  try {
    ha.value = await opsApi.status()
    statusError.value = ''
  } catch (e) {
    statusError.value = errorMessage(e, tr('opsView.statusLoadFailed'))
  }
}, 5000)
usePolling(refreshDesktop, 15000, { immediate: true })
const route = useRoute()
function scrollToBackup(): void {
  if (route.hash !== '#backup') return
  void nextTick(() => document.getElementById('backup')?.scrollIntoView({ block: 'start' }))
}
watch(() => route.hash, scrollToBackup)
onMounted(() => { void refresh(); scrollToBackup() })
</script>
