<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterTag" clearable filterable :placeholder="$t('alarmRulesView.kb8ceed')" style="width: 220px" @change="load">
          <el-option v-for="tag in tags" :key="tag.id" :label="`${tag.name} (#${tag.id})`" :value="tag.id" />
        </el-select>
        <span class="spacer" />
        <template v-if="selected.length">
          <el-text data-test="rule-batch-count">{{ $t('alarmRulesView.k805f4f', [selected.length]) }}</el-text>
          <el-button data-test="rule-batch-enable" :loading="batching" @click="batchSetEnabled(true)">{{ $t('alarmRulesView.kf5e360') }}</el-button>
          <el-button data-test="rule-batch-disable" :loading="batching" @click="batchSetEnabled(false)">{{ $t('alarmRulesView.k45f528') }}</el-button>
          <el-button type="danger" plain data-test="rule-batch-delete" :loading="batching" @click="batchDelete">{{ $t('alarmRulesView.k362aed') }}</el-button>
        </template>
        <el-button type="primary" :icon="Plus" data-test="rule-create" @click="openCreate">{{ $t('alarmRulesView.k945c08') }}</el-button>
      </div>
      <el-alert v-if="batchProgress" :title="batchProgress" type="info" :closable="false" show-icon style="margin-bottom: 8px" data-test="rule-batch-progress" />

      <el-table v-loading="loading" :data="rules" size="small" :empty-text="$t('alarmRulesView.ka0230f')" row-key="id" @selection-change="onSelect">
        <el-table-column type="selection" width="42" />
        <el-table-column :label="$t('alarmRulesView.k8cff1c')" width="125">
          <template #default="{ row }">{{ tagName(row.tagId) }}</template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.ke4e46c')" width="90">
          <template #default="{ row }">{{ alarmTypeLabels[row.type] }}</template>
        </el-table-column>
        <el-table-column :label="`${$t('alarmRulesView.ka05320')} / ${$t('alarmRulesView.k254849')}`" width="150">
          <template #default="{ row }">
            <span class="mono">{{ row.threshold }}</span>
            <el-tag :type="levelType(row.level)" size="small" style="margin-left: 6px">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="`${$t('alarmRulesView.k27cd56')} / ${$t('alarmRulesView.k3b86db')}`" width="170">
          <template #default="{ row }">
            <span class="mono">{{ row.delaySec }}s / {{ row.offDelaySec ?? 0 }}s</span>
            <span class="muted tiny">· {{ row.recoverDeadband }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.kb37a4e')" width="120">
          <template #default="{ row }">
            <span v-if="row.suppressMode && row.suppressMode !== 'None'" class="tiny">{{ $t('alarmRulesView.k045924', [tagName(row.suppressTagId), row.suppressMode === 'WhenOn' ? 1 : 0]) }}</span>
            <span v-else class="muted tiny">-</span>
          </template>
        </el-table-column>
        <el-table-column prop="message" :label="$t('alarmRulesView.kb62f14')" min-width="150" show-overflow-tooltip />
        <el-table-column :label="$t('alarmRulesView.kd4e9ca')" width="78">
          <template #default="{ row }">
            <el-tag :type="row.enabled ? 'success' : 'info'" size="small" :data-test="`rule-enabled-${row.id}`">{{ row.enabled ? $t('alarmRulesView.kd4e9ca') : $t('alarmRulesView.kbe70be') }}</el-tag>
            <el-tooltip v-if="row.outOfService" :content="row.outOfServiceReason || ''"><el-tag type="warning" size="small" data-test="oos-tag">{{ $t('alarmRulesView.ka3f7d9') }}</el-tag></el-tooltip>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.kf3ea6d')" width="210" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openEdit(row)">{{ $t('alarmRulesView.ka7f814') }}</el-button>
            <el-button link data-test="rule-copy" @click="openCopy(row)">{{ $t('alarmRulesView.k4edd1d') }}</el-button>
            <el-button v-if="!row.outOfService" link type="warning" @click="setOutOfService(row, true)">{{ $t('alarmRulesView.kd989e5') }}</el-button>
            <el-button v-else link type="success" @click="setOutOfService(row, false)">{{ $t('alarmRulesView.k79748c') }}</el-button>
            <el-popconfirm :title="$t('alarmRulesView.k2343c0')" @confirm="remove(row)">
              <template #reference><el-button link type="danger">{{ $t('alarmRulesView.k3755f5') }}</el-button></template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="dialogVisible" :title="editingId ? $t('alarmRulesView.kc9caa8') : (copying ? $t('alarmRulesView.k24245b') : $t('alarmRulesView.ke76def'))" width="520px" data-test="rule-dialog" @opened="onDialogOpened">
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
      <el-form ref="formRef" :model="form" :rules="validationRules" label-width="110px" status-icon>
        <el-form-item :label="$t('alarmRulesView.k8cff1c')" prop="tagId" :error="fieldErrors.tagId">
          <el-select v-model="form.tagId" filterable style="width: 100%" data-test="rule-tag">
            <el-option v-for="tag in tags" :key="tag.id" :label="`${tag.name} (#${tag.id})`" :value="tag.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k7655f4')" prop="type">
          <el-select v-model="form.type" style="width: 100%" data-test="rule-type">
            <el-option v-for="(label, value) in alarmTypeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.ka05320')" prop="threshold" :error="fieldErrors.threshold">
          <el-input-number v-model="form.threshold" :controls="false" style="width: 100%" data-test="rule-threshold" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k254849')" prop="level">
          <el-radio-group v-model="form.level" data-test="rule-level">
            <el-radio-button value="Info">{{ $t('alarmRulesView.kab3656') }}</el-radio-button>
            <el-radio-button value="Warning">{{ $t('alarmRulesView.k5521e3') }}</el-radio-button>
            <el-radio-button value="Critical">{{ $t('alarmRulesView.k81ffc6') }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kbaa5d6')" prop="delaySec" :error="fieldErrors.delaySec">
          <el-input-number v-model="form.delaySec" :min="0" :max="3600" style="width: 100%" data-test="rule-delay" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kcf7b90')" prop="offDelaySec">
          <el-input-number v-model="form.offDelaySec" :min="0" :max="3600" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k3b86db')" prop="recoverDeadband" :error="fieldErrors.recoverDeadband">
          <el-input-number v-model="form.recoverDeadband" :min="0" :step="0.1" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kb37a4e')" prop="suppressMode">
          <el-select v-model="form.suppressMode" style="width: 100%">
            <el-option v-for="(label, value) in suppressModeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="form.suppressMode !== 'None'" :label="$t('alarmRulesView.ka52424')" prop="suppressTagId" :error="fieldErrors.suppressTagId">
          <el-select v-model="form.suppressTagId" filterable style="width: 100%" :placeholder="$t('alarmRulesView.kd680cd')">
            <el-option v-for="tag in tags.filter((t) => t.id !== form.tagId)" :key="tag.id" :label="`${tag.name} (#${tag.id})`" :value="tag.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kb62f14')" prop="message">
          <el-input v-model="form.message" :placeholder="$t('alarmRulesView.k7c59bc')" data-test="rule-message" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kd4e9ca')" prop="enabled">
          <el-switch v-model="form.enabled" data-test="rule-enabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">{{ $t('alarmRulesView.k4d0b46') }}</el-button>
        <el-button type="primary" data-test="rule-save" :loading="saving" :disabled="!canSave" @click="save">{{ $t('alarmRulesView.kfadf24') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus'
import { Plus } from '@element-plus/icons-vue'
import { ruleApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import type { AlarmRule, AlarmRuleInput, Tag } from '../types'
import { alarmTypeLabels, levelLabel, levelType, suppressModeLabels } from '../utils'
import { tr } from '../i18n'
import { runBatch, summarizeBatch } from '../composables/batchRunner'
import { applyFieldErrors, clearFieldErrors, serverFormErrors, type FieldErrorMap } from '../composables/formErrors'

const rules = ref<AlarmRule[]>([])
const tags = ref<Tag[]>([])
const filterTag = ref<number | undefined>()
const loading = ref(false)
const saving = ref(false)
const batching = ref(false)
const batchProgress = ref('')
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)
const copying = ref(false)
const selected = ref<AlarmRule[]>([])
const formRef = ref<FormInstance>()
const fieldErrors = reactive<FieldErrorMap>({})

const emptyForm = (): AlarmRuleInput => ({
  tagId: 0,
  type: 'High',
  threshold: 0,
  level: 'Warning',
  delaySec: 3,
  recoverDeadband: 0,
  enabled: true,
  message: '',
  offDelaySec: 0,
  suppressTagId: null,
  suppressMode: 'None'
})
const form = reactive<AlarmRuleInput>(emptyForm())

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
  recoverDeadband: [{ type: 'number', min: 0, message: tr('alarmRulesView.ka8a2b8'), trigger: 'change' }]
}))
const canSave = computed(() => {
  if (!form.tagId) return false
  if (form.suppressMode !== 'None' && !form.suppressTagId) return false
  return true
})

function onSelect(rows: AlarmRule[]): void { selected.value = rows }

function toInput(rule: AlarmRule, patch: Partial<AlarmRuleInput> = {}): AlarmRuleInput {
  return {
    tagId: rule.tagId,
    type: rule.type,
    threshold: rule.threshold,
    level: rule.level,
    delaySec: rule.delaySec,
    recoverDeadband: rule.recoverDeadband,
    enabled: rule.enabled,
    message: rule.message ?? '',
    offDelaySec: rule.offDelaySec ?? 0,
    suppressTagId: rule.suppressTagId ?? null,
    suppressMode: rule.suppressMode ?? 'None',
    ...patch
  }
}

async function setOutOfService(rule: AlarmRule, outOfService: boolean): Promise<void> {
  let reason: string | undefined
  if (outOfService) {
    try {
      const result = await ElMessageBox.prompt(tr('alarmRulesView.k3db650'), tr('alarmRulesView.kbba488'), {
        inputValidator: (value: string) => (value && value.trim() ? true : tr('alarmRulesView.k75ebf5')),
        confirmButtonText: tr('alarmRulesView.kd989e5'),
        cancelButtonText: tr('alarmRulesView.k4d0b46')
      })
      reason = result.value.trim()
    } catch {
      return
    }
  }
  try {
    await ruleApi.outOfService(rule.id, outOfService, reason)
    ElMessage.success(outOfService ? tr('alarmRulesView.k6c7dcb') : tr('alarmRulesView.k6aa9ad'))
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmRulesView.k09e424')))
  }
}

function tagName(id?: number | null): string {
  return tags.value.find((x) => x.id === id)?.name ?? `#${id}`
}

async function load(): Promise<void> {
  loading.value = true
  try {
    rules.value = await ruleApi.list(filterTag.value)
    selected.value = []
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmRulesView.k9174d4')))
  } finally {
    loading.value = false
  }
}

function openCreate(): void {
  editingId.value = null
  copying.value = false
  clearFieldErrors(fieldErrors)
  Object.assign(form, emptyForm())
  form.tagId = filterTag.value ?? tags.value[0]?.id ?? 0
  dialogVisible.value = true
}

function openEdit(rule: AlarmRule): void {
  editingId.value = rule.id
  copying.value = false
  clearFieldErrors(fieldErrors)
  Object.assign(form, toInput(rule))
  dialogVisible.value = true
}

function openCopy(rule: AlarmRule): void {
  editingId.value = null
  copying.value = true
  clearFieldErrors(fieldErrors)
  Object.assign(form, toInput(rule, { message: rule.message ? tr('alarmRulesView.kc0769b', [rule.message]) : '' }))
  dialogVisible.value = true
}

async function onDialogOpened(): Promise<void> {
  await nextTick()
  formRef.value?.clearValidate()
}

async function save(): Promise<void> {
  clearFieldErrors(fieldErrors)
  try {
    await formRef.value?.validate()
  } catch {
    ElMessage.warning(tr('alarmRulesView.k02668e'))
    return
  }
  if (!canSave.value) {
    ElMessage.warning(tr('alarmRulesView.k1a545c'))
    return
  }

  if (form.suppressMode === 'None') form.suppressTagId = null

  saving.value = true
  try {
    if (editingId.value) await ruleApi.update(editingId.value, { ...form })
    else await ruleApi.create({ ...form })
    ElMessage.success(tr('alarmRulesView.k7e68eb'))
    dialogVisible.value = false
    await load()
  } catch (error) {
    const mapped = serverFormErrors(error, tr('alarmRulesView.k40525a'), ['tagId', 'threshold', 'suppressTagId', 'delaySec'])
    applyFieldErrors(fieldErrors, mapped.fields)
    ElMessage.error(mapped.message)
  } finally {
    saving.value = false
  }
}

async function remove(rule: AlarmRule): Promise<void> {
  try {
    await ruleApi.remove(rule.id)
    ElMessage.success(tr('alarmRulesView.kfb5fe1'))
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmRulesView.k72250c')))
  }
}

async function reportBatch(report: Awaited<ReturnType<typeof runBatch>>, okText: string): Promise<void> {
  batchProgress.value = ''
  if (report.failed.length === 0) ElMessage.success(okText)
  else await ElMessageBox.alert(summarizeBatch(report, tr('alarmRulesView.kbce2ef')), tr('alarmRulesView.kcfc1db'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('alarmRulesView.kcb63c6') })
  await load()
}

async function batchSetEnabled(enabled: boolean): Promise<void> {
  const rows = selected.value.slice()
  if (!rows.length) return
  batching.value = true
  const report = await runBatch(rows, (r) => `#${r.id} ${r.message || tagName(r.tagId)}`, async (r) => { await ruleApi.update(r.id, toInput(r, { enabled })) }, (p) => {
    batchProgress.value = tr('alarmRulesView.k4c5062', [enabled ? tr('alarmRulesView.kd4e9ca') : tr('alarmRulesView.kd989e5'), p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  })
  batching.value = false
  await reportBatch(report, tr('alarmRulesView.k611dcc', [enabled ? tr('alarmRulesView.kd4e9ca') : tr('alarmRulesView.kd989e5'), report.ok]))
}

async function batchDelete(): Promise<void> {
  const rows = selected.value.slice()
  if (!rows.length) return
  try {
    await ElMessageBox.confirm(tr('alarmRulesView.k6db0fb', [rows.length]), tr('alarmRulesView.k362aed'), { type: 'warning', confirmButtonText: tr('alarmRulesView.k3755f5'), cancelButtonText: tr('alarmRulesView.k4d0b46') })
  } catch { return }
  batching.value = true
  const report = await runBatch(rows, (r) => `#${r.id} ${r.message || tagName(r.tagId)}`, async (r) => { await ruleApi.remove(r.id) }, (p) => {
    batchProgress.value = tr('alarmRulesView.kee9792', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  })
  batching.value = false
  await reportBatch(report, tr('alarmRulesView.kad6890', [report.ok]))
}

onMounted(async () => {
  try {
    tags.value = await tagApi.list()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmRulesView.kdf92e4')))
  }
  await load()
})
</script>
