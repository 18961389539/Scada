<template>
  <el-dialog v-model="visible" :title="form.id ? $t('controlView.k38ca13') : $t('controlView.k69e7ba')" width="1240px" :before-close="guard.beforeClose" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving" data-test="sequence-dialog" @keydown="guard.onKey">
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="sequence-save-error" />
    <div :inert="saving" :aria-busy="saving">
    <el-form label-width="90px" size="small" :disabled="saving">
      <el-form-item :label="$t('controlView.k1be7ae')"><el-input v-model="form.name" maxlength="100" data-test="sequence-name" /></el-form-item>
      <el-form-item :label="$t('controlView.k17fc93')"><el-select v-model="form.areaId" style="width: 200px" data-test="sequence-area"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select>
        <el-checkbox v-model="form.dualApproval" style="margin-left: 16px">{{ $t('controlView.kf88a83') }}</el-checkbox></el-form-item>
      <el-form-item :label="$t('controlView.k26670d')"><el-input v-model="form.description" maxlength="500" /></el-form-item>
    </el-form>
    <div class="toolbar">
      <el-radio-group v-model="editorMode" size="small" data-test="sequence-editor-mode"><el-radio-button value="flow">{{ $t('controlView.k3bffba') }}</el-radio-button><el-radio-button value="table">{{ $t('controlView.k150074') }}</el-radio-button></el-radio-group>
      <template v-if="editorMode === 'flow'">
        <el-dropdown trigger="click" @command="(type: ControlStepType) => addStep(type)"><el-button size="small" data-test="flow-add">{{ $t('controlView.k2517d1') }}</el-button>
          <template #dropdown><el-dropdown-menu><el-dropdown-item v-for="(label, key) in stepTypeLabels" :key="key" :command="key" :data-test="`flow-add-${key}`">{{ label }}</el-dropdown-item></el-dropdown-menu></template>
        </el-dropdown>
      </template>
      <span class="spacer" />
      <el-tag v-if="validation" size="small" :type="validation.ok ? 'success' : 'danger'" data-test="sequence-validation">{{ validation.ok ? $t('controlView.k7035f8') : validation.message }}</el-tag>
    </div>
    <div v-if="editorMode === 'flow'" class="flow-editor">
      <SequenceFlow v-model="selectedStepId" :steps="form.steps" :editable="!saving" :error-ids="validation?.stepIds ?? []" :describe="describeStep" :height="520" style="flex: 1; min-width: 0" @changed="scheduleValidate()" />
      <div class="flow-props" data-test="flow-props">
        <template v-if="selectedStep">
          <el-form label-width="76px" size="small">
            <el-form-item :label="$t('controlView.k9f42da')"><el-input :model-value="selectedStep.id ?? ''" data-test="prop-id" @change="(v: string) => renameStep(selectedStep!, v)" /></el-form-item>
            <el-form-item :label="$t('controlView.ke4e46c')"><el-select v-model="selectedStep.type" data-test="prop-type" @change="scheduleValidate()"><el-option v-for="(label, key) in stepTypeLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item>
            <el-form-item :label="$t('controlView.k07d0b5')"><el-input v-model="selectedStep.name" data-test="prop-name" /></el-form-item>
            <template v-if="selectedStep.type === 'write'">
              <el-form-item :label="$t('controlView.kc570f0')"><el-select v-model="selectedStep.tagId" filterable data-test="prop-tag" @change="scheduleValidate()"><el-option v-for="t in tags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select></el-form-item>
              <el-form-item :label="$t('controlView.k5ae08a')"><el-input-number v-model="selectedStep.value" :controls="false" data-test="prop-value" @change="scheduleValidate()" /></el-form-item>
              <el-form-item :label="$t('controlView.k04d7bd')"><el-input v-model="selectedStep.confirmExpression" :placeholder="$t('controlView.kc64017')" @change="scheduleValidate()" /></el-form-item>
            </template>
            <el-form-item v-if="selectedStep.type === 'wait' || selectedStep.type === 'condition'" :label="$t('controlView.k72f1cf')"><el-input v-model="selectedStep.expression" :placeholder="$t('controlView.kc135ba')" data-test="prop-expression" @change="scheduleValidate()" /></el-form-item>
            <template v-if="selectedStep.type === 'condition'">
              <el-form-item :label="$t('controlView.kd90926')"><el-select v-model="selectedStep.then" clearable @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
              <el-form-item :label="$t('controlView.k640232')"><el-select v-model="selectedStep.else" clearable :placeholder="$t('controlView.kea0ef2')" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
            </template>
            <template v-if="selectedStep.type === 'parallel'">
              <el-form-item :label="$t('controlView.k2f6f9d')"><el-select v-model="selectedStep.branches" multiple @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
              <el-form-item :label="$t('controlView.k871c34')"><el-select v-model="selectedStep.join" @change="scheduleValidate()"><el-option v-for="id in joinIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
            </template>
            <el-form-item v-if="selectedStep.type === 'join'" :label="$t('controlView.k2607fc')"><el-radio-group v-model="selectedStep.joinMode" @change="scheduleValidate()"><el-radio-button value="all">{{ $t('controlView.k778fc8') }}</el-radio-button><el-radio-button value="any">{{ $t('controlView.k31af9a') }}</el-radio-button></el-radio-group></el-form-item>
            <template v-if="selectedStep.type === 'jump'">
              <el-form-item :label="$t('controlView.kfb25a9')"><el-select v-model="selectedStep.target" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
              <el-form-item :label="$t('controlView.k046305')"><el-input-number v-model="selectedStep.maxIterations" :min="1" :max="1000" data-test="prop-max" @change="scheduleValidate()" /></el-form-item>
            </template>
            <el-form-item v-if="['write', 'wait', 'join'].includes(selectedStep.type ?? 'write')" :label="$t('controlView.kea0ef2')"><el-select v-model="selectedStep.next" clearable :placeholder="$t('controlView.k25bc02')" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
            <el-form-item v-if="['write', 'wait'].includes(selectedStep.type ?? 'write')" :label="$t('controlView.k16436e')"><el-input-number v-model="selectedStep.timeoutMs" :min="100" :max="600000" :step="1000" data-test="prop-timeout" @change="scheduleValidate()" /></el-form-item>
            <el-form-item v-if="!['end', 'join'].includes(selectedStep.type ?? 'write')" :label="$t('controlView.kb05bd8')">
              <el-select v-model="selectedStep.onFailure" clearable :placeholder="$t('controlView.ka17f70')" style="width: 90px" data-test="prop-failure" @change="scheduleValidate()"><el-option value="stop" :label="$t('controlView.ka17f70')" /><el-option value="continue" :label="$t('controlView.k1fc1af')" /><el-option value="goto" :label="$t('controlView.kfb25a9')" /></el-select>
              <el-select v-if="selectedStep.onFailure === 'goto'" v-model="selectedStep.onFailureGoto" style="width: 90px" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
            </el-form-item>
          </el-form>
          <el-button size="small" type="danger" data-test="prop-delete" @click="deleteStep(selectedStep!)">{{ $t('controlView.k3a4b3d') }}</el-button>
        </template>
        <p v-else class="tiny muted">{{ $t('controlView.kb0349e') }}</p>
      </div>
    </div>
    <el-table v-else :data="form.steps" size="small" data-test="sequence-steps">
      <el-table-column :label="$t('controlView.k9f42da')" width="90"><template #default="{ row }"><el-input v-model="row.id" size="small" /></template></el-table-column>
      <el-table-column :label="$t('controlView.ke4e46c')" width="105"><template #default="{ row }"><el-select v-model="row.type" size="small" data-test="step-type"><el-option v-for="(label, key) in stepTypeLabels" :key="key" :value="key" :label="label" /></el-select></template></el-table-column>
      <el-table-column :label="$t('controlView.k07d0b5')" width="120"><template #default="{ row }"><el-input v-model="row.name" size="small" /></template></el-table-column>
      <el-table-column :label="$t('controlView.k163aec')" min-width="330"><template #default="{ row }">
        <div v-if="row.type === 'write'" class="step-fields">
          <el-select v-model="row.tagId" filterable size="small" style="width: 170px" :placeholder="$t('controlView.kc570f0')"><el-option v-for="t in tags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select>
          ← <el-input-number v-model="row.value" :controls="false" size="small" style="width: 70px" />
          <el-input v-model="row.confirmExpression" size="small" :placeholder="$t('controlView.k0ce1d5')" style="width: 170px" />
        </div>
        <div v-else-if="row.type === 'wait'" class="step-fields"><el-input v-model="row.expression" size="small" :placeholder="$t('controlView.kd41546')" style="width: 260px" /></div>
        <div v-else-if="row.type === 'condition'" class="step-fields">
          <el-input v-model="row.expression" size="small" :placeholder="$t('controlView.k2be2e1')" style="width: 150px" data-test="step-expression" />
          {{ $t('controlView.kd90926') }}<el-select v-model="row.then" size="small" style="width: 90px" data-test="step-then"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
          {{ $t('controlView.k640232') }}<el-select v-model="row.else" size="small" clearable :placeholder="$t('controlView.kea0ef2')" style="width: 90px" data-test="step-else"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
        </div>
        <div v-else-if="row.type === 'parallel'" class="step-fields">
          {{ $t('controlView.k2f6f9d') }}<el-select v-model="row.branches" multiple size="small" style="width: 170px"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
          {{ $t('controlView.k871c34') }}<el-select v-model="row.join" size="small" style="width: 90px"><el-option v-for="id in joinIds" :key="id" :value="id" :label="id" /></el-select>
        </div>
        <div v-else-if="row.type === 'join'" class="step-fields"><el-radio-group v-model="row.joinMode" size="small"><el-radio-button value="all">{{ $t('controlView.k79236c') }}</el-radio-button><el-radio-button value="any">{{ $t('controlView.kf690a1') }}</el-radio-button></el-radio-group></div>
        <div v-else-if="row.type === 'jump'" class="step-fields">
          {{ $t('controlView.kfb25a9') }}<el-select v-model="row.target" size="small" style="width: 90px"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
          {{ $t('controlView.k4c5dfc') }}<el-input-number v-model="row.maxIterations" :min="1" :max="1000" size="small" style="width: 90px" />{{ $t('controlView.k5e5b81') }}
        </div>
        <span v-else class="tiny muted">{{ $t('controlView.k4f4393') }}</span>
      </template></el-table-column>
      <el-table-column :label="$t('controlView.kea0ef2')" width="95"><template #default="{ row }"><el-select v-if="['write', 'wait', 'join'].includes(row.type)" v-model="row.next" size="small" clearable :placeholder="$t('controlView.k25bc02')"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></template></el-table-column>
      <el-table-column :label="$t('controlView.k16436e')" width="90"><template #default="{ row }"><el-input-number v-if="['write', 'wait'].includes(row.type)" v-model="row.timeoutMs" :controls="false" size="small" style="width: 75px" /></template></el-table-column>
      <el-table-column :label="$t('controlView.kb05bd8')" width="170"><template #default="{ row }">
        <template v-if="!['end', 'join'].includes(row.type)">
          <el-select v-model="row.onFailure" size="small" style="width: 75px" clearable :placeholder="$t('controlView.ka17f70')"><el-option value="stop" :label="$t('controlView.ka17f70')" /><el-option value="continue" :label="$t('controlView.k1fc1af')" /><el-option value="goto" :label="$t('controlView.kfb25a9')" /></el-select>
          <el-select v-if="row.onFailure === 'goto'" v-model="row.onFailureGoto" size="small" style="width: 80px"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
        </template>
      </template></el-table-column>
      <el-table-column label="" width="70"><template #default="{ $index }">
        <el-button size="small" link :disabled="$index === 0" @click="moveStep($index)">↑</el-button><el-button size="small" link type="danger" @click="form.steps.splice($index, 1)">{{ $t('controlView.k38cadf') }}</el-button>
      </template></el-table-column>
    </el-table>
    <el-button v-if="editorMode === 'table'" size="small" style="margin-top: 8px" data-test="sequence-add-step" @click="addStep()">{{ $t('controlView.k926319') }}</el-button>
    <p class="tiny muted">{{ $t('controlView.k9ae2cd') }}</p>
    </div>
    <template #footer><el-button :disabled="saving" @click="guard.close()">{{ $t('controlView.k4d0b46') }}</el-button><el-button type="primary" :loading="saving" data-test="sequence-save" @click="saveSequence()">{{ $t('controlView.kfadf24') }}</el-button></template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { controlApi, type ControlSequence, type ControlStep, type ControlStepType } from '../../api/control'
import { errorMessage } from '../../api/http'
import { tr } from '../../i18n'
import { useEditDialog } from '../../composables/useEditDialog'
import SequenceFlow from '../sequence/SequenceFlow.vue'

const props = defineProps<{
  areas: { id: number; name: string }[]
  tags: { id: number }[]
  tagName: (id: number | null) => string
  describeStep: (s: ControlStep) => string
}>()
const emit = defineEmits<{ saved: [] }>()

const visible = ref(false), saving = ref(false), error = ref('')
const optionalText = (value: string | null | undefined): string | null => value === '' || value == null ? null : value
const form = reactive<{ id?: number; name: string; areaId: number; description: string | null; dualApproval: boolean; revision: number; steps: ControlStep[] }>({ name: '', areaId: 1, description: null, dualApproval: false, revision: 0, steps: [] })
let disposed = false

const guard = useEditDialog({
  visible,
  saving,
  snapshot: () => JSON.stringify({
    ...form,
    description: optionalText(form.description),
    steps: form.steps.map(step => ({
      ...step,
      confirmExpression: optionalText(step.confirmExpression), expression: optionalText(step.expression),
      next: optionalText(step.next), then: optionalText(step.then), else: optionalText(step.else),
      join: optionalText(step.join), target: optionalText(step.target),
      onFailure: optionalText(step.onFailure), onFailureGoto: optionalText(step.onFailureGoto)
    }))
  }),
  save: saveSequence
})

const stepTypeLabels: Record<string, string> = { write: tr('controlView.kc570f0'), wait: tr('controlView.k25d23b'), condition: tr('controlView.k315a7e'), parallel: tr('controlView.k090e93'), join: tr('controlView.k871c34'), jump: tr('controlView.k817b2f'), end: tr('controlView.k76b988') }
const stepIds = computed(() => form.steps.map(x => x.id).filter((x): x is string => !!x))
const joinIds = computed(() => form.steps.filter(x => x.type === 'join').map(x => x.id).filter((x): x is string => !!x))
const editorMode = ref<'flow' | 'table'>('flow')
const selectedStepId = ref<string | null>(null)
const selectedStep = computed(() => form.steps.find(s => s.id === selectedStepId.value) ?? null)
const validation = ref<{ ok: boolean; message: string | null; stepIds: string[] } | null>(null)
let validateTimer: number | undefined

function addStep(type: ControlStepType = 'write'): void {
  const used = new Set(stepIds.value); let n = form.steps.length + 1; while (used.has(`s${n}`)) n++
  const step: ControlStep = { id: `s${n}`, type, name: type === 'write' ? tr('controlView.k182817', [n]) : `${stepTypeLabels[type]}${n}`, tagId: props.tags[0]?.id ?? 0, value: 0, confirmExpression: null, timeoutMs: 10000, delayMs: 0, joinMode: 'all', maxIterations: 3 }
  // 图形模式下新节点放在现有节点下方，避免与自动布局重叠。
  if (editorMode.value === 'flow') { const ys = form.steps.map(s => s.y ?? 0); step.x = 20; step.y = (ys.length ? Math.max(...ys) : 0) + 112 }
  form.steps.push(step); selectedStepId.value = step.id!; scheduleValidate()
}
function scheduleValidate(): void {
  window.clearTimeout(validateTimer)
  validateTimer = window.setTimeout(async () => {
    if (!form.steps.length) { validation.value = null; return }
    try { validation.value = await controlApi.validateSequence({ ...form, name: form.name || 'x', steps: cleanSteps() }) } catch { /* 校验接口失败时不阻断编辑，保存时后端仍会校验 */ }
  }, 300)
}
// 改编号时同步改写其他步骤对它的引用，避免断链。
function renameStep(step: ControlStep, value: string): void {
  const old = step.id, id = value.trim(); if (!id || id === old) return
  if (form.steps.some(s => s.id === id)) { error.value = tr('controlView.kd6486a', [id]); return }
  step.id = id
  for (const s of form.steps) {
    for (const k of ['next', 'then', 'else', 'join', 'target', 'onFailureGoto'] as const) if (s[k] === old) s[k] = id
    if (s.branches) s.branches = s.branches.map(b => (b === old ? id : b))
  }
  selectedStepId.value = id; scheduleValidate()
}
function deleteStep(step: ControlStep): void {
  const i = form.steps.indexOf(step); if (i < 0) return
  form.steps.splice(i, 1)
  for (const s of form.steps) {
    for (const k of ['next', 'then', 'else', 'join', 'target'] as const) if (s[k] === step.id) s[k] = null
    if (s.onFailureGoto === step.id) { s.onFailureGoto = null; s.onFailure = null }
    if (s.branches) s.branches = s.branches.filter(b => b !== step.id)
  }
  selectedStepId.value = null; scheduleValidate()
}
function moveStep(i: number): void { const [s] = form.steps.splice(i, 1); form.steps.splice(i - 1, 0, s!) }
function cleanSteps(): ControlStep[] {
  return form.steps.map(s => ({ ...s, confirmExpression: s.confirmExpression || null, expression: s.expression || null, next: s.next || null, else: s.else || null, onFailure: s.onFailure || null, onFailureGoto: s.onFailure === 'goto' ? s.onFailureGoto || null : null, tagId: (s.type ?? 'write') === 'write' ? s.tagId : null }))
}

function open(row?: ControlSequence): void {
  if (saving.value) return
  const areaId = props.areas[0]?.id
  if (!row && areaId == null) return
  Object.assign(form, row ? { id: row.id, name: row.name, areaId: row.areaId, description: row.description, dualApproval: row.dualApproval, revision: row.revision, steps: JSON.parse(JSON.stringify(row.steps)) }
    : { id: undefined, name: '', areaId, description: null, dualApproval: false, revision: 0, steps: [] })
  selectedStepId.value = null; validation.value = null; error.value = ''; visible.value = true
  if (row) scheduleValidate()
}

async function saveSequence(): Promise<void> {
  if (saving.value || disposed || !visible.value) return
  const payload = JSON.parse(JSON.stringify({ ...form, steps: cleanSteps() }))
  saving.value = true
  error.value = ''
  try {
    await controlApi.saveSequence(payload)
    if (disposed) return
    ElMessage.success(tr('controlView.kcdfab9')); visible.value = false
    emit('saved')
  } catch (e) {
    error.value = errorMessage(e, tr('controlView.k40525a'))
  } finally { saving.value = false }
}

onBeforeUnmount(() => { disposed = true; window.clearTimeout(validateTimer) })
defineExpose({ open })
</script>

<style scoped>
.step-fields { display: flex; gap: 4px; align-items: center; flex-wrap: wrap; }
.flow-editor { display: flex; gap: 10px; align-items: stretch; }
.flow-props { width: 300px; flex-shrink: 0; border: 1px solid var(--el-border-color-lighter); border-radius: 4px; padding: 8px; overflow: auto; max-height: 560px; }
@media (max-width: 900px) {
  .flow-editor { flex-direction: column; }
  .flow-props { box-sizing: border-box; width: 100%; max-height: none; }
}
</style>
