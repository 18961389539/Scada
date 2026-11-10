import { http } from './http'

export interface ChannelInfo {
  id: number
  name: string
  description: string
  protocol: string
  host: string
  port: number
  serialPort: string
  baudRate: number
  dataBits: number
  parity: 'None' | 'Odd' | 'Even'
  stopBits: 'One' | 'Two'
  timeoutMs: number
  enabled: boolean
  deviceCount: number
}

export interface ChannelInput {
  name: string
  protocol: string
  description: string
  host: string
  port: number
  serialPort: string
  baudRate: number
  dataBits: number
  parity: 'None' | 'Odd' | 'Even'
  stopBits: 'One' | 'Two'
  timeoutMs: number
  enabled: boolean
}

// 总线级观测：共享链路的瓶颈是"链路占用时间"，不是连接数。
export interface ChannelMemberStatistics {
  deviceId: number
  deviceName: string
  station: number
  transactions: number
  slowQueues: number
  failures: number
  waitAvgMs: number
  waitMaxMs: number
  busyMs: number
  busySharePercent: number
  lastTransactionTs: number
}

export interface ChannelStatistics {
  channelId: number
  name: string
  protocol: string
  endpoint: string
  connected: boolean
  connections: number
  leaseCount: number
  transactions: number
  busyPercent: number
  waitAvgMs: number
  waitMaxMs: number
  slowQueues: number
  lastError: string | null
  capturedTs: number
  members: ChannelMemberStatistics[]
}

// 只有"一条总线上挂多从站"的协议可以共享物理链路。
export const channelProtocols = ['ModbusTcp', 'ModbusRtuOverTcp', 'ModbusRtu', 'ModbusAscii'] as const
export const channelUsesSerial = (protocol: string): boolean => protocol === 'ModbusRtu' || protocol === 'ModbusAscii'

export const channelApi = {
  list: async (signal?: AbortSignal) => (await http.get<ChannelInfo[]>('/channels', { signal })).data,
  create: async (input: ChannelInput) => (await http.post<ChannelInfo>('/channels', input)).data,
  update: async (id: number, input: ChannelInput) => (await http.put<ChannelInfo>(`/channels/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/channels/${id}`),
  diagnostics: async (id: number, signal?: AbortSignal) =>
    (await http.get<ChannelStatistics>(`/channels/${id}/diagnostics`, { signal })).data
}
