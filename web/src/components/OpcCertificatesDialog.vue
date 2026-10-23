<template>
  <el-dialog v-model="visible" title="OPC UA 证书" width="1050px" top="5vh">
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
import { ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { opcApi, type OpcCertificate } from '../api/opc'
import { errorMessage } from '../api/http'
import { downloadBlob } from '../utils'
const visible = defineModel<boolean>({ required: true })
const tab = ref('rejected'), rows = ref<OpcCertificate[]>([]), loading = ref(false)
watch(visible, value => { if (value) void load() })
async function load(): Promise<void> { loading.value = true; try { rows.value = await opcApi.certificates() } catch (e) { ElMessage.error(errorMessage(e)) } finally { loading.value = false } }
async function change(cert: OpcCertificate, trust: boolean): Promise<void> {
  try {
    await ElMessageBox.confirm(`${trust ? '确认信任' : '确认撤销信任'}证书：${cert.subject}\nSHA-256：${cert.sha256}\n现有 OPC UA 连接将关闭并重新连接。`, trust ? '核对服务器证书' : '撤销服务器信任', { type: 'warning', confirmButtonText: trust ? '指纹已核对，信任' : '撤销信任' })
    loading.value = true; if (trust) await opcApi.trust(cert.sha256); else await opcApi.untrust(cert.sha256)
    ElMessage.success('证书信任已更新'); await load()
  } catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) } finally { loading.value = false }
}
async function download(cert: OpcCertificate): Promise<void> { try { downloadBlob(await opcApi.publicCertificate(cert.sha256), `scada-client-${cert.sha256.slice(0, 12)}.der`) } catch (e) { ElMessage.error(errorMessage(e)) } }
</script>
