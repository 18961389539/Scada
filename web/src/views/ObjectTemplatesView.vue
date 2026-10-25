<template>
  <div class="page object-library" v-loading="loading">
    <div class="panel">
    <div class="toolbar"><span class="hint">{{ $t('objectTemplatesView.k2e994e') }}</span><span class="spacer" /><el-button @click="load">{{ $t('objectTemplatesView.k38108e') }}</el-button><el-button type="primary" @click="createTemplate">{{ $t('objectTemplatesView.ka38a51') }}</el-button></div>
    <el-tabs v-model="tab">
      <el-tab-pane :label="$t('objectTemplatesView.k772a18')" name="templates">
        <div class="template-cards"><article v-for="template in templates" :key="template.id" class="template-card">
          <div class="card-top"><span class="template-icon">▦</span><el-tag :type="template.latestVersion ? 'success' : 'info'">{{ template.latestVersion ? $t('objectTemplatesView.kaae34f', [template.latestVersion]) : $t('objectTemplatesView.k0f4368') }}</el-tag></div>
          <h3>{{ template.name }}</h3><p class="muted">{{ $t('objectTemplatesView.keadec0', [template.instanceCount, formatTime(template.updatedTs)]) }}</p>
          <div class="toolbar"><el-button @click="router.push(`/config/object-templates/${template.id}`)">{{ $t('objectTemplatesView.kf5b7af') }}</el-button><el-button type="primary" :disabled="!template.latestVersion" @click="openCreate(template)">{{ $t('objectTemplatesView.k3d2981') }}</el-button><el-button type="danger" link @click="deleteTemplate(template)">{{ $t('objectTemplatesView.k3755f5') }}</el-button></div>
        </article></div>
        <el-empty v-if="!templates.length && !loading" :description="$t('objectTemplatesView.k46216f')" :image-size="80">
          <el-button type="primary" @click="createTemplate">{{ $t('objectTemplatesView.ka38a51') }}</el-button>
        </el-empty>
      </el-tab-pane>
      <el-tab-pane :label="$t('objectTemplatesView.k8487df')" name="instances">
        <div class="toolbar"><el-select v-model="filterTemplate" clearable :placeholder="$t('objectTemplatesView.k146966')" style="width: 220px"><el-option v-for="t in templates" :key="t.id" :label="t.name" :value="t.id" /></el-select><span class="spacer" />
          <el-button :disabled="!selected.length" type="primary" @click="openUpgrade(selected)">{{ $t('objectTemplatesView.k5c03b0') }}</el-button></div>
        <el-table :data="filteredInstances" row-key="id" @selection-change="setSelection">
          <el-table-column type="selection" width="45" /><el-table-column prop="name" :label="$t('objectTemplatesView.kd17761')" min-width="130" /><el-table-column prop="deviceName" :label="$t('objectTemplatesView.k14b858')" min-width="130" />
          <el-table-column prop="templateName" :label="$t('objectTemplatesView.k935a81')" min-width="130" /><el-table-column prop="baseAddress" :label="$t('objectTemplatesView.kbd3da4')" width="90" />
          <el-table-column :label="$t('objectTemplatesView.k989d1a')" width="140"><template #default="{ row }"><el-tag :type="row.appliedVersion < row.latestVersion ? 'warning' : 'success'">v{{ row.appliedVersion }} / v{{ row.latestVersion }}</el-tag></template></el-table-column>
          <el-table-column :label="$t('objectTemplatesView.kf3ea6d')" min-width="240"><template #default="{ row }"><el-button link type="primary" @click="router.push(`/config/screens/${row.screenId}`)">{{ $t('objectTemplatesView.k164a47') }}</el-button><el-button link :disabled="row.appliedVersion >= row.latestVersion" @click="openUpgrade([row])">{{ $t('objectTemplatesView.k542c56') }}</el-button><el-button link type="danger" @click="detach(row)">{{ $t('objectTemplatesView.k25a139') }}</el-button></template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
    </div>

    <el-dialog v-model="createVisible" :title="$t('objectTemplatesView.kb92d02', [creatingTemplate?.name ?? ''])" width="900px" :close-on-click-modal="false">
      <div :inert="busy"><el-form inline label-width="90px">
        <el-form-item :label="$t('objectTemplatesView.k136cdd')"><el-select v-model="createVersion" style="width: 130px" :aria-label="$t('objectTemplatesView.k5cb827')"><el-option v-for="v in createVersions" :key="v.version" :label="`v${v.version}`" :value="v.version" /></el-select></el-form-item>
        <el-form-item :label="$t('objectTemplatesView.k14b858')"><el-select v-model="batch.deviceId" filterable style="width: 220px" :aria-label="$t('objectTemplatesView.k4390e7')"><el-option v-for="d in devices" :key="d.id" :label="d.name" :value="d.id" /></el-select></el-form-item>
        <el-form-item :label="$t('objectTemplatesView.kdd4f17')"><el-input v-model="batch.prefix" :aria-label="$t('objectTemplatesView.k955619')" maxlength="35" style="width: 160px" /></el-form-item>
        <el-form-item :label="$t('objectTemplatesView.k01857d')"><el-input-number v-model="batch.first" :min="1" :max="99999" :aria-label="$t('objectTemplatesView.k01857d')" /></el-form-item>
        <el-form-item :label="$t('objectTemplatesView.k7a0819')"><el-input-number v-model="batch.count" :min="1" :max="100" :aria-label="$t('objectTemplatesView.k7a0819')" /></el-form-item>
        <el-form-item :label="$t('objectTemplatesView.k1ebd28')"><el-input-number v-model="batch.baseAddress" :min="0" :max="65535" :aria-label="$t('objectTemplatesView.k1ebd28')" /></el-form-item>
        <el-form-item :label="$t('objectTemplatesView.kcd4fea')"><el-input-number v-model="batch.stride" :min="0" :max="65535" :aria-label="$t('objectTemplatesView.kcd4fea')" /></el-form-item>
      </el-form>
      <el-alert :title="$t('objectTemplatesView.kc11fda')" type="info" :closable="false" />
      <el-table :data="createParameters" max-height="230" size="small"><el-table-column prop="name" :label="$t('objectTemplatesView.kd17761')" /><el-table-column prop="baseAddress" :label="$t('objectTemplatesView.kbd3da4')" /><el-table-column :label="$t('objectTemplatesView.k767b3d')"><template #default="{ row }">{{ $t('objectTemplatesView.kb634c6', [row.baseAddress]) }}</template></el-table-column></el-table>
      <div v-if="createPlan" class="preview-results"><el-alert v-for="(error, i) in createPlan.errors" :key="i" :title="error" type="error" :closable="false" />
        <div v-for="plan in createPlan.plans" :key="plan.name" class="plan-row"><strong>{{ plan.name }}</strong><span>{{ $t('objectTemplatesView.kc5d67a', [plan.tagCount, plan.alarmCount, plan.panelNodeCount]) }}</span><el-tag :type="plan.canApply ? 'success' : 'danger'">{{ plan.canApply ? $t('objectTemplatesView.k535b37') : $t('objectTemplatesView.k5da1c3') }}</el-tag><p v-for="error in plan.errors" :key="error" class="error">{{ error }}</p></div>
      </div></div>
      <template #footer><el-button :disabled="busy" @click="createVisible = false">{{ $t('objectTemplatesView.k4d0b46') }}</el-button><el-button :loading="busy" @click="previewCreate">{{ $t('objectTemplatesView.k888a50') }}</el-button><el-button type="primary" :loading="busy" :disabled="!createPlan?.canApply" @click="applyCreate">{{ $t('objectTemplatesView.kbce024') }}</el-button></template>
    </el-dialog>

    <el-dialog v-model="upgradeVisible" :title="$t('objectTemplatesView.k21aa98')" width="1000px" :close-on-click-modal="false">
      <div :inert="busy"><div class="toolbar"><span>{{ $t('objectTemplatesView.kd56fb1', [upgrading.length]) }}</span><el-select v-model="upgradeVersion" :aria-label="$t('objectTemplatesView.kec167c')" style="width: 180px"><el-option v-for="v in upgradeVersions" :key="v.version" :value="v.version" :label="$t('objectTemplatesView.k12268c', [v.version])" /></el-select><span class="spacer" /><el-button @click="resetResolutions">{{ $t('objectTemplatesView.kd9a726') }}</el-button><el-button @click="previewUpgrade">{{ upgradePlans.length ? $t('objectTemplatesView.k15fec6') : $t('objectTemplatesView.k542c56') }}</el-button></div>
      <el-alert :title="$t('objectTemplatesView.keccc36')" type="info" :closable="false" />
      <section v-for="plan in upgradePlans" :key="plan.instanceId!" class="upgrade-plan">
        <div class="toolbar"><strong>{{ plan.name }} → v{{ plan.version }}</strong><span class="spacer" /><el-tag :type="plan.canApply ? 'success' : 'warning'">{{ plan.canApply ? $t('objectTemplatesView.k546591') : $t('objectTemplatesView.k0a17df') }}</el-tag></div>
        <el-alert v-for="error in plan.errors" :key="error" :title="error" type="error" :closable="false" />
        <div v-for="conflict in plan.conflicts" :key="conflict.key" class="conflict">
          <div class="toolbar"><strong>{{ conflictLabel(conflict) }}</strong><span class="spacer" />
            <el-select v-model="resolutions[plan.instanceId!][conflict.key]" :placeholder="$t('objectTemplatesView.k72b064')" :aria-label="$t('objectTemplatesView.k2d6111')" style="width: 270px" @change="upgradeVerified = false">
              <el-option v-for="choice in conflict.choices" :key="choice" :value="choice" :label="choice === 'keepLocal' ? $t('objectTemplatesView.k218028') : $t('objectTemplatesView.k0df994')" /></el-select>
          </div>
          <div v-if="[conflict.baseline, conflict.local, conflict.incoming].every(v => v.length <= 120)" class="three-way"><div><small>{{ $t('objectTemplatesView.k782898') }}</small><pre>{{ conflict.baseline }}</pre></div><div><small>{{ $t('objectTemplatesView.kc36201') }}</small><pre>{{ conflict.local }}</pre></div><div><small>{{ $t('objectTemplatesView.k2996cf') }}</small><pre>{{ conflict.incoming }}</pre></div></div>
          <details v-else><summary>{{ $t('objectTemplatesView.k9de104') }}</summary><div class="three-way"><div><small>{{ $t('objectTemplatesView.k782898') }}</small><pre>{{ conflict.baseline }}</pre></div><div><small>{{ $t('objectTemplatesView.kc36201') }}</small><pre>{{ conflict.local }}</pre></div><div><small>{{ $t('objectTemplatesView.k2996cf') }}</small><pre>{{ conflict.incoming }}</pre></div></div></details>
        </div>
        <ul class="change-list"><li v-for="(change, i) in plan.changes" :key="i">{{ change.key }} · {{ change.detail }}</li></ul>
        <p v-if="!plan.changes.length && !plan.conflicts.length" class="muted">{{ $t('objectTemplatesView.k49c9fe') }}</p>
      </section></div>
      <template #footer><el-button :disabled="busy" @click="upgradeVisible = false">{{ $t('objectTemplatesView.k4d0b46') }}</el-button><el-button :loading="busy" @click="previewUpgrade">{{ $t('objectTemplatesView.k15fec6') }}</el-button><el-button type="primary" :loading="busy" :disabled="!upgradeVerified || !upgradePlans.length || upgradePlans.some(p => !p.canApply)" @click="applyUpgrade">{{ $t('objectTemplatesView.k8c3fd9') }}</el-button></template>
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
import { tr } from '../i18n'
const router = useRouter(), templates = ref<TemplateSummary[]>([]), instances = ref<ObjectInstance[]>([]), devices = ref<Device[]>([])
const tab = ref('templates'), filterTemplate = ref<number>(), selected = ref<ObjectInstance[]>([]), loading = ref(false), busy = ref(false)
const filteredInstances = computed(() => instances.value.filter(i => !filterTemplate.value || i.templateId === filterTemplate.value))
function setSelection(rows: ObjectInstance[]): void { selected.value = rows }
function conflictLabel(conflict: Conflict): string {
  const [kind, member, field] = conflict.key.split('.')
  const fields: Record<string, string> = { Name: tr('objectTemplatesView.k1be7ae'), DeviceId: tr('objectTemplatesView.k14b858'), Address: tr('objectTemplatesView.k67d2d7'), DataType: tr('objectTemplatesView.kb031dc'), Area: tr('objectTemplatesView.k17fc93'), Unit: tr('objectTemplatesView.k8ec8c8'), Scale: tr('objectTemplatesView.k8482e1'), Offset: tr('objectTemplatesView.ka87924'), MinValue: tr('objectTemplatesView.k6e3463'), MaxValue: tr('objectTemplatesView.k9a96e0'), Writable: tr('objectTemplatesView.k6d1637'), Description: tr('objectTemplatesView.k412f54'), ArchiveMode: tr('objectTemplatesView.ka4edb9'), Deadband: tr('objectTemplatesView.k76d7e1'), ArchivePeriodSec: tr('objectTemplatesView.k7b4fb2'), TagId: tr('objectTemplatesView.k586b61'), Type: tr('objectTemplatesView.k9f23cf'), Threshold: tr('objectTemplatesView.ka05320'), Level: tr('objectTemplatesView.kad7723'), DelaySec: tr('objectTemplatesView.k5157f1'), RecoverDeadband: tr('objectTemplatesView.k3b86db'), Message: tr('objectTemplatesView.kdc6de3'), Enabled: tr('objectTemplatesView.k5691b3') }
  if (kind !== 'tag' && kind !== 'alarm') return conflict.label
  const label = tr('objectTemplatesView.kb245f5', [kind === 'tag' ? tr('objectTemplatesView.k8cff1c') : tr('objectTemplatesView.k9f45b3'), member])
  return field === '$remove' ? tr('objectTemplatesView.k45d29a', [label]) : field === '$missing' ? tr('objectTemplatesView.k11fc19', [label]) : `${label} · ${fields[field] ?? field}`
}
const createVisible = ref(false), creatingTemplate = ref<TemplateSummary>(), createVersion = ref(0), createVersions = ref<TemplateDetail['versions']>([])
const batch = reactive({ deviceId: 0, prefix: tr('objectTemplatesView.k01f2c1'), first: 1, count: 1, baseAddress: 0, stride: 10 })
const createParameters = computed<InstanceParameters[]>(() => Array.from({ length: Math.min(100, Math.max(0, batch.count || 0)) }, (_, i) => ({ name: `${batch.prefix}${String(batch.first + i).padStart(2, '0')}`, deviceId: batch.deviceId, baseAddress: batch.baseAddress + batch.stride * i })))
const createPlan = ref<BatchPlan>(), reviewedCreate = ref<InstanceParameters[]>([])
watch([createParameters, createVersion], () => { createPlan.value = undefined })
const upgradeVisible = ref(false), upgrading = ref<ObjectInstance[]>([]), upgradeVersion = ref(0), upgradeVersions = ref<TemplateDetail['versions']>([])
const upgradePlans = ref<ObjectPlan[]>([]), resolutions = reactive<Record<number, Record<string, string>>>({}), upgradeVerified = ref(false)
watch(upgradeVersion, () => { upgradePlans.value = []; upgradeVerified.value = false; for (const row of upgrading.value) resolutions[row.id] = {} })
async function load(): Promise<void> { loading.value = true; try { [templates.value, instances.value, devices.value] = await Promise.all([objectTemplateApi.list(), objectTemplateApi.instances(), deviceApi.list()]) } catch (e) { ElMessage.error(errorMessage(e)) } finally { loading.value = false } }
async function createTemplate(): Promise<void> {
  try { const { value } = await ElMessageBox.prompt(tr('objectTemplatesView.k8bb9ae'), tr('objectTemplatesView.ka38a51'), { inputValue: tr('objectTemplatesView.k040e7b'), inputValidator: (v: string) => !!v?.trim() && v.trim().length <= 100 || tr('objectTemplatesView.kaa0b13') }); const created = await objectTemplateApi.create(value, await objectTemplateApi.pump()); await router.push(`/config/object-templates/${created.id}`) }
  catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) }
}
async function deleteTemplate(template: TemplateSummary): Promise<void> { try { await ElMessageBox.confirm(tr('objectTemplatesView.k21de6e', [template.name]), tr('objectTemplatesView.kb31745'), { type: 'warning' }); await objectTemplateApi.remove(template.id, template.revision); await load() } catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) } }
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
  try { const result = await objectTemplateApi.instantiate(creatingTemplate.value.id, createVersion.value, reviewedCreate.value); ElMessage.success(tr('objectTemplatesView.k0890f0', [result.length])); createVisible.value = false; tab.value = 'instances'; await load() }
  catch (e) { ElMessage.error(errorMessage(e)); createPlan.value = undefined } finally { busy.value = false }
}
async function openUpgrade(rows: ObjectInstance[]): Promise<void> {
  if (new Set(rows.map(r => r.templateId)).size !== 1) { ElMessage.warning(tr('objectTemplatesView.k6df90c')); return }
  try {
    const template = await objectTemplateApi.get(rows[0].templateId)
    upgradeVersions.value = template.versions.filter(v => rows.every(r => v.version > r.appliedVersion))
    if (!upgradeVersions.value.length) { ElMessage.info(tr('objectTemplatesView.kaca088')); return }
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
  try { await objectTemplateApi.upgrade(upgradePlans.value.map(p => ({ id: p.instanceId!, version: p.version, revision: p.revision, expectedState: p.state, resolutions: resolutions[p.instanceId!] }))); ElMessage.success(tr('objectTemplatesView.k310451')); upgradeVisible.value = false; selected.value = []; await load() }
  catch (e) { ElMessage.error(errorMessage(e)); upgradeVerified.value = false } finally { busy.value = false }
}
async function detach(instance: ObjectInstance): Promise<void> { try { await ElMessageBox.confirm(tr('objectTemplatesView.ke35541', [instance.name]), tr('objectTemplatesView.k4c9372'), { type: 'warning' }); await objectTemplateApi.detach(instance.id, instance.revision); await load() } catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) } }
onMounted(load)
</script>
<style scoped>
.template-cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(360px, 1fr)); gap: 18px; }.template-card { border: 1px solid var(--scada-border); background: var(--scada-panel); border-radius: 10px; padding: 22px; }
.card-top { display: flex; align-items: center; justify-content: space-between; }.template-icon { font-size: 36px; color: var(--scada-primary); }.template-card p { font-size: 12px; }
.preview-results, .upgrade-plan { margin-top: 20px; }.plan-row { border-bottom: 1px solid var(--scada-border); padding: 8px; display: flex; align-items: center; gap: 20px; flex-wrap: wrap; }.error { color: var(--scada-danger); width: 100%; margin: 4px 0; }
.conflict { padding: 14px; margin-top: 10px; background: var(--el-color-warning-light-9); border: 1px solid var(--el-color-warning-light-5); border-radius: 6px; }.three-way { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; }.three-way pre { max-height: 160px; overflow: auto; white-space: pre-wrap; word-break: break-all; font-size: 11px; background: var(--scada-panel-muted); padding: 8px; }
.upgrade-plan { border-top: 1px solid var(--scada-border); padding-top: 14px; }.change-list { font-size: 12px; color: var(--scada-text-muted); line-height: 1.9; }summary { cursor: pointer; color: var(--scada-warning); font-size: 12px; }.object-library :deep(.el-alert) { margin: 8px 0; }
</style>
