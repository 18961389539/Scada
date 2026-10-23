<template>
  <div class="page template-editor" v-loading="loading">
    <div class="toolbar" :inert="busy"><el-button @click="router.push('/config/object-templates')">模板列表</el-button>
      <el-input v-model="name" aria-label="模板名称" maxlength="100" style="width: 240px" />
      <el-tag :type="dirty ? 'warning' : 'success'">{{ dirty ? '未保存' : '已保存' }}</el-tag>
      <span class="muted">草稿 r{{ revision }} · 最新发布 {{ latestVersion ? `v${latestVersion}` : '无' }}</span><span class="spacer" />
      <el-button @click="validate">校验模板</el-button><el-button @click="save">保存模板</el-button><el-button type="primary" @click="publish">发布新版本</el-button>
    </div>
    <el-alert v-if="loadError" type="error" :title="loadError" :closable="false" />
    <template v-if="definition">
      <el-alert title="测点逻辑键用于跨版本匹配；基址由对象实例提供。发布新版本后，已有对象通过预览升级。" type="info" :closable="false" />
      <div :inert="busy">
      <el-tabs v-model="tab">
        <el-tab-pane label="测点定义" name="tags">
          <div class="toolbar"><span>地址 = 实例基址 + 相对偏移</span><span class="spacer" /><el-button @click="addTag">添加模板测点</el-button></div>
          <el-table :data="definition.tags" border size="small">
            <el-table-column label="逻辑键" width="145"><template #default="{ row }"><el-input v-model="row.key" aria-label="测点逻辑键" /></template></el-table-column>
            <el-table-column label="名称" width="140"><template #default="{ row }"><el-input v-model="row.properties.name" aria-label="模板测点名称" /></template></el-table-column>
            <el-table-column label="区域" width="155"><template #default="{ row }"><el-select v-model="row.properties.area"><el-option v-for="(label, value) in areas" :key="value" :label="label" :value="value" /></el-select></template></el-table-column>
            <el-table-column label="偏移" width="105"><template #default="{ row }"><el-input-number v-model="row.addressOffset" :min="0" :max="65535" :controls="false" style="width: 85px" /></template></el-table-column>
            <el-table-column label="类型" width="120"><template #default="{ row }"><el-select v-model="row.properties.dataType"><el-option v-for="type in dataTypes" :key="type" :value="type" :label="type" /></el-select></template></el-table-column>
            <el-table-column label="单位" width="85"><template #default="{ row }"><el-input v-model="row.properties.unit" maxlength="32" /></template></el-table-column>
            <el-table-column label="倍率" width="100"><template #default="{ row }"><el-input-number v-model="row.properties.scale" :controls="false" style="width: 80px" /></template></el-table-column>
            <el-table-column label="偏移量" width="100"><template #default="{ row }"><el-input-number v-model="row.properties.offset" :controls="false" style="width: 80px" /></template></el-table-column>
            <el-table-column label="下限" width="95"><template #default="{ row }"><el-input-number v-model="row.properties.minValue" :controls="false" style="width: 75px" /></template></el-table-column>
            <el-table-column label="上限" width="95"><template #default="{ row }"><el-input-number v-model="row.properties.maxValue" :controls="false" style="width: 75px" /></template></el-table-column>
            <el-table-column label="可写" width="65"><template #default="{ row }"><el-switch v-model="row.properties.writable" /></template></el-table-column>
            <el-table-column label="归档" width="120"><template #default="{ row }"><el-select v-model="row.properties.archiveMode"><el-option label="不归档" value="None" /><el-option label="变化存储" value="OnChange" /><el-option label="周期存储" value="Periodic" /></el-select></template></el-table-column>
            <el-table-column label="归档死区" width="110"><template #default="{ row }"><el-input-number v-model="row.properties.deadband" :min="0" :controls="false" style="width: 85px" /></template></el-table-column>
            <el-table-column label="归档秒数" width="110"><template #default="{ row }"><el-input-number v-model="row.properties.archivePeriodSec" :min="1" :controls="false" style="width: 85px" /></template></el-table-column>
            <el-table-column label="描述" min-width="170"><template #default="{ row }"><el-input v-model="row.properties.description" maxlength="500" /></template></el-table-column>
            <el-table-column label="操作" width="70" fixed="right"><template #default="{ $index }"><el-button type="danger" link @click="definition!.tags.splice($index, 1)">移除</el-button></template></el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane label="报警定义" name="alarms">
          <div class="toolbar"><span>报警绑定模板测点逻辑键</span><span class="spacer" /><el-button @click="addAlarm">添加模板报警</el-button></div>
          <el-table :data="definition.alarms" border size="small">
            <el-table-column label="逻辑键" width="135"><template #default="{ row }"><el-input v-model="row.key" aria-label="报警逻辑键" /></template></el-table-column>
            <el-table-column label="测点" min-width="160"><template #default="{ row }"><el-select v-model="row.tagKey"><el-option v-for="t in definition!.tags" :key="t.key" :label="`${t.properties.name} (${t.key})`" :value="t.key" /></el-select></template></el-table-column>
            <el-table-column label="类型" width="135"><template #default="{ row }"><el-select v-model="row.properties.type"><el-option v-for="(label, value) in alarmTypes" :key="value" :label="label" :value="value" /></el-select></template></el-table-column>
            <el-table-column label="阈值" width="120"><template #default="{ row }"><el-input-number v-model="row.properties.threshold" :controls="false" style="width: 100px" /></template></el-table-column>
            <el-table-column label="等级" width="120"><template #default="{ row }"><el-select v-model="row.properties.level"><el-option v-for="(label, value) in levels" :key="value" :label="label" :value="value" /></el-select></template></el-table-column>
            <el-table-column label="延时（秒）" width="120"><template #default="{ row }"><el-input-number v-model="row.properties.delaySec" :min="0" :controls="false" style="width: 95px" /></template></el-table-column>
            <el-table-column label="恢复死区" width="120"><template #default="{ row }"><el-input-number v-model="row.properties.recoverDeadband" :min="0" :controls="false" style="width: 95px" /></template></el-table-column>
            <el-table-column label="消息" min-width="200"><template #default="{ row }"><el-input v-model="row.properties.message" maxlength="500" /></template></el-table-column>
            <el-table-column label="启用" width="70"><template #default="{ row }"><el-switch v-model="row.properties.enabled" /></template></el-table-column>
            <el-table-column label="操作" width="70"><template #default="{ $index }"><el-button type="danger" link @click="definition!.alarms.splice($index, 1)">移除</el-button></template></el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane label="面板模板" name="panel" lazy>
          <ScreenDesignerView v-if="tab === 'panel'" :key="editorKey" embedded :disabled="busy" :model-value="editorDocument" :binding-tags="editorTags" @update:model-value="acceptPanel" @save="save" />
        </el-tab-pane>
        <el-tab-pane label="发布历史" name="versions">
          <el-table :data="versions"><el-table-column prop="version" label="发布版本" /><el-table-column label="时间"><template #default="{ row }">{{ formatTime(row.createdTs) }}</template></el-table-column><el-table-column prop="createdBy" label="发布人" />
            <el-table-column label="操作"><template #default="{ row }"><el-button link type="primary" @click="viewVersion(row.version)">查看版本</el-button></template></el-table-column></el-table>
        </el-tab-pane>
      </el-tabs>
      </div>
    </template>
    <el-dialog v-model="issuesVisible" title="模板校验" width="640px"><el-result v-if="!issues.length" icon="success" title="校验通过" /><ul v-else><li v-for="(issue, index) in issues" :key="index">{{ issue }}</li></ul></el-dialog>
    <el-dialog v-model="historyVisible" :title="`已发布版本 v${historyVersion}`" width="80%" destroy-on-close>
      <template v-if="historyDoc"><p>测点 {{ historyDoc.tags.length }} 个，报警 {{ historyDoc.alarms.length }} 条。此快照不可修改。</p><el-table :data="historyDoc.tags"><el-table-column prop="key" label="逻辑键" /><el-table-column prop="properties.name" label="名称" /><el-table-column prop="addressOffset" label="偏移" /><el-table-column prop="properties.dataType" label="类型" /></el-table>
        <div class="history-preview"><ScreenCanvas :document="historyDoc.panel" editing :zoom=".6" /></div></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { objectTemplateApi } from '../api/objectTemplates'
import type { ObjectTemplateDocument, TemplateDetail } from '../api/objectTemplates'
import type { ScreenDocument } from '../screens/model'
import type { Tag } from '../types'
import ScreenDesignerView from './ScreenDesignerView.vue'
import ScreenCanvas from '../components/ScreenCanvas.vue'
import { errorMessage } from '../api/http'
import { formatTime } from '../utils'
const route = useRoute(), router = useRouter(), id = Number(route.params.id)
const loading = ref(true), loadError = ref(''), busy = ref(false), name = ref(''), revision = ref(0), latestVersion = ref(0)
const definition = ref<ObjectTemplateDocument>(), versions = ref<TemplateDetail['versions']>([]), tab = ref('tags'), editorKey = ref(0)
const saved = ref(''), issues = ref<string[]>([]), issuesVisible = ref(false), historyVisible = ref(false), historyDoc = ref<ObjectTemplateDocument>(), historyVersion = ref(0)
let disposed = false
const areas = { Coil: '线圈', DiscreteInput: '离散输入', HoldingRegister: '保持寄存器', InputRegister: '输入寄存器' }
const dataTypes = ['Bool', 'Int16', 'UInt16', 'Int32', 'UInt32', 'Float', 'Double']
const alarmTypes = { High: '高限', HighHigh: '高高限', Low: '低限', LowLow: '低低限', StateChange: '状态变化' }, levels = { Info: '提示', Warning: '警告', Critical: '严重' }
const snapshot = () => JSON.stringify({ name: name.value, document: definition.value })
const dirty = computed(() => !!saved.value && snapshot() !== saved.value)
const keyIds = new Map<string, number>()
function keyId(key: string): number { if (!keyIds.has(key)) keyIds.set(key, keyIds.size + 1); return keyIds.get(key)! }
const editorTags = computed<Tag[]>(() => definition.value?.tags.map(t => ({ ...t.properties, id: keyId(t.key), name: `${t.properties.name} (${t.key})` })) ?? [])
const editorDocument = computed<ScreenDocument>(() => {
  const doc = JSON.parse(JSON.stringify(definition.value!.panel)) as ScreenDocument
  for (const n of doc.nodes) { n.tagId = definition.value!.bindings[n.id] ? keyId(definition.value!.bindings[n.id]) : null; n.action.tagId = definition.value!.actionBindings[n.id] ? keyId(definition.value!.actionBindings[n.id]) : null }
  return doc
})
function acceptPanel(doc: ScreenDocument): void {
  const reverse = new Map([...keyIds].map(([key, value]) => [value, key])), bindings: Record<string, string> = {}, actions: Record<string, string> = {}
  for (const node of doc.nodes) {
    if (node.tagId && reverse.has(node.tagId)) bindings[node.id] = reverse.get(node.tagId)!
    if (node.action.type === 'command' && node.action.tagId && reverse.has(node.action.tagId)) actions[node.id] = reverse.get(node.action.tagId)!
    node.tagId = null; node.action.tagId = null
  }
  definition.value!.panel = doc; definition.value!.bindings = bindings; definition.value!.actionBindings = actions
}
function addTag(): void { definition.value!.tags.push({ key: `tag${definition.value!.tags.length + 1}`, addressOffset: 0, properties: { id: 0, deviceId: 1, name: '新测点', area: 'HoldingRegister', address: 0, dataType: 'Float', scale: 1, offset: 0, unit: '', writable: false, archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600, minValue: 0, maxValue: 100, registerCount: 2 } }) }
function addAlarm(): void { definition.value!.alarms.push({ key: `alarm${definition.value!.alarms.length + 1}`, tagKey: definition.value!.tags[0]?.key ?? '', properties: { id: 0, tagId: 1, type: 'High', threshold: 100, level: 'Warning', delaySec: 0, recoverDeadband: 1, enabled: true, message: '' } }) }
function showErrors(e: unknown): void { ElMessage.error(errorMessage(e)); const errors = (e as { response?: { data?: { errors?: string[] } } }).response?.data?.errors; if (errors) { issues.value = errors; issuesVisible.value = true } }
async function save(): Promise<boolean> {
  if (!definition.value || busy.value) return false
  busy.value = true; const sent = snapshot()
  try { const payload = JSON.parse(sent); const result = await objectTemplateApi.save(id, payload.name, revision.value, payload.document); revision.value = result.revision; saved.value = sent; ElMessage.success('模板草稿已保存'); return true }
  catch (e) { showErrors(e); return false } finally { busy.value = false }
}
async function validate(): Promise<void> { try { issues.value = await objectTemplateApi.validate(definition.value!); issuesVisible.value = true } catch (e) { showErrors(e) } }
async function publish(): Promise<void> {
  if (busy.value || dirty.value && !await save()) return
  busy.value = true
  try { const result = await objectTemplateApi.publish(id, revision.value); revision.value = result.revision; latestVersion.value = result.latestVersion; versions.value = (await objectTemplateApi.get(id)).versions; ElMessage.success(`模板 v${latestVersion.value} 已发布`) }
  catch (e) { showErrors(e) } finally { busy.value = false }
}
async function viewVersion(version: number): Promise<void> { try { historyDoc.value = (await objectTemplateApi.version(id, version)).document; historyVersion.value = version; historyVisible.value = true } catch (e) { showErrors(e) } }
function unload(e: BeforeUnloadEvent): void { if (dirty.value) { e.preventDefault(); e.returnValue = '' } }
async function confirmLeave(): Promise<boolean> { if (!dirty.value) return true; try { await ElMessageBox.confirm('模板有未保存修改，确认离开？', '未保存修改', { confirmButtonText: '离开', cancelButtonText: '继续编辑', type: 'warning' }); return true } catch { return false } }
onBeforeRouteLeave(confirmLeave)
onBeforeRouteUpdate(confirmLeave)
onMounted(async () => { try { const result = await objectTemplateApi.get(id); name.value = result.name; revision.value = result.revision; latestVersion.value = result.latestVersion; definition.value = result.document; versions.value = result.versions; saved.value = snapshot(); editorKey.value++ } catch (e) { loadError.value = errorMessage(e) } finally { loading.value = false }; if (!disposed) window.addEventListener('beforeunload', unload) })
onBeforeUnmount(() => { disposed = true; window.removeEventListener('beforeunload', unload) })
</script>
<style scoped>
.template-editor :deep(.el-tabs__content) { overflow: visible; }.template-editor :deep(.el-table) { margin-top: 8px; }.template-editor :deep(.el-alert) { margin: 12px 0; }
.history-preview { overflow: auto; margin-top: 20px; }li { line-height: 2; }
</style>
