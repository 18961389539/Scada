<template>
  <el-dialog v-model="visible" :title="$t('tagsView.kdea846')" width="480px">
    <el-alert type="info" :closable="false" show-icon :title="$t('tagsView.kd7595e')" style="margin-bottom: 12px" />
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon style="margin-bottom: 12px" data-test="tag-preview-error" />
    <el-form label-width="100px">
      <el-form-item :label="$t('tagsView.k01f2c1')">
        <el-select v-model="deviceId" data-test="tag-preview-device" style="width: 100%" @change="selectDevice">
          <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
        </el-select>
      </el-form-item>
      <el-form-item :label="$t('tagsView.k17fc93')">
        <el-select v-model="form.area" style="width: 100%">
          <el-option v-for="(label, value) in areas" :key="value" :label="label" :value="value" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="!isOpc && !text" :label="$t('tagsView.k67d2d7')">
        <el-input-number v-model="form.address" :min="0" :max="65535" style="width: 100%" />
      </el-form-item>
      <el-form-item v-if="isOpc" label="NodeId"><el-input v-model="form.nodeId" /></el-form-item>
      <el-form-item v-if="text" :label="$t('tagsView.k67d2d7')"><el-input v-model="form.nodeId" :placeholder="text.addressPlaceholder" /></el-form-item>
      <el-form-item :label="$t('tagsView.kb031dc')">
        <el-select v-model="form.dataType" style="width: 100%">
          <el-option v-for="(label, value) in dataTypeLabels" :key="value" :label="label" :value="value" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="isS7 && form.area === 'DataBlock'" :label="$t('tagsView.kaf82d3')"><el-input-number v-model="form.dbNumber" :min="1" :max="65535" /></el-form-item>
      <el-form-item v-if="isS7 && form.dataType === 'Bool'" :label="$t('tagsView.k08ca6d')"><el-input-number v-model="form.bitOffset" :min="0" :max="7" /></el-form-item>
    </el-form>
    <el-alert
      v-if="result"
      :type="good ? 'success' : 'error'"
      :closable="false"
      show-icon
      :title="good ? $t('tagsView.k2d7a00', [formatValue(result.value)]) : result.message || $t('configurationUx.testUnknown')"
    />
    <ConnectionGuidance v-if="result && !good" :message="result.message" :quality="result.quality" :protocol="deviceRow?.protocol">
      <el-button v-if="deviceRow && can(auth.user, 'Configure', deviceRow.areaId)" link type="primary" @click="diagnosticsVisible = true">{{ $t('devicesView.ke7a75c') }}</el-button>
    </ConnectionGuidance>
    <template #footer>
      <el-button @click="visible = false">{{ $t('tagsView.k6c14bd') }}</el-button>
      <el-button type="primary" :loading="previewing" @click="run">{{ $t('tagsView.k591670') }}</el-button>
    </template>
  </el-dialog>
  <CollectorDiagnosticsDialog v-if="deviceRow && can(auth.user, 'Configure', deviceRow.areaId)" v-model="diagnosticsVisible" :device="deviceRow" />
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { deviceApi } from '../../api'
import { errorMessage } from '../../api/http'
import type { Device } from '../../types'
import { dataTypeLabels, formatValue, protocolAreas } from '../../utils'
import { protocolDef } from '../../protocols'
import { tr } from '../../i18n'
import { can } from '../../access/policies'
import { useAuthStore } from '../../stores/auth'
import ConnectionGuidance from '../ConnectionGuidance.vue'
import CollectorDiagnosticsDialog from '../CollectorDiagnosticsDialog.vue'

const props = defineProps<{ devices: Device[]; initialDeviceId?: number | null }>()

const auth = useAuthStore()
const visible = ref(false)
const previewing = ref(false)
const error = ref('')
const deviceId = ref<number>()
const diagnosticsVisible = ref(false)
const form = reactive({ area: 'HoldingRegister', address: 0, dataType: 'Float', dbNumber: 1, bitOffset: 0, nodeId: '' })

const deviceRow = computed(() => props.devices.find(device => device.id === deviceId.value))
const isS7 = computed(() => deviceRow.value?.protocol === 'SiemensS7')
const isOpc = computed(() => deviceRow.value?.protocol === 'OpcUa')
const areas = computed(() => protocolAreas(deviceRow.value?.protocol))
const text = computed(() => protocolDef(deviceRow.value?.protocol))
const result = ref<Awaited<ReturnType<typeof deviceApi.test>> | null>(null)
const good = computed(() => result.value?.connected && (!result.value.quality || result.value.quality === 'Good'))
let version = 0

function selectDevice(): void {
  form.area = text.value ? 'Address' : isOpc.value ? 'OpcNode' : isS7.value ? 'DataBlock' : 'HoldingRegister'
  form.bitOffset = 0
}

function open(): void {
  visible.value = true
  error.value = ''
  result.value = null
  const initial = props.initialDeviceId
  deviceId.value = initial && props.devices.some(device => device.id === initial) ? initial : props.devices[0]?.id
  selectDevice()
}

async function run(): Promise<void> {
  if (!deviceId.value) {
    error.value = tr('tagsView.k61f337')
    return
  }
  error.value = ''
  const current = ++version
  previewing.value = true
  try {
    const tested = await deviceApi.test(
      deviceId.value,
      form.area,
      form.address,
      form.dataType, form.dbNumber, form.dataType === 'Bool' ? form.bitOffset : 0, form.nodeId,
      form.dataType === 'String' ? 32 : 0
    )
    if (current === version) result.value = tested
  } catch (reason) {
    if (current === version) result.value = { connected: false, message: errorMessage(reason, tr('tagsView.kd9f607')) }
  } finally {
    if (current === version) previewing.value = false
  }
}

watch([deviceId, visible, () => JSON.stringify(form)], () => {
  version++
  result.value = null
  previewing.value = false
}, { flush: 'sync' })

defineExpose({ open })
</script>
