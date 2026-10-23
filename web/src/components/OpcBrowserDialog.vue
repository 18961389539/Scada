<template>
  <el-dialog v-model="visible" :title="`OPC UA 变量 · ${device.name}`" width="1150px" top="3vh" @closed="reset">
    <div :inert="previewVisible || applying">
      <el-alert v-if="error" type="error" :title="error" :closable="false" />
      <div class="toolbar"><el-breadcrumb><el-breadcrumb-item v-for="(part, index) in path" :key="index"><a @click="navigate(index)">{{ part.label }}</a></el-breadcrumb-item></el-breadcrumb>
        <span class="spacer" /><el-button :disabled="busy" @click="read(false)">从头读取</el-button><el-button :disabled="busy || !cursor" @click="read(true)">下一页</el-button></div>
      <el-table ref="table" :data="nodes" row-key="nodeId" v-loading="busy" max-height="290" @selection-change="select">
        <el-table-column type="selection" :reserve-selection="true" :selectable="(row: OpcNode) => row.nodeClass === 'Variable' && row.dataType !== null && row.readable && !row.error" width="45" />
        <el-table-column label="名称" min-width="160"><template #default="{ row }"><el-button link type="primary" @click="open(row)">{{ row.displayName }}</el-button></template></el-table-column>
        <el-table-column prop="nodeId" label="NodeId" min-width="340" show-overflow-tooltip />
        <el-table-column prop="dataType" label="类型" width="85" /><el-table-column label="访问" width="85"><template #default="{ row }">{{ row.readable ? row.writable ? '读 / 写' : '只读' : row.nodeClass }}</template></el-table-column>
        <el-table-column prop="error" label="说明" min-width="160" />
      </el-table>
      <div class="toolbar"><strong>已选择 {{ items.length }} / 500 个变量</strong><span class="muted">默认只读，允许写入须逐项勾选</span><span class="spacer" /><el-button @click="clear">清空选择</el-button></div>
      <el-table :data="items" max-height="220" size="small">
        <el-table-column label="测点名称" min-width="220"><template #default="{ row }"><el-input v-model="row.name" maxlength="100" aria-label="导入测点名称" /></template></el-table-column>
        <el-table-column prop="nodeId" label="NodeId" min-width="400" show-overflow-tooltip />
        <el-table-column label="允许写入" width="120"><template #default="{ row }"><el-checkbox v-model="row.writable" :disabled="!row.canWrite">允许写入</el-checkbox></template></el-table-column>
      </el-table>
    </div>
    <template #footer><el-button @click="visible = false">关闭</el-button><el-button type="primary" :loading="busy" :disabled="items.length < 1 || items.length > 500" @click="previewImport">预览导入</el-button></template>
    <el-dialog v-model="previewVisible" title="OPC UA 导入预览" width="1000px" append-to-body>
      <template v-if="preview">
        <el-alert :title="preview.canApply ? `将创建 ${preview.rows.length} 个测点，预览有效期 10 分钟` : '预览包含错误，请返回调整选择和名称'" :type="preview.canApply ? 'success' : 'error'" :closable="false" />
        <el-table :data="preview.rows" max-height="450"><el-table-column prop="name" label="测点" /><el-table-column prop="nodeId" label="NodeId" min-width="300" show-overflow-tooltip /><el-table-column prop="dataType" label="类型" width="90" /><el-table-column label="可写" width="65"><template #default="{ row }">{{ row.writable ? '是' : '否' }}</template></el-table-column><el-table-column prop="error" label="错误" min-width="200" /></el-table>
      </template>
      <template #footer><el-button :disabled="applying" @click="previewVisible = false">返回调整</el-button><el-button type="primary" :disabled="!preview?.canApply" :loading="applying" @click="apply">确认创建测点</el-button></template>
    </el-dialog>
  </el-dialog>
</template>
<script setup lang="ts">
import { ref, watch, onBeforeUnmount } from 'vue'
import { ElMessage } from 'element-plus'
import type { Device } from '../types'
import { opcApi, type OpcNode, type OpcPreview } from '../api/opc'
import { errorMessage } from '../api/http'
const props = defineProps<{ device: Device }>(), emit = defineEmits<{ imported: [] }>()
const visible = defineModel<boolean>({ required: true })
const nodes = ref<OpcNode[]>([]), cursor = ref<string | null>(null), busy = ref(false), error = ref(''), applying = ref(false)
const items = ref<{ nodeId: string; name: string; writable: boolean; canWrite: boolean }[]>([])
const path = ref([{ label: 'Objects', nodeId: 'i=85' }]), table = ref<{ clearSelection: () => void }>()
const preview = ref<OpcPreview | null>(null), previewVisible = ref(false)
let generation = 0
watch(visible, value => { if (value) { reset(); void read(false) } else { generation++; void closeCursor() } }, { immediate: true })
onBeforeUnmount(() => { generation++; void closeCursor() })
async function closeCursor(): Promise<void> { const old = cursor.value; cursor.value = null; if (old) try { await opcApi.close(props.device.id, old) } catch { /* Server expires abandoned cursors too. */ } }
function reset(): void { generation++; void closeCursor(); nodes.value = []; items.value = []; preview.value = null; previewVisible.value = false; error.value = ''; path.value = [{ label: 'Objects', nodeId: 'i=85' }]; table.value?.clearSelection() }
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
async function previewImport(): Promise<void> { const current = generation; busy.value = true; try { const result = await opcApi.preview(props.device.id, items.value.map(({ nodeId, name, writable }) => ({ nodeId, name, writable }))); if (current !== generation || !visible.value) return; preview.value = result; previewVisible.value = true } catch (e) { if (current === generation) ElMessage.error(errorMessage(e)) } finally { if (current === generation) busy.value = false } }
async function apply(): Promise<void> { if (!preview.value?.canApply) return; applying.value = true; try { const tags = await opcApi.apply(props.device.id, preview.value.id); ElMessage.success(`已创建 ${tags.length} 个测点`); previewVisible.value = false; visible.value = false; emit('imported') } catch (e) { ElMessage.error(errorMessage(e)); preview.value.canApply = false } finally { applying.value = false } }
</script>
