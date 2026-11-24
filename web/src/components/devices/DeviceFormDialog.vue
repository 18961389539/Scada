<template>
  <el-dialog v-model="visible" :title="editingId ? $t('devicesView.ke1db1a') : (copying ? $t('devicesView.k9d4ba0') : $t('devicesView.k56ac09'))" width="600px" top="5vh" data-test="device-dialog" :before-close="editDialog.beforeClose" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving" @keydown="editDialog.onKey" @opened="onDialogOpened" @closed="form.opcPassword = ''; form.clearOpcPassword = false">
    <el-alert v-if="!editingId && creatingForGateway" :title="$t('devicesView.kace170', [creatingForGateway])" type="info" :closable="false" />
    <el-alert
      v-if="!editingId"
      type="info"
      :closable="false"
      show-icon
      data-test="device-defaults-tip"
      :title="$t('devicesView.k35f2b4')"
      style="margin-bottom: 12px"
    />
    <el-alert v-if="fieldErrors._form" :title="fieldErrors._form" type="error" :closable="false" show-icon tabindex="-1" style="margin-bottom: 12px" data-test="device-form-error" />
    <el-form ref="formRef" :model="form" :rules="validationRules" :disabled="saving" label-width="110px" status-icon style="max-height: 68vh; overflow-y: auto; padding-right: 12px" @submit.prevent="save">
      <h4>{{ $t('configurationUx.basic') }}</h4>
      <el-form-item :label="$t('devicesView.k4433eb')" prop="name" :error="fieldErrors.name">
        <el-input v-model="form.name" data-test="device-name" :placeholder="$t('devicesView.kc55ec1')" maxlength="100" show-word-limit />
      </el-form-item>
      <el-form-item :label="$t('devicesView.kac1459')">
        <el-select v-model="form.areaId" data-test="device-area" style="width: 100%">
          <el-option v-for="a in areas" :key="a.id" :label="a.name" :value="a.id" />
        </el-select>
      </el-form-item>
      <el-form-item :label="$t('devicesView.kb0c431')">
        <el-select v-model="form.protocol" style="width: 100%" @change="protocolChanged">
          <el-option label="Modbus TCP" value="ModbusTcp" />
          <el-option :label="$t('devicesView.k6d140f')" value="ModbusRtu" />
          <el-option label="Modbus ASCII" value="ModbusAscii" />
          <el-option label="Modbus RTU over TCP" value="ModbusRtuOverTcp" />
          <el-option label="Siemens S7" value="SiemensS7" />
          <el-option label="OPC UA" value="OpcUa" />
          <el-option v-for="p in textProtocols" :key="p.value" :label="p.label" :value="p.value" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="channelChoices.length || channelBound" :label="$t('devicesView.channel')">
        <el-select v-model="form.channelId" clearable style="width: 100%" data-test="device-channel" :placeholder="$t('devicesView.channelNone')" @change="channelChanged">
          <el-option :label="$t('devicesView.channelNone')" :value="null" />
          <el-option v-for="c in channelChoices" :key="c.id" :label="`#${c.id} · ${c.name}`" :value="c.id" />
        </el-select>
        <div class="tiny muted">{{ $t('devicesView.channelHint') }}</div>
      </el-form-item>
      <el-form-item v-if="showHostPort" :label="$t('devicesView.k2e8a0c')" prop="host" :error="fieldErrors.host">
        <el-input v-model="form.host" data-test="device-host" :disabled="channelBound" placeholder="127.0.0.1" />
      </el-form-item>
      <el-form-item v-if="showHostPort" :label="$t('devicesView.k6cbb73')" prop="port" :error="fieldErrors.port">
        <el-input-number v-model="form.port" data-test="device-port" :disabled="channelBound" :min="1" :max="65535" style="width: 100%" />
      </el-form-item>
      <template v-if="showSerial">
        <el-form-item :label="$t('devicesView.kedd09e')" required><el-input v-model="form.serialPort" :disabled="channelBound" :placeholder="$t('devicesView.kdd7399')" /></el-form-item>
        <el-form-item :label="$t('devicesView.k917c91')"><el-input-number v-model="form.baudRate" :disabled="channelBound" :min="300" :max="921600" /></el-form-item>
        <el-form-item :label="$t('devicesView.k39a52f')"><el-select v-model="form.dataBits" :disabled="channelBound"><el-option :value="8" /></el-select></el-form-item>
        <el-form-item :label="$t('devicesView.k20feec')"><el-select v-model="form.parity" :disabled="channelBound"><el-option :label="$t('devicesView.k720777')" value="None" /><el-option :label="$t('devicesView.kc23797')" value="Odd" /><el-option :label="$t('devicesView.k4421c7')" value="Even" /></el-select></el-form-item>
        <el-form-item :label="$t('devicesView.ka04650')"><el-select v-model="form.stopBits" :disabled="channelBound"><el-option label="1" value="One" /><el-option label="2" value="Two" /></el-select></el-form-item>
      </template>
      <template v-if="form.protocol === 'SiemensS7'">
        <el-form-item label="CPU"><el-select v-model="form.cpu"><el-option v-for="cpu in ['S7200', 'S7300', 'S7400', 'S71200', 'S71500']" :key="cpu" :value="cpu" /></el-select></el-form-item>
        <el-form-item :label="$t('devicesView.kc40935')"><el-input-number v-model="form.rack" :min="0" :max="7" /></el-form-item>
        <el-form-item :label="$t('devicesView.k911ac3')"><el-input-number v-model="form.slot" :min="0" :max="31" /></el-form-item>
        <el-alert :title="$t('devicesView.k5b3f14')" type="info" :closable="false" />
      </template>
      <template v-if="form.protocol === 'OpcUa'">
        <el-form-item :label="$t('devicesView.k285482')" prop="opcEndpointUrl"><el-input v-model="form.opcEndpointUrl" data-test="device-opc-url" placeholder="opc.tcp://host:4840/path" /></el-form-item>
        <el-form-item :label="$t('devicesView.k38ba2b')"><el-select v-model="form.opcSecurityMode" @change="opcModeChanged"><el-option :label="$t('devicesView.kc870dd')" value="SignAndEncrypt" /><el-option :label="$t('devicesView.k8ba46c')" value="Sign" /><el-option :label="$t('devicesView.k6e017f')" value="None" /></el-select></el-form-item>
        <el-form-item :label="$t('devicesView.k7fb276')"><el-select v-model="form.opcSecurityPolicy"><el-option v-if="form.opcSecurityMode === 'None'" value="None" /><template v-else><el-option v-for="policy in ['Basic256Sha256', 'Aes128Sha256RsaOaep', 'Aes256Sha256RsaPss']" :key="policy" :value="policy" /></template></el-select></el-form-item>
        <el-form-item :label="$t('devicesView.ka1aaf3')"><el-input v-model="form.opcUserName" autocomplete="off" :placeholder="$t('devicesView.k4d54b9')" /></el-form-item>
        <el-form-item :label="$t('devicesView.kc839a8')"><el-input v-model="form.opcPassword" type="password" autocomplete="new-password" show-password :placeholder="storedPassword ? $t('devicesView.k050138') : $t('devicesView.ke22306')" /></el-form-item>
        <el-form-item v-if="storedPassword" :label="$t('devicesView.kbcf73a')"><el-checkbox v-model="form.clearOpcPassword">{{ $t('devicesView.k1ee6d7') }}</el-checkbox></el-form-item>
        <el-alert :title="form.opcSecurityMode === 'None' ? $t('devicesView.k334ebb') : $t('devicesView.kd9f837')" :type="form.opcSecurityMode === 'None' ? 'warning' : 'info'" :closable="false" />
      </template>
      <template v-if="currentDef">
        <el-alert v-if="currentDef.notice" :title="currentDef.notice" type="warning" :closable="false" />
        <el-form-item v-for="f in currentDef.fields" :key="f.key" :label="f.label" :required="f.type === 'number' && typeof f.default === 'number' && f.min != null && f.default < f.min">
          <el-select v-if="f.type === 'select'" v-model="options[f.key]" :data-test="`opt-${f.key}`" style="width: 100%"><el-option v-for="o in f.options" :key="o" :value="o" /></el-select>
          <el-switch v-else-if="f.type === 'bool'" v-model="options[f.key]" :disabled="f.locked" :data-test="`opt-${f.key}`" />
          <el-input-number v-else-if="f.type === 'number'" v-model="options[f.key]" :data-test="`opt-${f.key}`" :min="f.min" :max="f.max" style="width: 100%" />
          <el-input v-else v-model="options[f.key]" :data-test="`opt-${f.key}`" :type="f.type === 'textarea' ? 'textarea' : 'text'" :placeholder="f.placeholder" />
          <el-text v-if="f.help" type="info" size="small">{{ f.help }}</el-text>
        </el-form-item>
        <template v-if="currentDef.credentials">
          <el-form-item :label="$t('devicesView.ka1aaf3')"><el-input v-model="form.opcUserName" autocomplete="off" :placeholder="$t('devicesView.kb297e0')" /></el-form-item>
          <el-form-item :label="$t('devicesView.kc839a8')"><el-input v-model="form.opcPassword" type="password" autocomplete="new-password" show-password :placeholder="storedPassword ? $t('devicesView.k050138') : $t('devicesView.kc839a8')" /></el-form-item>
          <el-form-item v-if="storedPassword" :label="$t('devicesView.kbcf73a')"><el-checkbox v-model="form.clearOpcPassword">{{ $t('devicesView.k1ee6d7') }}</el-checkbox></el-form-item>
        </template>
      </template>
      <el-form-item v-if="showStation" :label="$t('devicesView.k60aa5c')">
        <el-input-number v-model="form.stationNumber" :min="stationMin" :max="stationMax" style="width: 100%" />
      </el-form-item>
      <el-form-item :label="$t('devicesView.kd4e9ca')">
        <el-switch v-model="form.enabled" :disabled="form.protocol === 'Iec61850'" data-test="device-enabled" />
        <p v-if="form.protocol === 'Iec61850'" class="tiny" data-test="device-enable-locked">{{ currentDef?.notice }}</p>
      </el-form-item>
      <el-form-item :label="$t('simulation.switchLabel')">
        <el-switch v-model="form.simulationEnabled" :disabled="simulationLocked" data-test="device-simulation" />
        <p class="tiny muted">{{ $t('simulation.switchHint') }}</p>
      </el-form-item>
      <template v-if="form.simulationEnabled">
        <el-alert :title="$t('simulation.warning')" type="warning" :closable="false" show-icon style="margin-bottom: 12px" />
        <el-form-item :label="$t('simulation.waveform')">
          <el-select v-model="simOptions.waveform" style="width: 100%" data-test="device-simulation-waveform">
            <el-option v-for="wave in simulationWaveforms" :key="wave.value" :label="$t(wave.key)" :value="wave.value" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('simulation.period')"><el-input-number v-model="simOptions.periodSec" :min="1" :max="86400" style="width: 100%" /></el-form-item>
        <el-form-item :label="$t('simulation.min')"><el-input-number v-model="simOptions.min" :controls="false" style="width: 100%" /></el-form-item>
        <el-form-item :label="$t('simulation.max')"><el-input-number v-model="simOptions.max" :controls="false" style="width: 100%" /></el-form-item>
        <el-form-item :label="$t('simulation.noise')"><el-input-number v-model="simOptions.noisePercent" :min="0" :max="50" style="width: 100%" /></el-form-item>
      </template>
      <details :open="advancedOpen" data-test="device-advanced" @toggle="advancedOpen = ($event.target as HTMLDetailsElement).open">
        <summary>{{ $t('configurationUx.advanced') }}</summary>
        <p class="tiny muted">{{ $t('configurationUx.advancedHint') }}</p>
      <el-form-item v-if="showStation" :label="$t('devicesView.ka15ac8')">
        <el-radio-group v-model="form.byteOrder">
          <el-radio-button value="ABCD">ABCD</el-radio-button>
          <el-radio-button value="BADC">BADC</el-radio-button>
          <el-radio-button value="CDAB">CDAB</el-radio-button>
          <el-radio-button value="DCBA">DCBA</el-radio-button>
        </el-radio-group>
      </el-form-item>
      <el-form-item :label="$t('devicesView.k750bbf')">
        <el-input-number v-model="form.pollIntervalMs" data-test="device-poll" :min="100" :max="600000" :step="100" style="width: 100%" />
      </el-form-item>
      <el-form-item :label="$t('devicesView.ke4bc6d')">
        <el-input-number v-model="form.timeoutMs" data-test="device-timeout" :min="100" :max="60000" :step="100" style="width: 100%" />
      </el-form-item>
      </details>
    </el-form>
    <el-alert v-if="draftTestResult" :type="draftTestGood ? 'success' : 'error'" :closable="false" show-icon :title="draftTestTitle" data-test="device-draft-test-result" />
    <ConnectionGuidance v-if="draftTestResult && !draftTestGood" :message="draftTestResult.message" :quality="draftTestResult.quality" :protocol="form.protocol" />
    <template #footer>
      <span class="edit-dialog-shortcut">{{ $t('editDialog.shortcut', [editDialog.modKey]) }}</span>
      <el-button v-if="canTestDraft" :loading="draftTesting" data-test="device-test-draft" @click="testDraft">{{ $t('devicesView.k10b7d8') }}</el-button>
      <el-button :disabled="saving" @click="editDialog.close">{{ $t('devicesView.k4d0b46') }}</el-button>
      <el-button type="primary" data-test="device-save" :loading="saving" :disabled="!canSave" @click="save">{{ $t('devicesView.kfadf24') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { deviceApi } from '../../api'
import { channelUsesSerial, type ChannelInfo } from '../../api/channels'
import { useAuthStore } from '../../stores/auth'
import { errorMessage } from '../../api/http'
import type { Area, Device, DeviceInput } from '../../types'
import { formatValue } from '../../utils'
import { can } from '../../access/policies'
import { defaultOptions, parseOptions, protocolDef, textProtocols, protocolOptionsComplete } from '../../protocols'
import { tr } from '../../i18n'
import { readLastNumber, rememberNumber } from '../../composables/lastChoice'
import { applyFieldErrors, clearFieldErrors, serverFormErrors, type FieldErrorMap } from '../../composables/formErrors'
import { focusFormError, useEditDialog } from '../../composables/useEditDialog'
import ConnectionGuidance from '../ConnectionGuidance.vue'

const props = defineProps<{ areas: Area[]; channels: ChannelInfo[] }>()
const emit = defineEmits<{ saved: [device: Device, gatewayId: number | null] }>()
const auth = useAuthStore()

const visible = ref(false)
const advancedOpen = ref(false)
const saving = ref(false)
watch(visible, shown => { if (shown) advancedOpen.value = false })
const editingRevision = ref<number | undefined>()
const editingId = ref<number | null>(null)
const copying = ref(false)
const formRef = ref<FormInstance>()
const fieldErrors = reactive<FieldErrorMap>({})

const validationRules = computed<FormRules>(() => ({
  name: [
    { required: true, message: tr('devicesView.kac75fb'), trigger: 'blur' },
    { min: 1, max: 100, message: tr('devicesView.k616c06'), trigger: 'blur' }
  ],
  host: [{
    validator: (_r, v, cb) => {
      if (showHostPort.value && !form.simulationEnabled && !String(v || '').trim()) cb(new Error(tr('devicesView.kb695a3')))
      else cb()
    }, trigger: 'blur'
  }],
  port: [{
    validator: (_r, v, cb) => {
      if (showHostPort.value && !form.simulationEnabled && !(v >= 1 && v <= 65535)) cb(new Error(tr('devicesView.ke6b89c')))
      else cb()
    }, trigger: 'change'
  }],
  serialPort: [{
    validator: (_r, v, cb) => {
      if (['ModbusRtu', 'ModbusAscii'].includes(form.protocol) && !form.simulationEnabled && !String(v || '').trim()) cb(new Error(tr('devicesView.keaabc9')))
      else cb()
    }, trigger: 'blur'
  }],
  opcEndpointUrl: [{
    validator: (_r, v, cb) => {
      if (form.protocol === 'OpcUa' && !form.simulationEnabled && !String(v || '').trim()) cb(new Error(tr('devicesView.k41d3d1')))
      else cb()
    }, trigger: 'blur'
  }]
}))

const canSave = computed(() => {
  if (textProtocols.value.length === 0 || !protocolOptionsComplete(protocolDef(form.protocol), options)) return false
  if (!String(form.name || '').trim()) return false
  if (form.simulationEnabled) return true
  if (form.protocol === 'OpcUa') return !!String(form.opcEndpointUrl || '').trim()
  if (showSerial.value) return !!String(form.serialPort || '').trim()
  if (showHostPort.value) return !!String(form.host || '').trim() && form.port >= 1
  return true
})
const storedPassword = ref(false)

// 未保存设备试连：用当前表单值直接连接，不要求先保存；网关设备仍须保存后由网关测试。
const draftTesting = ref(false)
const draftTestResult = ref<{ connected: boolean; value?: number; message?: string; quality?: string } | null>(null)
const draftTestGood = computed(() => draftTestResult.value?.connected && (!draftTestResult.value.quality || draftTestResult.value.quality === 'Good'))
const editingGatewayId = ref<number | null>(null)
const editingGatewayDevice = computed(() => editingGatewayId.value != null)
const creatingForGateway = ref<number | null>(null)
const simulationLocked = computed(() => creatingForGateway.value !== null || (editingGatewayDevice.value && !form.simulationEnabled))
const canTestDraft = computed(() => can(auth.user, 'Control', form.areaId) && canSave.value && !editingGatewayDevice.value)
const draftTestTitle = computed(() => {
  const result = draftTestResult.value
  if (!result) return ''
  if (!draftTestGood.value) return result.message || tr('configurationUx.testUnknown')
  return result.value != null ? tr('devicesView.kb834a8', [formatValue(result.value)]) : tr('devicesView.keb0663')
})
let draftTestVersion = 0

const emptyForm = (): DeviceInput => ({
  name: '',
  protocol: 'ModbusTcp',
  host: '127.0.0.1',
  port: 502,
  stationNumber: 1,
  byteOrder: 'ABCD',
  pollIntervalMs: 1000,
  timeoutMs: 3000,
  enabled: true,
  areaId: 1,
  channelId: null,
  serialPort: 'COM1', baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 'One', cpu: 'S71200', rack: 0, slot: 1,
  opcEndpointUrl: 'opc.tcp://localhost:4840', opcSecurityMode: 'SignAndEncrypt', opcSecurityPolicy: 'Basic256Sha256', opcUserName: '', opcPassword: '', clearOpcPassword: false,
  simulationEnabled: false,
  simulationOptions: ''
})
const form = reactive<DeviceInput>(emptyForm())
// 可选通道按协议过滤：一条通道只承载一种帧格式，跨协议绑定没有意义。
const channelChoices = computed(() => props.channels.filter(c => c.enabled && c.protocol === form.protocol && (!softElementProtocol(form.protocol) || options.transport === 'Rtu')))
const channelBound = computed(() => form.channelId != null)
const options = reactive<Record<string, string | number | boolean>>({})
const defaultSimulation = () => ({ waveform: 'Sine', periodSec: 60, min: null as number | null, max: null as number | null, noisePercent: 0, seed: 0 })
const simOptions = reactive(defaultSimulation())
const simulationWaveforms = [
  { value: 'Sine', key: 'simulation.sine' },
  { value: 'Square', key: 'simulation.square' },
  { value: 'Triangle', key: 'simulation.triangle' },
  { value: 'Counter', key: 'simulation.counter' },
  { value: 'Random', key: 'simulation.random' },
  { value: 'Constant', key: 'simulation.constant' }
]
function setSimulation(json?: string): void {
  Object.assign(simOptions, defaultSimulation())
  if (!json) return
  try { Object.assign(simOptions, JSON.parse(json)) } catch { /* 服务端保存前会校验 JSON，这里仅做表单回填 */ }
}
// 表单或协议选项变化后，之前的试连结果不再对应当前输入。
watch([() => JSON.stringify(form), () => JSON.stringify(options), visible], () => {
  draftTestVersion++; draftTestResult.value = null; draftTesting.value = false
}, { flush: 'sync' })
const editDialog = useEditDialog({ visible, saving, form: formRef, snapshot: () => JSON.stringify({ form, options }), initialField: 'name', save })
const currentDef = computed(() => protocolDef(form.protocol))
function softElementProtocol(protocol: string): boolean {
  return protocol === 'Inovance' || protocol === 'Xinje' || protocol === 'Delta'
}
const softElementRtu = computed(() => softElementProtocol(form.protocol) && options.transport === 'Rtu')
const showHostPort = computed(() => !softElementRtu.value && (currentDef.value ? currentDef.value.hostPort : !['ModbusRtu', 'OpcUa'].includes(form.protocol)))
const showSerial = computed(() => ['ModbusRtu', 'ModbusAscii'].includes(form.protocol) || softElementRtu.value)
const showStation = computed(() => ['ModbusTcp', 'ModbusRtu', 'ModbusAscii', 'ModbusRtuOverTcp', 'Inovance', 'Xinje', 'Delta'].includes(form.protocol))
const stationMin = computed(() => showSerial.value ? 1 : 0)
const stationMax = computed(() => showSerial.value ? 247 : 255)
function setOptions(values: Record<string, string | number | boolean>): void { for (const k of Object.keys(options)) delete options[k]; Object.assign(options, values) }

function protocolChanged(): void {
  const def = protocolDef(form.protocol)
  form.port = def ? def.defaultPort : form.protocol === 'SiemensS7' ? 102 : 502
  setOptions(def ? defaultOptions(def) : {})
  if (form.protocol === 'Inovance' || form.protocol === 'Xinje' || form.protocol === 'Delta') form.byteOrder = 'CDAB'
  if (form.protocol === 'Iec61850') { form.enabled = false; options.useReports = false }
  // 换协议后原来的通道不再匹配，应当解绑而不是留一个必然被服务端拒绝的组合。
  if (form.channelId != null && !channelChoices.value.some(c => c.id === form.channelId)) form.channelId = null
}
watch(() => options.transport, (transport) => {
  if (transport === 'Tcp' && form.channelId != null) form.channelId = null
})
// 选中通道后把端点回填到表单，仅用于展示；真正的下发在服务端保存时完成。
function channelChanged(value: number | null): void {
  if (value == null) return
  const channel = props.channels.find(c => c.id === value)
  if (!channel) return
  if (channelUsesSerial(channel.protocol)) {
    form.serialPort = channel.serialPort; form.baudRate = channel.baudRate
    form.dataBits = channel.dataBits; form.parity = channel.parity; form.stopBits = channel.stopBits
  } else {
    form.host = channel.host; form.port = channel.port
  }
  form.timeoutMs = channel.timeoutMs
}
function opcModeChanged(): void { form.opcSecurityPolicy = form.opcSecurityMode === 'None' ? 'None' : 'Basic256Sha256' }

function openCreate(gatewayId: number | null): void {
  const areaId = props.areas[0]?.id
  if (!areaId) return
  creatingForGateway.value = gatewayId
  editingId.value = null
  editingGatewayId.value = null
  copying.value = false
  clearFieldErrors(fieldErrors)
  storedPassword.value = false
  Object.assign(form, emptyForm())
  setOptions({})
  setSimulation()
  const rememberedArea = readLastNumber('device-area')
  form.areaId = rememberedArea != null && props.areas.some(area => area.id === rememberedArea) ? rememberedArea : areaId
  if (creatingForGateway.value !== null) form.enabled = false
  visible.value = true
}

function openEdit(device: Device): void {
  creatingForGateway.value = null
  editingId.value = device.id
  editingRevision.value = device.revision
  editingGatewayId.value = device.gatewayId ?? null
  copying.value = false
  clearFieldErrors(fieldErrors)
  storedPassword.value = device.hasOpcPassword
  Object.assign(form, {
    name: device.name,
    protocol: device.protocol,
    host: device.host,
    port: device.port,
    stationNumber: device.stationNumber,
    byteOrder: device.byteOrder,
    pollIntervalMs: device.pollIntervalMs,
    timeoutMs: device.timeoutMs,
    enabled: device.enabled,
    areaId: device.areaId ?? 1,
    channelId: device.channelId ?? null,
    serialPort: device.serialPort, baudRate: device.baudRate, dataBits: device.dataBits, parity: device.parity,
    stopBits: device.stopBits, cpu: device.cpu, rack: device.rack, slot: device.slot,
    opcEndpointUrl: device.opcEndpointUrl, opcSecurityMode: device.opcSecurityMode, opcSecurityPolicy: device.opcSecurityPolicy,
    opcUserName: device.opcUserName, opcPassword: '', clearOpcPassword: false,
    simulationEnabled: device.simulationEnabled ?? false, simulationOptions: device.simulationOptions ?? ''
  })
  setOptions(parseOptions(protocolDef(device.protocol), device.protocolOptions))
  setSimulation(device.simulationOptions)
  if (form.protocol === 'Iec61850') { form.enabled = false; options.useReports = false }
  visible.value = true
}

function fillFromDevice(device: Device, nameOverride?: string): void {
  Object.assign(form, {
    name: nameOverride ?? device.name,
    protocol: device.protocol,
    host: device.host,
    port: device.port,
    stationNumber: device.stationNumber,
    byteOrder: device.byteOrder,
    pollIntervalMs: device.pollIntervalMs,
    timeoutMs: device.timeoutMs,
    enabled: device.enabled,
    areaId: device.areaId ?? 1,
    channelId: device.channelId ?? null,
    serialPort: device.serialPort, baudRate: device.baudRate, dataBits: device.dataBits, parity: device.parity,
    stopBits: device.stopBits, cpu: device.cpu, rack: device.rack, slot: device.slot,
    opcEndpointUrl: device.opcEndpointUrl, opcSecurityMode: device.opcSecurityMode, opcSecurityPolicy: device.opcSecurityPolicy,
    opcUserName: device.opcUserName, opcPassword: '', clearOpcPassword: false,
    simulationEnabled: device.simulationEnabled ?? false, simulationOptions: device.simulationOptions ?? ''
  })
  setOptions(parseOptions(protocolDef(device.protocol), device.protocolOptions))
  setSimulation(device.simulationOptions)
  if (form.protocol === 'Iec61850') { form.enabled = false; options.useReports = false }
}

function openCopy(device: Device): void {
  creatingForGateway.value = null
  editingId.value = null
  editingGatewayId.value = null
  copying.value = true
  clearFieldErrors(fieldErrors)
  storedPassword.value = false
  fillFromDevice(device, tr('devicesView.kc0769b', [device.name]))
  form.enabled = false
  visible.value = true
}

function open(device?: Device | null, mode: { copy?: boolean; gatewayId?: number | null } = {}): void {
  if (!device) { openCreate(mode.gatewayId ?? null); return }
  if (mode.copy) openCopy(device)
  else openEdit(device)
}

async function onDialogOpened(): Promise<void> {
  await nextTick()
  formRef.value?.clearValidate()
  await editDialog.focusInitial()
}

function devicePayload(): DeviceInput {
  const iec61850 = form.protocol === 'Iec61850'
  return {
    ...form,
    enabled: iec61850 ? false : form.enabled,
    opcPassword: form.opcPassword?.length ? form.opcPassword : null,
    protocolOptions: currentDef.value ? JSON.stringify(iec61850 ? { ...options, useReports: false } : options) : '',
    simulationOptions: form.simulationEnabled ? JSON.stringify(simOptions) : '',
    revision: editingRevision.value
  }
}

async function save(): Promise<void> {
  if (!visible.value || saving.value) return
  saving.value = true
  let focusError = false
  clearFieldErrors(fieldErrors)
  try {
    const valid = await formRef.value?.validate().catch(() => false)
    if (!valid) {
      fieldErrors._form = tr('devicesView.k02668e'); focusError = true; return
    }
    if (!canSave.value) {
      fieldErrors._form = tr('devicesView.k9124df'); focusError = true; return
    }
    if (!editingId.value && !props.areas.some(area => area.id === form.areaId)) {
      fieldErrors._form = tr('devicesView.areaRequired'); focusError = true; return
    }
    const input = devicePayload()
    const saved = editingId.value
      ? await deviceApi.update(editingId.value, input)
      : await deviceApi.create(input)
    emit('saved', saved, creatingForGateway.value)
    rememberNumber('device-area', form.areaId)
    ElMessage.success(tr('devicesView.k7e68eb'))
    visible.value = false
  } catch (error) {
    const mapped = serverFormErrors(error, tr('devicesView.k40525a'), ['name', 'host', 'port', 'opcEndpointUrl', 'serialPort'])
    applyFieldErrors(fieldErrors, mapped.fields)
    fieldErrors._form = mapped.message
    focusError = true
  } finally {
    saving.value = false
    if (focusError) { advancedOpen.value = true; await nextTick(); await focusFormError(formRef.value) }
  }
}

async function testDraft(): Promise<void> {
  if (draftTesting.value || saving.value || !visible.value) return
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid || !canSave.value) return
  const version = ++draftTestVersion
  draftTesting.value = true
  draftTestResult.value = null
  try {
    const result = await deviceApi.testDraft(devicePayload(), editingId.value)
    if (version === draftTestVersion) draftTestResult.value = result
  } catch (error) {
    if (version === draftTestVersion) draftTestResult.value = { connected: false, message: errorMessage(error, tr('devicesView.k3c97f5')) }
  } finally {
    if (version === draftTestVersion) draftTesting.value = false
  }
}

onBeforeUnmount(() => { draftTestVersion++ })

defineExpose({ open })
</script>
