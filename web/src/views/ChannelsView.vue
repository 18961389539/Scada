<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <h3>{{ $t('channelsView.title') }}</h3>
        <span class="spacer" />
        <el-button :icon="Refresh" :loading="loading" data-test="channel-refresh" @click="load">{{ $t('channelsView.refresh') }}</el-button>
        <el-button v-if="canConfigure" type="primary" :icon="Plus" :disabled="loading" data-test="channel-add" @click="openCreate">{{ $t('channelsView.add') }}</el-button>
      </div>
      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="channel-load-error" />
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="channel-action-error" />
      <p class="tiny muted">{{ $t('channelsView.hint') }}</p>
      <el-table :data="channels" v-loading="loading" row-key="id" size="small" :empty-text="$t('channelsView.empty')" data-test="channel-table">
        <el-table-column :label="$t('channelsView.name')" min-width="180" show-overflow-tooltip>
          <template #default="{ row }">
            <strong>#{{ row.id }} · {{ row.name }}</strong>
            <div v-if="row.description" class="tiny muted">{{ row.description }}</div>
          </template>
        </el-table-column>
        <el-table-column prop="protocol" :label="$t('channelsView.protocol')" width="160" show-overflow-tooltip />
        <el-table-column :label="$t('channelsView.endpoint')" min-width="220" show-overflow-tooltip>
          <template #default="{ row }"><code class="digest">{{ endpointOf(row) }}</code></template>
        </el-table-column>
        <el-table-column :label="$t('channelsView.devices')" width="90" show-overflow-tooltip>
          <template #default="{ row }">{{ row.deviceCount }}</template>
        </el-table-column>
        <el-table-column :label="$t('channelsView.enabled')" width="90" show-overflow-tooltip>
          <template #default="{ row }">
            <el-tag :class="{ 'tag-quiet': row.enabled }" :type="row.enabled ? undefined : 'info'" size="small">
              {{ row.enabled ? $t('channelsView.on') : $t('channelsView.off') }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column v-if="canConfigure" :label="$t('channelsView.actions')" width="210" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" data-test="channel-diagnostics" @click="openStats(row)">{{ $t('channelsView.diag') }}</el-button>
            <el-button link type="primary" :disabled="busy" data-test="channel-edit" @click="openEdit(row)">{{ $t('channelsView.edit') }}</el-button>
            <el-button link type="danger" :disabled="busy" data-test="channel-delete" @click="remove(row)">{{ $t('channelsView.delete') }}</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="statsVisible" :title="$t('channelsView.diagTitle')" width="840px">
      <div v-loading="statsLoading" data-test="channel-diagnostics">
        <el-alert v-if="statsError" :title="statsError" type="error" :closable="false" show-icon />
        <template v-if="stats">
          <p class="tiny muted">{{ stats.name }} · <code class="digest">{{ stats.endpoint }}</code></p>
          <el-descriptions :column="3" border size="small">
            <el-descriptions-item :label="$t('channelsView.diagConnection')">
              <el-tag :type="stats.connected ? 'success' : 'info'" size="small">
                {{ stats.connected ? $t('channelsView.diagConnected') : $t('channelsView.diagDisconnected') }}
              </el-tag>
              <span class="tiny muted"> · {{ stats.leaseCount }} / {{ stats.connections }}</span>
            </el-descriptions-item>
            <el-descriptions-item :label="$t('channelsView.diagTransactions')">{{ stats.transactions }}</el-descriptions-item>
            <el-descriptions-item :label="$t('channelsView.diagBusy')">{{ stats.busyPercent }}%</el-descriptions-item>
            <el-descriptions-item :label="$t('channelsView.diagWaitAvg')">{{ stats.waitAvgMs }} ms</el-descriptions-item>
            <el-descriptions-item :label="$t('channelsView.diagWaitMax')">{{ stats.waitMaxMs }} ms</el-descriptions-item>
            <el-descriptions-item :label="$t('channelsView.diagContended')">{{ stats.slowQueues }}</el-descriptions-item>
            <el-descriptions-item :label="$t('channelsView.diagLastError')" :span="3">
              {{ stats.lastError ?? $t('channelsView.diagNoError') }}
            </el-descriptions-item>
          </el-descriptions>
          <el-table :data="stats.members" size="small" class="member-table" :empty-text="$t('channelsView.diagEmpty')">
            <el-table-column :label="$t('channelsView.diagMember')" min-width="200" show-overflow-tooltip>
              <template #default="{ row }">
                {{ row.deviceName }} <span class="tiny muted">#{{ row.deviceId }} · {{ row.station }}</span>
              </template>
            </el-table-column>
            <el-table-column prop="transactions" :label="$t('channelsView.diagTransactions')" width="90" />
            <el-table-column prop="waitAvgMs" :label="$t('channelsView.diagWaitAvg')" width="110" />
            <el-table-column prop="waitMaxMs" :label="$t('channelsView.diagWaitMax')" width="110" />
            <el-table-column :label="$t('channelsView.diagShare')" width="110">
              <template #default="{ row }">{{ row.busySharePercent }}%</template>
            </el-table-column>
            <el-table-column prop="slowQueues" :label="$t('channelsView.diagContended')" width="100" />
            <el-table-column prop="failures" :label="$t('channelsView.diagFailures')" width="90" />
          </el-table>
          <p class="tiny muted">{{ $t('channelsView.diagHint') }}</p>
        </template>
      </div>
      <template #footer>
        <el-button :loading="statsLoading" data-test="channel-diagnostics-refresh" @click="refreshStats">{{ $t('channelsView.refresh') }}</el-button>
        <el-button @click="statsVisible = false">{{ $t('channelsView.close') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="dialogVisible" :title="editingId ? $t('channelsView.editTitle') : $t('channelsView.addTitle')" width="560px" :close-on-click-modal="false" :close-on-press-escape="!busy" :show-close="!busy">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon data-test="channel-save-error" />
      <el-form label-width="120px" :disabled="busy" @submit.prevent="save">
        <el-form-item :label="$t('channelsView.name')" required>
          <el-input v-model="form.name" maxlength="100" show-word-limit data-test="channel-name" :placeholder="$t('channelsView.namePlaceholder')" />
        </el-form-item>
        <el-form-item :label="$t('channelsView.description')">
          <el-input v-model="form.description" maxlength="500" show-word-limit data-test="channel-description" />
        </el-form-item>
        <el-form-item :label="$t('channelsView.protocol')" required>
          <el-select v-model="form.protocol" style="width: 100%" data-test="channel-protocol">
            <el-option v-for="protocol in protocols" :key="protocol" :label="protocol" :value="protocol" />
          </el-select>
        </el-form-item>
        <template v-if="usesSerial">
          <el-form-item :label="$t('channelsView.serialPort')" required><el-input v-model="form.serialPort" data-test="channel-serial" placeholder="COM3" /></el-form-item>
          <el-form-item :label="$t('channelsView.baudRate')"><el-input-number v-model="form.baudRate" :min="300" :max="921600" /></el-form-item>
          <el-form-item :label="$t('channelsView.parity')">
            <el-select v-model="form.parity">
              <el-option :label="$t('channelsView.parityNone')" value="None" />
              <el-option :label="$t('channelsView.parityOdd')" value="Odd" />
              <el-option :label="$t('channelsView.parityEven')" value="Even" />
            </el-select>
          </el-form-item>
          <el-form-item :label="$t('channelsView.stopBits')">
            <el-select v-model="form.stopBits"><el-option label="1" value="One" /><el-option label="2" value="Two" /></el-select>
          </el-form-item>
        </template>
        <template v-else>
          <el-form-item :label="$t('channelsView.host')" required><el-input v-model="form.host" data-test="channel-host" placeholder="192.168.1.10" /></el-form-item>
          <el-form-item :label="$t('channelsView.port')" required><el-input-number v-model="form.port" :min="1" :max="65535" /></el-form-item>
        </template>
        <el-form-item :label="$t('channelsView.timeout')"><el-input-number v-model="form.timeoutMs" :min="100" :max="60000" :step="500" /></el-form-item>
        <el-form-item :label="$t('channelsView.enabled')"><el-switch v-model="form.enabled" data-test="channel-enabled" /></el-form-item>
      </el-form>
      <p class="tiny muted">{{ $t('channelsView.authority') }}</p>
      <template #footer>
        <el-button :disabled="busy" @click="dialogVisible = false">{{ $t('channelsView.cancel') }}</el-button>
        <el-button type="primary" :loading="busy" data-test="channel-save" @click="save">{{ $t('channelsView.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { Plus, Refresh } from '@element-plus/icons-vue'
import { channelApi, channelProtocols, channelUsesSerial, type ChannelInfo, type ChannelInput, type ChannelStatistics } from '../api/channels'
import { useAuthStore } from '../stores/auth'
import { errorMessage } from '../api/http'
import { canGlobal } from '../access/policies'
import { tr } from '../i18n'

const auth = useAuthStore()
const canConfigure = computed(() => canGlobal(auth.user, 'Configure'))
const protocols = channelProtocols

const channels = ref<ChannelInfo[]>([])
const loading = ref(false)
const busy = ref(false)
const loadError = ref('')
const actionError = ref('')
const formError = ref('')
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)

// 总线诊断：共享链路的瓶颈是"链路占用时间"，所以看的是排队与占用，不是连接数。
const statsVisible = ref(false)
const statsLoading = ref(false)
const statsError = ref('')
const stats = ref<ChannelStatistics | null>(null)
const statsChannelId = ref<number | null>(null)

const emptyForm = (): ChannelInput => ({
  name: '', protocol: 'ModbusTcp', description: '',
  host: '127.0.0.1', port: 502, serialPort: 'COM1',
  baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 'One',
  timeoutMs: 3000, enabled: true
})
const form = reactive<ChannelInput>(emptyForm())
const usesSerial = computed(() => channelUsesSerial(form.protocol))

function endpointOf(row: ChannelInfo): string {
  return channelUsesSerial(row.protocol)
    ? `${row.serialPort} · ${row.baudRate}/${row.dataBits}/${row.parity}/${row.stopBits}`
    : `${row.host}:${row.port}`
}

async function load(): Promise<void> {
  loading.value = true
  try {
    channels.value = await channelApi.list()
    loadError.value = ''
  } catch (error) {
    loadError.value = errorMessage(error, tr('channelsView.loadFail'))
  } finally {
    loading.value = false
  }
}

function openCreate(): void {
  editingId.value = null
  Object.assign(form, emptyForm())
  formError.value = ''
  dialogVisible.value = true
}

function openEdit(row: ChannelInfo): void {
  editingId.value = row.id
  Object.assign(form, {
    name: row.name, protocol: row.protocol, description: row.description,
    host: row.host, port: row.port, serialPort: row.serialPort,
    baudRate: row.baudRate, dataBits: row.dataBits, parity: row.parity, stopBits: row.stopBits,
    timeoutMs: row.timeoutMs, enabled: row.enabled
  })
  formError.value = ''
  dialogVisible.value = true
}

async function save(): Promise<void> {
  if (busy.value) return
  if (!form.name.trim()) { formError.value = tr('channelsView.nameRequired'); return }
  busy.value = true
  formError.value = ''
  try {
    if (editingId.value) await channelApi.update(editingId.value, { ...form })
    else await channelApi.create({ ...form })
    dialogVisible.value = false
    await load()
  } catch (error) {
    formError.value = errorMessage(error, tr('channelsView.saveFail'))
  } finally {
    busy.value = false
  }
}

async function remove(row: ChannelInfo): Promise<void> {
  if (busy.value) return
  // 删除会让通道上的设备失去连接来源，必须先告知影响面再确认。
  if (row.deviceCount > 0) { actionError.value = tr('channelsView.deleteBlocked'); return }
  try {
    await ElMessageBox.confirm(tr('channelsView.deleteConfirm', [row.name]), tr('channelsView.delete'), { type: 'warning' })
  } catch { return }
  busy.value = true
  actionError.value = ''
  try {
    await channelApi.remove(row.id)
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('channelsView.deleteFail'))
  } finally {
    busy.value = false
  }
}

async function openStats(row: ChannelInfo): Promise<void> {
  statsChannelId.value = row.id
  stats.value = null
  statsError.value = ''
  statsVisible.value = true
  await refreshStats()
}

async function refreshStats(): Promise<void> {
  const id = statsChannelId.value
  if (id == null) return
  statsLoading.value = true
  try {
    stats.value = await channelApi.diagnostics(id)
    statsError.value = ''
  } catch (error) {
    statsError.value = errorMessage(error, tr('channelsView.diagLoadFail'))
  } finally {
    statsLoading.value = false
  }
}

onMounted(load)
</script>

<style scoped>
.member-table { margin-top: 12px; }
</style>
