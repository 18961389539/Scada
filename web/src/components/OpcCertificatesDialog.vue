<template>
  <el-dialog v-model="visible" title="OPC UA 证书" width="1050px" top="5vh">
    <div class="toolbar"><strong>证书作用范围</strong><el-select v-model="selected" aria-label="证书作用范围" :disabled="changing" style="width: 420px">
      <el-option :value="0" label="中心本机" /><el-option v-for="device in remoteDevices" :key="device.id" :value="device.id" :label="`网关 #${device.gatewayId} · ${device.name}`" />
    </el-select></div>
    <el-alert :title="scope ? `正在操作网关 #${scope.gatewayId} 的证书库，影响此网关全部 OPC UA 设备。` : selected ? '所选网关设备已变更，请重新选择；未切换到中心证书库。' : '正在操作中心本机的证书库，不会改变远程网关的信任。'" type="warning" :closable="false" />
    <el-alert title="连接失败后，先核对服务器证书的 SHA-256 指纹，再建立信任。客户端公钥证书需要在服务器侧信任；私钥不会下载。" type="info" :closable="false" />
    <el-tabs v-model="tab"><el-tab-pane label="待信任" name="rejected" /><el-tab-pane label="已信任" name="trusted" /><el-tab-pane label="客户端公钥" name="own" /></el-tabs>
    <el-table :data="rows.filter(x => x.store === tab)" v-loading="loading" max-height="500" empty-text="此证书库暂无证书">
      <el-table-column prop="subject" label="主题" min-width="180" show-overflow-tooltip />
      <el-table-column prop="sha256" label="SHA-256 指纹" min-width="300"><template #default="{ row }"><span style="word-break: break-all; user-select: all">{{ row.sha256 }}</span></template></el-table-column>
      <el-table-column label="有效期至" width="170"><template #default="{ row }">{{ new Date(row.notAfter).toLocaleString() }}</template></el-table-column>
      <el-table-column label="操作" width="130"><template #default="{ row }">
        <el-button v-if="tab === 'rejected'" link type="primary" :disabled="loading" @click="change(row, true)">信任证书</el-button>
        <el-button v-else-if="tab === 'trusted'" link type="danger" :disabled="loading" @click="change(row, false)">撤销信任</el-button>
        <el-button v-else link type="primary" @click="download(row)">下载公钥</el-button>
      </template></el-table-column>
    </el-table>
    <template #footer><el-button @click="load">刷新证书</el-button><el-button @click="visible = false">关闭</el-button></template>
  </el-dialog>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { opcApi, type OpcCertificate, type OpcCertificateScope } from '../api/opc'
import type { Device } from '../types'
import { errorMessage } from '../api/http'
import { downloadBlob } from '../utils'
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
    if (selected.value && !target) throw new Error('所选网关设备已变更，请重新选择证书作用范围')
    await ElMessageBox.confirm(`${target ? `网关 #${target.gatewayId}` : '中心本机'}\n${trust ? '确认信任' : '确认撤销信任'}证书：${cert.subject}\nSHA-256：${cert.sha256}\n该采集位置的 OPC UA 连接将关闭并重新连接。`, trust ? '核对服务器证书' : '撤销服务器信任', { type: 'warning', confirmButtonText: trust ? '指纹已核对，信任' : '撤销信任' })
    if (key !== scopeKey.value || !visible.value) throw new Error('采集位置已变化，请重新选择并核对证书作用范围')
    loading.value = true; if (trust) await opcApi.trust(cert.sha256, target); else await opcApi.untrust(cert.sha256, target)
    ElMessage.success('证书信任已更新'); if (visible.value && key === scopeKey.value) await load()
  } catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) } finally { loading.value = false; changing.value = false }
}
async function download(cert: OpcCertificate): Promise<void> { const target = scope.value; try { if (selected.value && !target) throw new Error('采集位置已变化，请重新选择'); downloadBlob(await opcApi.publicCertificate(cert.sha256, target), `scada-client-${cert.sha256.slice(0, 12)}.der`) } catch (e) { ElMessage.error(errorMessage(e)) } }
</script>
