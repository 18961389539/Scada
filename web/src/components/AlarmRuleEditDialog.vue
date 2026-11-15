<template>
  <el-dialog v-model="dialogVisible" :title="editingId ? $t('alarmRulesView.kc9caa8') : (copying ? $t('alarmRulesView.k24245b') : $t('alarmRulesView.ke76def'))" width="520px" data-test="rule-dialog" :before-close="editDialog.beforeClose" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving" @keydown="editDialog.onKey" @opened="onDialogOpened">
    <el-alert v-if="saveError" :title="saveError" type="error" :closable="false" show-icon tabindex="-1" data-test="rule-save-error" />
    <el-alert
      v-if="!editingId"
      type="info"
      :closable="false"
      show-icon
      data-test="rule-defaults-tip"
      :title="$t('alarmRulesView.k4cad37')"
      style="margin-bottom: 12px"
    />
    <el-alert v-if="fieldErrors._form" :title="fieldErrors._form" type="error" :closable="false" show-icon style="margin-bottom: 12px" />
    <el-form ref="formRef" :model="form" :rules="validationRules" :disabled="saving" label-width="110px" status-icon @submit.prevent="save">
      <el-form-item :label="$t('alarmRulesView.k8cff1c')" prop="tagId" :error="fieldErrors.tagId">
        <el-select :model-value="form.tagId > 0 ? form.tagId : undefined" filterable remote :remote-method="searchRuleTags" :loading="ruleTagLoading" style="width: 100%" data-test="rule-tag" @update:model-value="onRuleTagPicked" @visible-change="onRuleTagsOpen">
          <el-option v-for="tag in ruleTagOptions" :key="tag.id" :label="alarmRuleTagLabel(tag)" :value="tag.id" />
        </el-select>
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.k7655f4')" prop="type">
        <el-select v-model="form.type" style="width: 100%" data-test="rule-type" @change="onTypeChange">
          <el-option v-for="(label, value) in ruleTypeOptions" :key="value" :label="label" :value="value" />
          <el-option v-if="form.type === 'StateChange'" :label="alarmTypeLabels.StateChange" value="StateChange" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="form.type === 'Expression'" :label="$t('alarmRulesView.expression')" prop="expression" :error="fieldErrors.expression">
        <el-input v-model="form.expression" type="textarea" :rows="3" maxlength="4096" :placeholder="$t('alarmRulesView.expressionPlaceholder')" data-test="rule-expression" />
        <span class="tiny muted">{{ $t('alarmRulesView.expressionHint') }}</span>
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.k31f7ba')" prop="category">
        <el-select v-model="form.category" style="width: 100%" data-test="rule-category">
          <el-option v-for="(label, value) in alarmCategoryLabels" :key="value" :label="label" :value="value" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="form.type === 'Discrete'" :label="$t('alarmRulesView.kca6c9e')" prop="activeWhen">
        <el-radio-group v-model="form.activeWhen" data-test="rule-active-when">
          <el-radio-button value="On">{{ activeWhenLabels.On }}</el-radio-button>
          <el-radio-button value="Off">{{ activeWhenLabels.Off }}</el-radio-button>
          <el-radio-button value="Change">{{ activeWhenLabels.Change }}</el-radio-button>
        </el-radio-group>
      </el-form-item>
      <el-form-item v-if="form.type === 'Discrete'">
        <p class="tiny muted">{{ $t('alarmRulesView.k1cb89d') }}</p>
      </el-form-item>
      <el-form-item v-if="needsBitIndex" :label="$t('alarmRulesView.bitIndex')" prop="bitIndex" :error="fieldErrors.bitIndex">
        <el-input-number v-model="form.bitIndex" :min="0" :max="selectedBitMax" :controls="false" style="width: 100%" data-test="rule-bit-index" />
        <span class="tiny muted">{{ $t('alarmRulesView.bitIndexHint', [selectedBitMax]) }}</span>
      </el-form-item>
      <el-form-item v-if="isLevelType" :label="$t('alarmRulesView.ka05320')" prop="threshold" :error="fieldErrors.threshold">
        <el-input-number v-model="form.threshold" :controls="false" style="width: 100%" data-test="rule-threshold" />
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.kd02c6b')" prop="consequence">
        <el-select v-model="form.consequence" style="width: 100%" data-test="rule-consequence">
          <el-option v-for="(label, value) in alarmConsequenceLabels" :key="value" :label="label" :value="value" />
        </el-select>
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.k14a01b')" prop="responseTimeSec" :error="fieldErrors.responseTimeSec">
        <el-input-number v-model="form.responseTimeSec" :min="0" :max="86400" style="width: 100%" data-test="rule-response-time" />
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.k421fc5')" prop="autoPriority">
        <el-switch v-model="form.autoPriority" data-test="rule-auto-priority" />
        <span class="tiny muted" style="margin-left: 8px">{{ $t('alarmRulesView.k464d7d') }}</span>
      </el-form-item>
      <el-form-item v-if="form.autoPriority" :label="$t('alarmRulesView.k254849')">
        <el-tag :type="levelType(autoLevel)" data-test="rule-auto-level">{{ levelLabel(autoLevel) }}</el-tag>
      </el-form-item>
      <el-form-item v-else :label="$t('alarmRulesView.k254849')" prop="level">
        <el-radio-group v-model="form.level" data-test="rule-level">
          <el-radio-button value="Info">{{ $t('alarmRulesView.kab3656') }}</el-radio-button>
          <el-radio-button value="Warning">{{ $t('alarmRulesView.k5521e3') }}</el-radio-button>
          <el-radio-button value="Critical">{{ $t('alarmRulesView.k81ffc6') }}</el-radio-button>
        </el-radio-group>
      </el-form-item>
      <el-form-item v-if="formShowsTiming" :label="$t('alarmRulesView.kbaa5d6')" prop="delaySec" :error="fieldErrors.delaySec">
        <el-input-number v-model="form.delaySec" :min="0" :max="3600" style="width: 100%" data-test="rule-delay" />
      </el-form-item>
      <el-form-item v-if="formShowsTiming" :label="$t('alarmRulesView.kcf7b90')" prop="offDelaySec">
        <el-input-number v-model="form.offDelaySec" :min="0" :max="3600" style="width: 100%" />
      </el-form-item>
      <el-form-item v-if="isLevelType" :label="$t('alarmRulesView.k3b86db')" prop="recoverDeadband" :error="fieldErrors.recoverDeadband">
        <el-input-number v-model="form.recoverDeadband" :min="0" :step="0.1" style="width: 100%" />
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.kb37a4e')" prop="suppressMode">
        <el-select v-model="form.suppressMode" style="width: 100%">
          <el-option v-for="(label, value) in suppressModeLabels" :key="value" :label="label" :value="value" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="form.suppressMode !== 'None'" :label="$t('alarmRulesView.ka52424')" prop="suppressTagId" :error="fieldErrors.suppressTagId">
        <el-select v-model="form.suppressTagId" filterable remote clearable :remote-method="searchSuppressTags" :loading="suppressTagLoading" style="width: 100%" :placeholder="$t('alarmRulesView.kd680cd')" @visible-change="onSuppressTagsOpen">
          <el-option v-for="tag in suppressTagOptions" :key="tag.id" :label="alarmRuleTagLabel(tag)" :value="tag.id" />
        </el-select>
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.kb62f14')" prop="message">
        <el-input v-model="form.message" :placeholder="$t('alarmRulesView.k7c59bc')" data-test="rule-message" />
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.messageEn')" prop="messageEn">
        <el-input v-model="form.messageEn" maxlength="500" :placeholder="$t('alarmRulesView.messageEnHint')" data-test="rule-message-en" />
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.cause')" prop="cause">
        <el-input v-model="form.cause" :placeholder="$t('alarmRulesView.causePlaceholder')" maxlength="200" data-test="rule-cause" />
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.causeEn')" prop="causeEn">
        <el-input v-model="form.causeEn" maxlength="200" :placeholder="$t('alarmRulesView.causeEnHint')" data-test="rule-cause-en" />
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.action')" prop="action">
        <el-input v-model="form.action" :placeholder="$t('alarmRulesView.actionPlaceholder')" maxlength="200" data-test="rule-action" />
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.actionEn')" prop="actionEn">
        <el-input v-model="form.actionEn" maxlength="200" :placeholder="$t('alarmRulesView.actionEnHint')" data-test="rule-action-en" />
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.kd4e9ca')" prop="enabled">
        <el-switch v-model="form.enabled" data-test="rule-enabled" />
      </el-form-item>
    </el-form>
    <template #footer>
      <span class="edit-dialog-shortcut">{{ $t('editDialog.shortcut', [editDialog.modKey]) }}</span>
      <el-button :disabled="saving" @click="editDialog.close">{{ $t('alarmRulesView.k4d0b46') }}</el-button>
      <el-button type="primary" data-test="rule-save" :loading="saving" :disabled="!canSave" @click="save">{{ $t('alarmRulesView.kfadf24') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, reactive, ref } from 'vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { ruleApi } from '../api'
import type { AlarmRule, AlarmRuleInput, Tag } from '../types'
import { activeWhenLabels, alarmCategoryLabels, alarmConsequenceLabels, alarmPriorityLevel, alarmTypeLabels, levelLabel, levelType, suppressModeLabels } from '../utils'
import { tr } from '../i18n'
import { applyFieldErrors, clearFieldErrors, serverFormErrors, type FieldErrorMap } from '../composables/formErrors'
import { rememberNumber } from '../composables/lastChoice'
import { focusFormError, useEditDialog } from '../composables/useEditDialog'
import { useAlarmRuleTagPicker, withPinnedTags } from '../composables/useAlarmRuleTagPicker'
import { alarmRuleTagLabel, discreteDataTypes, emptyAlarmRuleInput, isLevelRule, showsTiming, toAlarmRuleInput } from '../views/alarmRuleModel'

const props = defineProps<{
  resolveTag: (id?: number | null) => Promise<Tag | undefined>
  ensureDevices: () => Promise<void>
  canConfigureTag: (tag: Tag) => boolean
}>()
const emit = defineEmits<{ (event: 'saved'): void }>()

const dialogVisible = ref(false), editingId = ref<number | null>(null), copying = ref(false)
const saving = ref(false), saveError = ref('')
const formRef = ref<FormInstance>()
const fieldErrors = reactive<FieldErrorMap>({})
const form = reactive<AlarmRuleInput>(emptyAlarmRuleInput())
const ruleTypeOptions = computed(() => Object.entries(alarmTypeLabels).filter(([value]) => value !== 'StateChange'))

const knownTags = ref<Tag[]>([])
function rememberTags(list: Tag[]): void {
  if (!list.length) return
  const map = new Map(knownTags.value.map(tag => [tag.id, tag]))
  for (const tag of list) map.set(tag.id, tag)
  knownTags.value = [...map.values()]
}

const selectedTag = computed(() => knownTags.value.find(tag => tag.id === form.tagId))
const needsBitIndex = computed(() => form.type === 'Discrete' && selectedTag.value != null && selectedTag.value.dataType !== 'Bool')
const selectedBitMax = computed(() => selectedTag.value?.dataType === 'Int16' || selectedTag.value?.dataType === 'UInt16' ? 15 : 31)
const isLevelType = computed(() => isLevelRule(form))
const formShowsTiming = computed(() => showsTiming(form.type, form.activeWhen))
const autoLevel = computed(() => alarmPriorityLevel(form.consequence, form.responseTimeSec))

function allowRuleTag(tag: Tag): boolean {
  if (!props.canConfigureTag(tag)) return false
  return form.type !== 'Discrete' || discreteDataTypes.includes(tag.dataType)
}

const ruleTagPicker = useAlarmRuleTagPicker({
  resolveTag: props.resolveTag,
  ensureDevices: props.ensureDevices,
  onLoaded: rememberTags,
  accept: () => allowRuleTag,
  pinnedId: () => form.tagId || undefined,
  forcePin: () => editingId.value != null || copying.value
})
const suppressTagPicker = useAlarmRuleTagPicker({
  resolveTag: props.resolveTag,
  ensureDevices: props.ensureDevices,
  onLoaded: rememberTags,
  accept: () => (tag: Tag) => tag.id !== form.tagId,
  pinnedId: () => form.suppressTagId ?? undefined,
  forcePin: () => true
})
const { items: ruleTagOptions, loading: ruleTagLoading, search: searchRuleTags, open: openRuleTags, reset: resetRuleTags } = ruleTagPicker
const { items: suppressTagOptions, loading: suppressTagLoading, search: searchSuppressTags, open: openSuppressTags, reset: resetSuppressTags } = suppressTagPicker
function onRuleTagsOpen(open: boolean): void { openRuleTags(open) }
function onSuppressTagsOpen(open: boolean): void { openSuppressTags(open) }

// 位报警默认即时判定：切换到位报警时清掉模拟量专用的延时与死区，仍可在表单里手动调整。
function onTypeChange(type: AlarmRule['type']): void {
  if (type !== 'Discrete') {
    form.bitIndex = null
    void searchRuleTags('')
    return
  }
  if (!selectedTag.value || !discreteDataTypes.includes(selectedTag.value.dataType) || !props.canConfigureTag(selectedTag.value)) form.tagId = 0
  form.activeWhen = 'On'
  form.delaySec = 0
  form.offDelaySec = 0
  form.recoverDeadband = 0
  form.bitIndex = selectedTag.value?.dataType === 'Bool' ? null : form.bitIndex ?? 0
  void searchRuleTags('')
}
function onTagChange(): void {
  if (form.type !== 'Discrete') return
  form.bitIndex = selectedTag.value?.dataType === 'Bool' ? null : form.bitIndex ?? 0
}
function onRuleTagPicked(value: number | undefined): void {
  form.tagId = value ?? 0
  onTagChange()
}

const editDialog = useEditDialog({ visible: dialogVisible, saving, form: formRef, snapshot: () => JSON.stringify(form), initialField: 'threshold', save })

const validationRules = computed<FormRules>(() => ({
  tagId: [{ required: true, type: 'number', min: 1, message: tr('alarmRulesView.k1a545c'), trigger: 'change' }],
  suppressTagId: [{
    validator: (_r, _v, cb) => {
      if (form.suppressMode !== 'None' && !form.suppressTagId) cb(new Error(tr('alarmRulesView.kfb2726')))
      else cb()
    },
    trigger: 'change'
  }],
  delaySec: [{ type: 'number', min: 0, max: 3600, message: tr('alarmRulesView.k233836'), trigger: 'change' }],
  offDelaySec: [{ type: 'number', min: 0, max: 3600, message: tr('alarmRulesView.k04a0a0'), trigger: 'change' }],
  responseTimeSec: [
    { type: 'number', min: 0, max: 86400, message: tr('alarmRulesView.kf07468'), trigger: 'change' },
    {
      validator: (_r, _v, cb) => {
        if (form.autoPriority && form.responseTimeSec <= 0) cb(new Error(tr('alarmRulesView.kdf43a3')))
        else cb()
      },
      trigger: 'change'
    }
  ],
  recoverDeadband: [{ type: 'number', min: 0, message: tr('alarmRulesView.ka8a2b8'), trigger: 'change' }],
  expression: [{
    validator: (_r, _v, cb) => {
      if (form.type === 'Expression' && !form.expression?.trim()) cb(new Error(tr('alarmRulesView.expressionRequired')))
      else cb()
    },
    trigger: 'change'
  }],
  bitIndex: [{
    validator: (_r, _v, cb) => {
      if (needsBitIndex.value && form.bitIndex == null) cb(new Error(tr('alarmRulesView.bitIndexRequired')))
      else cb()
    },
    trigger: 'change'
  }]
}))
const canSave = computed(() => {
  if (!form.tagId) return false
  if (form.suppressMode !== 'None' && !form.suppressTagId) return false
  if (form.type === 'Expression' && !form.expression?.trim()) return false
  if (needsBitIndex.value && form.bitIndex == null) return false
  return true
})

async function openCreate(preferredTagId?: number): Promise<void> {
  editingId.value = null
  copying.value = false
  clearFieldErrors(fieldErrors)
  saveError.value = ''
  Object.assign(form, emptyAlarmRuleInput())
  await props.ensureDevices().catch(() => undefined)
  const tag = preferredTagId ? await props.resolveTag(preferredTagId) : undefined
  if (tag) rememberTags([tag])
  form.tagId = tag && allowRuleTag(tag) ? tag.id : 0
  resetRuleTags(form.tagId && tag ? [tag] : [])
  resetSuppressTags([])
  dialogVisible.value = true
}

async function openEdit(rule: AlarmRule): Promise<void> {
  editingId.value = rule.id
  copying.value = false
  clearFieldErrors(fieldErrors)
  saveError.value = ''
  Object.assign(form, toAlarmRuleInput(rule))
  await props.ensureDevices().catch(() => undefined)
  const tag = await props.resolveTag(rule.tagId)
  const suppress = rule.suppressTagId ? await props.resolveTag(rule.suppressTagId) : undefined
  if (tag) rememberTags([tag])
  if (suppress) rememberTags([suppress])
  resetRuleTags(withPinnedTags(tag ? [tag] : [], tag, allowRuleTag, true))
  resetSuppressTags(suppress ? [suppress] : [])
  dialogVisible.value = true
}

async function openCopy(rule: AlarmRule): Promise<void> {
  editingId.value = null
  copying.value = true
  clearFieldErrors(fieldErrors)
  saveError.value = ''
  Object.assign(form, toAlarmRuleInput(rule, { message: rule.message ? tr('alarmRulesView.kc0769b', [rule.message]) : '' }))
  await props.ensureDevices().catch(() => undefined)
  const tag = await props.resolveTag(rule.tagId)
  const suppress = rule.suppressTagId ? await props.resolveTag(rule.suppressTagId) : undefined
  if (tag) rememberTags([tag])
  if (suppress) rememberTags([suppress])
  resetRuleTags(withPinnedTags(tag ? [tag] : [], tag, allowRuleTag, true))
  resetSuppressTags(suppress ? [suppress] : [])
  dialogVisible.value = true
}

async function onDialogOpened(): Promise<void> {
  await nextTick()
  formRef.value?.clearValidate()
  await editDialog.focusInitial()
  void searchRuleTags('')
  if (form.suppressMode !== 'None') void searchSuppressTags('')
}

async function save(): Promise<void> {
  if (!dialogVisible.value || saving.value) return
  saving.value = true
  let focusError = false
  clearFieldErrors(fieldErrors)
  saveError.value = ''
  try {
    const valid = await formRef.value?.validate().catch(() => false)
    if (!valid) {
      saveError.value = tr('alarmRulesView.k02668e'); focusError = true; return
    }
    if (!canSave.value) {
      saveError.value = tr('alarmRulesView.k1a545c'); focusError = true; return
    }
    if (form.suppressMode === 'None') form.suppressTagId = null
    if (form.type !== 'Discrete') form.bitIndex = null
    if (form.type !== 'Expression') form.expression = ''
    if (editingId.value) await ruleApi.update(editingId.value, { ...form })
    else {
      await ruleApi.create({ ...form })
      rememberNumber('rule-tag', form.tagId)
    }
    ElMessage.success(tr('alarmRulesView.k7e68eb'))
    saveError.value = ''
    dialogVisible.value = false
    emit('saved')
  } catch (error) {
    const mapped = serverFormErrors(error, tr('alarmRulesView.k40525a'), ['tagId', 'threshold', 'suppressTagId', 'delaySec', 'bitIndex', 'expression'])
    applyFieldErrors(fieldErrors, mapped.fields)
    saveError.value = mapped.message
    focusError = true
  } finally {
    saving.value = false
    if (focusError) await focusFormError(formRef.value)
  }
}

defineExpose({ openCreate, openEdit, openCopy })
</script>
