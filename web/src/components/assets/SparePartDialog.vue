<template>
  <el-dialog v-model="visible" :title="form.id ? $t('assets.part.editTitle') : $t('assets.part.addTitle')" width="680px" destroy-on-close>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="part-save-error" />
    <el-form label-width="100px" size="small">
      <el-row :gutter="12">
        <el-col :span="12"><el-form-item :label="$t('assets.part.code')"><el-input v-model="form.code" data-test="part-code" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('assets.part.name')"><el-input v-model="form.name" data-test="part-name" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('assets.part.specification')"><el-input v-model="form.specification" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('assets.part.unit')"><el-input v-model="form.unit" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('assets.part.stock')"><el-input-number v-model="form.stock" :min="0" style="width: 100%" data-test="part-stock" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('assets.part.minStock')"><el-input-number v-model="form.minStock" :min="0" style="width: 100%" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('assets.part.location')"><el-input v-model="form.location" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('assets.part.supplier')"><el-input v-model="form.supplier" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('assets.part.unitPrice')"><el-input-number v-model="form.unitPrice" :min="0" :precision="2" style="width: 100%" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('assets.part.enabled')"><el-switch v-model="form.enabled" /></el-form-item></el-col>
        <el-col :span="24"><el-form-item :label="$t('assets.part.description')"><el-input v-model="form.description" type="textarea" :rows="2" /></el-form-item></el-col>
      </el-row>
    </el-form>
    <template #footer>
      <el-button @click="visible = false">{{ $t('assets.cancel') }}</el-button>
      <el-button type="primary" :loading="saving" data-test="part-save" @click="save">{{ $t('assets.save') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { sparePartApi } from '../../api'
import { errorMessage } from '../../api/http'
import { tr } from '../../i18n'
import type { SparePart, SparePartInput } from '../../types'

const emit = defineEmits<{ changed: [] }>()
const visible = ref(false)
const saving = ref(false)
const error = ref('')
const form = reactive({
  id: 0,
  code: '', name: '', specification: '', unit: '', stock: 0, minStock: 0, location: '', supplier: '',
  unitPrice: null as number | null, description: '', enabled: true
})

function open(row?: SparePart): void {
  form.id = row?.id ?? 0
  form.code = row?.code ?? ''
  form.name = row?.name ?? ''
  form.specification = row?.specification ?? ''
  form.unit = row?.unit ?? ''
  form.stock = row?.stock ?? 0
  form.minStock = row?.minStock ?? 0
  form.location = row?.location ?? ''
  form.supplier = row?.supplier ?? ''
  form.unitPrice = row?.unitPrice ?? null
  form.description = row?.description ?? ''
  form.enabled = row?.enabled ?? true
  error.value = ''
  visible.value = true
}

async function save(): Promise<void> {
  saving.value = true
  error.value = ''
  try {
    const input: SparePartInput = {
      code: form.code.trim(), name: form.name.trim(), specification: form.specification.trim(),
      unit: form.unit.trim(), stock: form.stock, minStock: form.minStock, location: form.location.trim(),
      supplier: form.supplier.trim(), unitPrice: form.unitPrice, description: form.description.trim(),
      enabled: form.enabled
    }
    if (form.id) await sparePartApi.update(form.id, input)
    else await sparePartApi.create(input)
    visible.value = false
    emit('changed')
    ElMessage.success(tr('assets.saved'))
  } catch (reason) {
    error.value = errorMessage(reason, tr('assets.saveFailed'))
  } finally {
    saving.value = false
  }
}

defineExpose({ open })
</script>
