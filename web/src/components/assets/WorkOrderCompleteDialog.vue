<template>
  <el-dialog v-model="visible" :title="$t('assets.workOrder.completeTitle', [target?.number ?? ''])" width="560px" destroy-on-close>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="wo-complete-error" />
    <el-form label-width="110px" size="small">
      <el-form-item :label="$t('assets.workOrder.resolution')">
        <el-input v-model="form.resolution" type="textarea" :rows="4" data-test="wo-resolution" />
      </el-form-item>
      <el-form-item :label="$t('assets.workOrder.downtime')">
        <el-input-number v-model="form.downtimeMinutes" :min="0" :max="100000" data-test="wo-downtime" />
      </el-form-item>
      <div class="hint">{{ $t('assets.workOrder.downtimeHint') }}</div>
    </el-form>
    <template #footer>
      <el-button @click="visible = false">{{ $t('assets.cancel') }}</el-button>
      <el-button type="primary" :loading="saving" data-test="wo-complete-submit" @click="submit">{{ $t('assets.workOrder.complete') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { workOrderApi } from '../../api'
import { errorMessage } from '../../api/http'
import { tr } from '../../i18n'
import type { MaintenanceWorkOrder } from '../../types'

const emit = defineEmits<{ changed: [] }>()
const visible = ref(false)
const saving = ref(false)
const error = ref('')
const target = ref<MaintenanceWorkOrder | null>(null)
const form = reactive({ resolution: '', downtimeMinutes: 0 })

function open(row: MaintenanceWorkOrder): void {
  target.value = row
  form.resolution = ''
  form.downtimeMinutes = 0
  error.value = ''
  visible.value = true
}

async function submit(): Promise<void> {
  if (!target.value) return
  if (!form.resolution.trim()) {
    error.value = tr('assets.workOrder.resolutionRequired')
    return
  }
  saving.value = true
  error.value = ''
  try {
    await workOrderApi.setStatus(target.value.id, {
      status: 'Completed', resolution: form.resolution.trim(), downtimeMinutes: form.downtimeMinutes
    })
    visible.value = false
    emit('changed')
    ElMessage.success(tr('assets.saved'))
  } catch (reason) {
    error.value = errorMessage(reason, tr('assets.actionFailed'))
  } finally {
    saving.value = false
  }
}

defineExpose({ open })
</script>
