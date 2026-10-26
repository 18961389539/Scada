<template>
  <el-dialog v-model="visible" :title="$t('opcBrowserDialog.ka8fdfc', [device.name])" width="1150px" top="3vh" @closed="reset">
    <div :inert="previewVisible || applying">
      <el-alert v-if="error" type="error" :title="error" :closable="false" data-test="opc-error" />
      <div class="toolbar"><el-breadcrumb><el-breadcrumb-item v-for="(part, index) in path" :key="index"><a @click="navigate(index)">{{ part.label }}</a></el-breadcrumb-item></el-breadcrumb>
        <span class="spacer" /><el-button :disabled="busy" @click="read(false)">{{ $t('opcBrowserDialog.k1b7856') }}</el-button><el-button :disabled="busy || !cursor" @click="read(true)">{{ $t('opcBrowserDialog.k67a246') }}</el-button></div>
      <el-table ref="table" :data="nodes" row-key="nodeId" v-loading="busy" max-height="290" @selection-change="select">
        <el-table-column type="selection" :reserve-selection="true" :selectable="(row: OpcNode) => row.nodeClass === 'Variable' && row.dataType !== null && row.readable && !row.error" width="45" />
        <el-table-column :label="$t('opcBrowserDialog.k1be7ae')" min-width="160"><template #default="{ row }"><el-button link type="primary" @click="open(row)">{{ row.displayName }}</el-button></template></el-table-column>
        <el-table-column prop="nodeId" label="NodeId" min-width="340" show-overflow-tooltip />
        <el-table-column prop="dataType" :label="$t('opcBrowserDialog.ke4e46c')" width="85" /><el-table-column :label="$t('opcBrowserDialog.k7f5641')" width="85"><template #default="{ row }">{{ row.readable ? row.writable ? $t('opcBrowserDialog.k88eaa5') : $t('opcBrowserDialog.kffc1d0') : row.nodeClass }}</template></el-table-column>
        <el-table-column prop="error" :label="$t('opcBrowserDialog.k26670d')" min-width="160" />
      </el-table>
      <div class="toolbar"><strong>{{ $t('opcBrowserDialog.k4081f0', [items.length]) }}</strong><span class="muted">{{ $t('opcBrowserDialog.k4b181e') }}</span><span class="spacer" /><el-button @click="clear">{{ $t('opcBrowserDialog.ka6d3de') }}</el-button></div>
      <el-table :data="items" max-height="220" size="small">
        <el-table-column :label="$t('opcBrowserDialog.kd70b6b')" min-width="220"><template #default="{ row }"><el-input v-model="row.name" maxlength="100" :aria-label="$t('opcBrowserDialog.kc0e4d8')" /></template></el-table-column>
        <el-table-column prop="nodeId" label="NodeId" min-width="400" show-overflow-tooltip />
        <el-table-column :label="$t('opcBrowserDialog.kc3dd2c')" width="120"><template #default="{ row }"><el-checkbox v-model="row.writable" :disabled="!row.canWrite">{{ $t('opcBrowserDialog.kc3dd2c') }}</el-checkbox></template></el-table-column>
      </el-table>
    </div>
    <template #footer><el-button @click="visible = false">{{ $t('opcBrowserDialog.k6c14bd') }}</el-button><el-button type="primary" :loading="busy" :disabled="items.length < 1 || items.length > 500" @click="previewImport">{{ $t('opcBrowserDialog.kb3edbf') }}</el-button></template>
    <el-dialog v-model="previewVisible" :title="$t('opcBrowserDialog.kbe4bd5')" width="1000px" append-to-body>
      <template v-if="preview">
        <el-alert v-if="previewError" type="error" :title="previewError" :closable="false" data-test="opc-apply-error" />
        <el-alert :title="preview.canApply ? $t('opcBrowserDialog.k537f32', [preview.rows.length]) : $t('opcBrowserDialog.k472cbb')" :type="preview.canApply ? 'success' : 'error'" :closable="false" />
        <el-table :data="preview.rows" max-height="450"><el-table-column prop="name" :label="$t('opcBrowserDialog.k8cff1c')" /><el-table-column prop="nodeId" label="NodeId" min-width="300" show-overflow-tooltip /><el-table-column prop="dataType" :label="$t('opcBrowserDialog.ke4e46c')" width="90" /><el-table-column :label="$t('opcBrowserDialog.k26ae2c')" width="65"><template #default="{ row }">{{ row.writable ? $t('opcBrowserDialog.k30160a') : $t('opcBrowserDialog.k8bf5c1') }}</template></el-table-column><el-table-column prop="error" :label="$t('opcBrowserDialog.kb859c7')" min-width="200" /></el-table>
      </template>
      <template #footer><el-button :disabled="applying" @click="previewVisible = false">{{ $t('opcBrowserDialog.k0793b6') }}</el-button><el-button type="primary" :disabled="!preview?.canApply" :loading="applying" @click="apply">{{ $t('opcBrowserDialog.k961606') }}</el-button></template>
    </el-dialog>
  </el-dialog>
</template>
<script setup lang="ts">
import { ref, watch, onBeforeUnmount } from 'vue'
import { ElMessage } from 'element-plus'
import type { Device } from '../types'
import { opcApi, type OpcNode, type OpcPreview } from '../api/opc'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'
const props = defineProps<{ device: Device }>(), emit = defineEmits<{ imported: [] }>()
const visible = defineModel<boolean>({ required: true })
const nodes = ref<OpcNode[]>([]), cursor = ref<string | null>(null), busy = ref(false), error = ref(''), applying = ref(false)
const items = ref<{ nodeId: string; name: string; writable: boolean; canWrite: boolean }[]>([])
const path = ref([{ label: 'Objects', nodeId: 'i=85' }]), table = ref<{ clearSelection: () => void }>()
const preview = ref<OpcPreview | null>(null), previewVisible = ref(false), previewError = ref('')
let generation = 0
watch(visible, value => { if (value) { reset(); void read(false) } else { generation++; void closeCursor() } }, { immediate: true })
onBeforeUnmount(() => { generation++; void closeCursor() })
async function closeCursor(): Promise<void> { const old = cursor.value; cursor.value = null; if (old) try { await opcApi.close(props.device.id, old) } catch { /* Server expires abandoned cursors too. */ } }
function reset(): void { generation++; void closeCursor(); nodes.value = []; items.value = []; preview.value = null; previewVisible.value = false; previewError.value = ''; error.value = ''; path.value = [{ label: 'Objects', nodeId: 'i=85' }]; table.value?.clearSelection() }
async function read(next: boolean): Promise<void> {
  const current = ++generation, deviceId = props.device.id; busy.value = true; error.value = ''
  try {
    if (!next) await closeCursor()
    const response = await opcApi.browse(deviceId, path.value.at(-1)!.nodeId, next ? cursor.value : null)
    if (current !== generation || !visible.value) { if (response.cursor) await opcApi.close(deviceId, response.cursor); return }
    nodes.value = response.nodes; cursor.value = response.cursor
  } catch (e) { if (current === generation) error.value = errorMessage(e) } finally { if (current === generation) busy.value = false }
}
function select(selected: OpcNode[]): void {
  const before = new Map(items.value.map(x => [x.nodeId, x])); items.value = selected.map(x => before.get(x.nodeId) ?? { nodeId: x.nodeId, name: x.displayName.slice(0, 100), writable: false, canWrite: x.writable })
}
function clear(): void { table.value?.clearSelection(); items.value = [] }
function open(node: OpcNode): void { if (busy.value) return; path.value.push({ label: node.displayName, nodeId: node.nodeId }); void read(false) }
function navigate(index: number): void { if (busy.value) return; path.value = path.value.slice(0, index + 1); void read(false) }
async function previewImport(): Promise<void> {
  const current = generation; busy.value = true; error.value = ''
  try {
    const result = await opcApi.preview(props.device.id, items.value.map(({ nodeId, name, writable }) => ({ nodeId, name, writable })))
    if (current !== generation || !visible.value) return
    preview.value = result; previewError.value = ''; previewVisible.value = true
  } catch (e) { if (current === generation) error.value = errorMessage(e, tr('opcBrowserDialog.previewFailed')) }
  finally { if (current === generation) busy.value = false }
}
async function apply(): Promise<void> {
  if (!preview.value?.canApply) return
  applying.value = true; previewError.value = ''
  try {
    const tags = await opcApi.apply(props.device.id, preview.value.id)
    ElMessage.success(tr('opcBrowserDialog.k5e1fa9', [tags.length])); previewVisible.value = false; visible.value = false; emit('imported')
  } catch (e) { previewError.value = errorMessage(e, tr('opcBrowserDialog.applyFailed')) }
  finally { applying.value = false }
}
</script>
