<template>
  <el-dialog v-model="visible" :title="$t('assets.part.movementTitle')" width="520px" destroy-on-close>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="part-movement-error" />
    <el-form label-width="110px" size="small">
      <el-form-item :label="$t('assets.part.movementType')">
        <el-radio-group v-model="form.type" data-test="part-movement-type">
          <el-radio-button v-for="t in movementTypeValues" :key="t" :value="t">{{ movementTypeLabel(t) }}</el-radio-button>
        </el-radio-group>
      </el-form-item>
      <el-form-item :label="form.type === 'Adjust' ? $t('assets.part.adjustQuantity') : $t('assets.part.quantity')">
        <el-input-number v-model="form.quantity" :min="form.type === 'Adjust' ? 0 : 0.001" :step="1" data-test="part-movement-qty" />
      </el-form-item>
      <el-form-item v-if="form.type === 'Out'" :label="$t('assets.part.workOrder')">
        <el-select v-model="form.workOrderId" clearable :placeholder="$t('assets.none')" style="width: 100%">
          <el-option v-for="o in props.openOrders" :key="o.id" :label="`${o.number} ${o.title}`" :value="o.id" />
        </el-select>
      </el-form-item>
      <el-form-item :label="$t('assets.part.note')"><el-input v-model="form.note" type="textarea" :rows="2" /></el-form-item>
      <div v-if="form.type === 'Adjust'" class="hint">{{ $t('assets.part.adjustHint') }}</div>
    </el-form>
    <template #footer>
      <el-button @click="visible = false">{{ $t('assets.cancel') }}</el-button>
      <el-button type="primary" :loading="saving" data-test="part-movement-save" @click="submit">{{ $t('assets.save') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { sparePartApi } from '../../api'
import { errorMessage } from '../../api/http'
import { tr } from '../../i18n'
import type { MaintenanceWorkOrder, SparePart, SparePartMovementType } from '../../types'

const props = defineProps<{ openOrders: MaintenanceWorkOrder[] }>()
const emit = defineEmits<{ changed: [] }>()

const movementTypeValues: SparePartMovementType[] = ['In', 'Out', 'Adjust']
const visible = ref(false)
const saving = ref(false)
const error = ref('')
const target = ref<SparePart | null>(null)
const form = reactive({
  type: 'In' as SparePartMovementType,
  quantity: 1,
  note: '',
  workOrderId: null as number | null
})

function movementTypeLabel(type: SparePartMovementType): string {
  if (type === 'In') return tr('assets.part.stockIn')
  if (type === 'Out') return tr('assets.part.stockOut')
  return tr('assets.part.adjust')
}

function open(row: SparePart, type: SparePartMovementType): void {
  target.value = row
  form.type = type
  form.quantity = type === 'Adjust' ? row.stock : 1
  form.note = ''
  form.workOrderId = null
  error.value = ''
  visible.value = true
}

async function submit(): Promise<void> {
  if (!target.value) return
  if (!Number.isFinite(form.quantity) || (form.type === 'Adjust' ? form.quantity < 0 : form.quantity <= 0)) {
    error.value = tr('assets.part.quantityHint')
    return
  }
  saving.value = true
  error.value = ''
  try {
    await sparePartApi.addMovement(target.value.id, {
      type: form.type, quantity: form.quantity, note: form.note.trim(),
      workOrderId: form.type === 'Out' ? form.workOrderId : null
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
