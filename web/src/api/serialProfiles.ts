import { http } from './http'

export interface SerialProfileInfo {
  id: number | null
  name: string
  source: 'database' | 'config'
  editable: boolean
  description: string
  enabled: boolean
  port: string
  baudRate: number
  dataBits: number
  parity: 'None' | 'Odd' | 'Even'
  stopBits: 'One' | 'Two'
  encoding: string
  newLine: string
  readTimeoutMs: number
  maxReplyChars: number
  dtrEnable: boolean
  rtsEnable: boolean
  revision: number
  conflict?: string | null
  lastUsedAt?: number | null
  lastOk?: boolean | null
  lastError?: string | null
  lastElapsedMs?: number | null
}

export interface SerialProfileList {
  items: SerialProfileInfo[]
  workerCount: number
  effectiveWorkerCount: number
  serialReady: boolean
}

export interface SerialProfileInput {
  name: string
  description: string
  enabled: boolean
  port: string
  baudRate: number
  dataBits: number
  parity: 'None' | 'Odd' | 'Even'
  stopBits: 'One' | 'Two'
  encoding: string
  newLine: string
  readTimeoutMs: number
  maxReplyChars: number
  dtrEnable: boolean
  rtsEnable: boolean
  revision?: number
}

export const serialProfileApi = {
  list: async (signal?: AbortSignal) => (await http.get<SerialProfileList>('/serial-profiles', { signal })).data,
  ports: async () => (await http.get<string[]>('/serial-profiles/ports')).data,
  create: async (input: SerialProfileInput) => (await http.post<SerialProfileInfo>('/serial-profiles', input)).data,
  update: async (id: number, input: SerialProfileInput) => (await http.put<SerialProfileInfo>(`/serial-profiles/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/serial-profiles/${id}`)
}
