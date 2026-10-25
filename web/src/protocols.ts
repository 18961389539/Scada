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
  },
  {
    value: 'MitsubishiMc', label: '三菱 MC（3E 帧）', defaultPort: 5000, hostPort: true, credentials: false,
    fields: [
      { key: 'format', label: '报文格式', type: 'select', default: 'Binary', options: ['Binary', 'Ascii'] },
      { key: 'transport', label: '传输', type: 'select', default: 'Tcp', options: ['Tcp', 'Udp'] },
      { key: 'networkNo', label: '网络号', type: 'number', default: 0, min: 0, max: 255 },
      { key: 'pcNo', label: 'PC 号', type: 'number', default: 255, min: 0, max: 255 },
      { key: 'moduleIo', label: '请求目标模块 IO', type: 'number', default: 1023, min: 0, max: 65535, help: '默认 0x03FF（本站 CPU）' },
      { key: 'moduleStation', label: '请求目标站号', type: 'number', default: 0, min: 0, max: 255 },
      { key: 'monitoringTimer', label: '监视定时器 (×250ms)', type: 'number', default: 8, min: 0, max: 65535 },
      { key: 'highWordFirst', label: '32 位高字在前', type: 'bool', default: false, help: '三菱默认低字在前' },
      { key: 'maxGap', label: '批量读合并空隙', type: 'number', default: 16, min: 0, max: 256 }
    ],
    addressPlaceholder: 'D100、D100.3、M10、X1F、W1A0、ZR1000',
    addressHelp: '软元件+编号；X/Y/B/W/SB/SW/DX/DY 为十六进制编号，其余十进制；字的位（D100.3）只读',
    notice: 'PLC 侧须在以太网参数中开放 MC 协议端口（SLMP），二进制/ASCII 与 TCP/UDP 须与此处一致；运行中写入须允许 RUN 中写入。'
  },
  {
    value: 'OmronFins', label: '欧姆龙 FINS', defaultPort: 9600, hostPort: true, credentials: false,
    fields: [
      { key: 'transport', label: '传输', type: 'select', default: 'Udp', options: ['Udp', 'Tcp'] },
      { key: 'destNetwork', label: 'PLC 网络号', type: 'number', default: 0, min: 0, max: 127 },
      { key: 'destNode', label: 'PLC 节点号', type: 'number', default: 0, min: 0, max: 254, help: 'UDP 必填（通常为 PLC IP 末段）；TCP 为 0 时用握手返回值' },
      { key: 'destUnit', label: 'PLC 单元号', type: 'number', default: 0, min: 0, max: 255, help: '0 = CPU 单元' },
      { key: 'sourceNetwork', label: '本机网络号', type: 'number', default: 0, min: 0, max: 127 },
      { key: 'sourceNode', label: '本机节点号', type: 'number', default: 0, min: 0, max: 254, help: 'UDP 必填且网络内唯一；TCP 为 0 时由 PLC 分配' },
      { key: 'highWordFirst', label: '32 位高字在前', type: 'bool', default: false },
      { key: 'maxGap', label: '批量读合并空隙', type: 'number', default: 16, min: 0, max: 256 }
    ],
    addressPlaceholder: 'D100、CIO10.05、W3.00、H10、A100、T5、CNT5',
    addressHelp: '区域+十进制字地址[.位]；区域 CIO/W(WR)/H(HR)/A(AR)/D(DM)/T(TIM)/CNT；位写为 PLC 原子位操作'
  },
  {
    value: 'Iec104', label: 'IEC 60870-5-104 主站', defaultPort: 2404, hostPort: true, credentials: false,
    fields: [
      { key: 'commonAddress', label: '公共地址 (CA)', type: 'number', default: 1, min: 1, max: 65534 },
      { key: 'originatorAddress', label: '源发地址', type: 'number', default: 0, min: 0, max: 255 },
      { key: 'commandMode', label: '命令方式', type: 'select', default: 'SelectExecute', options: ['SelectExecute', 'Direct'], help: '选择-执行（SBO）或直接执行；测点可用 ;sbo=0|1 覆盖' },
      { key: 'waitActTerm', label: '等待激活终止', type: 'bool', default: true, help: '未收到 ACTTERM 时报告“结果未知”' },
      { key: 'giIntervalSec', label: '总召唤周期 (s)', type: 'number', default: 900, min: 0, max: 86400, help: '0 = 仅连接与子站初始化后召唤' },
      { key: 'counterIntervalSec', label: '电度召唤周期 (s)', type: 'number', default: 0, min: 0, max: 86400 },
      { key: 'clockSync', label: '连接后对时', type: 'bool', default: true },
      { key: 'deviceTimeUtc', label: '子站时标为 UTC', type: 'bool', default: true },
      { key: 't1Sec', label: 't1 (s)', type: 'number', default: 15, min: 1, max: 255 },
      { key: 't2Sec', label: 't2 (s)', type: 'number', default: 10, min: 1, max: 255 },
      { key: 't3Sec', label: 't3 (s)', type: 'number', default: 20, min: 1, max: 172800 },
      { key: 'k', label: 'k', type: 'number', default: 12, min: 1, max: 32767 },
      { key: 'w', label: 'w', type: 'number', default: 8, min: 1, max: 32767 }
    ],
    addressPlaceholder: 'M_SP:1001;cmd=C_SC:5001 或 M_ME_NC:3001',
    addressHelp: '类型:信息对象地址；类型 M_SP/M_DP/M_ME_NA/M_ME_NB/M_ME_NC/M_IT/M_ST/M_BO；可写追加 ;cmd=C_SC|C_DC|C_SE_NA|C_SE_NB|C_SE_NC:地址[;sbo=0|1]',
    notice: '遥控选择-执行需多次往返确认，请确保 Scada:CommandTimeoutMs 大于 2～3 倍设备超时。'
  },
  {
    value: 'BacnetIp', label: 'BACnet/IP', defaultPort: 47808, hostPort: true, credentials: false,
    fields: [
      { key: 'deviceInstance', label: '设备实例号', type: 'number', default: 0, min: 0, max: 4194302, help: '目标设备对象实例号，用于 Who-Is 定向与 I-Am 核对' },
      { key: 'whoIs', label: '连接时 Who-Is', type: 'bool', default: true, help: '未收到 I-Am 时回退为读取设备对象确认可达' },
      { key: 'writePriority', label: '默认写优先级', type: 'number', default: 8, min: 1, max: 16, help: '1～16（6 保留）；测点可用 ;pri= 覆盖' },
      { key: 'readMultiple', label: 'ReadPropertyMultiple', type: 'bool', default: true, help: '设备拒绝时自动回退 ReadProperty' },
      { key: 'maxObjectsPerRequest', label: '单次读取对象数', type: 'number', default: 16, min: 1, max: 64 },
      { key: 'readRetries', label: '读重发次数', type: 'number', default: 1, min: 0, max: 3, help: '写命令不重发' },
      { key: 'useCov', label: 'COV 订阅', type: 'bool', default: false, help: '对支持的对象订阅非确认 COV 通知，不支持的继续轮询' },
      { key: 'covLifetimeSec', label: 'COV 订阅时长 (s)', type: 'number', default: 300, min: 60, max: 28800 },
      { key: 'localPort', label: '本机 UDP 端口', type: 'number', default: 0, min: 0, max: 65535, help: '0 = 自动；设备只回 47808 时填 47808' }
    ],
    addressPlaceholder: 'AI:1、AV:10;pri=12、BO:3、MSV:2',
    addressHelp: '对象类型:实例号[;pri=1～16]；类型 AI/AO/AV/BI/BO/BV/MSV；读 Present_Value 与 Status_Flags，AO/AV/BO/BV/MSV 可写',
    notice: '不支持经路由器访问的 MS/TP 设备（DNET/DADR）与 BBMD 外部设备注册。'
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
