<template>
  <div class="page template-editor" v-loading="loading">
    <div class="toolbar" :inert="busy"><el-button @click="router.push('/config/object-templates')">{{ $t('objectTemplateEditorView.k14c9bc') }}</el-button>
      <el-input v-model="name" :aria-label="$t('objectTemplateEditorView.kbbc511')" maxlength="100" style="width: 240px" />
      <el-tag :type="dirty ? 'warning' : 'success'">{{ dirty ? $t('objectTemplateEditorView.k4123f1') : $t('objectTemplateEditorView.kcdfab9') }}</el-tag>
      <span class="muted">{{ $t('objectTemplateEditorView.k23ffb3', [revision, latestVersion ? `v${latestVersion}` : $t('objectTemplateEditorView.k720777')]) }}</span><span class="spacer" />
      <el-button @click="validate">{{ $t('objectTemplateEditorView.kbbe550') }}</el-button><el-button @click="save">{{ $t('objectTemplateEditorView.kfbfc58') }}</el-button><el-button type="primary" @click="publish">{{ $t('objectTemplateEditorView.k5f3ff9') }}</el-button>
    </div>
    <el-alert v-if="loadError" type="error" :title="loadError" :closable="false" />
    <el-alert v-if="actionError" type="error" :title="actionError" :closable="false" show-icon data-test="template-editor-error" />
    <template v-if="definition">
      <el-alert :title="$t('objectTemplateEditorView.k72eae1')" type="info" :closable="false" />
      <div :inert="busy">
      <el-tabs v-model="tab">
        <el-tab-pane :label="$t('objectTemplateEditorView.k3f9859')" name="tags">
          <div class="toolbar"><span>{{ $t('objectTemplateEditorView.kc76c5b') }}</span><span class="spacer" /><el-button @click="addTag">{{ $t('objectTemplateEditorView.keb0f47') }}</el-button></div>
          <el-table :data="definition.tags" border size="small">
            <el-table-column :label="$t('objectTemplateEditorView.k4c583d')" width="145"><template #default="{ row }"><el-input v-model="row.key" :aria-label="$t('objectTemplateEditorView.k7cd079')" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k1be7ae')" width="140"><template #default="{ row }"><el-input v-model="row.properties.name" :aria-label="$t('objectTemplateEditorView.k49183c')" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k17fc93')" width="155"><template #default="{ row }"><el-select v-model="row.properties.area"><el-option v-for="(label, value) in areas" :key="value" :label="label" :value="value" /></el-select></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.kde4c14')" width="105"><template #default="{ row }"><el-input-number v-model="row.addressOffset" :min="0" :max="65535" :controls="false" style="width: 85px" /></template></el-table-column>
            <el-table-column label="OPC NodeId" width="260"><template #default="{ row }"><el-input v-if="row.properties.area === 'OpcNode'" v-model="row.properties.nodeId" :placeholder="$t('objectTemplateEditorView.k6ffd14')" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.ke4e46c')" width="120"><template #default="{ row }"><el-select v-model="row.properties.dataType" @change="row.properties.bitOffset = 0"><el-option v-for="type in dataTypes" :key="type" :value="type" :label="dataTypeLabels[type] ?? type" /></el-select></template></el-table-column>
            <el-table-column label="S7 DB" width="95"><template #default="{ row }"><el-input-number v-if="row.properties.area === 'DataBlock'" v-model="row.properties.dbNumber" :min="1" :max="65535" :controls="false" style="width: 75px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k304afb')" width="85"><template #default="{ row }"><el-input-number v-if="['DataBlock', 'Memory', 'Input', 'Output'].includes(row.properties.area) && row.properties.dataType === 'Bool'" v-model="row.properties.bitOffset" :min="0" :max="7" :controls="false" style="width: 65px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k8ec8c8')" width="85"><template #default="{ row }"><el-input v-model="row.properties.unit" maxlength="32" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k8482e1')" width="100"><template #default="{ row }"><el-input-number v-model="row.properties.scale" :controls="false" style="width: 80px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.ka87924')" width="100"><template #default="{ row }"><el-input-number v-model="row.properties.offset" :controls="false" style="width: 80px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.kaf3af1')" width="95"><template #default="{ row }"><el-input-number v-model="row.properties.minValue" :controls="false" style="width: 75px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k0517c1')" width="95"><template #default="{ row }"><el-input-number v-model="row.properties.maxValue" :controls="false" style="width: 75px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k26ae2c')" width="65"><template #default="{ row }"><el-switch v-model="row.properties.writable" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.kddfde7')" width="120"><template #default="{ row }"><el-select v-model="row.properties.archiveMode"><el-option :label="$t('objectTemplateEditorView.k8f29bd')" value="None" /><el-option :label="$t('objectTemplateEditorView.kda2ce3')" value="OnChange" /><el-option :label="$t('objectTemplateEditorView.k164395')" value="Periodic" /></el-select></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k76d7e1')" width="110"><template #default="{ row }"><el-input-number v-model="row.properties.deadband" :min="0" :controls="false" style="width: 85px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.kd25985')" width="110"><template #default="{ row }"><el-input-number v-model="row.properties.archivePeriodSec" :min="1" :controls="false" style="width: 85px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k412f54')" min-width="170"><template #default="{ row }"><el-input v-model="row.properties.description" maxlength="500" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.kf3ea6d')" width="70" fixed="right"><template #default="{ $index }"><el-button type="danger" link @click="definition!.tags.splice($index, 1)">{{ $t('objectTemplateEditorView.k2f752c') }}</el-button></template></el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane :label="$t('objectTemplateEditorView.kaf4fca')" name="alarms">
          <div class="toolbar"><span>{{ $t('objectTemplateEditorView.k131bf7') }}</span><span class="spacer" /><el-button @click="addAlarm">{{ $t('objectTemplateEditorView.kf54954') }}</el-button></div>
          <el-table :data="definition.alarms" border size="small">
            <el-table-column :label="$t('objectTemplateEditorView.k4c583d')" width="135"><template #default="{ row }"><el-input v-model="row.key" :aria-label="$t('objectTemplateEditorView.k0fb5f1')" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k8cff1c')" min-width="160"><template #default="{ row }"><el-select v-model="row.tagKey"><el-option v-for="t in definition!.tags" :key="t.key" :label="`${t.properties.name} (${t.key})`" :value="t.key" /></el-select></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.ke4e46c')" width="135"><template #default="{ row }"><el-select v-model="row.properties.type"><el-option v-for="(label, value) in alarmTypes" :key="value" :label="label" :value="value" /></el-select></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.ka05320')" width="120"><template #default="{ row }"><el-input-number v-model="row.properties.threshold" :controls="false" style="width: 100px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k5c42c0')" width="120"><template #default="{ row }"><el-select v-model="row.properties.level"><el-option v-for="(label, value) in levels" :key="value" :label="label" :value="value" /></el-select></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.kbc1a7f')" width="120"><template #default="{ row }"><el-input-number v-model="row.properties.delaySec" :min="0" :controls="false" style="width: 95px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.k3b86db')" width="120"><template #default="{ row }"><el-input-number v-model="row.properties.recoverDeadband" :min="0" :controls="false" style="width: 95px" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.kdc6de3')" min-width="200"><template #default="{ row }"><el-input v-model="row.properties.message" maxlength="500" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.kd4e9ca')" width="70"><template #default="{ row }"><el-switch v-model="row.properties.enabled" /></template></el-table-column>
            <el-table-column :label="$t('objectTemplateEditorView.kf3ea6d')" width="70"><template #default="{ $index }"><el-button type="danger" link @click="definition!.alarms.splice($index, 1)">{{ $t('objectTemplateEditorView.k2f752c') }}</el-button></template></el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane :label="$t('objectTemplateEditorView.k4fb3e5')" name="panel" lazy>
          <ScreenDesignerView v-if="tab === 'panel'" :key="editorKey" embedded :disabled="busy" :model-value="editorDocument" :binding-tags="editorTags" @update:model-value="acceptPanel" @save="save" />
        </el-tab-pane>
        <el-tab-pane :label="$t('objectTemplateEditorView.k26eaef')" name="versions">
          <el-table :data="versions"><el-table-column prop="version" :label="$t('objectTemplateEditorView.kc789c1')" /><el-table-column :label="$t('objectTemplateEditorView.k89b4aa')"><template #default="{ row }">{{ formatTime(row.createdTs) }}</template></el-table-column><el-table-column prop="createdBy" :label="$t('objectTemplateEditorView.k18d0fa')" />
            <el-table-column :label="$t('objectTemplateEditorView.kf3ea6d')"><template #default="{ row }"><el-button link type="primary" @click="viewVersion(row.version)">{{ $t('objectTemplateEditorView.k311404') }}</el-button></template></el-table-column></el-table>
        </el-tab-pane>
      </el-tabs>
      </div>
    </template>
    <el-dialog v-model="issuesVisible" :title="$t('objectTemplateEditorView.k77937a')" width="640px" data-test="template-editor-issues">
      <el-alert v-if="issuesError" type="error" :title="issuesError" :closable="false" show-icon data-test="template-editor-issues-error" />
      <template v-else><el-result v-if="!issues.length" icon="success" :title="$t('objectTemplateEditorView.k7035f8')" /><ul v-else><li v-for="(issue, index) in issues" :key="index">{{ issue }}</li></ul></template></el-dialog>
    <el-dialog v-model="historyVisible" :title="$t('objectTemplateEditorView.kcd8e49', [historyVersion])" width="80%" destroy-on-close data-test="template-version-dialog">
      <el-alert v-if="historyError" type="error" :title="historyError" :closable="false" show-icon data-test="template-version-error" />
      <template v-else-if="historyDoc"><p>{{ $t('objectTemplateEditorView.k32006c', [historyDoc.tags.length, historyDoc.alarms.length]) }}</p><el-table :data="historyDoc.tags" data-test="template-version-tags"><el-table-column prop="key" :label="$t('objectTemplateEditorView.k4c583d')" /><el-table-column prop="properties.name" :label="$t('objectTemplateEditorView.k1be7ae')" /><el-table-column prop="addressOffset" :label="$t('objectTemplateEditorView.kde4c14')" /><el-table-column :label="$t('objectTemplateEditorView.ke4e46c')"><template #default="{ row }">{{ dataTypeLabels[row.properties.dataType] ?? row.properties.dataType }}</template></el-table-column></el-table>
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
import { dataTypeLabels, formatTime } from '../utils'
import { tr } from '../i18n'
const route = useRoute(), router = useRouter(), id = Number(route.params.id)
const loading = ref(true), loadError = ref(''), actionError = ref(''), busy = ref(false), name = ref(''), revision = ref(0), latestVersion = ref(0)
const definition = ref<ObjectTemplateDocument>(), versions = ref<TemplateDetail['versions']>([]), tab = ref('tags'), editorKey = ref(0)
const saved = ref(''), issues = ref<string[]>([]), issuesError = ref(''), issuesVisible = ref(false), historyVisible = ref(false), historyDoc = ref<ObjectTemplateDocument>(), historyError = ref(''), historyVersion = ref(0)
let disposed = false
const areas = { Coil: tr('objectTemplateEditorView.kf019ca'), DiscreteInput: tr('objectTemplateEditorView.k97accb'), HoldingRegister: tr('objectTemplateEditorView.k21dca7'), InputRegister: tr('objectTemplateEditorView.kbfbcca'), DataBlock: 'S7 DB', Memory: 'S7 M', Input: 'S7 I', Output: 'S7 Q', OpcNode: tr('objectTemplateEditorView.kf7748d') }
const dataTypes = ['Bool', 'Int16', 'UInt16', 'Int32', 'UInt32', 'Float', 'Double']
const alarmTypes = { High: tr('objectTemplateEditorView.k88deca'), HighHigh: tr('objectTemplateEditorView.kfe8edb'), Low: tr('objectTemplateEditorView.kb44804'), LowLow: tr('objectTemplateEditorView.k09df8e'), StateChange: tr('objectTemplateEditorView.kc5277e') }, levels = { Info: tr('objectTemplateEditorView.kab3656'), Warning: tr('objectTemplateEditorView.k5521e3'), Critical: tr('objectTemplateEditorView.k81ffc6') }
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
function addTag(): void { definition.value!.tags.push({ key: `tag${definition.value!.tags.length + 1}`, addressOffset: 0, properties: { id: 0, deviceId: 1, kind: 'Physical', expression: '', calculationEnabled: true, calculationIntervalMs: 1000, inputMaxAgeMs: 15000, calculationRevision: 1, name: tr('objectTemplateEditorView.k7111d9'), area: 'HoldingRegister', address: 0, dbNumber: 1, bitOffset: 0, nodeId: '', dataType: 'Float', scale: 1, offset: 0, unit: '', writable: false, archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600, minValue: 0, maxValue: 100, registerCount: 2 } }) }
function addAlarm(): void { definition.value!.alarms.push({ key: `alarm${definition.value!.alarms.length + 1}`, tagKey: definition.value!.tags[0]?.key ?? '', properties: { id: 0, tagId: 1, type: 'High', threshold: 100, level: 'Warning', delaySec: 0, recoverDeadband: 1, enabled: true, message: '' } }) }
function structuredErrors(e: unknown): string[] | null {
  const errors = (e as { response?: { data?: { errors?: string[] } } }).response?.data?.errors
  return errors?.length ? errors : null
}
function showIssues(list: string[]): void { issues.value = list; issuesError.value = ''; issuesVisible.value = true }
async function save(): Promise<boolean> {
  if (!definition.value || busy.value) return false
  busy.value = true; actionError.value = ''; const sent = snapshot()
  try { const payload = JSON.parse(sent); const result = await objectTemplateApi.save(id, payload.name, revision.value, payload.document); revision.value = result.revision; saved.value = sent; actionError.value = ''; ElMessage.success(tr('objectTemplateEditorView.k97de92')); return true }
  catch (e) { const errors = structuredErrors(e); if (errors) showIssues(errors); else actionError.value = errorMessage(e, tr('objectTemplateEditorView.actionFailed')); return false }
  finally { busy.value = false }
}
async function validate(): Promise<void> {
  try { issues.value = await objectTemplateApi.validate(definition.value!); issuesError.value = ''; issuesVisible.value = true }
  catch (e) { const errors = structuredErrors(e); if (errors) showIssues(errors); else { issues.value = []; issuesError.value = errorMessage(e, tr('objectTemplateEditorView.validateFailed')); issuesVisible.value = true } }
}
async function publish(): Promise<void> {
  if (busy.value || dirty.value && !await save()) return
  busy.value = true; actionError.value = ''
  try { const result = await objectTemplateApi.publish(id, revision.value); revision.value = result.revision; latestVersion.value = result.latestVersion; versions.value = (await objectTemplateApi.get(id)).versions; actionError.value = ''; ElMessage.success(tr('objectTemplateEditorView.k14537b', [latestVersion.value])) }
  catch (e) { const errors = structuredErrors(e); if (errors) showIssues(errors); else actionError.value = errorMessage(e, tr('objectTemplateEditorView.actionFailed')) }
  finally { busy.value = false }
}
async function viewVersion(version: number): Promise<void> {
  historyVersion.value = version
  try { historyDoc.value = (await objectTemplateApi.version(id, version)).document; historyError.value = ''; historyVisible.value = true }
  catch (e) { historyDoc.value = undefined; historyError.value = errorMessage(e, tr('objectTemplateEditorView.actionFailed')); historyVisible.value = true }
}
function unload(e: BeforeUnloadEvent): void { if (dirty.value) { e.preventDefault(); e.returnValue = '' } }
async function confirmLeave(): Promise<boolean> { if (!dirty.value) return true; try { await ElMessageBox.confirm(tr('objectTemplateEditorView.k044b8c'), tr('objectTemplateEditorView.k70ac7e'), { confirmButtonText: tr('objectTemplateEditorView.k0ba189'), cancelButtonText: tr('objectTemplateEditorView.k3a481b'), type: 'warning' }); return true } catch { return false } }
onBeforeRouteLeave(confirmLeave)
onBeforeRouteUpdate(confirmLeave)
onMounted(async () => { try { const result = await objectTemplateApi.get(id); name.value = result.name; revision.value = result.revision; latestVersion.value = result.latestVersion; definition.value = result.document; versions.value = result.versions; saved.value = snapshot(); editorKey.value++ } catch (e) { loadError.value = errorMessage(e) } finally { loading.value = false }; if (!disposed) window.addEventListener('beforeunload', unload) })
onBeforeUnmount(() => { disposed = true; window.removeEventListener('beforeunload', unload) })
</script>
<style scoped>
.template-editor :deep(.el-tabs__content) { overflow: visible; }.template-editor :deep(.el-table) { margin-top: 8px; }.template-editor :deep(.el-alert) { margin: 12px 0; }
.history-preview { overflow: auto; margin-top: 20px; }li { line-height: 2; }
</style>
