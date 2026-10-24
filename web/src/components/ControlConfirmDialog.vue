<template>
  <el-dialog v-model="visible" title="控制确认" width="480px" append-to-body data-test="control-confirm" @closed="finish(false)">
    <div v-loading="loading">
      <el-descriptions v-if="check" :column="1" size="small" border>
        <el-descriptions-item label="操作">{{ title }}</el-descriptions-item>
        <el-descriptions-item label="测点">{{ check.tagName }}（#{{ check.tagId }}）</el-descriptions-item>
        <el-descriptions-item label="当前值 → 目标值"><strong>{{ check.current ?? '无有效值' }} → {{ value }}</strong></el-descriptions-item>
        <el-descriptions-item v-if="check.limitMin != null || check.limitMax != null" label="操作限幅">[{{ check.limitMin ?? '-' }}, {{ check.limitMax ?? '-' }}]</el-descriptions-item>
        <el-descriptions-item v-if="check.maxStep != null" label="单次变化上限">{{ check.maxStep }}</el-descriptions-item>
        <el-descriptions-item v-if="check.feedback" label="反馈条件">{{ check.feedback }}</el-descriptions-item>
        <el-descriptions-item label="控制权">{{ check.mode === 'Local' ? '就地' : '远程' }}</el-descriptions-item>
        <el-descriptions-item v-if="check.lockouts.length" label="挂牌">{{ check.lockouts.join('；') }}</el-descriptions-item>
      </el-descriptions>
      <el-alert v-for="r in check?.refusals ?? []" :key="r" :title="r" type="error" :closable="false" show-icon style="margin-top: 8px" data-test="control-refusal" />
      <el-alert v-if="check && !check.refusals.length && check.dualApproval" type="warning" :closable="false" show-icon style="margin-top: 8px"
        title="该测点需要双人复核：确认后进入待复核，由另一位有权限的用户在“现场操作 → 待复核”批准后执行" />
      <el-alert v-if="check && !check.refusals.length && !check.dualApproval" type="info" :closable="false" show-icon style="margin-top: 8px"
        title="确认后串行下发并回读校验；配置了反馈条件的在超时内未到位判为失败" />
    </div>
    <template #footer>
      <el-button @click="finish(false)">取消</el-button>
      <el-button type="primary" :disabled="!check || check.refusals.length > 0" data-test="control-confirm-ok" @click="finish(true)">{{ check?.dualApproval ? '提交复核' : '确认下发' }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { ElMessage } from 'element-plus'
import { controlApi, type ControlCheck } from '../api/control'
import { errorMessage } from '../api/http'

const visible = ref(false), loading = ref(false), check = ref<ControlCheck | null>(null), title = ref(''), value = ref(0)
let resolver: ((ok: boolean) => void) | null = null

function finish(ok: boolean): void { visible.value = false; const r = resolver; resolver = null; r?.(ok) }

// 先做服务端预检查（限幅、变化率、许可、就地/远程、挂牌），通过后由操作人确认；返回是否确认。
async function open(tagId: number, target: number, label: string): Promise<boolean> {
  title.value = label; value.value = target; check.value = null; visible.value = true; loading.value = true
  try { check.value = await controlApi.check(tagId, target) }
  catch (e) { ElMessage.error(errorMessage(e, '控制检查失败')); visible.value = false; return false }
  finally { loading.value = false }
  return await new Promise<boolean>(resolve => { resolver = resolve })
}
defineExpose({ open })
</script>
