import { http } from './http'

export type SnSource = 'Plc' | 'WebScan'
export type SnFormat = 'Numeric' | 'AsciiLowByteFirst' | 'AsciiHighByteFirst'
export interface TraceabilityField { name: string; tagId: number }
export interface TraceabilityStatus {
  jobId: number; state: string; pendingSn: string | null; scanExpiresTs: number | null; reply: number | null; error: string | null
}
export interface TraceabilityJobInput {
  name: string; enabled: boolean; triggerTagId: number; snSource: SnSource; snFormat: SnFormat
  snTagIds: number[]; fields: TraceabilityField[]; scanExpirySeconds: number; revision: number
}
export interface TraceabilityJob extends TraceabilityJobInput {
  id: number; canConfigure: boolean; canScan: boolean; status: TraceabilityStatus
}
export interface TraceabilityRecord {
  id: number; jobId: number; jobName: string; sn: string; capturedAt: string; acknowledged: boolean
  values: { name: string; tagId: number; value: number; unit: string | null }[]
}
export interface TraceabilityQuery { sn?: string; jobId?: number; from?: number; to?: number; page: number; pageSize: number }
export const traceabilityApi = {
  jobs: async () => (await http.get<TraceabilityJob[]>('/traceability/jobs')).data,
  save: async (id: number | undefined, input: TraceabilityJobInput) =>
    (id ? await http.put<TraceabilityJob>(`/traceability/jobs/${id}`, input) : await http.post<TraceabilityJob>('/traceability/jobs', input)).data,
  scan: async (id: number, sn: string) => (await http.post<TraceabilityStatus>(`/traceability/jobs/${id}/scan`, { sn })).data,
  clear: async (id: number) => { await http.delete(`/traceability/jobs/${id}/scan`) },
  records: async (params: TraceabilityQuery) =>
    (await http.get<{ total: number; items: TraceabilityRecord[] }>('/traceability/records', { params })).data
}