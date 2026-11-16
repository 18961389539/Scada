<template>
  <el-dialog v-model="visible" :title="$t('controlView.k754e7b', [form ? tagName(form.tagId) : ''])" width="560px" :before-close="guard.beforeClose" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving" data-test="point-dialog" @keydown="guard.onKey">
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="point-save-error" />
    <el-form v-if="form" label-width="120px" size="small" :disabled="saving">
      <el-form-item :label="$t('controlView.k2f3719')"><el-switch v-model="form.requireConfirm" /><span class="tiny muted">　{{ $t('controlView.k613065') }}</span></el-form-item>
      <el-form-item :label="$t('controlView.kfabc6b')"><el-switch v-model="form.dualApproval" data-test="point-dual" />　{{ $t('controlView.k222460') }} <template v-if="form.dualApproval"><el-input-number v-model="form.approvalTimeoutSec" :min="30" :max="3600" /> {{ $t('controlView.keb6aab') }}</template></el-form-item>
      <el-form-item :label="$t('controlView.kb3bb3c')"><el-input-number v-model="form.limitMin" :controls="false" :placeholder="$t('controlView.kaf3af1')" style="width: 110px" /> ～ <el-input-number v-model="form.limitMax" :controls="false" :placeholder="$t('controlView.k0517c1')" style="width: 110px" data-test="point-max" /></el-form-item>
      <el-form-item :label="$t('controlView.k2a415d')"><el-input-number v-model="form.maxStep" :controls="false" :min="0" :placeholder="$t('controlView.k09c4fc')" style="width: 110px" /></el-form-item>
      <el-form-item :label="$t('controlView.k0f7d64')"><el-input v-model="form.permitExpression" :placeholder="$t('controlView.k89c17f')" data-test="point-permit" /></el-form-item>
      <el-form-item v-if="form.permitExpression?.trim()" :label="$t('controlView.k296eb0')"><el-input v-model="form.permitMessage" maxlength="200" :placeholder="$t('controlView.k0aa008')" /></el-form-item>
      <el-form-item :label="$t('controlView.k463dc4')"><el-input v-model="form.feedbackExpression" :placeholder="$t('controlView.k79b797')" /></el-form-item>
      <el-form-item v-if="form.feedbackExpression?.trim()" :label="$t('controlView.k7ca8c6')"><el-input-number v-model="form.feedbackTimeoutMs" :min="100" :max="600000" :step="1000" /> {{ $t('controlView.kf3c3d5') }}</el-form-item>
    </el-form>
    <template #footer><el-button :disabled="saving" @click="guard.close()">{{ $t('controlView.k4d0b46') }}</el-button><el-button type="primary" :loading="saving" data-test="point-save" @click="savePoint()">{{ $t('controlView.kfadf24') }}</el-button></template>
  </el-dialog>
</template>

<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { controlApi, type ControlPoint } from '../../api/control'
import { errorMessage } from '../../api/http'
import { tr } from '../../i18n'
import { useEditDialog } from '../../composables/useEditDialog'

const props = defineProps<{
  points: ControlPoint[]
  tagName: (id: number | null) => string
}>()
const emit = defineEmits<{ saved: [] }>()

const visible = ref(false), saving = ref(false), error = ref('')
const form = ref<ControlPoint | null>(null)
const optionalText = (value: string | null | undefined): string | null => value === '' || value == null ? null : value
let disposed = false

const guard = useEditDialog({
  visible,
  saving,
  snapshot: () => JSON.stringify(form.value && {
    ...form.value,
    permitExpression: optionalText(form.value.permitExpression),
    permitMessage: optionalText(form.value.permitMessage),
    feedbackExpression: optionalText(form.value.feedbackExpression)
  }),
  save: savePoint
})

function open(tagId: number): void {
  if (saving.value) return
  const existing = props.points.find(x => x.tagId === tagId)
  error.value = ''
  form.value = existing ? { ...existing } : { tagId, requireConfirm: true, dualApproval: false, approvalTimeoutSec: 300, permitExpression: null, permitMessage: null, limitMin: null, limitMax: null, maxStep: null, feedbackExpression: null, feedbackTimeoutMs: 10000 }
  visible.value = true
}

async function savePoint(): Promise<void> {
  if (!form.value || saving.value || disposed || !visible.value) return
  const payload = { ...form.value }
  saving.value = true
  error.value = ''
  try {
    await controlApi.savePoint(payload)
    if (disposed) return
    ElMessage.success(tr('controlView.kcdfab9')); visible.value = false
    emit('saved')
  } catch (e) {
    error.value = errorMessage(e, tr('controlView.k40525a'))
  } finally { saving.value = false }
}

onBeforeUnmount(() => { disposed = true })
defineExpose({ open })
</script>
