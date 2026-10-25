<template>
  <el-dialog v-model="visible" :title="$t('gatewayAssignmentDialog.k5ad09b')" width="620px" :close-on-click-modal="false">
    <div v-loading="loading">
      <el-alert v-if="error" :title="error" type="error" :closable="false" />
      <template v-if="source">
        <el-descriptions :column="1" border>
          <el-descriptions-item :label="$t('gatewayAssignmentDialog.k01f2c1')">#{{ source.deviceId }} · {{ source.deviceName }}</el-descriptions-item>
          <el-descriptions-item :label="$t('gatewayAssignmentDialog.kb458ea')">{{ source.gatewayId === null ? $t('gatewayAssignmentDialog.k3963d7') : $t('gatewayAssignmentDialog.kc3a1fc', [source.gatewayId, source.gatewayName]) }}</el-descriptions-item>
        </el-descriptions>
        <el-alert :type="source.canMove ? 'info' : 'warning'" :closable="false" :title="source.blockReason ?? $t('gatewayAssignmentDialog.kcf1bd8')" />
        <el-form label-width="95px" style="margin-top: 20px"><el-form-item :label="$t('gatewayAssignmentDialog.k1e85fb')">
          <el-select v-model="target" :aria-label="$t('gatewayAssignmentDialog.k6b6838')" style="width: 100%" :disabled="saving || !source.canMove">
            <el-option :value="0" :label="$t('gatewayAssignmentDialog.k3963d7')" />
            <el-option v-for="gateway in gateways" :key="gateway.id" :value="gateway.id" :disabled="!gateway.enabled" :label="$t('gatewayAssignmentDialog.k260635', [gateway.id, gateway.name, gateway.enabled ? '' : $t('gatewayAssignmentDialog.k598178')])" />
          </el-select>
        </el-form-item></el-form>
      </template>
    </div>
    <template #footer>
      <el-button :disabled="saving" @click="load(false)">{{ $t('gatewayAssignmentDialog.k2a905e') }}</el-button>
      <el-button :disabled="saving" @click="visible = false">{{ $t('gatewayAssignmentDialog.k4d0b46') }}</el-button>
      <el-button type="primary" :loading="saving" :disabled="loading || !fresh || !targetAllowed || !source?.canMove || target === (source.gatewayId ?? 0)" @click="save">{{ $t('gatewayAssignmentDialog.k944c2d') }}</el-button>
    </template>
  </el-dialog>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { gatewayApi, type GatewayAssignment, type GatewayInfo } from '../api/gateways'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'
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
  } catch (e) { if (alive && current === generation && !request.signal.aborted) error.value = errorMessage(e, tr('gatewayAssignmentDialog.k77a2f4')) }
  finally { if (alive && current === generation) loading.value = false }
}
async function save(): Promise<void> {
  if (!source.value?.canMove || !fresh.value || !targetAllowed.value || saving.value || loading.value) return
  const original = source.value, destination = target.value, current = generation
  const label = destination === 0 ? tr('gatewayAssignmentDialog.k3963d7') : tr('gatewayAssignmentDialog.kc3a1fc', [destination, gateways.value.find(x => x.id === destination)?.name ?? ''])
  saving.value = true; let submitted = false
  try {
    await ElMessageBox.confirm(tr('gatewayAssignmentDialog.kdb02be', [original.deviceId, original.deviceName, label]), tr('gatewayAssignmentDialog.kd78ef6'), { type: 'warning', confirmButtonText: tr('gatewayAssignmentDialog.k20052b') })
    if (!alive || !visible.value || current !== generation) return
    submitted = true; await gatewayApi.assign(original, destination || null)
    if (alive && visible.value && current === generation) { ElMessage.success(tr('gatewayAssignmentDialog.k30a48b')); visible.value = false; emit('changed') }
  } catch (e) {
    if (e !== 'cancel' && e !== 'close' && alive && visible.value && current === generation) {
      error.value = tr('gatewayAssignmentDialog.k6fe2eb', [errorMessage(e), submitted ? tr('gatewayAssignmentDialog.k2b126e') : ''])
      if (submitted) await load(false)
    }
  } finally { if (alive) saving.value = false }
}
</script>
