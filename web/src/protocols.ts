import { tr } from './i18n'
// 文本地址协议（TagArea.Address）的前端定义：设备参数表单、默认端口与地址语法提示。
// 后端校验以 Scada.Core.Protocols 中各协议的 IProtocolSpec 为准，此处仅用于渲染表单与提示。
export interface ProtocolField {
  key: string
  label: string
  type: 'text' | 'number' | 'select' | 'bool' | 'textarea'
  default: string | number | boolean
  options?: string[]
  min?: number
  max?: number
  placeholder?: string
  help?: string
}

export interface ProtocolDef {
  value: string
  label: string
  defaultPort: number
  hostPort: boolean
  credentials: boolean
  fields: ProtocolField[]
  addressPlaceholder: string
  addressHelp: string
  notice?: string
}

export const textProtocols: ProtocolDef[] = [
  {
    value: 'Mqtt', label: tr('protocols.k85cc9d'), defaultPort: 1883, hostPort: true, credentials: true,
    fields: [
    { key: 'clientId', label: tr('protocols.k920416'), type: 'text', default: '', placeholder: tr('protocols.k01f463') },
    { key: 'version', label: tr('protocols.k5793c2'), type: 'select', default: 'V500', options: ['V500', 'V311'] },
    { key: 'useTls', label: 'TLS', type: 'bool', default: false },
    { key: 'allowUntrustedCertificate', label: tr('protocols.k91ae87'), type: 'bool', default: false, help: tr('protocols.ka4a388') },
    { key: 'allowPlaintextPassword', label: tr('protocols.k659c62'), type: 'bool', default: false, help: tr('protocols.kcf8baf') },
    { key: 'keepAliveSec', label: tr('protocols.kf01fbd'), type: 'number', default: 30, min: 5, max: 3600 },
      { key: 'qos', label: 'QoS', type: 'number', default: 1, min: 0, max: 2 },
      { key: 'cleanSession', label: tr('protocols.kefcec3'), type: 'bool', default: true },
      { key: 'retainWrites', label: tr('protocols.kb64e3b'), type: 'bool', default: false },
      { key: 'confirmWrites', label: tr('protocols.kfbdf65'), type: 'bool', default: true, help: tr('protocols.k16f1af') },
      { key: 'staleAfterMs', label: tr('protocols.k6e99fa'), type: 'number', default: 0, min: 0, max: 86400000, help: tr('protocols.kade0e0') }
    ],
    addressPlaceholder: 'plant/boiler|$.temp;w=plant/boiler/set;wpath=$.value;ts=$.ts',
    addressHelp: tr('protocols.kc378f3')
  },
  {
    value: 'SparkplugB', label: 'MQTT Sparkplug B', defaultPort: 1883, hostPort: true, credentials: true,
    fields: [
      { key: 'groupId', label: 'Group ID', type: 'text', default: '' },
      { key: 'edgeNodeId', label: 'Edge Node ID', type: 'text', default: '' },
      { key: 'deviceId', label: 'Device ID', type: 'text', default: '', placeholder: tr('protocols.k0b8f28') },
      { key: 'primaryHostId', label: 'Primary Host ID', type: 'text', default: '', help: tr('protocols.k2f164b') },
      { key: 'requestRebirth', label: tr('protocols.k0d723b'), type: 'bool', default: true },
      { key: 'confirmWrites', label: tr('protocols.kd4cb51'), type: 'bool', default: true },
    { key: 'clientId', label: tr('protocols.k920416'), type: 'text', default: '', placeholder: tr('protocols.k01f463') },
    { key: 'version', label: tr('protocols.k5793c2'), type: 'select', default: 'V500', options: ['V500', 'V311'] },
    { key: 'useTls', label: 'TLS', type: 'bool', default: false },
    { key: 'allowUntrustedCertificate', label: tr('protocols.k91ae87'), type: 'bool', default: false, help: tr('protocols.ka4a388') },
    { key: 'allowPlaintextPassword', label: tr('protocols.k659c62'), type: 'bool', default: false, help: tr('protocols.kcf8baf') },
    { key: 'keepAliveSec', label: tr('protocols.kf01fbd'), type: 'number', default: 30, min: 5, max: 3600 },
    ],
    addressPlaceholder: 'Inputs/Temperature',
    addressHelp: tr('protocols.k081bc9')
  },
  {
    value: 'MitsubishiMc', label: tr('protocols.kc2fc66'), defaultPort: 5000, hostPort: true, credentials: false,
    fields: [
      { key: 'format', label: tr('protocols.k453f47'), type: 'select', default: 'Binary', options: ['Binary', 'Ascii'] },
      { key: 'transport', label: tr('protocols.k14b6b3'), type: 'select', default: 'Tcp', options: ['Tcp', 'Udp'] },
      { key: 'networkNo', label: tr('protocols.ke5af88'), type: 'number', default: 0, min: 0, max: 255 },
      { key: 'pcNo', label: tr('protocols.kd3ebc6'), type: 'number', default: 255, min: 0, max: 255 },
      { key: 'moduleIo', label: tr('protocols.kc9270d'), type: 'number', default: 1023, min: 0, max: 65535, help: tr('protocols.ke2bf38') },
      { key: 'moduleStation', label: tr('protocols.k338ed2'), type: 'number', default: 0, min: 0, max: 255 },
      { key: 'monitoringTimer', label: tr('protocols.k30f7f3'), type: 'number', default: 8, min: 0, max: 65535 },
      { key: 'highWordFirst', label: tr('protocols.kbcb4ea'), type: 'bool', default: false, help: tr('protocols.k5250f0') },
      { key: 'maxGap', label: tr('protocols.k7c5907'), type: 'number', default: 16, min: 0, max: 256 }
    ],
    addressPlaceholder: 'D100、D100.3、M10、X1F、W1A0、ZR1000',
    addressHelp: tr('protocols.k947bf7'),
    notice: tr('protocols.k31ab95')
  },
  {
    value: 'OmronFins', label: tr('protocols.k589dd4'), defaultPort: 9600, hostPort: true, credentials: false,
    fields: [
      { key: 'transport', label: tr('protocols.k14b6b3'), type: 'select', default: 'Udp', options: ['Udp', 'Tcp'] },
      { key: 'destNetwork', label: tr('protocols.k5e32b3'), type: 'number', default: 0, min: 0, max: 127 },
      { key: 'destNode', label: tr('protocols.kc8b52c'), type: 'number', default: 0, min: 0, max: 254, help: tr('protocols.kbdd818') },
      { key: 'destUnit', label: tr('protocols.kcad17f'), type: 'number', default: 0, min: 0, max: 255, help: tr('protocols.k45bc58') },
      { key: 'sourceNetwork', label: tr('protocols.kc676ea'), type: 'number', default: 0, min: 0, max: 127 },
      { key: 'sourceNode', label: tr('protocols.kd107dc'), type: 'number', default: 0, min: 0, max: 254, help: tr('protocols.kf38242') },
      { key: 'highWordFirst', label: tr('protocols.kbcb4ea'), type: 'bool', default: false },
      { key: 'maxGap', label: tr('protocols.k7c5907'), type: 'number', default: 16, min: 0, max: 256 }
    ],
    addressPlaceholder: 'D100、CIO10.05、W3.00、H10、A100、T5、CNT5',
    addressHelp: tr('protocols.ka9c0bd')
  },
  {
    value: 'Iec104', label: tr('protocols.k1f8f6b'), defaultPort: 2404, hostPort: true, credentials: false,
    fields: [
      { key: 'commonAddress', label: tr('protocols.ka8738c'), type: 'number', default: 1, min: 1, max: 65534 },
      { key: 'originatorAddress', label: tr('protocols.k4baa03'), type: 'number', default: 0, min: 0, max: 255 },
      { key: 'commandMode', label: tr('protocols.k40f8dd'), type: 'select', default: 'SelectExecute', options: ['SelectExecute', 'Direct'], help: tr('protocols.k58d70d') },
      { key: 'waitActTerm', label: tr('protocols.kb15ee7'), type: 'bool', default: true, help: tr('protocols.k65b800') },
      { key: 'giIntervalSec', label: tr('protocols.k6bd1d8'), type: 'number', default: 900, min: 0, max: 86400, help: tr('protocols.k120d54') },
      { key: 'counterIntervalSec', label: tr('protocols.kc59dea'), type: 'number', default: 0, min: 0, max: 86400 },
      { key: 'clockSync', label: tr('protocols.k138e54'), type: 'bool', default: true },
      { key: 'deviceTimeUtc', label: tr('protocols.k941e2f'), type: 'bool', default: true },
      { key: 't1Sec', label: 't1 (s)', type: 'number', default: 15, min: 1, max: 255 },
      { key: 't2Sec', label: 't2 (s)', type: 'number', default: 10, min: 1, max: 255 },
      { key: 't3Sec', label: 't3 (s)', type: 'number', default: 20, min: 1, max: 172800 },
      { key: 'k', label: 'k', type: 'number', default: 12, min: 1, max: 32767 },
      { key: 'w', label: 'w', type: 'number', default: 8, min: 1, max: 32767 }
    ],
    addressPlaceholder: tr('protocols.kf6e9ef'),
    addressHelp: tr('protocols.k292a6e'),
    notice: tr('protocols.k078f54')
  },
  {
    value: 'BacnetIp', label: 'BACnet/IP', defaultPort: 47808, hostPort: true, credentials: false,
    fields: [
      { key: 'deviceInstance', label: tr('protocols.k9b82e7'), type: 'number', default: 0, min: 0, max: 4194302, help: tr('protocols.k0ded08') },
      { key: 'whoIs', label: tr('protocols.k1be3a9'), type: 'bool', default: true, help: tr('protocols.ka3015e') },
      { key: 'writePriority', label: tr('protocols.k5d6d59'), type: 'number', default: 8, min: 1, max: 16, help: tr('protocols.ka31759') },
      { key: 'readMultiple', label: 'ReadPropertyMultiple', type: 'bool', default: true, help: tr('protocols.k7f4060') },
      { key: 'maxObjectsPerRequest', label: tr('protocols.k0423a0'), type: 'number', default: 16, min: 1, max: 64 },
      { key: 'readRetries', label: tr('protocols.k115c35'), type: 'number', default: 1, min: 0, max: 3, help: tr('protocols.k29e72b') },
      { key: 'useCov', label: tr('protocols.k3d9997'), type: 'bool', default: false, help: tr('protocols.kb2cc87') },
      { key: 'covLifetimeSec', label: tr('protocols.k55e18f'), type: 'number', default: 300, min: 60, max: 28800 },
      { key: 'localPort', label: tr('protocols.ke1df15'), type: 'number', default: 0, min: 0, max: 65535, help: tr('protocols.keba495') }
    ],
    addressPlaceholder: 'AI:1、AV:10;pri=12、BO:3、MSV:2',
    addressHelp: tr('protocols.kad2c9a'),
    notice: tr('protocols.ke1ab45')
  },
  {
    value: 'OpcDa', label: tr('protocols.k0c887c'), defaultPort: 0, hostPort: false, credentials: false,
    fields: [
      { key: 'progId', label: tr('protocols.k581c5d'), type: 'text', default: '', help: tr('protocols.kdad167') },
      { key: 'node', label: tr('protocols.k323319'), type: 'text', default: '', help: tr('protocols.kac1e80') },
      { key: 'readFromDevice', label: tr('protocols.k2e0e82'), type: 'bool', default: false, help: tr('protocols.kabe258') },
      { key: 'updateRateMs', label: tr('protocols.k69a9cf'), type: 'number', default: 1000, min: 50, max: 3600000 },
      { key: 'timestampsUtc', label: tr('protocols.k48e3f3'), type: 'bool', default: false, help: tr('protocols.kfd8ead') }
    ],
    addressPlaceholder: 'Channel1.Device1.Tag1',
    addressHelp: tr('protocols.k248d7c'),
    notice: tr('protocols.k61622d')
  },
  {
    value: 'Iec61850', label: tr('protocols.kfaee37'), defaultPort: 102, hostPort: true, credentials: false,
    fields: [
      { key: 'useReports', label: tr('protocols.kcf36a3'), type: 'bool', default: false }
    ],
    addressPlaceholder: 'IED1LD0/MMXU1.MX.TotW.mag.f',
    addressHelp: tr('protocols.k1d54e4'),
    notice: tr('protocols.k96f300')
  }
]

export function protocolDef(protocol?: string): ProtocolDef | undefined {
  return textProtocols.find(x => x.value === protocol)
}

export function protocolLabel(protocol: string): string {
  if (protocol === 'ModbusTcp') return 'Modbus TCP'
  if (protocol === 'ModbusRtu') return tr('devicesView.k6d140f')
  if (protocol === 'SiemensS7') return 'Siemens S7'
  if (protocol === 'OpcUa') return 'OPC UA'
  return protocolDef(protocol)?.label ?? protocol
}

export function isTextProtocol(protocol?: string): boolean { return protocolDef(protocol) !== undefined }

export function defaultOptions(def: ProtocolDef): Record<string, string | number | boolean> {
  return Object.fromEntries(def.fields.map(f => [f.key, f.default]))
}

/** 解析已保存的协议参数 JSON，缺失字段补默认值，未知字段保留。 */
export function parseOptions(def: ProtocolDef | undefined, json: string | undefined): Record<string, string | number | boolean> {
  if (!def) return {}
  let saved: Record<string, string | number | boolean> = {}
  try { saved = json ? JSON.parse(json) : {} } catch { saved = {} }
  return { ...defaultOptions(def), ...saved }
}
