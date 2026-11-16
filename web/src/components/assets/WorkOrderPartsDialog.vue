<template>
  <el-dialog v-model="visible" :title="$t('assets.workOrder.partsTitle', [target?.number ?? ''])" width="760px" destroy-on-close>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="wo-parts-error" />
    <div v-if="target && props.canControlArea(target.areaId) && !props.isClosed(target.status)" class="toolbar filter-row">
      <el-select v-model="form.partId" filterable :placeholder="$t('assets.workOrder.part')" style="width: 240px" data-test="wo-part-select">
        <el-option v-for="p in props.enabledParts" :key="p.id" :label="`${p.code} ${p.name} (${props.formatQty(p.stock)} ${p.unit})`" :value="p.id" />
      </el-select>
      <el-input-number v-model="form.quantity" :min="0.001" :step="1" data-test="wo-part-qty" />
      <el-input v-model="form.note" :placeholder="$t('assets.workOrder.note')" style="width: 180px" />
      <el-button type="primary" :loading="saving" data-test="wo-part-issue" @click="submit">{{ $t('assets.workOrder.issuePart') }}</el-button>
    </div>
    <el-table :data="movements" size="small" :empty-text="$t('assets.workOrder.partEmpty')" data-test="wo-parts-table">
      <el-table-column :label="$t('assets.part.time')" width="150">
        <template #default="{ row }">{{ formatTime(row.ts) }}</template>
      </el-table-column>
      <el-table-column :label="$t('assets.workOrder.part')" min-width="150" show-overflow-tooltip>
        <template #default="{ row }">{{ props.partName(row.partId) }}</template>
      </el-table-column>
      <el-table-column :label="$t('assets.workOrder.quantity')" width="100">
        <template #default="{ row }">{{ props.formatQty(row.quantity) }}</template>
      </el-table-column>
      <el-table-column :label="$t('assets.part.balanceAfter')" width="100">
        <template #default="{ row }">{{ props.formatQty(row.balanceAfter) }}</template>
      </el-table-column>
      <el-table-column prop="operator" :label="$t('assets.part.operator')" width="110" show-overflow-tooltip />
      <el-table-column prop="note" :label="$t('assets.part.note')" min-width="120" show-overflow-tooltip />
    </el-table>
  </el-dialog>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { workOrderApi } from '../../api'
import { errorMessage } from '../../api/http'
import { tr } from '../../i18n'
import { formatTime } from '../../utils'
import type { MaintenanceWorkOrder, SparePart, SparePartMovement, WorkOrderStatus } from '../../types'

const props = defineProps<{
  enabledParts: SparePart[]
  canControlArea: (areaId: number) => boolean
  isClosed: (status: WorkOrderStatus) => boolean
  partName: (id: number) => string
  formatQty: (value: number) => string
}>()
const emit = defineEmits<{ changed: [] }>()

const visible = ref(false)
const saving = ref(false)
const error = ref('')
const target = ref<MaintenanceWorkOrder | null>(null)
const movements = ref<SparePartMovement[]>([])
const form = reactive({ partId: null as number | null, quantity: 1, note: '' })
let detailSeq = 0

async function open(row: MaintenanceWorkOrder): Promise<void> {
  const seq = ++detailSeq
  target.value = row
  movements.value = []
  form.partId = null
  form.quantity = 1
  form.note = ''
  error.value = ''
  visible.value = true
  try {
    const parts = await workOrderApi.parts(row.id)
    if (seq !== detailSeq || target.value?.id !== row.id) return
    movements.value = parts
  } catch (reason) {
    if (seq !== detailSeq || target.value?.id !== row.id) return
    error.value = errorMessage(reason, tr('assets.loadFailed'))
  }
}

async function submit(): Promise<void> {
  if (!target.value) return
  if (form.partId == null || !Number.isFinite(form.quantity) || form.quantity <= 0) {
    error.value = tr('assets.part.quantityHint')
    return
  }
  saving.value = true
  error.value = ''
  try {
    await workOrderApi.issuePart(target.value.id, {
      partId: form.partId, quantity: form.quantity, note: form.note.trim()
    })
    movements.value = await workOrderApi.parts(target.value.id)
    emit('changed')
    form.quantity = 1
    form.note = ''
    ElMessage.success(tr('assets.saved'))
  } catch (reason) {
    error.value = errorMessage(reason, tr('assets.actionFailed'))
  } finally {
    saving.value = false
  }
}

defineExpose({ open })
</script>
