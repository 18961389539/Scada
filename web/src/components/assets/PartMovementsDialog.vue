<template>
  <el-dialog v-model="visible" :title="$t('assets.part.movementsTitle', [target?.name ?? ''])" width="760px" destroy-on-close>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="part-movements-error" />
    <el-table :data="movements" size="small" :empty-text="$t('assets.part.noMovements')" data-test="part-movements-table">
      <el-table-column :label="$t('assets.part.time')" width="150">
        <template #default="{ row }">{{ formatTime(row.ts) }}</template>
      </el-table-column>
      <el-table-column :label="$t('assets.part.movementType')" width="100">
        <template #default="{ row }">{{ movementTypeLabel(row.type) }}</template>
      </el-table-column>
      <el-table-column :label="$t('assets.part.quantity')" width="100">
        <template #default="{ row }">{{ props.formatQty(row.quantity) }}</template>
      </el-table-column>
      <el-table-column :label="$t('assets.part.balanceAfter')" width="100">
        <template #default="{ row }">{{ props.formatQty(row.balanceAfter) }}</template>
      </el-table-column>
      <el-table-column :label="$t('assets.part.workOrder')" width="150" show-overflow-tooltip>
        <template #default="{ row }">{{ props.orderNumber(row.workOrderId) }}</template>
      </el-table-column>
      <el-table-column prop="operator" :label="$t('assets.part.operator')" width="110" show-overflow-tooltip />
      <el-table-column prop="note" :label="$t('assets.part.note')" min-width="120" show-overflow-tooltip />
    </el-table>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { sparePartApi } from '../../api'
import { errorMessage } from '../../api/http'
import { tr } from '../../i18n'
import { formatTime } from '../../utils'
import type { SparePart, SparePartMovement, SparePartMovementType } from '../../types'

const props = defineProps<{
  orderNumber: (id?: number | null) => string
  formatQty: (value: number) => string
}>()

const visible = ref(false)
const error = ref('')
const target = ref<SparePart | null>(null)
const movements = ref<SparePartMovement[]>([])
let detailSeq = 0

function movementTypeLabel(type: SparePartMovementType): string {
  if (type === 'In') return tr('assets.part.stockIn')
  if (type === 'Out') return tr('assets.part.stockOut')
  return tr('assets.part.adjust')
}

async function open(row: SparePart): Promise<void> {
  const seq = ++detailSeq
  target.value = row
  movements.value = []
  error.value = ''
  visible.value = true
  try {
    const result = await sparePartApi.movements(row.id)
    if (seq !== detailSeq || target.value?.id !== row.id) return
    movements.value = result
  } catch (reason) {
    if (seq !== detailSeq || target.value?.id !== row.id) return
    error.value = errorMessage(reason, tr('assets.loadFailed'))
  }
}

defineExpose({ open })
</script>
