import { http } from './http'

export type SnSource = 'Plc' | 'WebScan' | 'Tcp' | 'Serial'
export type SnFormat = 'Numeric' | 'AsciiLowByteFirst' | 'AsciiHighByteFirst'
export interface TraceabilityScannerOptions {
  tcpMode: 'Client' | 'Server'; host: string; port: number; serialPort: string; baudRate: number
  dataBits: number; parity: 'None' | 'Odd' | 'Even'; stopBits: 'One' | 'Two'
  terminator: 'CrLf' | 'Lf' | 'Cr'; encoding: 'Utf8' | 'Ascii'
}
export type TraceabilityOutcome = 'NotEvaluated' | 'Passed' | 'Failed' | 'Unknown'
export interface TraceabilityField { name: string; tagId: number; outcomeMapping?: { passValue: number; failValue: number } | null }
export interface TraceabilityStatus {
  jobId: number; state: string; pendingSn: string | null; scanExpiresTs: number | null; reply: number | null; error: string | null
}
export interface TraceabilityJobInput {
  name: string; enabled: boolean; triggerTagId: number; snSource: SnSource; snFormat: SnFormat
  snTagIds: number[]; fields: TraceabilityField[]; scanExpirySeconds: number; revision: number; scanner: TraceabilityScannerOptions
}
export interface TraceabilityJob extends TraceabilityJobInput {
  id: number; canConfigure: boolean; canScan: boolean; status: TraceabilityStatus; scannerStatus: { state: string; error: string | null }
}
export interface TraceabilityRecord {
  id: number; jobId: number; jobName: string; sn: string; capturedAt: string; acknowledged: boolean
  outcome: TraceabilityOutcome
  values: { name: string; tagId: number; value: number; unit: string | null; outcome?: TraceabilityOutcome | null }[]
}
export interface TraceabilityQuery { sn?: string; jobId?: number; from?: number; to?: number; outcome?: TraceabilityOutcome; page: number; pageSize: number }
export const traceabilityApi = {
  jobs: async () => (await http.get<TraceabilityJob[]>('/traceability/jobs')).data,
  save: async (id: number | undefined, input: TraceabilityJobInput) =>
    (id ? await http.put<TraceabilityJob>(`/traceability/jobs/${id}`, input) : await http.post<TraceabilityJob>('/traceability/jobs', input)).data,
  scan: async (id: number, sn: string) => (await http.post<TraceabilityStatus>(`/traceability/jobs/${id}/scan`, { sn })).data,
  clear: async (id: number) => { await http.delete(`/traceability/jobs/${id}/scan`) },
  records: async (params: TraceabilityQuery) =>
    (await http.get<{ total: number; items: TraceabilityRecord[] }>('/traceability/records', { params })).data
}