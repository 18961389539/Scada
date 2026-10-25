<template>
  <el-dialog v-model="visible" :title="$t('opcCertificatesDialog.k1c11ad')" width="1050px" top="5vh">
    <div class="toolbar"><strong>{{ $t('opcCertificatesDialog.kc9762e') }}</strong><el-select v-model="selected" :aria-label="$t('opcCertificatesDialog.kc9762e')" :disabled="changing" style="width: 420px">
      <el-option :value="0" :label="$t('opcCertificatesDialog.k3963d7')" /><el-option v-for="device in remoteDevices" :key="device.id" :value="device.id" :label="$t('opcCertificatesDialog.kc3a1fc', [device.gatewayId, device.name])" />
    </el-select></div>
    <el-alert :title="scope ? $t('opcCertificatesDialog.k1e41cd', [scope.gatewayId]) : selected ? $t('opcCertificatesDialog.k9859fa') : $t('opcCertificatesDialog.k58f7a7')" type="warning" :closable="false" />
    <el-alert :title="$t('opcCertificatesDialog.k1fec3d')" type="info" :closable="false" />
    <el-tabs v-model="tab"><el-tab-pane :label="$t('opcCertificatesDialog.kefa905')" name="rejected" /><el-tab-pane :label="$t('opcCertificatesDialog.k28dc9b')" name="trusted" /><el-tab-pane :label="$t('opcCertificatesDialog.k438f1c')" name="own" /></el-tabs>
    <el-table :data="rows.filter(x => x.store === tab)" v-loading="loading" max-height="500" :empty-text="$t('opcCertificatesDialog.kb6bb9c')">
      <el-table-column prop="subject" :label="$t('opcCertificatesDialog.ke848dd')" min-width="180" show-overflow-tooltip />
      <el-table-column prop="sha256" :label="$t('opcCertificatesDialog.kb1005a')" min-width="300"><template #default="{ row }"><span style="word-break: break-all; user-select: all">{{ row.sha256 }}</span></template></el-table-column>
      <el-table-column :label="$t('opcCertificatesDialog.k37eb47')" width="170"><template #default="{ row }">{{ new Date(row.notAfter).toLocaleString() }}</template></el-table-column>
      <el-table-column :label="$t('opcCertificatesDialog.kf3ea6d')" width="130"><template #default="{ row }">
        <el-button v-if="tab === 'rejected'" link type="primary" :disabled="loading" @click="change(row, true)">{{ $t('opcCertificatesDialog.k78d1be') }}</el-button>
        <el-button v-else-if="tab === 'trusted'" link type="danger" :disabled="loading" @click="change(row, false)">{{ $t('opcCertificatesDialog.k4c0c58') }}</el-button>
        <el-button v-else link type="primary" @click="download(row)">{{ $t('opcCertificatesDialog.k5e4718') }}</el-button>
      </template></el-table-column>
    </el-table>
    <template #footer><el-button @click="load">{{ $t('opcCertificatesDialog.k170d44') }}</el-button><el-button @click="visible = false">{{ $t('opcCertificatesDialog.k6c14bd') }}</el-button></template>
  </el-dialog>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { opcApi, type OpcCertificate, type OpcCertificateScope } from '../api/opc'
import type { Device } from '../types'
import { errorMessage } from '../api/http'
import { downloadBlob } from '../utils'
import { tr } from '../i18n'
const visible = defineModel<boolean>({ required: true })
const props = withDefaults(defineProps<{ devices?: Device[] }>(), { devices: () => [] })
const selected = ref(0), changing = ref(false)
const remoteDevices = computed(() => props.devices.filter(x => x.protocol === 'OpcUa' && x.gatewayId != null))
const scope = computed<OpcCertificateScope | undefined>(() => { const device = remoteDevices.value.find(x => x.id === selected.value); return device ? { deviceId: device.id, gatewayId: device.gatewayId! } : undefined })
const scopeKey = computed(() => `${selected.value}:${scope.value?.gatewayId ?? 'centre'}`)
const tab = ref('rejected'), rows = ref<OpcCertificate[]>([]), loading = ref(false)
let generation = 0, controller: AbortController | undefined
watch([visible, scopeKey], ([shown]) => { generation++; controller?.abort(); rows.value = []; if (shown) void load() })
onBeforeUnmount(() => { generation++; controller?.abort() })
async function load(): Promise<void> {
  const current = ++generation, target = scope.value; controller?.abort(); const request = controller = new AbortController(); loading.value = true
  if (selected.value && !target) { loading.value = false; return }
  try { const result = await opcApi.certificates(target, request.signal); if (current === generation && visible.value) rows.value = result }
  catch (e) { if (current === generation && !request.signal.aborted) ElMessage.error(errorMessage(e)) } finally { if (current === generation) loading.value = false }
}
async function change(cert: OpcCertificate, trust: boolean): Promise<void> {
  const target = scope.value, key = scopeKey.value; changing.value = true
  try {
    if (selected.value && !target) throw new Error(tr('opcCertificatesDialog.k8401e6'))
    await ElMessageBox.confirm(tr('opcCertificatesDialog.kacd0d3', [target ? tr('opcCertificatesDialog.k38b4cc', [target.gatewayId]) : tr('opcCertificatesDialog.k3963d7'), trust ? tr('opcCertificatesDialog.k92d416') : tr('opcCertificatesDialog.kade994'), cert.subject, cert.sha256]), trust ? tr('opcCertificatesDialog.ke7a1be') : tr('opcCertificatesDialog.kfff124'), { type: 'warning', confirmButtonText: trust ? tr('opcCertificatesDialog.kf90f47') : tr('opcCertificatesDialog.k4c0c58') })
    if (key !== scopeKey.value || !visible.value) throw new Error(tr('opcCertificatesDialog.k58daa8'))
    loading.value = true; if (trust) await opcApi.trust(cert.sha256, target); else await opcApi.untrust(cert.sha256, target)
    ElMessage.success(tr('opcCertificatesDialog.k217b84')); if (visible.value && key === scopeKey.value) await load()
  } catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) } finally { loading.value = false; changing.value = false }
}
async function download(cert: OpcCertificate): Promise<void> { const target = scope.value; try { if (selected.value && !target) throw new Error(tr('opcCertificatesDialog.k11772e')); downloadBlob(await opcApi.publicCertificate(cert.sha256, target), `scada-client-${cert.sha256.slice(0, 12)}.der`) } catch (e) { ElMessage.error(errorMessage(e)) } }
</script>
