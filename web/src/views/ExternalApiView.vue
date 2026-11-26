<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <span class="hint">{{ $t('externalApiView.subtitle') }}</span>
        <span class="spacer" />
        <el-button data-test="ext-refresh" @click="load">{{ $t('externalApiView.refresh') }}</el-button>
        <el-button type="primary" data-test="ext-add" @click="openCreate">{{ $t('externalApiView.create') }}</el-button>
      </div>
      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="external-load-error" />
      <el-alert v-if="areasError" :title="areasError" type="error" :closable="false" show-icon data-test="ext-areas-error" />
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="ext-action-error" />
      <el-alert v-for="warning in certWarnings" :key="warning.code" type="warning" :closable="false" show-icon :title="warning.message" data-test="ext-cert-warning" />

      <div v-if="status" class="status-row" data-test="ext-status">
        <div class="status-item">
          <span class="status-label">{{ $t('externalApiView.rest') }}</span>
          <span class="status-value">
            <span class="status-dot" :class="status.rest?.enabled ? 'is-on' : 'is-off'" />
            {{ status.rest?.enabled ? $t('externalApiView.on') : $t('externalApiView.off') }}
          </span>
        </div>
        <div class="status-item">
          <span class="status-label">{{ $t('externalApiView.tcp') }}</span>
          <span class="status-value mono" :title="status.tcp?.enabled ? `${status.tcp.listenAddress}:${status.tcp.port}` : ''">
            <span class="status-dot" :class="status.tcp?.enabled ? 'is-on' : 'is-off'" />
            {{ status.tcp?.enabled ? `${status.tcp.listenAddress}:${status.tcp.port}` : $t('externalApiView.off') }}
          </span>
        </div>
        <div class="status-item">
          <span class="status-label">{{ $t('externalApiView.opcua') }}</span>
          <span class="status-value mono" :title="opcUaLabel">
            <span class="status-dot" :class="status.opcUa?.enabled ? (status.opcUa?.running ? 'is-on' : 'is-warn') : 'is-off'" />
            {{ opcUaLabel }}
          </span>
        </div>
        <div class="status-item">
          <span class="status-label">{{ $t('externalApiView.openapi') }}</span>
          <span class="status-value mono">/openapi/v1.json</span>
        </div>
        <div class="status-item">
          <span class="status-label">{{ $t('externalApiView.modbus') }}</span>
          <span class="status-value mono" :title="slaveTitle(status.modbusSlave)">
            <span class="status-dot" :class="slaveDot(status.modbusSlave)" />
            {{ slaveLabel(status.modbusSlave, 'allowWrite') }}
          </span>
        </div>
        <div class="status-item">
          <span class="status-label">{{ $t('externalApiView.iec104') }}</span>
          <span class="status-value mono" :title="slaveTitle(status.iec104Server)">
            <span class="status-dot" :class="slaveDot(status.iec104Server)" />
            {{ slaveLabel(status.iec104Server, 'allowControl') }}
          </span>
        </div>
      </div>
      <div class="mcp-status" data-test="ext-modbus-quality">
        <div class="mcp-head">
          <span class="status-label">{{ $t('externalApiView.modbusQualityTitle') }}</span>
        </div>
        <p class="tiny muted">{{ $t('externalApiView.modbusQualityHint') }}</p>
        <dl class="mcp-grid">
          <dt>{{ $t('externalApiView.modbusBadQualityPolicy') }}</dt>
          <dd data-test="ext-modbus-bad-quality">{{ modbusQualityPolicy }}</dd>
          <dt>{{ $t('externalApiView.modbusQualityBits') }}</dt>
          <dd>{{ $t('externalApiView.modbusNoQualityBits') }}</dd>
          <dt>{{ $t('externalApiView.simulationExportQuality') }}</dt>
          <dd data-test="ext-simulation-export-quality">{{ status?.modbusSlave?.simulationExportQuality || 'Uncertain' }}</dd>
        </dl>
      </div>
      <div v-if="status?.mcp" class="mcp-status" data-test="ext-mcp-status">
        <div class="mcp-head">
          <span class="status-label">{{ $t('externalApiView.mcp') }}</span>
          <span class="status-value">
            <span class="status-dot" :class="status.mcp.enabled ? 'is-on' : 'is-off'" />
            {{ status.mcp.enabled ? $t('externalApiView.on') : $t('externalApiView.off') }}
          </span>
          <span class="mono muted">{{ status.mcp.path || '/api/mcp' }}</span>
        </div>
        <p class="tiny muted">{{ $t('externalApiView.mcpReadOnlyHint') }}</p>
        <dl class="mcp-grid">
          <div>
            <dt>{{ $t('externalApiView.mcpSource') }}</dt>
            <dd data-test="ext-mcp-source">{{ mcpSourceLabel }}</dd>
          </div>
          <div>
            <dt>{{ $t('externalApiView.mcpControlWrites') }}</dt>
            <dd data-test="ext-mcp-control">{{ status.mcp.allowControlWrites ? $t('externalApiView.mcpWritesOn') : $t('externalApiView.mcpWritesOff') }}</dd>
          </div>
          <div>
            <dt>{{ $t('externalApiView.mcpConfigWrites') }}</dt>
            <dd data-test="ext-mcp-config">{{ status.mcp.allowConfigurationWrites ? $t('externalApiView.mcpWritesOn') : $t('externalApiView.mcpWritesOff') }}</dd>
          </div>
          <div>
            <dt>{{ $t('externalApiView.mcpLastActivity') }}</dt>
            <dd data-test="ext-mcp-last">{{ status.mcp.lastActivityTs ? formatTime(status.mcp.lastActivityTs) : $t('externalApiView.mcpNoActivity') }}</dd>
          </div>
        </dl>
        <div v-if="status.mcp.recent?.length" class="mcp-recent" data-test="ext-mcp-recent">
          <div class="tiny muted">{{ $t('externalApiView.mcpRecent') }}</div>
          <ul>
            <li v-for="(item, index) in status.mcp.recent" :key="index">
              <span class="mono">{{ formatTime(item.ts) }}</span>
              {{ item.action }}
              <span class="muted">{{ item.result }}</span>
            </li>
          </ul>
        </div>
      </div>
      <el-alert type="info" :closable="false" show-icon data-test="ext-opcua-alarm-rebuild"
        :title="$t('externalApiView.opcUaAlarmRebuildHint')" style="margin-bottom: 12px" />
      <el-alert
        v-if="mappingWarnings.length"
        type="warning"
        :closable="false"
        show-icon
        data-test="ext-map-warnings"
        :title="$t('externalApiView.mappingWarnings')"
        style="margin-bottom: 12px"
      >
        <ul class="warn-list">
          <li v-for="(warning, index) in mappingWarnings" :key="index">{{ warning }}</li>
        </ul>
      </el-alert>

      <div data-test="ext-table">
        <el-table :data="keys" size="small" v-loading="loading" :empty-text="loadError || undefined">
          <el-table-column prop="name" :label="$t('externalApiView.name')" min-width="140" show-overflow-tooltip />
          <el-table-column prop="keyPrefix" :label="$t('externalApiView.prefix')" width="140" show-overflow-tooltip />
          <el-table-column :label="$t('externalApiView.scopes')" min-width="200" show-overflow-tooltip>
            <template #default="{ row }">{{ (row.scopes || []).join(', ') }}</template>
          </el-table-column>
          <el-table-column :label="$t('externalApiView.areas')" width="120" show-overflow-tooltip>
            <template #default="{ row }">{{ row.areaIds?.length ? row.areaIds.join(',') : $t('externalApiView.allAreas') }}</template>
          </el-table-column>
          <el-table-column prop="rateLimitPerMinute" :label="$t('externalApiView.rate')" width="90" show-overflow-tooltip />
          <el-table-column :label="$t('externalApiView.enabled')" width="90">
            <template #default="{ row }">
              <el-switch :model-value="row.enabled" :disabled="!!row.revokedTs" @change="(v: boolean) => toggle(row, v)" />
            </template>
          </el-table-column>
          <el-table-column :label="$t('externalApiView.createdAt')" width="160" show-overflow-tooltip>
            <template #default="{ row }">{{ row.createdTs ? formatTime(row.createdTs) : '—' }}</template>
          </el-table-column>
          <el-table-column :label="$t('externalApiView.lastUsed')" width="160" show-overflow-tooltip>
            <template #default="{ row }">{{ row.lastUsedTs ? formatTime(row.lastUsedTs) : $t('externalApiView.neverUsed') }}</template>
          </el-table-column>
          <el-table-column prop="notes" :label="$t('externalApiView.notes')" min-width="140" show-overflow-tooltip />
          <el-table-column :label="$t('externalApiView.actions')" width="120" fixed="right">
            <template #default="{ row }">
              <el-popconfirm v-if="!row.revokedTs" :title="$t('externalApiView.confirmRevoke')" @confirm="revoke(row.id)">
                <template #reference><el-button link type="danger" data-test="ext-revoke">{{ $t('externalApiView.revoke') }}</el-button></template>
              </el-popconfirm>
              <span v-else class="muted">{{ $t('externalApiView.revoked') }}</span>
            </template>
          </el-table-column>
        </el-table>
      </div>
    </div>

    <el-dialog v-model="dialog" :title="$t('externalApiView.createTitle')" width="520px" destroy-on-close :before-close="createGuard.beforeClose">
      <el-alert v-if="createError" :title="createError" type="error" :closable="false" show-icon data-test="ext-save-error" />
      <el-form label-width="110px" size="small">
        <el-form-item :label="$t('externalApiView.name')"><el-input v-model="form.name" data-test="ext-name" /></el-form-item>
        <el-form-item :label="$t('externalApiView.scopes')">
          <el-checkbox-group v-model="form.scopes">
            <el-checkbox value="ReadTags">ReadTags</el-checkbox>
            <el-checkbox value="ReadHistory">ReadHistory</el-checkbox>
            <el-checkbox value="ReadAlarms">ReadAlarms</el-checkbox>
            <el-checkbox value="Write">Write</el-checkbox>
          </el-checkbox-group>
        </el-form-item>
        <el-form-item :label="$t('externalApiView.areas')">
          <el-select v-model="form.areaIds" multiple clearable :placeholder="$t('externalApiView.allAreas')" style="width:100%">
            <el-option v-for="a in areas" :key="a.id" :label="a.name" :value="a.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('externalApiView.rate')"><el-input-number v-model="form.rateLimitPerMinute" :min="1" :max="100000" /></el-form-item>
        <el-form-item :label="$t('externalApiView.notes')"><el-input v-model="form.notes" type="textarea" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createGuard.close()">{{ $t('externalApiView.cancel') }}</el-button>
        <el-button type="primary" data-test="ext-save" :loading="saving" @click="create">{{ $t('externalApiView.save') }}</el-button>
      </template>
    </el-dialog>

    <!-- secret 只在创建时返回一次：遮罩、Esc 和 X 都不能误关，否则只能撤销重建 -->
    <el-dialog v-model="secretDialog" :title="$t('externalApiView.secretTitle')" width="560px" :close-on-click-modal="false" :close-on-press-escape="false" :show-close="false">
      <p>{{ $t('externalApiView.secretHint') }}</p>
      <el-input :model-value="secret" readonly type="password" show-password data-test="ext-secret">
        <template #append>
          <el-button data-test="ext-copy" @click="copySecret">{{ $t('externalApiView.copySecret') }}</el-button>
        </template>
      </el-input>
      <el-alert v-if="copyError" :title="copyError" type="error" :closable="false" show-icon data-test="ext-copy-error" style="margin-top: 8px" />
      <template #footer>
        <el-button type="primary" @click="secretDialog = false">{{ $t('externalApiView.copied') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { externalApi, type ExternalApiKey, type ExternalEndpointStatus, type ExternalStatus } from '../api/external'
import { healthApi } from '../api'
import { http, errorMessage } from '../api/http'
import { formatTime } from '../utils'
import { tr } from '../i18n'
import { useEditDialog } from '../composables/useEditDialog'

const loading = ref(false), saving = ref(false), dialog = ref(false), secretDialog = ref(false)
const keys = ref<ExternalApiKey[]>([]), areas = ref<{ id: number; name: string }[]>([]), status = ref<ExternalStatus | null>(null)
const certWarnings = ref<{ code: string; message: string }[]>([])
const loadError = ref(''), areasError = ref(''), actionError = ref(''), createError = ref(''), copyError = ref('')
const secret = ref('')
const form = reactive({ name: '', scopes: ['ReadTags'] as string[], areaIds: [] as number[], rateLimitPerMinute: 120, notes: '' })
// 建 Key 要填名字、勾权限、选区域，中途关掉就白填。这里守一道，同时保证重开是干净表单
// （以前不重置，上次没保存的内容会留在里面，看着像已经建好了）。
const formDefaults = () => ({ name: '', scopes: ['ReadTags'] as string[], areaIds: [] as number[], rateLimitPerMinute: 120, notes: '' })
const createGuard = useEditDialog({ visible: dialog, saving, snapshot: () => JSON.stringify(form) })
const opcUaLabel = computed(() => {
  const o = status.value?.opcUa
  if (!o?.enabled) return tr('externalApiView.off')
  const ep = o.endpoint || o.endpointUrl || ''
  const write = o.allowWrite ? tr('externalApiView.writeAllowed') : tr('externalApiView.writeDenied')
  const state = o.running ? tr('externalApiView.running') : tr('externalApiView.starting')
  return `${state} ${ep} · ${write}`
})

const mappingWarnings = computed(() => status.value?.modbusSlave?.warnings?.filter(Boolean) ?? [])
const modbusQualityPolicy = computed(() => {
  const slave = status.value?.modbusSlave
  if (!slave) return tr('externalApiView.modbusQualityUnknown')
  if (slave.keepLastGood !== false && (slave.badQualityValue === null || slave.badQualityValue === undefined))
    return tr('externalApiView.modbusKeepLastGood')
  return tr('externalApiView.modbusSubstituteValue', [String(slave.badQualityValue)])
})
const mcpSourceLabel = computed(() => {
  const mcp = status.value?.mcp
  if (!mcp) return ''
  if (mcp.sourceRestriction === 'loopback') return tr('externalApiView.mcpSourceLoopback')
  if (mcp.sourceRestriction === 'any') return tr('externalApiView.mcpSourceAny')
  const list = (mcp.allowedCidrs ?? []).join(', ')
  return list ? tr('externalApiView.mcpSourceAllowlist', [list]) : tr('externalApiView.mcpSourceLoopback')
})

function slaveDot(item?: ExternalEndpointStatus): string {
  if (!item?.enabled) return 'is-off'
  return item.running ? 'is-on' : 'is-warn'
}

function writeLabel(item: ExternalEndpointStatus | undefined, flag: 'allowWrite' | 'allowControl'): string {
  const allowed = flag === 'allowControl' ? item?.allowControl : item?.allowWrite
  return allowed ? tr('externalApiView.writeAllowed') : tr('externalApiView.writeDenied')
}

function slaveLabel(item: ExternalEndpointStatus | undefined, flag: 'allowWrite' | 'allowControl'): string {
  if (!item?.enabled) return tr('externalApiView.off')
  const endpoint = item.listenAddress && item.port != null ? `${item.listenAddress}:${item.port}` : ''
  const state = item.running ? tr('externalApiView.running') : tr('externalApiView.starting')
  const mapped = tr('externalApiView.mappedPoints', [item.mappedPoints ?? 0, item.connections ?? 0])
  return `${state} ${endpoint} · ${writeLabel(item, flag)} · ${mapped}`
}

function slaveTitle(item?: ExternalEndpointStatus): string {
  if (!item?.enabled) return ''
  const warnings = item.warnings?.length ? ` · ${item.warnings.join('；')}` : ''
  return `${slaveLabel(item, item.allowControl != null ? 'allowControl' : 'allowWrite')}${warnings}`
}

async function load() {
  loading.value = true
  try {
    const [keyRows, statusRow, areaRows, health] = await Promise.all([
      externalApi.listKeys(),
      externalApi.status(),
      http.get('/areas').then(r => r.data).then(list => { areasError.value = ''; return list }).catch((error) => { areasError.value = errorMessage(error, tr('externalApiView.areasFailed')); return areas.value }),
      healthApi.get().catch(() => null)
    ])
    keys.value = keyRows
    status.value = statusRow
    areas.value = areaRows
    certWarnings.value = (health?.warnings ?? []).filter(w =>
      w.code.startsWith('report_signing_cert') || w.code.startsWith('external_tcp_tls_cert'))
    loadError.value = ''
  } catch (e) {
    loadError.value = errorMessage(e, tr('externalApiView.actionFailed'))
  }
  finally { loading.value = false }
}

function openCreate(): void { Object.assign(form, formDefaults()); createError.value = ''; dialog.value = true }
async function create() {
  if (!form.name.trim() || !form.scopes.length) { createError.value = tr('externalApiView.needNameScope'); return }
  saving.value = true
  createError.value = ''
  try {
    const r = await externalApi.createKey({
      name: form.name.trim(), scopes: form.scopes,
      areaIds: form.areaIds.length ? form.areaIds : null,
      rateLimitPerMinute: form.rateLimitPerMinute, notes: form.notes || undefined
    })
    secret.value = r.secret
    copyError.value = ''
    dialog.value = false
    secretDialog.value = true
    form.name = ''; form.scopes = ['ReadTags']; form.areaIds = []; form.notes = ''
    await load()
  } catch (e) { createError.value = errorMessage(e, tr('externalApiView.saveFailed')) }
  finally { saving.value = false }
}

// 便携部署是 http://<ip>:5080，属于非安全上下文，navigator.clipboard 常常不可用。
function legacyCopy(value: string): void {
  const input = document.createElement('textarea')
  input.value = value
  input.style.position = 'fixed'
  input.style.opacity = '0'
  document.body.appendChild(input)
  input.select()
  try { if (!document.execCommand('copy')) throw new Error('copy rejected') }
  finally { document.body.removeChild(input) }
}
async function copySecret(): Promise<void> {
  copyError.value = ''
  try {
    if (navigator.clipboard?.writeText) await navigator.clipboard.writeText(secret.value)
    else legacyCopy(secret.value)
    ElMessage.success(tr('externalApiView.copiedSecret'))
  } catch {
    // 只在 API 缺失时兜底；writeText 自己失败要如实报「复制失败」，不要假装成功。
    copyError.value = tr('externalApiView.copyFailed')
  }
}
async function revoke(id: number) {
  actionError.value = ''
  try { await externalApi.revokeKey(id); actionError.value = ''; await load() } catch (e) { actionError.value = errorMessage(e, tr('externalApiView.actionFailed')) }
}
async function toggle(row: ExternalApiKey, enabled: boolean) {
  actionError.value = ''
  try { await externalApi.setEnabled(row.id, enabled); actionError.value = ''; await load() } catch (e) { actionError.value = errorMessage(e, tr('externalApiView.actionFailed')); await load() }
}

onMounted(load)
</script>

<style scoped>
.status-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
  gap: var(--scada-space-3);
  margin-bottom: var(--scada-space-4);
}
.status-item {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 10px var(--scada-space-4);
  border: 1px solid var(--scada-border);
  border-radius: var(--scada-radius-sm);
  background: var(--scada-panel-muted);
}
.status-label {
  font-size: var(--scada-fs-caption);
  color: var(--scada-text-muted);
  letter-spacing: 0.02em;
}
.status-value {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  font-size: var(--scada-fs-body);
  color: var(--scada-text);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.status-item--wide {
  grid-column: span 1;
}
.status-dot {
  flex: none;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--scada-text-muted);
}
.status-dot.is-on {
  background: var(--scada-text-secondary);
}
.status-dot.is-off {
  background: transparent;
  box-shadow: inset 0 0 0 1.5px var(--scada-border-strong);
}
.status-dot.is-warn {
  background: var(--scada-warning);
  box-shadow: 0 0 0 3px rgba(212, 136, 6, 0.14);
}
.muted { color: var(--scada-text-muted); }
.warn-list { margin: 6px 0 0; padding-left: 1.1em; }
.mcp-status {
  margin-bottom: var(--scada-space-4);
  padding: 10px var(--scada-space-4);
  border: 1px solid var(--scada-border);
  border-radius: var(--scada-radius-sm);
  background: var(--scada-panel-muted);
}
.mcp-head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 4px; }
.mcp-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 8px 16px; margin: 8px 0 0; }
.mcp-grid dt { font-size: var(--scada-fs-caption); color: var(--scada-text-muted); }
.mcp-grid dd { margin: 2px 0 0; }
.mcp-recent { margin-top: 10px; }
.mcp-recent ul { margin: 4px 0 0; padding-left: 1.1em; }
.mcp-recent li { display: flex; gap: 8px; flex-wrap: wrap; font-size: var(--scada-fs-caption); }

</style>
