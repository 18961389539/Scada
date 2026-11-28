<template>
  <el-dialog v-model="visible" :title="$t('devicesView.k0e247b')" width="480px">
    <p v-if="targetText" class="tiny muted" data-test="device-test-target">{{ targetText }}</p>
    <el-form label-width="100px">
      <el-form-item :label="$t('devicesView.k17fc93')">
        <el-select v-model="form.area" style="width: 100%">
          <el-option v-for="(label, value) in areas" :key="value" :label="label" :value="value" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="text" :label="$t('devicesView.k67d2d7')"><el-input v-model="form.nodeId" :placeholder="text.addressPlaceholder" /></el-form-item>
      <el-form-item v-if="!isOpc && !text" :label="$t('devicesView.k67d2d7')">
        <el-input-number v-model="form.address" :min="0" :max="65535" style="width: 100%" />
      </el-form-item>
      <el-form-item v-if="isOpc" label="NodeId"><el-input v-model="form.nodeId" placeholder="nsu=urn:plant;s=Boiler.Temperature" /></el-form-item>
      <el-form-item :label="$t('devicesView.kb031dc')">
        <el-select v-model="form.dataType" style="width: 100%">
          <el-option v-for="(label, value) in dataTypeLabels" :key="value" :label="label" :value="value" />
        </el-select>
      </el-form-item>
      <template v-if="isS7">
        <el-form-item v-if="form.area === 'DataBlock'" :label="$t('devicesView.kaf82d3')"><el-input-number v-model="form.dbNumber" :min="1" :max="65535" /></el-form-item>
        <el-form-item v-if="form.dataType === 'Bool'" :label="$t('devicesView.k08ca6d')"><el-input-number v-model="form.bitOffset" :min="0" :max="7" /></el-form-item>
        <el-text type="info">{{ $t('devicesView.k91ff21') }}</el-text>
      </template>
    </el-form>
    <el-alert
      v-if="result"
      :type="good ? 'success' : 'error'"
      :closable="false"
      show-icon
      :title="good ? $t('devicesView.kdeaab5', [formatValue(result.value)]) : result.message || $t('configurationUx.testUnknown')"
    />
    <ConnectionGuidance v-if="result && !good" :message="result.message" :quality="result.quality" :protocol="device?.protocol">
      <el-button v-if="canDiagnose" link type="primary" @click="diagnose">{{ $t('devicesView.ke7a75c') }}</el-button>
      <el-button v-if="isOpc && canOpenCertificates" link @click="emit('certificates')">{{ $t('devicesView.k1c11ad') }}</el-button>
    </ConnectionGuidance>
    <template #footer>
      <el-button @click="visible = false">{{ $t('devicesView.k6c14bd') }}</el-button>
      <el-button type="primary" :loading="testing" @click="runTest">{{ $t('devicesView.ke373f7') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { deviceApi } from '../../api'
import { errorMessage } from '../../api/http'
import type { Device, DeviceAcquisitionInfo } from '../../types'
import { dataTypeLabels, formatValue, protocolAreas } from '../../utils'
import { protocolDef } from '../../protocols'
import { tr } from '../../i18n'
import { can } from '../../access/policies'
import { useAuthStore } from '../../stores/auth'
import ConnectionGuidance from '../ConnectionGuidance.vue'

defineProps<{ canOpenCertificates: boolean }>()
const emit = defineEmits<{ diagnose: [device: Device]; certificates: [] }>()

const auth = useAuthStore()
const visible = ref(false)
const testing = ref(false)
const deviceId = ref<number | null>(null)
const device = ref<Device | undefined>()
const form = reactive({ area: 'HoldingRegister', address: 0, dataType: 'Float', dbNumber: 1, bitOffset: 0, nodeId: '' })
const result = ref<{ connected: boolean; value?: number; message?: string; quality?: string } | null>(null)
const isS7 = computed(() => device.value?.protocol === 'SiemensS7')
const text = computed(() => protocolDef(device.value?.protocol))
const isOpc = computed(() => device.value?.protocol === 'OpcUa')
const areas = computed(() => protocolAreas(device.value?.protocol))
const good = computed(() => result.value?.connected && (!result.value.quality || result.value.quality === 'Good'))
const canDiagnose = computed(() => !!device.value && can(auth.user, 'Configure', device.value.areaId))
const acquisition = ref<DeviceAcquisitionInfo | undefined>()
const targetText = computed(() => {
  const row = device.value
  if (!row) return ''
  const acq = acquisition.value
  const path = acq?.activePath === 'Standby' ? tr('devicesView.pathStandby') : tr('devicesView.pathPrimary')
  const rtu = ['ModbusRtu', 'ModbusAscii'].includes(row.protocol)
  const serial = acq?.activeSerialPort || row.serialPort
  const host = acq?.activeHost || row.host
  const port = acq?.activePort ?? row.port
  const address = row.protocol === 'OpcUa' ? (row.opcEndpointUrl || '') : rtu ? `${serial} · ${row.baudRate}` : `${host}:${port}`
  const gatewayId = acq?.activeGatewayId ?? row.activeGatewayId ?? row.gatewayId
  const place = gatewayId != null ? tr('devicesView.k38b4cc', [gatewayId]) : tr('devicesView.k3963d7')
  return tr('devicesView.testTarget', [path, address, place])
})
let version = 0
watch([deviceId, visible, () => JSON.stringify(form)], () => {
  version++; result.value = null; testing.value = false
}, { flush: 'sync' })

function diagnose(): void {
  if (device.value) emit('diagnose', device.value)
}

function open(row: Device, info?: DeviceAcquisitionInfo): void {
  device.value = row
  deviceId.value = row.id
  acquisition.value = info
  Object.assign(form, { area: protocolDef(row.protocol) ? 'Address' : row.protocol === 'OpcUa' ? 'OpcNode' : row.protocol === 'SiemensS7' ? 'DataBlock' : 'HoldingRegister', address: 0, dataType: 'Float', dbNumber: 1, bitOffset: 0, nodeId: '' })
  result.value = null
  visible.value = true
}

async function runTest(): Promise<void> {
  if (!deviceId.value) return
  const current = ++version
  testing.value = true
  try {
    const response = await deviceApi.test(deviceId.value, form.area, form.address, form.dataType, form.dbNumber, form.dataType === 'Bool' ? form.bitOffset : 0, form.nodeId, form.dataType === 'String' ? 32 : 0)
    if (current === version) result.value = response
  } catch (error) {
    if (current === version) result.value = { connected: false, message: errorMessage(error, tr('devicesView.k4184cd')) }
  } finally {
    if (current === version) testing.value = false
  }
}

onBeforeUnmount(() => { version++ })

defineExpose({ open })
</script>
