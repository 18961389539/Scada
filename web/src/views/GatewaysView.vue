<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-input v-model="keyword" :placeholder="$t('gatewaysView.kef44db')" clearable style="width: 200px" />
        <span class="spacer" />
        <span class="tiny muted" data-test="last-read">{{ lastRead ? `${$t('gatewaysView.k7661d9', [formatTime(lastRead)])} · ${lastReadAgo}` : $t('gatewaysView.kb6b13c') }}</span>
        <el-switch v-model="autoRefresh" :active-text="$t('gatewaysView.kc2d817')" />
        <el-button :loading="loading" :disabled="busy" @click="refresh()">{{ $t('gatewaysView.kacce65') }}</el-button>
        <el-button type="primary" :disabled="busy || loading" @click="openCreate">{{ $t('gatewaysView.k7a077a') }}</el-button>
      </div>
      <el-alert v-if="readError" :title="readError" type="error" :closable="false" show-icon data-test="gateway-read-error" />
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="gateway-action-error" />
      <el-alert v-else-if="stale" :title="$t('gatewaysView.k3ffa86')" type="warning" :closable="false" />
      <el-table :data="filtered" v-loading="loading" :empty-text="readError && !filtered.length ? readError : $t('gatewaysView.kb32145')" row-key="id">
        <el-table-column :label="$t('gatewaysView.k86ab11')" min-width="200"><template #default="{ row }"><strong>#{{ row.id }} · {{ row.name }}</strong><div class="tiny muted">{{ row.agentVersion ? $t('gatewaysView.k719151', [row.agentVersion]) : $t('gatewaysView.k6d9feb') }}</div></template></el-table-column>
        <el-table-column :label="$t('gatewaysView.k7b19fd')" width="100"><template #default="{ row }"><el-tag :type="connectionType(row)">{{ connectionLabel(row) }}</el-tag></template></el-table-column>
        <el-table-column :label="$t('gatewaysView.kfe6728')" min-width="150"><template #default="{ row }"><el-tag :type="confirmed(row) ? 'success' : 'info'">{{ configurationLabel(row) }}</el-tag></template></el-table-column>
        <el-table-column :label="$t('gatewaysView.k01f2c1')" width="105"><template #default="{ row }">{{ $t('gatewaysView.k1be22b', [row.deviceCount]) }}<div class="tiny muted">{{ $t('gatewaysView.k573a02', [row.enabledDeviceCount]) }}</div></template></el-table-column>
        <el-table-column :label="$t('gatewaysView.k534682')" width="175"><template #default="{ row }">{{ time(row.lastSeenTs) }}</template></el-table-column>
        <el-table-column :label="$t('gatewaysView.k52edc8')" min-width="155"><template #default="{ row }">{{ row.pendingBatches < 0 ? $t('gatewaysView.kaf807e') : $t('gatewaysView.k6f5d5d', [row.pendingBatches, bytes(row.pendingBytes)]) }}<div class="tiny muted">{{ $t('gatewaysView.k13f190', [row.inboxBatches, bytes(row.inboxBytes)]) }}</div></template></el-table-column>
        <el-table-column :label="$t('gatewaysView.kf3ea6d')" width="205" fixed="right"><template #default="{ row }">
          <el-button link type="primary" :disabled="busy" @click="openDetails(row.id)">{{ $t('gatewaysView.k4f55ee') }}</el-button>
          <el-button link type="primary" :disabled="busy" @click="openEdit(row)">{{ $t('gatewaysView.ka7f814') }}</el-button>
          <el-dropdown :disabled="busy" @command="(command: unknown) => action(row, command)">
            <el-button link type="primary" :disabled="busy" :aria-label="$t('gatewaysView.k72790b')">{{ $t('gatewaysView.k1fbf96') }}</el-button>
            <template #dropdown><el-dropdown-menu>
              <el-dropdown-item command="toggle">{{ row.enabled ? $t('gatewaysView.kd5060b') : $t('gatewaysView.ke5b2f6') }}</el-dropdown-item>
              <el-dropdown-item command="rotate">{{ $t('gatewaysView.kcb02fd') }}</el-dropdown-item>
              <!-- 释放会话是危险操作，与启停/轮换分隔并标红，避免顺手点到 -->
              <el-dropdown-item divided command="release"><span class="danger-item">{{ $t('gatewaysView.kc14c3b') }}</span></el-dropdown-item>
            </el-dropdown-menu></template>
          </el-dropdown>
        </template></el-table-column>
      </el-table>
      <p class="tiny muted">{{ $t('gatewaysView.k609c2c') }}</p>
    </div>

    <el-dialog v-model="editVisible" :title="editing ? $t('gatewaysView.k234945') : $t('gatewaysView.k7a077a')" width="520px" :before-close="editDialog.beforeClose" :close-on-click-modal="false" :close-on-press-escape="!busy" :show-close="!busy" @keydown="editDialog.onKey">
      <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" show-icon data-test="gateway-save-error" />
      <el-form label-width="80px" @submit.prevent="saveName"><el-form-item :label="$t('gatewaysView.k2d6d7a')" required><el-input v-model="name" maxlength="100" show-word-limit :aria-label="$t('gatewaysView.k2d6d7a')" data-test="gateway-name" /></el-form-item></el-form>
      <template #footer><el-button :disabled="busy" @click="editDialog.close()">{{ $t('gatewaysView.k4d0b46') }}</el-button><el-button type="primary" :loading="busy" @click="saveName">{{ editing ? $t('gatewaysView.k056421') : $t('gatewaysView.kddf217') }}</el-button></template>
    </el-dialog>

    <el-dialog v-if="secret" :model-value="true" :title="$t('gatewaysView.k0c8c67')" width="650px" :close-on-click-modal="false" :close-on-press-escape="false" @close="secret = null">
      <el-alert :title="$t('gatewaysView.k04227b')" type="warning" :closable="false" />
      <el-descriptions :column="1" border><el-descriptions-item :label="$t('gatewaysView.k86ab11')">#{{ secret.id }} · {{ secret.name }}</el-descriptions-item><el-descriptions-item :label="$t('gatewaysView.kca2705')">{{ centreUrl }}</el-descriptions-item></el-descriptions>
      <el-input :model-value="secret.token" type="password" show-password readonly :aria-label="$t('gatewaysView.k0c2956')" style="margin-top: 15px" />
      <el-alert v-if="copyError" :title="copyError" type="error" :closable="false" show-icon data-test="gateway-copy-error" style="margin-top: 8px" />
      <p class="tiny muted">{{ $t('gatewaysView.ka778ff') }}</p>
      <template #footer><el-button @click="copyKey">{{ $t('gatewaysView.kc00294') }}</el-button><el-button @click="downloadKey">{{ $t('gatewaysView.k25fdeb') }}</el-button><el-button type="primary" @click="secret = null">{{ $t('gatewaysView.ka233cb') }}</el-button></template>
    </el-dialog>

    <el-dialog v-model="releaseVisible" :title="$t('gatewaysView.k4178b8')" width="620px" :close-on-click-modal="false">
      <el-alert type="warning" :closable="false" :title="$t('gatewaysView.k024744')" />
      <p>{{ $t('gatewaysView.kbbc5dc', [releaseTarget?.id, releaseTarget?.name]) }}</p>
      <el-checkbox v-model="stopped">{{ $t('gatewaysView.k3d5521') }}</el-checkbox>
      <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" />
      <template #footer><el-button :disabled="busy" @click="releaseVisible = false">{{ $t('gatewaysView.k4d0b46') }}</el-button><el-button type="danger" :disabled="!stopped" :loading="busy" @click="release">{{ $t('gatewaysView.k801a22') }}</el-button></template>
    </el-dialog>

    <el-dialog v-if="detail" v-model="detailsVisible" :title="$t('gatewaysView.kc3a1fc', [detail.id, detail.name])" width="1080px" top="4vh" @closed="closeDetails">
      <el-alert v-if="stale" :title="$t('gatewaysView.kaaa1d1')" type="warning" :closable="false" />
      <el-descriptions :column="3" border>
        <el-descriptions-item :label="$t('gatewaysView.k7b19fd')">{{ connectionLabel(detail) }}</el-descriptions-item><el-descriptions-item :label="$t('gatewaysView.k534682')">{{ time(detail.lastSeenTs) }}</el-descriptions-item><el-descriptions-item :label="$t('gatewaysView.k74be69')">{{ time(detail.leaseUntilTs) }}</el-descriptions-item>
        <el-descriptions-item :label="$t('gatewaysView.kaea9d8')">{{ detail.agentVersion ?? $t('gatewaysView.kaf807e') }}</el-descriptions-item><el-descriptions-item :label="$t('gatewaysView.k8b977d')">{{ configurationLabel(detail) }}</el-descriptions-item><el-descriptions-item :label="$t('gatewaysView.k8841e7')">{{ detail.capabilities.includes('engineering-v1') ? $t('gatewaysView.ka9aab1') : detail.agentVersion ? $t('gatewaysView.k5b82c5') : $t('gatewaysView.kaf807e') }}</el-descriptions-item>
        <el-descriptions-item :label="$t('gatewaysView.k0ba548')">{{ detail.pendingBatches < 0 ? $t('gatewaysView.kaf807e') : $t('gatewaysView.k7b4e1f', [detail.pendingBatches, bytes(detail.pendingBytes)]) }}</el-descriptions-item><el-descriptions-item :label="$t('gatewaysView.k9bab88')">{{ time(detail.oldestPendingTs) }}</el-descriptions-item><el-descriptions-item :label="$t('gatewaysView.kf1aa52')">{{ $t('gatewaysView.k7b4e1f', [detail.inboxBatches, bytes(detail.inboxBytes)]) }}</el-descriptions-item>
        <el-descriptions-item :label="$t('gatewaysView.k5bfdb5')" :span="3"><code class="digest">{{ detail.currentRevision }}</code></el-descriptions-item>
        <el-descriptions-item :label="$t('gatewaysView.kdb2092')" :span="3"><code class="digest">{{ detail.appliedRevision ?? $t('gatewaysView.k98832f') }}</code></el-descriptions-item>
      </el-descriptions>
      <el-alert v-if="detail.reportedError" :title="$t('gatewaysView.kf9a96b', [detail.reportedError])" type="warning" :closable="false" />
      <el-alert v-if="detail.inboxError" :title="$t('gatewaysView.k303566', [detail.inboxError])" type="error" :closable="false" />
      <div class="toolbar" style="margin-top: 18px"><strong>{{ $t('gatewaysView.k571870') }}</strong><el-input v-model="deviceKeyword" size="small" clearable :placeholder="$t('gatewaysView.deviceFilter')" style="width: 180px; margin-left: 8px" data-test="gateway-device-filter" /><span class="spacer" /><el-button :disabled="busy" @click="router.push({ path: '/config/devices', query: { gatewayId: String(detail.id) } })">{{ $t('gatewaysView.k5ee80f') }}</el-button></div>
      <el-table :data="filteredAssigned" :empty-text="$t('gatewaysView.k77f270')" max-height="320" data-test="gateway-devices">
        <el-table-column prop="name" :label="$t('gatewaysView.k01f2c1')" min-width="180" /><el-table-column :label="$t('gatewaysView.kb0c431')" min-width="150" show-overflow-tooltip><template #default="{ row }">{{ protocolLabel(row.protocol) }}</template></el-table-column><el-table-column prop="tagCount" :label="$t('gatewaysView.k8cff1c')" width="75" />
        <el-table-column :label="$t('gatewaysView.k62e951')" width="120"><template #default="{ row }"><el-tag :type="row.enabled && row.online && !stale ? 'success' : 'info'">{{ !row.enabled ? $t('gatewaysView.kd989e5') : stale ? $t('gatewaysView.k98f3fb') : row.online ? $t('gatewaysView.k0373ff') : $t('gatewaysView.k6fbfd1') }}</el-tag></template></el-table-column>
        <el-table-column prop="error" :label="$t('gatewaysView.k85a349')" min-width="150" show-overflow-tooltip />
        <el-table-column :label="$t('gatewaysView.kf3ea6d')" width="140"><template #default="{ row }"><el-button link type="primary" :disabled="busy" @click="assignmentId = row.id; assignmentVisible = true">{{ $t('gatewaysView.k5ad09b') }}</el-button></template></el-table-column>
      </el-table>
      <template #footer><el-button :disabled="busy" @click="refresh()">{{ $t('gatewaysView.k00d6e6') }}</el-button><el-button @click="detailsVisible = false">{{ $t('gatewaysView.k6c14bd') }}</el-button></template>
    </el-dialog>
    <GatewayAssignmentDialog v-if="assignmentId" v-model="assignmentVisible" :device-id="assignmentId" @changed="refresh()" />
  </div>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { gatewayApi, type GatewayInfo, type GatewayDevice } from '../api/gateways'
import { errorMessage } from '../api/http'
import { downloadBlob, formatTime } from '../utils'
import GatewayAssignmentDialog from '../components/GatewayAssignmentDialog.vue'
import { usePolling } from '../composables/usePolling'
import { useNowTicker } from '../composables/useNowTicker'
import { useEditDialog } from '../composables/useEditDialog'
import { protocolLabel } from '../protocols'
import { tr } from '../i18n'
const route = useRoute(), router = useRouter(), centreUrl = `${location.origin}/`
const rows = ref<GatewayInfo[]>([]), assigned = ref<GatewayDevice[]>([]), keyword = ref(''), autoRefresh = ref(true)
const loading = ref(false), busy = ref(false), readError = ref(''), actionError = ref(''), copyError = ref(''), mutationError = ref(''), lastRead = ref(0)
const { now: tick } = useNowTicker()
const editVisible = ref(false), editing = ref<GatewayInfo | null>(null), name = ref('')
const secret = ref<{ id: number; name: string; token: string } | null>(null)
const releaseVisible = ref(false), releaseTarget = ref<GatewayInfo | null>(null), stopped = ref(false)
const detailsVisible = ref(false), detailId = ref<number | null>(null), assignmentId = ref<number | null>(null), assignmentVisible = ref(false)
const detail = computed(() => rows.value.find(x => x.id === detailId.value) ?? null)
const filtered = computed(() => rows.value.filter(x => `${x.id} ${x.name}`.toLocaleLowerCase().includes(keyword.value.trim().toLocaleLowerCase())))
let alive = true, generation = 0, controller: AbortController | undefined, refreshedAt = 0, serverAtRead = 0
const stale = computed(() => !lastRead.value || !!readError.value || tick.value - refreshedAt > 10000)

// 绝对时间用来对照日志，相对时间用来判断数据是否新鲜（tick 每秒推进）
const lastReadAgo = computed(() => {
  if (!lastRead.value) return ''
  const seconds = Math.floor(Math.max(0, (tick.value - refreshedAt) / 1000))
  if (seconds < 60) return tr('gatewaysView.agoSec', [seconds])
  const minutes = Math.floor(seconds / 60)
  return minutes < 60 ? tr('gatewaysView.agoMin', [minutes]) : tr('gatewaysView.agoHour', [Math.floor(minutes / 60)])
})

// 详情里的设备清单可能很长，加个就地筛选
const deviceKeyword = ref('')
const filteredAssigned = computed(() => {
  const kw = deviceKeyword.value.trim().toLocaleLowerCase()
  if (!kw) return assigned.value
  return assigned.value.filter((d) => `${d.name} ${d.protocol} ${protocolLabel(d.protocol)}`.toLocaleLowerCase().includes(kw))
})
const estimatedNow = () => serverAtRead + Math.max(0, tick.value - refreshedAt)
const connected = (gateway: GatewayInfo) => !stale.value && gateway.enabled && gateway.connected && gateway.leaseUntilTs > estimatedNow()
const confirmed = (gateway: GatewayInfo) => connected(gateway) && gateway.appliedRevision === gateway.currentRevision
function connectionLabel(gateway: GatewayInfo): string { return !gateway.enabled ? tr('gatewaysView.kd989e5') : stale.value ? tr('gatewaysView.k98f3fb') : connected(gateway) ? tr('gatewaysView.k65fe35') : tr('gatewaysView.kf2f3e9') }
function connectionType(gateway: GatewayInfo): 'info' | 'success' | 'warning' { return !gateway.enabled || stale.value ? 'info' : connected(gateway) ? 'success' : 'warning' }
function configurationLabel(gateway: GatewayInfo): string { return stale.value ? tr('gatewaysView.k98f3fb') : confirmed(gateway) ? tr('gatewaysView.k5d9a97') : !gateway.appliedRevision ? tr('gatewaysView.k98832f') : gateway.appliedRevision !== gateway.currentRevision ? tr('gatewaysView.k8f3128') : tr('gatewaysView.k4724f4') }
function time(ts?: number | null): string { return ts && ts > 0 ? formatTime(ts) : tr('gatewaysView.kaf807e') }
function bytes(value: number): string { return value < 1024 ? `${value} B` : value < 1048576 ? `${(value / 1024).toFixed(1)} KiB` : `${(value / 1048576).toFixed(1)} MiB` }
async function refresh(force = false, background = false): Promise<void> {
  if (!alive || busy.value && !force) return
  const current = ++generation; controller?.abort(); const request = controller = new AbortController(); if (!background) loading.value = true; const started = performance.now(), selected = detailId.value
  try {
    const [list, devices] = await Promise.all([gatewayApi.list(request.signal), selected ? gatewayApi.devices(selected, request.signal) : Promise.resolve([])])
    if (!alive || current !== generation) return
    rows.value = list; assigned.value = devices; readError.value = ''; lastRead.value = Date.now(); refreshedAt = Date.now()
    serverAtRead = (list[0]?.serverTs ?? Date.now()) + Math.max(0, refreshedAt - started)
  } catch (e) { if (alive && current === generation && !request.signal.aborted) readError.value = errorMessage(e, tr('gatewaysView.k88ab6a')) }
  finally { if (alive && current === generation && !background) loading.value = false }
}
function begin(): void { busy.value = true; generation++; controller?.abort(); loading.value = false; mutationError.value = '' }
function openCreate(): void { editing.value = null; name.value = ''; mutationError.value = ''; editVisible.value = true }
function openEdit(gateway: GatewayInfo): void { editing.value = { ...gateway }; name.value = gateway.name; mutationError.value = ''; editVisible.value = true }
function uncertain(e: unknown): string { return tr('gatewaysView.kc70c7d', [errorMessage(e)]) }
// 关闭网关编辑弹窗时保留未保存的输入：新建中断或误点遮罩不再直接丢数据。
const editDialog = useEditDialog({
  visible: editVisible,
  saving: busy,
  snapshot: () => JSON.stringify({ id: editing.value?.id ?? null, name: name.value }),
  save: saveName
})
function validKey(id: number, token: string): void { if (!Number.isInteger(id) || id < 1 || typeof token !== 'string' || !/^[A-Za-z0-9_-]{40,256}$/.test(token)) throw new Error(tr('gatewaysView.k536f6c')) }
async function saveName(): Promise<void> {
  const value = name.value.trim(); if (!value || value.length > 100 || busy.value) { if (!busy.value) mutationError.value = tr('gatewaysView.k00c4a5'); return }
  const original = editing.value; begin()
  try {
    if (original) await gatewayApi.update(original, value, original.enabled)
    else { const created = await gatewayApi.create(value); validKey(created.id, created.token); if (alive) { copyError.value = ''; secret.value = { ...created, name: value } } }
    if (alive) { editVisible.value = false; ElMessage.success(original ? tr('gatewaysView.kd7a82a') : tr('gatewaysView.k65f12b')); await refresh(true) }
  } catch (e) { if (alive) { mutationError.value = uncertain(e); await refresh(true) } }
  finally { if (alive) busy.value = false }
}
async function action(gateway: GatewayInfo, command: unknown): Promise<void> {
  if (busy.value || command !== 'toggle' && command !== 'rotate' && command !== 'release') return
  const original = { ...gateway }
  if (command === 'release') { releaseTarget.value = original; stopped.value = false; mutationError.value = ''; releaseVisible.value = true; return }
  begin(); let submitted = false
  try {
    const rotate = command === 'rotate'
    await ElMessageBox.confirm(rotate ? tr('gatewaysView.k73beb2', [gateway.id, gateway.name])
      : tr('gatewaysView.k62286a', [gateway.enabled ? tr('gatewaysView.kd989e5') : tr('gatewaysView.kd4e9ca'), gateway.id, gateway.name, gateway.enabled ? tr('gatewaysView.k6ed696') : tr('gatewaysView.kdd6c0d')]),
      rotate ? tr('gatewaysView.k069cd7') : gateway.enabled ? tr('gatewaysView.kd5060b') : tr('gatewaysView.ke5b2f6'), { type: 'warning', confirmButtonText: rotate ? tr('gatewaysView.ka792d6') : tr('gatewaysView.k3161d3') })
    if (!alive) return
    submitted = true
    actionError.value = ''
    if (rotate) { const result = await gatewayApi.rotate(original); validKey(original.id, result.token); if (alive) { copyError.value = ''; secret.value = { id: original.id, name: original.name, token: result.token } } }
    else await gatewayApi.update(original, original.name, !original.enabled)
    if (alive) { actionError.value = ''; ElMessage.success(rotate ? tr('gatewaysView.k96d56b') : tr('gatewaysView.k1f10ac')); await refresh(true) }
  } catch (e) { if (e !== 'cancel' && e !== 'close' && alive) { actionError.value = submitted ? uncertain(e) : errorMessage(e, tr('gatewaysView.actionFailed')); await refresh(true) } }
  finally { if (alive) busy.value = false }
}
async function release(): Promise<void> {
  if (!stopped.value || !releaseTarget.value || busy.value) return
  const original = releaseTarget.value; begin()
  try { await gatewayApi.release(original); if (alive) { releaseVisible.value = false; ElMessage.success(tr('gatewaysView.k6dea6d')); await refresh(true) } }
  catch (e) { if (alive) { mutationError.value = uncertain(e); await refresh(true) } }
  finally { if (alive) busy.value = false }
}
function openDetails(id: number): void { void router.replace({ query: { ...route.query, gatewayId: String(id) } }) }
function closeDetails(): void { if (detailsVisible.value) return; const query = { ...route.query }; delete query.gatewayId; void router.replace({ query }) }
async function copyKey(): Promise<void> {
  if (!secret.value) return
  copyError.value = ''
  try { await navigator.clipboard.writeText(secret.value.token); ElMessage.success(tr('gatewaysView.k1f5ce4')) }
  catch { copyError.value = tr('gatewaysView.k209284') }
}
function downloadKey(): void { if (!secret.value) return; downloadBlob(new Blob([JSON.stringify({ gatewayId: secret.value.id, serverUrl: centreUrl, token: secret.value.token }, null, 2)], { type: 'application/json' }), `scada-gateway-${secret.value.id}-key.json`) }
watch(() => route.query.gatewayId, value => { const id = typeof value === 'string' && /^[1-9]\d*$/.test(value) ? Number(value) : null; detailId.value = id; detailsVisible.value = id !== null; void refresh() }, { immediate: true })
watch(() => route.fullPath, () => { secret.value = null })
// 网关状态 5s 兜底轮询；关闭自动刷新、正在加载或有写操作在途时跳过本轮。
usePolling(() => refresh(false, true), 5000, { enabled: () => autoRefresh.value && !loading.value && !busy.value })
onBeforeUnmount(() => { alive = false; generation++; controller?.abort(); secret.value = null })
</script>
<style scoped>
.digest { overflow-wrap: anywhere; font-size: 12px; user-select: all; }
.danger-item { color: var(--el-color-danger); }
</style>
