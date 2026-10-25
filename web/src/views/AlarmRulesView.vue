<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterTag" clearable filterable :placeholder="$t('alarmRulesView.kb8ceed')" style="width: 220px" @change="load">
          <el-option v-for="tag in tags" :key="tag.id" :label="`${tag.name} (#${tag.id})`" :value="tag.id" />
        </el-select>
        <span class="spacer" />
        <el-button type="primary" :icon="Plus" @click="openCreate">{{ $t('alarmRulesView.k945c08') }}</el-button>
      </div>

      <el-table v-loading="loading" :data="rules" size="small" :empty-text="$t('alarmRulesView.ka0230f')">
        <el-table-column prop="id" label="ID" width="60" />
        <el-table-column :label="$t('alarmRulesView.k8cff1c')" min-width="140">
          <template #default="{ row }">{{ tagName(row.tagId) }}</template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.ke4e46c')" width="100">
          <template #default="{ row }">{{ alarmTypeLabels[row.type] }}</template>
        </el-table-column>
        <el-table-column prop="threshold" :label="$t('alarmRulesView.ka05320')" width="90" />
        <el-table-column :label="$t('alarmRulesView.k254849')" width="90">
          <template #default="{ row }">
            <el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.k27cd56')" width="110">
          <template #default="{ row }">{{ row.delaySec }}s / {{ row.offDelaySec ?? 0 }}s</template>
        </el-table-column>
        <el-table-column prop="recoverDeadband" :label="$t('alarmRulesView.k3b86db')" width="90" />
        <el-table-column :label="$t('alarmRulesView.kb37a4e')" width="150">
          <template #default="{ row }">
            <span v-if="row.suppressMode && row.suppressMode !== 'None'" class="tiny">{{ $t('alarmRulesView.k045924', [tagName(row.suppressTagId), row.suppressMode === 'WhenOn' ? 1 : 0]) }}</span>
            <span v-else class="muted tiny">-</span>
          </template>
        </el-table-column>
        <el-table-column prop="message" :label="$t('alarmRulesView.kb62f14')" min-width="180" />
        <el-table-column :label="$t('alarmRulesView.kd4e9ca')" width="80">
          <template #default="{ row }">
            <el-tag :type="row.enabled ? 'success' : 'info'" size="small">{{ row.enabled ? $t('alarmRulesView.kd4e9ca') : $t('alarmRulesView.kbe70be') }}</el-tag>
            <el-tooltip v-if="row.outOfService" :content="row.outOfServiceReason || ''"><el-tag type="warning" size="small" data-test="oos-tag">{{ $t('alarmRulesView.ka3f7d9') }}</el-tag></el-tooltip>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.kf3ea6d')" width="190" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openEdit(row)">{{ $t('alarmRulesView.ka7f814') }}</el-button>
            <el-button v-if="!row.outOfService" link type="warning" @click="setOutOfService(row, true)">{{ $t('alarmRulesView.kd989e5') }}</el-button>
            <el-button v-else link type="success" @click="setOutOfService(row, false)">{{ $t('alarmRulesView.k79748c') }}</el-button>
            <el-popconfirm :title="$t('alarmRulesView.k2343c0')" @confirm="remove(row)">
              <template #reference><el-button link type="danger">{{ $t('alarmRulesView.k3755f5') }}</el-button></template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="dialogVisible" :title="editingId ? $t('alarmRulesView.kc9caa8') : $t('alarmRulesView.ke76def')" width="520px">
      <el-form :model="form" label-width="110px">
        <el-form-item :label="$t('alarmRulesView.k8cff1c')" required>
          <el-select v-model="form.tagId" filterable style="width: 100%">
            <el-option v-for="tag in tags" :key="tag.id" :label="`${tag.name} (#${tag.id})`" :value="tag.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k7655f4')">
          <el-select v-model="form.type" style="width: 100%">
            <el-option v-for="(label, value) in alarmTypeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.ka05320')">
          <el-input-number v-model="form.threshold" :controls="false" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k254849')">
          <el-radio-group v-model="form.level">
            <el-radio-button value="Info">{{ $t('alarmRulesView.kab3656') }}</el-radio-button>
            <el-radio-button value="Warning">{{ $t('alarmRulesView.k5521e3') }}</el-radio-button>
            <el-radio-button value="Critical">{{ $t('alarmRulesView.k81ffc6') }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kbaa5d6')">
          <el-input-number v-model="form.delaySec" :min="0" :max="3600" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kcf7b90')">
          <el-input-number v-model="form.offDelaySec" :min="0" :max="3600" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k3b86db')">
          <el-input-number v-model="form.recoverDeadband" :min="0" :step="0.1" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kb37a4e')">
          <el-select v-model="form.suppressMode" style="width: 100%">
            <el-option v-for="(label, value) in suppressModeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="form.suppressMode !== 'None'" :label="$t('alarmRulesView.ka52424')" required>
          <el-select v-model="form.suppressTagId" filterable style="width: 100%" :placeholder="$t('alarmRulesView.kd680cd')">
            <el-option v-for="tag in tags.filter((t) => t.id !== form.tagId)" :key="tag.id" :label="`${tag.name} (#${tag.id})`" :value="tag.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kb62f14')">
          <el-input v-model="form.message" :placeholder="$t('alarmRulesView.k7c59bc')" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kd4e9ca')">
          <el-switch v-model="form.enabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">{{ $t('alarmRulesView.k4d0b46') }}</el-button>
        <el-button type="primary" :loading="saving" @click="save">{{ $t('alarmRulesView.kfadf24') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus } from '@element-plus/icons-vue'
import { ruleApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import type { AlarmRule, AlarmRuleInput, Tag } from '../types'
import { alarmTypeLabels, levelLabel, levelType, suppressModeLabels } from '../utils'
import { tr } from '../i18n'

const rules = ref<AlarmRule[]>([])
const tags = ref<Tag[]>([])
const filterTag = ref<number | undefined>()
const loading = ref(false)
const saving = ref(false)
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)

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
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmRulesView.k9174d4')))
  } finally {
    loading.value = false
  }
}

function openCreate(): void {
  editingId.value = null
  Object.assign(form, emptyForm())
  form.tagId = filterTag.value ?? tags.value[0]?.id ?? 0
  dialogVisible.value = true
}

function openEdit(rule: AlarmRule): void {
  editingId.value = rule.id
  Object.assign(form, {
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
    suppressMode: rule.suppressMode ?? 'None'
  })
  dialogVisible.value = true
}

async function save(): Promise<void> {
  if (!form.tagId) {
    ElMessage.warning(tr('alarmRulesView.k1a545c'))
    return
  }

  if (form.suppressMode !== 'None' && !form.suppressTagId) {
    ElMessage.warning(tr('alarmRulesView.ka03de3'))
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
    ElMessage.error(errorMessage(error, tr('alarmRulesView.k40525a')))
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

onMounted(async () => {
  try {
    tags.value = await tagApi.list()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('alarmRulesView.kdf92e4')))
  }
  await load()
})
</script>
