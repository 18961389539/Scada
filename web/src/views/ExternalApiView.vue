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
      </div>

      <div data-test="ext-table">
        <el-table :data="keys" size="small" v-loading="loading" :empty-text="loadError || undefined">
          <el-table-column prop="name" :label="$t('externalApiView.name')" min-width="140" />
          <el-table-column prop="keyPrefix" :label="$t('externalApiView.prefix')" width="140" />
          <el-table-column :label="$t('externalApiView.scopes')" min-width="200">
            <template #default="{ row }">{{ (row.scopes || []).join(', ') }}</template>
          </el-table-column>
          <el-table-column :label="$t('externalApiView.areas')" width="120">
            <template #default="{ row }">{{ row.areaIds?.length ? row.areaIds.join(',') : $t('externalApiView.allAreas') }}</template>
          </el-table-column>
          <el-table-column prop="rateLimitPerMinute" :label="$t('externalApiView.rate')" width="90" />
          <el-table-column :label="$t('externalApiView.enabled')" width="90">
            <template #default="{ row }">
              <el-switch :model-value="row.enabled" :disabled="!!row.revokedTs" @change="(v: boolean) => toggle(row, v)" />
            </template>
          </el-table-column>
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

    <el-dialog v-model="dialog" :title="$t('externalApiView.createTitle')" width="520px" destroy-on-close>
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
        <el-button @click="dialog = false">{{ $t('externalApiView.cancel') }}</el-button>
        <el-button type="primary" data-test="ext-save" :loading="saving" @click="create">{{ $t('externalApiView.save') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="secretDialog" :title="$t('externalApiView.secretTitle')" width="560px">
      <p>{{ $t('externalApiView.secretHint') }}</p>
      <el-input :model-value="secret" readonly data-test="ext-secret">
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
import { externalApi, type ExternalApiKey } from '../api/external'
import { http, errorMessage } from '../api/http'
import { tr } from '../i18n'

const loading = ref(false), saving = ref(false), dialog = ref(false), secretDialog = ref(false)
const keys = ref<ExternalApiKey[]>([]), areas = ref<{ id: number; name: string }[]>([]), status = ref<any>(null)
const loadError = ref(''), areasError = ref(''), actionError = ref(''), createError = ref(''), copyError = ref('')
const secret = ref('')
const form = reactive({ name: '', scopes: ['ReadTags'] as string[], areaIds: [] as number[], rateLimitPerMinute: 120, notes: '' })
const opcUaLabel = computed(() => {
  const o = status.value?.opcUa
  if (!o?.enabled) return tr('externalApiView.off')
  const ep = o.endpoint || o.endpointUrl || ''
  return o.running ? `${tr('externalApiView.running')} ${ep}` : `${tr('externalApiView.starting')} ${ep}`
})

async function load() {
  loading.value = true
  try {
    ;[keys.value, status.value, areas.value] = await Promise.all([
      externalApi.listKeys(),
      externalApi.status(),
      http.get('/areas').then(r => r.data).then(list => { areasError.value = ''; return list }).catch((error) => { areasError.value = errorMessage(error, tr('externalApiView.areasFailed')); return areas.value })
    ])
    loadError.value = ''
  } catch (e) {
    loadError.value = errorMessage(e, tr('externalApiView.actionFailed'))
  }
  finally { loading.value = false }
}

function openCreate(): void { createError.value = ''; dialog.value = true }
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

async function copySecret(): Promise<void> {
  copyError.value = ''
  try {
    await navigator.clipboard.writeText(secret.value)
    ElMessage.success(tr('externalApiView.copiedSecret'))
  } catch {
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
  background: var(--scada-success);
  box-shadow: 0 0 0 3px rgba(30, 158, 106, 0.14);
}
.status-dot.is-warn {
  background: var(--scada-warning);
  box-shadow: 0 0 0 3px rgba(212, 136, 6, 0.14);
}
.status-dot.is-off {
  background: var(--scada-border-strong);
}
.muted { color: var(--scada-text-muted); }
</style>
