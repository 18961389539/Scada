import { shallowRef } from 'vue'
import { tr } from './i18n'
import { http } from './api/http'

// 文本地址协议的设备参数表单、默认端口与地址语法提示。
// 元数据以后端 IProtocolSpec / GET /api/protocols 为唯一来源，此处只做本地化与缓存。
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
  locked?: boolean
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

export interface ProtocolFieldDto {
  key: string
  type: ProtocolField['type']
  default: string | number | boolean
  labelKey?: string | null
  label?: string | null
  options?: string[] | null
  min?: number | null
  max?: number | null
  placeholderKey?: string | null
  placeholder?: string | null
  helpKey?: string | null
  help?: string | null
  locked?: boolean
}

export interface ProtocolCatalogItem {
  value: string
  label?: string | null
  labelKey?: string | null
  defaultPort: number
  hostPort: boolean
  credentials: boolean
  fields: ProtocolFieldDto[]
  addressPlaceholder: string
  addressPlaceholderKey?: string | null
  addressHelpKey?: string | null
  noticeKey?: string | null
}

function text(key?: string | null, fallback?: string | null): string {
  if (key) return tr(key)
  return fallback ?? ''
}

export function localizeProtocol(item: ProtocolCatalogItem): ProtocolDef {
  return {
    value: item.value,
    label: text(item.labelKey, item.label) || item.value,
    defaultPort: item.defaultPort,
    hostPort: item.hostPort,
    credentials: item.credentials,
    fields: item.fields.map(field => ({
      key: field.key,
      label: text(field.labelKey, field.label) || field.key,
      type: field.type,
      default: field.default,
      options: field.options ?? undefined,
      min: field.min ?? undefined,
      max: field.max ?? undefined,
      placeholder: text(field.placeholderKey, field.placeholder) || undefined,
      help: text(field.helpKey, field.help) || undefined,
      locked: field.locked || undefined
    })),
    addressPlaceholder: text(item.addressPlaceholderKey, item.addressPlaceholder),
    addressHelp: text(item.addressHelpKey),
    notice: text(item.noticeKey) || undefined
  }
}

/** 响应式目录：登录后由 ensureProtocolCatalog 填充。模板中自动解包。 */
export const textProtocols = shallowRef<ProtocolDef[]>([])

let pending: Promise<void> | null = null
let loaded = false

export async function ensureProtocolCatalog(): Promise<void> {
  if (loaded) return
  if (!pending) {
    pending = (async () => {
      const { data } = await http.get<ProtocolCatalogItem[]>('/protocols')
      textProtocols.value = (data ?? []).map(localizeProtocol)
      loaded = true
    })().catch(error => {
      pending = null
      throw error
    })
  }
  await pending
}

export function protocolDef(protocol?: string): ProtocolDef | undefined {
  return textProtocols.value.find(x => x.value === protocol)
}

export function protocolLabel(protocol: string): string {
  if (protocol === 'ModbusTcp') return 'Modbus TCP'
  if (protocol === 'ModbusRtu') return tr('devicesView.k6d140f')
  if (protocol === 'ModbusAscii') return 'Modbus ASCII'
  if (protocol === 'ModbusRtuOverTcp') return 'Modbus RTU over TCP'
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
