import { errorMessage } from '../api/http'

/** 根据服务端 400 文案猜测应高亮的表单字段（无字段级 errors 时的启发式）。 */
const FIELD_HINTS: [RegExp, string][] = [
  [/测点名称|名称须|名称已存在|名称不能|name/i, 'name'], // i18n-ignore
  [/必须指定设备|设备不存在|device/i, 'deviceId'], // i18n-ignore
  [/必须指定测点|测点不存在|tagId/i, 'tagId'], // i18n-ignore
  [/NodeId|节点|地址须|地址及|OPC UA/i, 'nodeId'], // i18n-ignore
  [/地址/, 'address'], // i18n-ignore
  [/阈值|threshold/i, 'threshold'], // i18n-ignore
  [/延时|delay/i, 'delaySec'], // i18n-ignore
  [/死区|deadband/i, 'recoverDeadband'], // i18n-ignore
  [/抑制/, 'suppressTagId'], // i18n-ignore
  [/主机|host|Endpoint|串口|serial/i, 'host'], // i18n-ignore
  [/端口|port/i, 'port'], // i18n-ignore
  [/表达式|expression/i, 'expression'], // i18n-ignore
  [/描述或单位|单位过长|unit/i, 'unit'], // i18n-ignore
  [/周期存储|存档周期|archivePeriod/i, 'archivePeriodSec'], // i18n-ignore
  [/量程|最小值|最大值|minValue|maxValue/i, 'minValue'], // i18n-ignore
  [/节拍|额定产量/, 'idealCycleSec'], // i18n-ignore
  [/班次起止分钟|结束分钟|开始分钟/, 'startMinute'], // i18n-ignore
  [/结束时间须晚于|须晚于开始时间/, 'end'], // i18n-ignore
  [/修正原因/, 'reason'], // i18n-ignore
  [/原因代码/, 'code'] // i18n-ignore
]

export type FieldErrorMap = Record<string, string>

export function mapServerFieldError(message: string, preferredFields?: string[]): FieldErrorMap {
  const text = (message || '').trim()
  if (!text) return {}
  if (preferredFields?.length) {
    for (const field of preferredFields) {
      const hit = FIELD_HINTS.find(([re, f]) => f === field && re.test(text))
      if (hit) return { [field]: text }
    }
  }
  for (const [re, field] of FIELD_HINTS) {
    if (re.test(text)) return { [field]: text }
  }
  return { _form: text }
}

/** 从 axios/未知错误提取 message，并映射到字段。 */
export function serverFormErrors(error: unknown, fallback: string, preferredFields?: string[]): {
  message: string
  fields: FieldErrorMap
} {
  const message = errorMessage(error, fallback)
  return { message, fields: mapServerFieldError(message, preferredFields) }
}

export function clearFieldErrors(target: FieldErrorMap): void {
  for (const key of Object.keys(target)) delete target[key]
}

export function applyFieldErrors(target: FieldErrorMap, fields: FieldErrorMap): void {
  clearFieldErrors(target)
  Object.assign(target, fields)
}
