<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterTag" clearable filterable placeholder="全部测点" style="width: 220px" @change="load">
          <el-option v-for="tag in tags" :key="tag.id" :label="`${tag.name} (#${tag.id})`" :value="tag.id" />
        </el-select>
        <span class="spacer" />
        <el-button type="primary" :icon="Plus" @click="openCreate">新增规则</el-button>
      </div>

      <el-table v-loading="loading" :data="rules" size="small" empty-text="暂无报警规则">
        <el-table-column prop="id" label="ID" width="60" />
        <el-table-column label="测点" min-width="140">
          <template #default="{ row }">{{ tagName(row.tagId) }}</template>
        </el-table-column>
        <el-table-column label="类型" width="100">
          <template #default="{ row }">{{ alarmTypeLabels[row.type] }}</template>
        </el-table-column>
        <el-table-column prop="threshold" label="阈值" width="90" />
        <el-table-column label="级别" width="90">
          <template #default="{ row }">
            <el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="触发/恢复延时" width="110">
          <template #default="{ row }">{{ row.delaySec }}s / {{ row.offDelaySec ?? 0 }}s</template>
        </el-table-column>
        <el-table-column prop="recoverDeadband" label="恢复死区" width="90" />
        <el-table-column label="条件抑制" width="150">
          <template #default="{ row }">
            <span v-if="row.suppressMode && row.suppressMode !== 'None'" class="tiny">{{ tagName(row.suppressTagId) }} = {{ row.suppressMode === 'WhenOn' ? 1 : 0 }} 时</span>
            <span v-else class="muted tiny">-</span>
          </template>
        </el-table-column>
        <el-table-column prop="message" label="报警信息" min-width="180" />
        <el-table-column label="启用" width="80">
          <template #default="{ row }">
            <el-tag :type="row.enabled ? 'success' : 'info'" size="small">{{ row.enabled ? '启用' : '禁用' }}</el-tag>
            <el-tooltip v-if="row.outOfService" :content="row.outOfServiceReason || ''"><el-tag type="warning" size="small" data-test="oos-tag">停用中</el-tag></el-tooltip>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="190" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
            <el-button v-if="!row.outOfService" link type="warning" @click="setOutOfService(row, true)">停用</el-button>
            <el-button v-else link type="success" @click="setOutOfService(row, false)">恢复</el-button>
            <el-popconfirm title="确认删除该规则？" @confirm="remove(row)">
              <template #reference><el-button link type="danger">删除</el-button></template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="dialogVisible" :title="editingId ? '编辑报警规则' : '新增报警规则'" width="520px">
      <el-form :model="form" label-width="110px">
        <el-form-item label="测点" required>
          <el-select v-model="form.tagId" filterable style="width: 100%">
            <el-option v-for="tag in tags" :key="tag.id" :label="`${tag.name} (#${tag.id})`" :value="tag.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="规则类型">
          <el-select v-model="form.type" style="width: 100%">
            <el-option v-for="(label, value) in alarmTypeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item label="阈值">
          <el-input-number v-model="form.threshold" :controls="false" style="width: 100%" />
        </el-form-item>
        <el-form-item label="级别">
          <el-radio-group v-model="form.level">
            <el-radio-button value="Info">提示</el-radio-button>
            <el-radio-button value="Warning">警告</el-radio-button>
            <el-radio-button value="Critical">严重</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="触发延时 (秒)">
          <el-input-number v-model="form.delaySec" :min="0" :max="3600" style="width: 100%" />
        </el-form-item>
        <el-form-item label="恢复延时 (秒)">
          <el-input-number v-model="form.offDelaySec" :min="0" :max="3600" style="width: 100%" />
        </el-form-item>
        <el-form-item label="恢复死区">
          <el-input-number v-model="form.recoverDeadband" :min="0" :step="0.1" style="width: 100%" />
        </el-form-item>
        <el-form-item label="条件抑制">
          <el-select v-model="form.suppressMode" style="width: 100%">
            <el-option v-for="(label, value) in suppressModeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="form.suppressMode !== 'None'" label="条件测点" required>
          <el-select v-model="form.suppressTagId" filterable style="width: 100%" placeholder="例如设备运行信号">
            <el-option v-for="tag in tags.filter((t) => t.id !== form.tagId)" :key="tag.id" :label="`${tag.name} (#${tag.id})`" :value="tag.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="报警信息">
          <el-input v-model="form.message" placeholder="留空则自动生成" />
        </el-form-item>
        <el-form-item label="启用">
          <el-switch v-model="form.enabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
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
      const result = await ElMessageBox.prompt('停用期间该规则不评估，活动报警以“停用”结束。请填写原因：', '停用报警规则', {
        inputValidator: (value: string) => (value && value.trim() ? true : '必须填写原因'),
        confirmButtonText: '停用',
        cancelButtonText: '取消'
      })
      reason = result.value.trim()
    } catch {
      return
    }
  }
  try {
    await ruleApi.outOfService(rule.id, outOfService, reason)
    ElMessage.success(outOfService ? '已停用' : '已恢复')
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '操作失败'))
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
    ElMessage.error(errorMessage(error, '加载规则失败'))
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
    ElMessage.warning('请选择测点')
    return
  }

  if (form.suppressMode !== 'None' && !form.suppressTagId) {
    ElMessage.warning('请选择条件测点')
    return
  }
  if (form.suppressMode === 'None') form.suppressTagId = null

  saving.value = true
  try {
    if (editingId.value) await ruleApi.update(editingId.value, { ...form })
    else await ruleApi.create({ ...form })
    ElMessage.success('保存成功')
    dialogVisible.value = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '保存失败'))
  } finally {
    saving.value = false
  }
}

async function remove(rule: AlarmRule): Promise<void> {
  try {
    await ruleApi.remove(rule.id)
    ElMessage.success('已删除')
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '删除失败'))
  }
}

onMounted(async () => {
  try {
    tags.value = await tagApi.list()
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载测点失败'))
  }
  await load()
})
</script>
