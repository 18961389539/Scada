<template>
  <el-dialog v-model="visible" title="更改采集位置" width="620px" :close-on-click-modal="false">
    <div v-loading="loading">
      <el-alert v-if="error" :title="error" type="error" :closable="false" />
      <template v-if="source">
        <el-descriptions :column="1" border>
          <el-descriptions-item label="设备">#{{ source.deviceId }} · {{ source.deviceName }}</el-descriptions-item>
          <el-descriptions-item label="当前位置">{{ source.gatewayId === null ? '中心本机' : `网关 #${source.gatewayId} · ${source.gatewayName}` }}</el-descriptions-item>
        </el-descriptions>
        <el-alert :type="source.canMove ? 'info' : 'warning'" :closable="false" :title="source.blockReason ?? '已满足迁移前提，保存时会再次检查。迁移后保持设备停用，请到设备配置核对现场地址后启用。'" />
        <el-form label-width="95px" style="margin-top: 20px"><el-form-item label="目标位置">
          <el-select v-model="target" aria-label="目标采集位置" style="width: 100%" :disabled="saving || !source.canMove">
            <el-option :value="0" label="中心本机" />
            <el-option v-for="gateway in gateways" :key="gateway.id" :value="gateway.id" :disabled="!gateway.enabled" :label="`网关 #${gateway.id} · ${gateway.name}${gateway.enabled ? '' : '（已停用）'}`" />
          </el-select>
        </el-form-item></el-form>
      </template>
    </div>
    <template #footer>
      <el-button :disabled="saving" @click="load(false)">刷新迁移条件</el-button>
      <el-button :disabled="saving" @click="visible = false">取消</el-button>
      <el-button type="primary" :loading="saving" :disabled="loading || !fresh || !targetAllowed || !source?.canMove || target === (source.gatewayId ?? 0)" @click="save">保存采集位置</el-button>
    </template>
  </el-dialog>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { gatewayApi, type GatewayAssignment, type GatewayInfo } from '../api/gateways'
import { errorMessage } from '../api/http'
const visible = defineModel<boolean>({ required: true })
const props = defineProps<{ deviceId: number; suggestedGatewayId?: number }>()
const emit = defineEmits<{ changed: [] }>()
const source = ref<GatewayAssignment | null>(null), gateways = ref<GatewayInfo[]>([]), target = ref(0)
const loading = ref(false), saving = ref(false), error = ref('')
const fresh = ref(false), targetAllowed = computed(() => target.value === 0 || gateways.value.some(x => x.id === target.value && x.enabled))
let generation = 0, controller: AbortController | undefined, alive = true
watch([visible, () => props.deviceId], ([shown]) => { generation++; controller?.abort(); source.value = null; error.value = ''; if (shown) void load(true) }, { immediate: true })
onBeforeUnmount(() => { alive = false; generation++; controller?.abort() })
async function load(reset: boolean): Promise<void> {
  const current = ++generation; controller?.abort(); const request = controller = new AbortController(); loading.value = true; fresh.value = false
  try {
    const [device, list] = await Promise.all([gatewayApi.assignment(props.deviceId, request.signal), gatewayApi.list(request.signal)])
    if (!alive || !visible.value || generation !== current) return
    source.value = device; gateways.value = list; fresh.value = true; if (reset) target.value = props.suggestedGatewayId ?? device.gatewayId ?? 0
  } catch (e) { if (alive && current === generation && !request.signal.aborted) error.value = errorMessage(e, '读取迁移条件失败') }
  finally { if (alive && current === generation) loading.value = false }
}
async function save(): Promise<void> {
  if (!source.value?.canMove || !fresh.value || !targetAllowed.value || saving.value || loading.value) return
  const original = source.value, destination = target.value, current = generation
  const label = destination === 0 ? '中心本机' : `网关 #${destination} · ${gateways.value.find(x => x.id === destination)?.name ?? ''}`
  saving.value = true; let submitted = false
  try {
    await ElMessageBox.confirm(`将设备 #${original.deviceId} ${original.deviceName} 迁至${label}。设备仍保持停用，网络地址和串口将以目标主机为准。`, '核对采集位置', { type: 'warning', confirmButtonText: '确认迁移' })
    if (!alive || !visible.value || current !== generation) return
    submitted = true; await gatewayApi.assign(original, destination || null)
    if (alive && visible.value && current === generation) { ElMessage.success('采集位置已更新，设备保持停用'); visible.value = false; emit('changed') }
  } catch (e) {
    if (e !== 'cancel' && e !== 'close' && alive && visible.value && current === generation) {
      error.value = `${errorMessage(e)}${submitted ? '；请刷新实际采集位置和迁移条件后再操作。' : ''}`
      if (submitted) await load(false)
    }
  } finally { if (alive) saving.value = false }
}
</script>
