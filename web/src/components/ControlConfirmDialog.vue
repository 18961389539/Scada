<template>
  <el-dialog v-model="visible" :title="$t('controlConfirmDialog.kdee660')" width="480px" append-to-body data-test="control-confirm" @update:model-value="finish(false)">
    <div v-loading="loading">
      <el-descriptions v-if="check" :column="1" size="small" border>
        <el-descriptions-item :label="$t('controlConfirmDialog.kf3ea6d')">{{ title }}</el-descriptions-item>
        <el-descriptions-item :label="$t('controlConfirmDialog.k8cff1c')">{{ check.tagName }}（#{{ check.tagId }}）</el-descriptions-item>
        <el-descriptions-item :label="$t('controlConfirmDialog.kd49433')"><strong>{{ check.current ?? $t('controlConfirmDialog.kb20e33') }} → {{ value }}</strong></el-descriptions-item>
        <el-descriptions-item v-if="check.limitMin != null || check.limitMax != null" :label="$t('controlConfirmDialog.kb3bb3c')">[{{ check.limitMin ?? '-' }}, {{ check.limitMax ?? '-' }}]</el-descriptions-item>
        <el-descriptions-item v-if="check.maxStep != null" :label="$t('controlConfirmDialog.k2a415d')">{{ check.maxStep }}</el-descriptions-item>
        <el-descriptions-item v-if="check.feedback" :label="$t('controlConfirmDialog.k463dc4')">{{ check.feedback }}</el-descriptions-item>
        <el-descriptions-item :label="$t('controlConfirmDialog.k0e9ffe')">{{ check.mode === 'Local' ? $t('controlConfirmDialog.kee521f') : $t('controlConfirmDialog.k801947') }}</el-descriptions-item>
        <el-descriptions-item v-if="check.lockouts.length" :label="$t('controlConfirmDialog.k8d2356')">{{ check.lockouts.join('；') }}</el-descriptions-item>
      </el-descriptions>
      <el-alert v-if="checkError" :title="checkError" type="error" :closable="false" show-icon style="margin-top: 8px" data-test="control-check-error" />
      <el-alert v-for="r in check?.refusals ?? []" :key="r" :title="r" type="error" :closable="false" show-icon style="margin-top: 8px" data-test="control-refusal" />
      <el-alert v-if="check && !check.refusals.length && check.dualApproval" type="warning" :closable="false" show-icon style="margin-top: 8px"
        :title="$t('controlConfirmDialog.k21e633')" />
      <el-alert v-if="check && !check.refusals.length && !check.dualApproval" type="info" :closable="false" show-icon style="margin-top: 8px"
        :title="$t('controlConfirmDialog.k63bcfd')" />
    </div>
    <template #footer>
      <el-button @click="finish(false)">{{ $t('controlConfirmDialog.k4d0b46') }}</el-button>
      <el-button type="primary" :disabled="!check || check.refusals.length > 0" data-test="control-confirm-ok" @click="finish(true)">{{ check?.dualApproval ? $t('controlConfirmDialog.k61385c') : $t('controlConfirmDialog.k00bb4a') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import { controlApi, type ControlCheck } from '../api/control'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'

const visible = ref(false), loading = ref(false), check = ref<ControlCheck | null>(null), checkError = ref(''), title = ref(''), value = ref(0)
let resolver: ((ok: boolean) => void) | null = null
let generation = 0

function finish(ok: boolean): void { generation++; visible.value = false; const r = resolver; resolver = null; r?.(ok) }

// 先做服务端预检查（限幅、变化率、许可、就地/远程、挂牌），通过后由操作人确认；返回是否确认。
function open(tagId: number, target: number, label: string): Promise<boolean> {
  finish(false)
  const currentGeneration = generation
  title.value = label; value.value = target; check.value = null; checkError.value = ''; visible.value = true; loading.value = true
  const decision = new Promise<boolean>(resolve => { resolver = resolve })
  void controlApi.check(tagId, target).then(result => {
    if (currentGeneration === generation) check.value = result
  }).catch(e => {
    if (currentGeneration === generation) checkError.value = errorMessage(e, tr('controlConfirmDialog.k831e7c'))
  }).finally(() => {
    if (currentGeneration === generation) loading.value = false
  })
  return decision
}
onBeforeUnmount(() => finish(false))
defineExpose({ open })
</script>
