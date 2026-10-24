<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <strong>设备列表</strong>
        <span class="spacer" />
        <el-button @click="certificatesVisible = true">OPC UA 证书</el-button>
        <el-button type="primary" :icon="Plus" @click="openCreate">新增设备</el-button>
      </div>
      <el-table v-loading="loading" :data="devices" size="small" empty-text="暂无设备">
        <el-table-column prop="id" label="ID" width="60" />
        <el-table-column prop="name" label="名称" min-width="130" />
        <el-table-column prop="protocol" label="协议" width="110" />
        <el-table-column label="采集位置" width="110"><template #default="{ row }">{{ row.gatewayId === null ? '本地' : `网关 #${row.gatewayId}` }}</template></el-table-column>
        <el-table-column label="地址" min-width="150">
          <template #default="{ row }">{{ row.protocol === 'OpcUa' ? row.opcEndpointUrl : row.protocol === 'ModbusRtu' ? `${row.serialPort} · ${row.baudRate}` : `${row.host}:${row.port}` }}</template>
        </el-table-column>
        <el-table-column prop="stationNumber" label="站号" width="70" />
        <el-table-column prop="byteOrder" label="字节序" width="90" />
        <el-table-column prop="pollIntervalMs" label="采集周期" width="95">
          <template #default="{ row }">{{ row.pollIntervalMs }} ms</template>
        </el-table-column>
        <el-table-column prop="timeoutMs" label="超时" width="80">
          <template #default="{ row }">{{ row.timeoutMs }} ms</template>
        </el-table-column>
        <el-table-column label="启用" width="80">
          <template #default="{ row }">
            <el-tag :type="row.enabled ? 'success' : 'info'" size="small">{{ row.enabled ? '启用' : '停用' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="370" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openTest(row)">测试连接</el-button>
            <el-button link type="primary" @click="diagnosticDevice = row; diagnosticsVisible = true">采集诊断</el-button>
            <el-button v-if="row.protocol === 'OpcUa'" link type="primary" @click="openBrowse(row)">浏览变量</el-button>
            <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
            <el-popconfirm title="确认删除该设备及其测点？" @confirm="remove(row)">
              <template #reference><el-button link type="danger">删除</el-button></template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="dialogVisible" :title="editingId ? '编辑设备' : '新增设备'" width="600px" top="5vh" @closed="form.opcPassword = ''; form.clearOpcPassword = false">
      <el-form :model="form" label-width="110px" style="max-height: 68vh; overflow-y: auto; padding-right: 12px">
        <el-form-item label="设备名称" required>
          <el-input v-model="form.name" placeholder="如 1# 锅炉" />
        </el-form-item>
        <el-form-item label="协议">
          <el-select v-model="form.protocol" style="width: 100%" @change="protocolChanged">
            <el-option label="Modbus TCP" value="ModbusTcp" />
            <el-option label="Modbus RTU（串口）" value="ModbusRtu" />
            <el-option label="Siemens S7" value="SiemensS7" />
            <el-option label="OPC UA" value="OpcUa" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="!['ModbusRtu', 'OpcUa'].includes(form.protocol)" label="主机" required>
          <el-input v-model="form.host" placeholder="127.0.0.1" />
        </el-form-item>
        <el-form-item v-if="!['ModbusRtu', 'OpcUa'].includes(form.protocol)" label="端口" required>
          <el-input-number v-model="form.port" :min="1" :max="65535" style="width: 100%" />
        </el-form-item>
        <template v-if="form.protocol === 'ModbusRtu'">
          <el-form-item label="串口" required><el-input v-model="form.serialPort" placeholder="COM1 或 /dev/ttyUSB0" /></el-form-item>
          <el-form-item label="波特率"><el-input-number v-model="form.baudRate" :min="300" :max="921600" /></el-form-item>
          <el-form-item label="数据位"><el-select v-model="form.dataBits"><el-option :value="8" /></el-select></el-form-item>
          <el-form-item label="校验"><el-select v-model="form.parity"><el-option label="无" value="None" /><el-option label="奇" value="Odd" /><el-option label="偶" value="Even" /></el-select></el-form-item>
          <el-form-item label="停止位"><el-select v-model="form.stopBits"><el-option label="1" value="One" /><el-option label="2" value="Two" /></el-select></el-form-item>
        </template>
        <template v-if="form.protocol === 'SiemensS7'">
          <el-form-item label="CPU"><el-select v-model="form.cpu"><el-option v-for="cpu in ['S7200', 'S7300', 'S7400', 'S71200', 'S71500']" :key="cpu" :value="cpu" /></el-select></el-form-item>
          <el-form-item label="机架"><el-input-number v-model="form.rack" :min="0" :max="7" /></el-form-item>
          <el-form-item label="插槽"><el-input-number v-model="form.slot" :min="0" :max="31" /></el-form-item>
          <el-alert title="S7-1200/1500 使用绝对地址接入时，须启用 PUT/GET 并为对应 DB 关闭优化块访问；本驱动使用 PLC 固定字节序。" type="info" :closable="false" />
        </template>
        <template v-if="form.protocol === 'OpcUa'">
          <el-form-item label="端点 URL" required><el-input v-model="form.opcEndpointUrl" placeholder="opc.tcp://host:4840/path" /></el-form-item>
          <el-form-item label="安全模式"><el-select v-model="form.opcSecurityMode" @change="opcModeChanged"><el-option label="签名并加密" value="SignAndEncrypt" /><el-option label="签名" value="Sign" /><el-option label="无（None）" value="None" /></el-select></el-form-item>
          <el-form-item label="安全策略"><el-select v-model="form.opcSecurityPolicy"><el-option v-if="form.opcSecurityMode === 'None'" value="None" /><template v-else><el-option v-for="policy in ['Basic256Sha256', 'Aes128Sha256RsaOaep', 'Aes256Sha256RsaPss']" :key="policy" :value="policy" /></template></el-select></el-form-item>
          <el-form-item label="用户名"><el-input v-model="form.opcUserName" autocomplete="off" placeholder="留空使用匿名身份" /></el-form-item>
          <el-form-item label="密码"><el-input v-model="form.opcPassword" type="password" autocomplete="new-password" show-password :placeholder="storedPassword ? '已保存，留空保留原密码' : '服务器身份密码'" /></el-form-item>
          <el-form-item v-if="storedPassword" label="清除密码"><el-checkbox v-model="form.clearOpcPassword">保存时清除已保存密码</el-checkbox></el-form-item>
          <el-alert :title="form.opcSecurityMode === 'None' ? '此模式不提供报文签名或加密，仅支持匿名接入。' : '请通过 OPC UA 证书页面核对并信任服务器证书，服务器也需要信任客户端公钥。用户名密码认证须使用签名并加密。'" :type="form.opcSecurityMode === 'None' ? 'warning' : 'info'" :closable="false" />
        </template>
        <el-form-item v-if="['ModbusTcp', 'ModbusRtu'].includes(form.protocol)" label="站号">
          <el-input-number v-model="form.stationNumber" :min="form.protocol === 'ModbusRtu' ? 1 : 0" :max="form.protocol === 'ModbusRtu' ? 247 : 255" style="width: 100%" />
        </el-form-item>
        <el-form-item v-if="['ModbusTcp', 'ModbusRtu'].includes(form.protocol)" label="字节序">
          <el-radio-group v-model="form.byteOrder">
            <el-radio-button value="ABCD">ABCD</el-radio-button>
            <el-radio-button value="BADC">BADC</el-radio-button>
            <el-radio-button value="CDAB">CDAB</el-radio-button>
            <el-radio-button value="DCBA">DCBA</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="采集周期 (ms)">
          <el-input-number v-model="form.pollIntervalMs" :min="100" :max="600000" :step="100" style="width: 100%" />
        </el-form-item>
        <el-form-item label="超时 (ms)">
          <el-input-number v-model="form.timeoutMs" :min="100" :max="60000" :step="100" style="width: 100%" />
        </el-form-item>
        <el-form-item label="启用">
          <el-switch v-model="form.enabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="testVisible" title="测试连接 / 读取预览" width="480px">
      <el-form label-width="100px">
        <el-form-item label="区域">
          <el-select v-model="testForm.area" style="width: 100%">
            <el-option v-for="(label, value) in testAreas" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="!testIsOpc" label="地址">
          <el-input-number v-model="testForm.address" :min="0" :max="65535" style="width: 100%" />
        </el-form-item>
        <el-form-item v-if="testIsOpc" label="NodeId"><el-input v-model="testForm.nodeId" placeholder="nsu=urn:plant;s=Boiler.Temperature" /></el-form-item>
        <el-form-item label="数据类型">
          <el-select v-model="testForm.dataType" style="width: 100%">
            <el-option v-for="(label, value) in dataTypeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <template v-if="testIsS7">
          <el-form-item v-if="testForm.area === 'DataBlock'" label="DB 编号"><el-input-number v-model="testForm.dbNumber" :min="1" :max="65535" /></el-form-item>
          <el-form-item v-if="testForm.dataType === 'Bool'" label="位偏移"><el-input-number v-model="testForm.bitOffset" :min="0" :max="7" /></el-form-item>
          <el-text type="info">地址为从 0 开始的字节偏移；开关量另选位偏移。</el-text>
        </template>
      </el-form>
      <el-alert
        v-if="testResult"
        :type="testGood ? 'success' : 'error'"
        :closable="false"
        show-icon
        :title="testGood ? `连接成功，原始值：${formatValue(testResult.value)}` : testResult.message"
      />
      <template #footer>
        <el-button @click="testVisible = false">关闭</el-button>
        <el-button type="primary" :loading="testing" @click="runTest">测试读取</el-button>
      </template>
    </el-dialog>
    <OpcCertificatesDialog v-model="certificatesVisible" />
    <CollectorDiagnosticsDialog v-if="diagnosticDevice" v-model="diagnosticsVisible" :device="diagnosticDevice" />
    <OpcBrowserDialog v-if="browseDevice" v-model="browseVisible" :device="browseDevice" @imported="load" />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { Plus } from '@element-plus/icons-vue'
import { deviceApi } from '../api'
import { errorMessage } from '../api/http'
import type { Device, DeviceInput } from '../types'
import { dataTypeLabels, formatValue, protocolAreas } from '../utils'
import OpcCertificatesDialog from '../components/OpcCertificatesDialog.vue'
import OpcBrowserDialog from '../components/OpcBrowserDialog.vue'
import CollectorDiagnosticsDialog from '../components/CollectorDiagnosticsDialog.vue'

const devices = ref<Device[]>([])
const loading = ref(false)
const saving = ref(false)
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)
const storedPassword = ref(false), certificatesVisible = ref(false), browseVisible = ref(false), browseDevice = ref<Device | null>(null)
const diagnosticsVisible = ref(false), diagnosticDevice = ref<Device | null>(null)
function openBrowse(device: Device): void { browseDevice.value = device; browseVisible.value = true }

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
  serialPort: 'COM1', baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 'One', cpu: 'S71200', rack: 0, slot: 1,
  opcEndpointUrl: 'opc.tcp://localhost:4840', opcSecurityMode: 'SignAndEncrypt', opcSecurityPolicy: 'Basic256Sha256', opcUserName: '', opcPassword: '', clearOpcPassword: false
})
const form = reactive<DeviceInput>(emptyForm())

const testVisible = ref(false)
const testing = ref(false)
const testDeviceId = ref<number | null>(null)
const testForm = reactive({ area: 'HoldingRegister', address: 0, dataType: 'Float', dbNumber: 1, bitOffset: 0, nodeId: '' })
const testIsS7 = computed(() => devices.value.find(x => x.id === testDeviceId.value)?.protocol === 'SiemensS7')
const testIsOpc = computed(() => devices.value.find(x => x.id === testDeviceId.value)?.protocol === 'OpcUa')
const testAreas = computed(() => protocolAreas(devices.value.find(x => x.id === testDeviceId.value)?.protocol))
function protocolChanged(): void { form.port = form.protocol === 'SiemensS7' ? 102 : 502 }
function opcModeChanged(): void { form.opcSecurityPolicy = form.opcSecurityMode === 'None' ? 'None' : 'Basic256Sha256' }
const testResult = ref<{ connected: boolean; value?: number; message?: string; quality?: string } | null>(null)
const testGood = computed(() => testResult.value?.connected && (!testResult.value.quality || testResult.value.quality === 'Good'))

async function load(): Promise<void> {
  loading.value = true
  try {
    devices.value = await deviceApi.list()
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载设备失败'))
  } finally {
    loading.value = false
  }
}

function openCreate(): void {
  editingId.value = null
  storedPassword.value = false
  Object.assign(form, emptyForm())
  dialogVisible.value = true
}

function openEdit(device: Device): void {
  editingId.value = device.id
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
    serialPort: device.serialPort, baudRate: device.baudRate, dataBits: device.dataBits, parity: device.parity,
    stopBits: device.stopBits, cpu: device.cpu, rack: device.rack, slot: device.slot,
    opcEndpointUrl: device.opcEndpointUrl, opcSecurityMode: device.opcSecurityMode, opcSecurityPolicy: device.opcSecurityPolicy,
    opcUserName: device.opcUserName, opcPassword: '', clearOpcPassword: false
  })
  dialogVisible.value = true
}

async function save(): Promise<void> {
  if (!form.name.trim() || (form.protocol === 'OpcUa' ? !form.opcEndpointUrl.trim() : form.protocol === 'ModbusRtu' ? !form.serialPort.trim() : !form.host.trim())) {
    ElMessage.warning('请填写设备名称和连接地址')
    return
  }

  saving.value = true
  try {
    const input = { ...form, opcPassword: form.opcPassword?.length ? form.opcPassword : null }
    if (editingId.value) await deviceApi.update(editingId.value, input)
    else await deviceApi.create(input)
    ElMessage.success('保存成功')
    dialogVisible.value = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '保存失败'))
  } finally {
    saving.value = false
  }
}

async function remove(device: Device): Promise<void> {
  try {
    await deviceApi.remove(device.id)
    ElMessage.success('已删除')
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '删除失败'))
  }
}

function openTest(device: Device): void {
  testDeviceId.value = device.id
  Object.assign(testForm, { area: device.protocol === 'OpcUa' ? 'OpcNode' : device.protocol === 'SiemensS7' ? 'DataBlock' : 'HoldingRegister', address: 0, dataType: 'Float', dbNumber: 1, bitOffset: 0, nodeId: '' })
  testResult.value = null
  testVisible.value = true
}

async function runTest(): Promise<void> {
  if (!testDeviceId.value) return
  testing.value = true
  try {
    testResult.value = await deviceApi.test(testDeviceId.value, testForm.area, testForm.address, testForm.dataType, testForm.dbNumber, testForm.dataType === 'Bool' ? testForm.bitOffset : 0, testForm.nodeId)
  } catch (error) {
    testResult.value = { connected: false, message: errorMessage(error, '测试失败') }
  } finally {
    testing.value = false
  }
}

onMounted(load)
</script>
