<template>
  <div class="page object-library" v-loading="loading">
    <div class="toolbar"><h2>设备对象与模板</h2><span class="muted">用版本化模板批量生成测点、报警及设备面板</span><span class="spacer" /><el-button @click="load">刷新</el-button><el-button type="primary" @click="createTemplate">新建模板</el-button></div>
    <el-tabs v-model="tab">
      <el-tab-pane label="设备模板" name="templates">
        <div class="template-cards"><article v-for="template in templates" :key="template.id" class="template-card">
          <div class="card-top"><span class="template-icon">▦</span><el-tag :type="template.latestVersion ? 'success' : 'info'">{{ template.latestVersion ? `已发布 v${template.latestVersion}` : '草稿' }}</el-tag></div>
          <h3>{{ template.name }}</h3><p class="muted">{{ template.instanceCount }} 个对象实例 · {{ formatTime(template.updatedTs) }}</p>
          <div class="toolbar"><el-button @click="router.push(`/config/object-templates/${template.id}`)">编辑模板</el-button><el-button type="primary" :disabled="!template.latestVersion" @click="openCreate(template)">批量生成对象</el-button><el-button type="danger" link @click="deleteTemplate(template)">删除</el-button></div>
        </article></div>
        <el-empty v-if="!templates.length && !loading" description="创建模板后，可为同类设备批量生成配置" />
      </el-tab-pane>
      <el-tab-pane label="对象实例" name="instances">
        <div class="toolbar"><el-select v-model="filterTemplate" clearable placeholder="全部模板" style="width: 220px"><el-option v-for="t in templates" :key="t.id" :label="t.name" :value="t.id" /></el-select><span class="spacer" />
          <el-button :disabled="!selected.length" type="primary" @click="openUpgrade(selected)">升级选中对象</el-button></div>
        <el-table :data="filteredInstances" row-key="id" @selection-change="setSelection">
          <el-table-column type="selection" width="45" /><el-table-column prop="name" label="对象名称" min-width="130" /><el-table-column prop="deviceName" label="采集设备" min-width="130" />
          <el-table-column prop="templateName" label="来源模板" min-width="130" /><el-table-column prop="baseAddress" label="基址" width="90" />
          <el-table-column label="版本" width="140"><template #default="{ row }"><el-tag :type="row.appliedVersion < row.latestVersion ? 'warning' : 'success'">v{{ row.appliedVersion }} / v{{ row.latestVersion }}</el-tag></template></el-table-column>
          <el-table-column label="操作" min-width="240"><template #default="{ row }"><el-button link type="primary" @click="router.push(`/config/screens/${row.screenId}`)">面板草稿</el-button><el-button link :disabled="row.appliedVersion >= row.latestVersion" @click="openUpgrade([row])">预览升级</el-button><el-button link type="danger" @click="detach(row)">解除关联</el-button></template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>

    <el-dialog v-model="createVisible" :title="`批量生成 · ${creatingTemplate?.name ?? ''}`" width="900px" :close-on-click-modal="false">
      <div :inert="busy"><el-form inline label-width="90px">
        <el-form-item label="模板版本"><el-select v-model="createVersion" style="width: 130px" aria-label="实例模板版本"><el-option v-for="v in createVersions" :key="v.version" :label="`v${v.version}`" :value="v.version" /></el-select></el-form-item>
        <el-form-item label="采集设备"><el-select v-model="batch.deviceId" filterable style="width: 220px" aria-label="实例采集设备"><el-option v-for="d in devices" :key="d.id" :label="d.name" :value="d.id" /></el-select></el-form-item>
        <el-form-item label="名称前缀"><el-input v-model="batch.prefix" aria-label="对象名称前缀" maxlength="35" style="width: 160px" /></el-form-item>
        <el-form-item label="起始编号"><el-input-number v-model="batch.first" :min="1" :max="99999" aria-label="起始编号" /></el-form-item>
        <el-form-item label="对象数量"><el-input-number v-model="batch.count" :min="1" :max="100" aria-label="对象数量" /></el-form-item>
        <el-form-item label="起始基址"><el-input-number v-model="batch.baseAddress" :min="0" :max="65535" aria-label="起始基址" /></el-form-item>
        <el-form-item label="地址间距"><el-input-number v-model="batch.stride" :min="0" :max="65535" aria-label="地址间距" /></el-form-item>
      </el-form>
      <el-alert title="将一次性创建测点和报警；设备面板保存为草稿，检查绑定和控制动作后再发布。" type="info" :closable="false" />
      <el-table :data="createParameters" max-height="230" size="small"><el-table-column prop="name" label="对象名称" /><el-table-column prop="baseAddress" label="基址" /><el-table-column label="测点地址"><template #default="{ row }">基址 {{ row.baseAddress }} + 模板测点偏移</template></el-table-column></el-table>
      <div v-if="createPlan" class="preview-results"><el-alert v-for="(error, i) in createPlan.errors" :key="i" :title="error" type="error" :closable="false" />
        <div v-for="plan in createPlan.plans" :key="plan.name" class="plan-row"><strong>{{ plan.name }}</strong><span>{{ plan.tagCount }} 测点 / {{ plan.alarmCount }} 报警 / {{ plan.panelNodeCount }} 图元</span><el-tag :type="plan.canApply ? 'success' : 'danger'">{{ plan.canApply ? '可生成' : '需修正' }}</el-tag><p v-for="error in plan.errors" :key="error" class="error">{{ error }}</p></div>
      </div></div>
      <template #footer><el-button :disabled="busy" @click="createVisible = false">取消</el-button><el-button :loading="busy" @click="previewCreate">预览生成</el-button><el-button type="primary" :loading="busy" :disabled="!createPlan?.canApply" @click="applyCreate">确认生成</el-button></template>
    </el-dialog>

    <el-dialog v-model="upgradeVisible" title="预览模板升级" width="1000px" :close-on-click-modal="false">
      <div :inert="busy"><div class="toolbar"><span>选中 {{ upgrading.length }} 个对象</span><el-select v-model="upgradeVersion" aria-label="升级目标版本" style="width: 180px"><el-option v-for="v in upgradeVersions" :key="v.version" :value="v.version" :label="`目标 v${v.version}`" /></el-select><span class="spacer" /><el-button @click="resetResolutions">重置处理选择</el-button><el-button @click="previewUpgrade">{{ upgradePlans.length ? '重新预览' : '预览升级' }}</el-button></div>
      <el-alert title="比较生成基线、现场配置和新版模板。未冲突的本地修改保留；运行面板不会自动发布。移除成员须明确选择保留或删除。" type="info" :closable="false" />
      <section v-for="plan in upgradePlans" :key="plan.instanceId!" class="upgrade-plan">
        <div class="toolbar"><strong>{{ plan.name }} → v{{ plan.version }}</strong><span class="spacer" /><el-tag :type="plan.canApply ? 'success' : 'warning'">{{ plan.canApply ? '可升级' : '存在待处理项' }}</el-tag></div>
        <el-alert v-for="error in plan.errors" :key="error" :title="error" type="error" :closable="false" />
        <div v-for="conflict in plan.conflicts" :key="conflict.key" class="conflict">
          <div class="toolbar"><strong>{{ conflictLabel(conflict) }}</strong><span class="spacer" />
            <el-select v-model="resolutions[plan.instanceId!][conflict.key]" placeholder="请选择处理方式" aria-label="冲突处理方式" style="width: 270px" @change="upgradeVerified = false">
              <el-option v-for="choice in conflict.choices" :key="choice" :value="choice" :label="choice === 'keepLocal' ? '保留现场（移除时解除关联）' : '采用模板（移除时删除配置）'" /></el-select>
          </div>
          <div v-if="[conflict.baseline, conflict.local, conflict.incoming].every(v => v.length <= 120)" class="three-way"><div><small>生成基线</small><pre>{{ conflict.baseline }}</pre></div><div><small>当前现场</small><pre>{{ conflict.local }}</pre></div><div><small>新版模板</small><pre>{{ conflict.incoming }}</pre></div></div>
          <details v-else><summary>查看三方内容</summary><div class="three-way"><div><small>生成基线</small><pre>{{ conflict.baseline }}</pre></div><div><small>当前现场</small><pre>{{ conflict.local }}</pre></div><div><small>新版模板</small><pre>{{ conflict.incoming }}</pre></div></div></details>
        </div>
        <ul class="change-list"><li v-for="(change, i) in plan.changes" :key="i">{{ change.key }} · {{ change.detail }}</li></ul>
        <p v-if="!plan.changes.length && !plan.conflicts.length" class="muted">配置无需改动，仅更新已应用模板版本。</p>
      </section></div>
      <template #footer><el-button :disabled="busy" @click="upgradeVisible = false">取消</el-button><el-button :loading="busy" @click="previewUpgrade">重新预览</el-button><el-button type="primary" :loading="busy" :disabled="!upgradeVerified || !upgradePlans.length || upgradePlans.some(p => !p.canApply)" @click="applyUpgrade">确认升级</el-button></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { objectTemplateApi } from '../api/objectTemplates'
import type { BatchPlan, Conflict, InstanceParameters, ObjectInstance, ObjectPlan, TemplateDetail, TemplateSummary } from '../api/objectTemplates'
import { deviceApi } from '../api'
import { errorMessage } from '../api/http'
import type { Device } from '../types'
import { formatTime } from '../utils'
const router = useRouter(), templates = ref<TemplateSummary[]>([]), instances = ref<ObjectInstance[]>([]), devices = ref<Device[]>([])
const tab = ref('templates'), filterTemplate = ref<number>(), selected = ref<ObjectInstance[]>([]), loading = ref(false), busy = ref(false)
const filteredInstances = computed(() => instances.value.filter(i => !filterTemplate.value || i.templateId === filterTemplate.value))
function setSelection(rows: ObjectInstance[]): void { selected.value = rows }
function conflictLabel(conflict: Conflict): string {
  const [kind, member, field] = conflict.key.split('.')
  const fields: Record<string, string> = { Name: '名称', DeviceId: '采集设备', Address: '地址', DataType: '数据类型', Area: '区域', Unit: '单位', Scale: '倍率', Offset: '偏移量', MinValue: '量程下限', MaxValue: '量程上限', Writable: '可写状态', Description: '描述', ArchiveMode: '归档方式', Deadband: '归档死区', ArchivePeriodSec: '归档周期', TagId: '绑定测点', Type: '报警类型', Threshold: '阈值', Level: '报警级别', DelaySec: '延时', RecoverDeadband: '恢复死区', Message: '消息', Enabled: '启用状态' }
  if (kind !== 'tag' && kind !== 'alarm') return conflict.label
  const label = `${kind === 'tag' ? '测点' : '报警'} ${member}`
  return field === '$remove' ? `模板已移除${label}，请选择保留或删除` : field === '$missing' ? `本地缺少${label}，需要重新生成` : `${label} · ${fields[field] ?? field}`
}
const createVisible = ref(false), creatingTemplate = ref<TemplateSummary>(), createVersion = ref(0), createVersions = ref<TemplateDetail['versions']>([])
const batch = reactive({ deviceId: 0, prefix: '设备', first: 1, count: 1, baseAddress: 0, stride: 10 })
const createParameters = computed<InstanceParameters[]>(() => Array.from({ length: Math.min(100, Math.max(0, batch.count || 0)) }, (_, i) => ({ name: `${batch.prefix}${String(batch.first + i).padStart(2, '0')}`, deviceId: batch.deviceId, baseAddress: batch.baseAddress + batch.stride * i })))
const createPlan = ref<BatchPlan>(), reviewedCreate = ref<InstanceParameters[]>([])
watch([createParameters, createVersion], () => { createPlan.value = undefined })
const upgradeVisible = ref(false), upgrading = ref<ObjectInstance[]>([]), upgradeVersion = ref(0), upgradeVersions = ref<TemplateDetail['versions']>([])
const upgradePlans = ref<ObjectPlan[]>([]), resolutions = reactive<Record<number, Record<string, string>>>({}), upgradeVerified = ref(false)
watch(upgradeVersion, () => { upgradePlans.value = []; upgradeVerified.value = false; for (const row of upgrading.value) resolutions[row.id] = {} })
async function load(): Promise<void> { loading.value = true; try { [templates.value, instances.value, devices.value] = await Promise.all([objectTemplateApi.list(), objectTemplateApi.instances(), deviceApi.list()]) } catch (e) { ElMessage.error(errorMessage(e)) } finally { loading.value = false } }
async function createTemplate(): Promise<void> {
  try { const { value } = await ElMessageBox.prompt('输入设备模板名称（以水泵示例开始，可自由修改）', '新建模板', { inputValue: '水泵模板', inputValidator: (v: string) => !!v?.trim() && v.trim().length <= 100 || '请输入 1～100 个字符' }); const created = await objectTemplateApi.create(value, await objectTemplateApi.pump()); await router.push(`/config/object-templates/${created.id}`) }
  catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) }
}
async function deleteTemplate(template: TemplateSummary): Promise<void> { try { await ElMessageBox.confirm(`删除模板“${template.name}”及其发布历史？存在关联实例时无法删除。`, '删除模板', { type: 'warning' }); await objectTemplateApi.remove(template.id, template.revision); await load() } catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) } }
async function openCreate(template: TemplateSummary): Promise<void> { try { createVersions.value = (await objectTemplateApi.get(template.id)).versions; creatingTemplate.value = template; createVersion.value = template.latestVersion; batch.deviceId = devices.value[0]?.id ?? 0; createPlan.value = undefined; createVisible.value = true } catch (e) { ElMessage.error(errorMessage(e)) } }
async function previewCreate(): Promise<void> {
  if (!creatingTemplate.value || busy.value) return
  busy.value = true; const parameters = JSON.parse(JSON.stringify(createParameters.value)), version = createVersion.value
  try { const result = await objectTemplateApi.previewCreate(creatingTemplate.value.id, version, parameters); createPlan.value = result; reviewedCreate.value = parameters }
  catch (e) { ElMessage.error(errorMessage(e)) } finally { busy.value = false }
}
async function applyCreate(): Promise<void> {
  if (!creatingTemplate.value || !createPlan.value?.canApply || busy.value) return
  busy.value = true
  try { const result = await objectTemplateApi.instantiate(creatingTemplate.value.id, createVersion.value, reviewedCreate.value); ElMessage.success(`已生成 ${result.length} 个对象，设备面板待检查发布`); createVisible.value = false; tab.value = 'instances'; await load() }
  catch (e) { ElMessage.error(errorMessage(e)); createPlan.value = undefined } finally { busy.value = false }
}
async function openUpgrade(rows: ObjectInstance[]): Promise<void> {
  if (new Set(rows.map(r => r.templateId)).size !== 1) { ElMessage.warning('请每次选择同一模板的对象'); return }
  try {
    const template = await objectTemplateApi.get(rows[0].templateId)
    upgradeVersions.value = template.versions.filter(v => rows.every(r => v.version > r.appliedVersion))
    if (!upgradeVersions.value.length) { ElMessage.info('选中对象已应用最新版本，或没有共同的更高版本'); return }
    upgrading.value = [...rows]; for (const row of rows) resolutions[row.id] = {}
    upgradeVersion.value = upgradeVersions.value[0].version; upgradePlans.value = []; upgradeVerified.value = false; upgradeVisible.value = true
  } catch (e) { ElMessage.error(errorMessage(e)) }
}
async function previewUpgrade(): Promise<void> {
  if (busy.value) return
  busy.value = true; upgradeVerified.value = false
  try { upgradePlans.value = await Promise.all(upgrading.value.map(i => objectTemplateApi.previewUpgrade(i, upgradeVersion.value, resolutions[i.id]))); upgradeVerified.value = true }
  catch (e) { ElMessage.error(errorMessage(e)) } finally { busy.value = false }
}
async function resetResolutions(): Promise<void> { for (const row of upgrading.value) resolutions[row.id] = {}; await previewUpgrade() }
async function applyUpgrade(): Promise<void> {
  if (busy.value || !upgradeVerified.value || upgradePlans.value.some(p => !p.canApply)) return
  busy.value = true
  try { await objectTemplateApi.upgrade(upgradePlans.value.map(p => ({ id: p.instanceId!, version: p.version, revision: p.revision, expectedState: p.state, resolutions: resolutions[p.instanceId!] }))); ElMessage.success('对象模板升级完成，面板修改已保存为草稿'); upgradeVisible.value = false; selected.value = []; await load() }
  catch (e) { ElMessage.error(errorMessage(e)); upgradeVerified.value = false } finally { busy.value = false }
}
async function detach(instance: ObjectInstance): Promise<void> { try { await ElMessageBox.confirm(`解除“${instance.name}”的模板关联？测点、报警和面板均保留，后续不再随模板升级。`, '解除模板关联', { type: 'warning' }); await objectTemplateApi.detach(instance.id, instance.revision); await load() } catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) } }
onMounted(load)
</script>
<style scoped>
.template-cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(360px, 1fr)); gap: 18px; }.template-card { border: 1px solid #e2e8f0; background: white; border-radius: 10px; padding: 22px; }
.card-top { display: flex; align-items: center; justify-content: space-between; }.template-icon { font-size: 36px; color: #1668dc; }.template-card p { font-size: 12px; }
.preview-results, .upgrade-plan { margin-top: 20px; }.plan-row { border-bottom: 1px solid #e2e8f0; padding: 8px; display: flex; align-items: center; gap: 20px; flex-wrap: wrap; }.error { color: #dc2626; width: 100%; margin: 4px 0; }
.conflict { padding: 14px; margin-top: 10px; background: #fff8eb; border: 1px solid #f3d9a2; border-radius: 6px; }.three-way { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; }.three-way pre { max-height: 160px; overflow: auto; white-space: pre-wrap; word-break: break-all; font-size: 11px; background: white; padding: 8px; }
.upgrade-plan { border-top: 1px solid #e2e8f0; padding-top: 14px; }.change-list { font-size: 12px; color: #64748b; line-height: 1.9; }summary { cursor: pointer; color: #926320; font-size: 12px; }.object-library :deep(.el-alert) { margin: 8px 0; }
</style>
