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
    value: 'Mqtt', label: 'MQTT（JSON/主题映射）', defaultPort: 1883, hostPort: true, credentials: true,
    fields: [
    { key: 'clientId', label: '客户端 ID', type: 'text', default: '', placeholder: '留空自动生成（每次连接唯一）' },
    { key: 'version', label: 'MQTT 版本', type: 'select', default: 'V500', options: ['V500', 'V311'] },
    { key: 'useTls', label: 'TLS', type: 'bool', default: false },
    { key: 'allowUntrustedCertificate', label: '跳过证书校验', type: 'bool', default: false, help: '仅调试使用，生产环境请导入受信 CA' },
    { key: 'allowPlaintextPassword', label: '允许明文密码', type: 'bool', default: false, help: '未启用 TLS 时发送密码，仅限隔离网络' },
    { key: 'keepAliveSec', label: '心跳 (s)', type: 'number', default: 30, min: 5, max: 3600 },
      { key: 'qos', label: 'QoS', type: 'number', default: 1, min: 0, max: 2 },
      { key: 'cleanSession', label: '清除会话', type: 'bool', default: true },
      { key: 'retainWrites', label: '写入保留消息', type: 'bool', default: false },
      { key: 'confirmWrites', label: '写入等待状态回报', type: 'bool', default: true, help: '关闭后发布成功即视为写入完成，由控制闭环回读判断' },
      { key: 'staleAfterMs', label: '失效判定 (ms)', type: 'number', default: 0, min: 0, max: 86400000, help: '超过该时长无新消息则质量为不确定；0 不判定' }
    ],
    addressPlaceholder: 'plant/boiler|$.temp;w=plant/boiler/set;wpath=$.value;ts=$.ts',
    addressHelp: '状态主题[|JSON路径][;w=写入主题][;wpath=写入JSON路径][;ts=时间戳路径]；不支持通配符'
  },
  {
    value: 'SparkplugB', label: 'MQTT Sparkplug B', defaultPort: 1883, hostPort: true, credentials: true,
    fields: [
      { key: 'groupId', label: 'Group ID', type: 'text', default: '' },
      { key: 'edgeNodeId', label: 'Edge Node ID', type: 'text', default: '' },
      { key: 'deviceId', label: 'Device ID', type: 'text', default: '', placeholder: '留空表示节点级度量' },
      { key: 'primaryHostId', label: 'Primary Host ID', type: 'text', default: '', help: '非空时发布 STATE（每个代理只在一台设备上填写）' },
      { key: 'requestRebirth', label: '自动请求 Rebirth', type: 'bool', default: true },
      { key: 'confirmWrites', label: '写入等待 DATA 回报', type: 'bool', default: true },
    { key: 'clientId', label: '客户端 ID', type: 'text', default: '', placeholder: '留空自动生成（每次连接唯一）' },
    { key: 'version', label: 'MQTT 版本', type: 'select', default: 'V500', options: ['V500', 'V311'] },
    { key: 'useTls', label: 'TLS', type: 'bool', default: false },
    { key: 'allowUntrustedCertificate', label: '跳过证书校验', type: 'bool', default: false, help: '仅调试使用，生产环境请导入受信 CA' },
    { key: 'allowPlaintextPassword', label: '允许明文密码', type: 'bool', default: false, help: '未启用 TLS 时发送密码，仅限隔离网络' },
    { key: 'keepAliveSec', label: '心跳 (s)', type: 'number', default: 30, min: 5, max: 3600 },
    ],
    addressPlaceholder: 'Inputs/Temperature',
    addressHelp: '度量名（metric name），须出现在 NBIRTH/DBIRTH 中'
  }
]

export function protocolDef(protocol?: string): ProtocolDef | undefined {
  return textProtocols.find(x => x.value === protocol)
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
