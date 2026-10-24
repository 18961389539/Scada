<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <strong>采集网关</strong>
        <el-input v-model="keyword" placeholder="名称或编号" clearable style="width: 200px" />
        <span class="spacer" />
        <span class="tiny muted">{{ lastRead ? `最后刷新 ${formatTime(lastRead)}` : '尚未刷新' }}</span>
        <el-switch v-model="autoRefresh" active-text="每 5 秒刷新" />
        <el-button :loading="loading" :disabled="busy" @click="refresh()">刷新网关</el-button>
        <el-button type="primary" :disabled="busy || loading" @click="openCreate">新建网关</el-button>
      </div>
      <el-alert v-if="readError" :title="readError" type="error" :closable="false" />
      <el-alert v-else-if="stale" title="当前状态尚未更新，请刷新后核对连接和配置。" type="warning" :closable="false" />
      <el-table :data="filtered" v-loading="loading" empty-text="暂无匹配网关" row-key="id">
        <el-table-column label="网关" min-width="200"><template #default="{ row }"><strong>#{{ row.id }} · {{ row.name }}</strong><div class="tiny muted">{{ row.agentVersion ? `进程版本 ${row.agentVersion}` : '进程尚未上报' }}</div></template></el-table-column>
        <el-table-column label="中心连接" width="100"><template #default="{ row }"><el-tag :type="connectionType(row)">{{ connectionLabel(row) }}</el-tag></template></el-table-column>
        <el-table-column label="当前配置" min-width="150"><template #default="{ row }"><el-tag :type="confirmed(row) ? 'success' : 'info'">{{ configurationLabel(row) }}</el-tag></template></el-table-column>
        <el-table-column label="设备" width="105"><template #default="{ row }">{{ row.deviceCount }} 台<div class="tiny muted">启用 {{ row.enabledDeviceCount }} 台</div></template></el-table-column>
        <el-table-column label="最近心跳" width="175"><template #default="{ row }">{{ time(row.lastSeenTs) }}</template></el-table-column>
        <el-table-column label="待传 / 中心待处理" min-width="155"><template #default="{ row }">{{ row.pendingBatches < 0 ? '未上报' : `${row.pendingBatches} 批 · ${bytes(row.pendingBytes)}` }}<div class="tiny muted">中心 {{ row.inboxBatches }} 批 · {{ bytes(row.inboxBytes) }}</div></template></el-table-column>
        <el-table-column label="操作" width="205" fixed="right"><template #default="{ row }">
          <el-button link type="primary" :disabled="busy" @click="openDetails(row.id)">详情</el-button>
          <el-button link type="primary" :disabled="busy" @click="openEdit(row)">编辑</el-button>
          <el-dropdown :disabled="busy" @command="(command: unknown) => action(row, command)">
            <el-button link type="primary" :disabled="busy" aria-label="更多网关操作">操作 ▾</el-button>
            <template #dropdown><el-dropdown-menu>
              <el-dropdown-item command="toggle">{{ row.enabled ? '停用网关' : '启用网关' }}</el-dropdown-item>
              <el-dropdown-item command="rotate">轮换密钥</el-dropdown-item>
              <el-dropdown-item command="release">释放会话</el-dropdown-item>
            </el-dropdown-menu></template>
          </el-dropdown>
        </template></el-table-column>
      </el-table>
      <p class="tiny muted">中心连接表示会话租约有效；设备通信健康请在详情中核对。待传积压来自最近心跳，离线后保留最后上报值。</p>
    </div>

    <el-dialog v-model="editVisible" :title="editing ? '编辑网关' : '新建网关'" width="520px" :close-on-click-modal="false">
      <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" />
      <el-form label-width="80px" @submit.prevent="saveName"><el-form-item label="网关名称" required><el-input v-model="name" maxlength="100" show-word-limit aria-label="网关名称" /></el-form-item></el-form>
      <template #footer><el-button :disabled="busy" @click="editVisible = false">取消</el-button><el-button type="primary" :loading="busy" @click="saveName">{{ editing ? '保存名称' : '创建并生成密钥' }}</el-button></template>
    </el-dialog>

    <el-dialog v-if="secret" :model-value="true" title="保存本次网关密钥" width="650px" :close-on-click-modal="false" :close-on-press-escape="false" @close="secret = null">
      <el-alert title="密钥仅在本次显示。请安全保存，关闭后列表和详情均无法找回；遗失后需要轮换。" type="warning" :closable="false" />
      <el-descriptions :column="1" border><el-descriptions-item label="网关">#{{ secret.id }} · {{ secret.name }}</el-descriptions-item><el-descriptions-item label="中心地址">{{ centreUrl }}</el-descriptions-item></el-descriptions>
      <el-input :model-value="secret.token" type="password" show-password readonly aria-label="本次网关密钥" style="margin-top: 15px" />
      <p class="tiny muted">现场网关应使用 HTTPS 连接中心，并使用独立的数据目录。密钥记录请仅交给受信任的部署人员。</p>
      <template #footer><el-button @click="copyKey">复制密钥</el-button><el-button @click="downloadKey">下载密钥记录</el-button><el-button type="primary" @click="secret = null">关闭并清除</el-button></template>
    </el-dialog>

    <el-dialog v-model="releaseVisible" title="释放网关会话" width="620px" :close-on-click-modal="false">
      <el-alert type="warning" :closable="false" title="释放会使原会话失效，并允许新进程连接。离线的旧进程要恢复联系后才会获知撤权，中心无法替代现场确认停机。" />
      <p>目标：#{{ releaseTarget?.id }} · {{ releaseTarget?.name }}</p>
      <el-checkbox v-model="stopped">已确认旧网关进程及现场 I/O 均已停止</el-checkbox>
      <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" />
      <template #footer><el-button :disabled="busy" @click="releaseVisible = false">取消</el-button><el-button type="danger" :disabled="!stopped" :loading="busy" @click="release">释放已停止进程的会话</el-button></template>
    </el-dialog>

    <el-dialog v-if="detail" v-model="detailsVisible" :title="`网关 #${detail.id} · ${detail.name}`" width="1080px" top="4vh" @closed="closeDetails">
      <el-alert v-if="stale" title="连接与配置状态尚未更新，请先刷新核对。" type="warning" :closable="false" />
      <el-descriptions :column="3" border>
        <el-descriptions-item label="中心连接">{{ connectionLabel(detail) }}</el-descriptions-item><el-descriptions-item label="最近心跳">{{ time(detail.lastSeenTs) }}</el-descriptions-item><el-descriptions-item label="租约到期">{{ time(detail.leaseUntilTs) }}</el-descriptions-item>
        <el-descriptions-item label="进程版本">{{ detail.agentVersion ?? '未上报' }}</el-descriptions-item><el-descriptions-item label="配置确认">{{ configurationLabel(detail) }}</el-descriptions-item><el-descriptions-item label="远程工程">{{ detail.capabilities.includes('engineering-v1') ? '支持' : detail.agentVersion ? '需升级网关' : '未上报' }}</el-descriptions-item>
        <el-descriptions-item label="待传积压">{{ detail.pendingBatches < 0 ? '未上报' : `${detail.pendingBatches} 批 / ${bytes(detail.pendingBytes)}` }}</el-descriptions-item><el-descriptions-item label="最旧待传">{{ time(detail.oldestPendingTs) }}</el-descriptions-item><el-descriptions-item label="中心待处理">{{ detail.inboxBatches }} 批 / {{ bytes(detail.inboxBytes) }}</el-descriptions-item>
        <el-descriptions-item label="中心配置摘要" :span="3"><code class="digest">{{ detail.currentRevision }}</code></el-descriptions-item>
        <el-descriptions-item label="最近确认摘要" :span="3"><code class="digest">{{ detail.appliedRevision ?? '尚未确认' }}</code></el-descriptions-item>
      </el-descriptions>
      <el-alert v-if="detail.reportedError" :title="`网关上报：${detail.reportedError}`" type="warning" :closable="false" />
      <el-alert v-if="detail.inboxError" :title="`中心处理：${detail.inboxError}`" type="error" :closable="false" />
      <div class="toolbar" style="margin-top: 18px"><strong>已分配设备</strong><span class="spacer" /><el-button :disabled="busy" @click="router.push({ path: '/config/devices', query: { gatewayId: String(detail.id) } })">打开设备配置</el-button></div>
      <el-table :data="assigned" empty-text="尚未分配设备" max-height="320">
        <el-table-column prop="name" label="设备" min-width="180" /><el-table-column prop="protocol" label="协议" width="105" /><el-table-column prop="tagCount" label="测点" width="75" />
        <el-table-column label="状态" width="120"><template #default="{ row }"><el-tag :type="row.enabled && row.online && !stale ? 'success' : 'info'">{{ !row.enabled ? '停用' : stale ? '待刷新' : row.online ? '在线' : '离线 / 等待' }}</el-tag></template></el-table-column>
        <el-table-column prop="error" label="采集提示" min-width="150" show-overflow-tooltip />
        <el-table-column label="操作" width="140"><template #default="{ row }"><el-button link type="primary" :disabled="busy" @click="assignmentId = row.id; assignmentVisible = true">更改采集位置</el-button></template></el-table-column>
      </el-table>
      <template #footer><el-button :disabled="busy" @click="refresh()">刷新详情</el-button><el-button @click="detailsVisible = false">关闭</el-button></template>
    </el-dialog>
    <GatewayAssignmentDialog v-if="assignmentId" v-model="assignmentVisible" :device-id="assignmentId" @changed="refresh()" />
  </div>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { gatewayApi, type GatewayInfo, type GatewayDevice } from '../api/gateways'
import { errorMessage } from '../api/http'
import { downloadBlob, formatTime } from '../utils'
import GatewayAssignmentDialog from '../components/GatewayAssignmentDialog.vue'
const route = useRoute(), router = useRouter(), centreUrl = `${location.origin}/`
const rows = ref<GatewayInfo[]>([]), assigned = ref<GatewayDevice[]>([]), keyword = ref(''), autoRefresh = ref(true)
const loading = ref(false), busy = ref(false), readError = ref(''), mutationError = ref(''), lastRead = ref(0), tick = ref(performance.now())
const editVisible = ref(false), editing = ref<GatewayInfo | null>(null), name = ref('')
const secret = ref<{ id: number; name: string; token: string } | null>(null)
const releaseVisible = ref(false), releaseTarget = ref<GatewayInfo | null>(null), stopped = ref(false)
const detailsVisible = ref(false), detailId = ref<number | null>(null), assignmentId = ref<number | null>(null), assignmentVisible = ref(false)
const detail = computed(() => rows.value.find(x => x.id === detailId.value) ?? null)
const filtered = computed(() => rows.value.filter(x => `${x.id} ${x.name}`.toLocaleLowerCase().includes(keyword.value.trim().toLocaleLowerCase())))
let alive = true, generation = 0, controller: AbortController | undefined, refreshedAt = 0, serverAtRead = 0
let polling: number | undefined, ticker: number | undefined
const stale = computed(() => !lastRead.value || !!readError.value || tick.value - refreshedAt > 10000)
const estimatedNow = () => serverAtRead + Math.max(0, tick.value - refreshedAt)
const connected = (gateway: GatewayInfo) => !stale.value && gateway.enabled && gateway.connected && gateway.leaseUntilTs > estimatedNow()
const confirmed = (gateway: GatewayInfo) => connected(gateway) && gateway.appliedRevision === gateway.currentRevision
function connectionLabel(gateway: GatewayInfo): string { return !gateway.enabled ? '停用' : stale.value ? '待刷新' : connected(gateway) ? '已连接' : '未连接' }
function connectionType(gateway: GatewayInfo): 'info' | 'success' | 'warning' { return !gateway.enabled || stale.value ? 'info' : connected(gateway) ? 'success' : 'warning' }
function configurationLabel(gateway: GatewayInfo): string { return stale.value ? '待刷新' : confirmed(gateway) ? '已确认当前配置' : !gateway.appliedRevision ? '尚未确认' : gateway.appliedRevision !== gateway.currentRevision ? '有待应用配置' : '版本一致，连接失效' }
function time(ts?: number | null): string { return ts && ts > 0 ? formatTime(ts) : '未上报' }
function bytes(value: number): string { return value < 1024 ? `${value} B` : value < 1048576 ? `${(value / 1024).toFixed(1)} KiB` : `${(value / 1048576).toFixed(1)} MiB` }
async function refresh(force = false): Promise<void> {
  if (!alive || busy.value && !force) return
  const current = ++generation; controller?.abort(); const request = controller = new AbortController(); loading.value = true; const started = performance.now(), selected = detailId.value
  try {
    const [list, devices] = await Promise.all([gatewayApi.list(request.signal), selected ? gatewayApi.devices(selected, request.signal) : Promise.resolve([])])
    if (!alive || current !== generation) return
    rows.value = list; assigned.value = devices; readError.value = ''; lastRead.value = Date.now(); refreshedAt = performance.now(); tick.value = refreshedAt
    serverAtRead = (list[0]?.serverTs ?? Date.now()) + Math.max(0, refreshedAt - started)
  } catch (e) { if (alive && current === generation && !request.signal.aborted) readError.value = errorMessage(e, '读取网关状态失败') }
  finally { if (alive && current === generation) loading.value = false }
}
function begin(): void { busy.value = true; generation++; controller?.abort(); loading.value = false; mutationError.value = '' }
function openCreate(): void { editing.value = null; name.value = ''; mutationError.value = ''; editVisible.value = true }
function openEdit(gateway: GatewayInfo): void { editing.value = { ...gateway }; name.value = gateway.name; mutationError.value = ''; editVisible.value = true }
function uncertain(e: unknown): string { return `${errorMessage(e)}；请刷新并核对实际状态，不会自动重复提交。` }
function validKey(id: number, token: string): void { if (!Number.isInteger(id) || id < 1 || typeof token !== 'string' || !/^[A-Za-z0-9_-]{40,256}$/.test(token)) throw new Error('中心返回的网关密钥信息无效') }
async function saveName(): Promise<void> {
  const value = name.value.trim(); if (!value || value.length > 100 || busy.value) { if (!busy.value) ElMessage.warning('网关名称须为 1～100 个字符'); return }
  const original = editing.value; begin()
  try {
    if (original) await gatewayApi.update(original, value, original.enabled)
    else { const created = await gatewayApi.create(value); validKey(created.id, created.token); if (alive) secret.value = { ...created, name: value } }
    if (alive) { editVisible.value = false; ElMessage.success(original ? '名称已保存' : '网关已创建'); await refresh(true) }
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
    await ElMessageBox.confirm(rotate ? `轮换网关 #${gateway.id} ${gateway.name} 的密钥。中心立即拒绝旧密钥，需要到现场更新；离线网关恢复联系后才获知撤权。新密钥仅显示一次。`
      : `${gateway.enabled ? '停用' : '启用'}网关 #${gateway.id} ${gateway.name}。${gateway.enabled ? '中心将拒绝原身份；离线进程在恢复联系后才获知停用。' : '网关重新连接并确认配置后才能接受新控制。'}`,
      rotate ? '轮换网关密钥' : gateway.enabled ? '停用网关' : '启用网关', { type: 'warning', confirmButtonText: rotate ? '确认轮换' : '确认操作' })
    if (!alive) return
    submitted = true
    if (rotate) { const result = await gatewayApi.rotate(original); validKey(original.id, result.token); if (alive) secret.value = { id: original.id, name: original.name, token: result.token } }
    else await gatewayApi.update(original, original.name, !original.enabled)
    if (alive) { ElMessage.success(rotate ? '密钥已轮换，请保存新密钥' : '网关设置已更新'); await refresh(true) }
  } catch (e) { if (e !== 'cancel' && e !== 'close' && alive) { ElMessage.error(submitted ? uncertain(e) : errorMessage(e)); await refresh(true) } }
  finally { if (alive) busy.value = false }
}
async function release(): Promise<void> {
  if (!stopped.value || !releaseTarget.value || busy.value) return
  const original = releaseTarget.value; begin()
  try { await gatewayApi.release(original); if (alive) { releaseVisible.value = false; ElMessage.success('原会话已释放'); await refresh(true) } }
  catch (e) { if (alive) { mutationError.value = uncertain(e); await refresh(true) } }
  finally { if (alive) busy.value = false }
}
function openDetails(id: number): void { void router.replace({ query: { ...route.query, gatewayId: String(id) } }) }
function closeDetails(): void { if (detailsVisible.value) return; const query = { ...route.query }; delete query.gatewayId; void router.replace({ query }) }
async function copyKey(): Promise<void> { if (!secret.value) return; try { await navigator.clipboard.writeText(secret.value.token); ElMessage.success('密钥已复制') } catch { ElMessage.error('复制失败，请下载密钥记录后安全保存') } }
function downloadKey(): void { if (!secret.value) return; downloadBlob(new Blob([JSON.stringify({ gatewayId: secret.value.id, serverUrl: centreUrl, token: secret.value.token }, null, 2)], { type: 'application/json' }), `scada-gateway-${secret.value.id}-key.json`) }
watch(() => route.query.gatewayId, value => { const id = typeof value === 'string' && /^[1-9]\d*$/.test(value) ? Number(value) : null; detailId.value = id; detailsVisible.value = id !== null; void refresh() }, { immediate: true })
watch(() => route.fullPath, () => { secret.value = null })
onMounted(() => { polling = window.setInterval(() => { if (autoRefresh.value && !loading.value && !busy.value) void refresh() }, 5000); ticker = window.setInterval(() => { tick.value = performance.now() }, 1000) })
onBeforeUnmount(() => { alive = false; generation++; controller?.abort(); secret.value = null; if (polling) clearInterval(polling); if (ticker) clearInterval(ticker) })
</script>
<style scoped>
.digest { overflow-wrap: anywhere; font-size: 12px; user-select: all; }
</style>
