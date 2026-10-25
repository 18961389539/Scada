<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <h2>{{ $t('externalApiView.title') }}</h2>
        <span class="muted">{{ $t('externalApiView.subtitle') }}</span>
        <span class="spacer" />
        <el-button data-test="ext-refresh" @click="load">{{ $t('externalApiView.refresh') }}</el-button>
        <el-button type="primary" data-test="ext-add" @click="dialog = true">{{ $t('externalApiView.create') }}</el-button>
      </div>

      <el-descriptions v-if="status" :column="3" size="small" border class="mb">
        <el-descriptions-item :label="$t('externalApiView.rest')">{{ status.rest?.enabled ? $t('externalApiView.on') : $t('externalApiView.off') }}</el-descriptions-item>
        <el-descriptions-item :label="$t('externalApiView.tcp')">{{ status.tcp?.enabled ? `${status.tcp.listenAddress}:${status.tcp.port}` : $t('externalApiView.off') }}</el-descriptions-item>
        <el-descriptions-item :label="$t('externalApiView.opcua')">{{ status.opcUa?.enabled ? status.opcUa.endpointUrl : $t('externalApiView.off') }}</el-descriptions-item>
        <el-descriptions-item :label="$t('externalApiView.openapi')" :span="3">/openapi/v1.json</el-descriptions-item>
      </el-descriptions>

      <div data-test="ext-table">
        <el-table :data="keys" size="small" v-loading="loading">
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
      <el-input :model-value="secret" readonly data-test="ext-secret" />
      <template #footer>
        <el-button type="primary" @click="secretDialog = false">{{ $t('externalApiView.copied') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { externalApi, type ExternalApiKey } from '../api/external'
import { http, errorMessage } from '../api/http'
import { tr } from '../i18n'

const loading = ref(false), saving = ref(false), dialog = ref(false), secretDialog = ref(false)
const keys = ref<ExternalApiKey[]>([]), areas = ref<{ id: number; name: string }[]>([]), status = ref<any>(null)
const secret = ref('')
const form = reactive({ name: '', scopes: ['ReadTags'] as string[], areaIds: [] as number[], rateLimitPerMinute: 120, notes: '' })

async function load() {
  loading.value = true
  try {
    ;[keys.value, status.value, areas.value] = await Promise.all([
      externalApi.listKeys(),
      externalApi.status(),
      http.get('/areas').then(r => r.data)
    ])
  } catch (e) { ElMessage.error(errorMessage(e)) }
  finally { loading.value = false }
}

async function create() {
  if (!form.name.trim() || !form.scopes.length) { ElMessage.warning(tr('externalApiView.needNameScope')); return }
  saving.value = true
  try {
    const r = await externalApi.createKey({
      name: form.name.trim(), scopes: form.scopes,
      areaIds: form.areaIds.length ? form.areaIds : null,
      rateLimitPerMinute: form.rateLimitPerMinute, notes: form.notes || undefined
    })
    secret.value = r.secret
    dialog.value = false
    secretDialog.value = true
    form.name = ''; form.scopes = ['ReadTags']; form.areaIds = []; form.notes = ''
    await load()
  } catch (e) { ElMessage.error(errorMessage(e)) }
  finally { saving.value = false }
}

async function revoke(id: number) {
  try { await externalApi.revokeKey(id); await load() } catch (e) { ElMessage.error(errorMessage(e)) }
}
async function toggle(row: ExternalApiKey, enabled: boolean) {
  try { await externalApi.setEnabled(row.id, enabled); await load() } catch (e) { ElMessage.error(errorMessage(e)); await load() }
}

onMounted(load)
</script>

<style scoped>
.mb { margin-bottom: 16px; }
.muted { color: #8aa0b8; }
</style>
