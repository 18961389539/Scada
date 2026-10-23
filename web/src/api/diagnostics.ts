import { http } from './http'
import type { TagQuality } from '../types'

export interface DiagnosticPoint {
  tagId: number; name: string; address: string; dataType: string; rawValue: number | null; value: number | null
  quality: TagQuality | null; ts: number | null; sourceTs: number | null; lastGoodTs: number | null
  ageMs: number | null; stale: boolean; error: string | null
}
export interface CollectorDiagnostics {
  deviceId: number; name: string; protocol: string; connection: string; state: string; operation: string
  operationTagId: number | null; currentError: string | null; enabled: boolean; configurationPending: boolean; configVersion: number
  capturedTs: number; startedTs: number; pollIntervalMs: number; timeoutMs: number; staleAfterMs: number; pendingCommands: number; pointCount: number
  quality: { good: number; bad: number; uncertain: number; commFailure: number; unknown: number; stale: number }
  metrics: {
    connectAttempts: number; connectFailures: number; pollAttempts: number; pollSuccesses: number; pollFailures: number; degradedPolls: number; consecutiveFailures: number
    lastAttemptTs: number | null; lastSuccessTs: number | null; lastFailureTs: number | null; nextRetryTs: number | null; lastFailure: string | null
    commandsSucceeded: number; commandsFailed: number; commandsExpired: number; commandsRejected: number; lastCommandTs: number | null
    lastPollMs: number; windowAvgMs: number; windowMaxMs: number; windowP95Ms: number; windowSamples: number
  }
  readGroups: { index: number; area: string; dbNumber: number | null; startAddress: number | null; length: number; unit: string; tagIds: number[] }[]
  points: DiagnosticPoint[]
  events: { id: number; ts: number; level: string; kind: string; message: string; tagId: number | null }[]
  samples: { ts: number; durationMs: number; success: boolean; good: number; bad: number; error: string | null }[]
}
export interface DiagnosticsQuery { page: number; pageSize: number; search?: string; quality?: TagQuality; staleOnly?: boolean }
export interface DiagnosticsPage { diagnostics: CollectorDiagnostics; total: number; page: number; pageSize: number }
export const diagnosticsApi = {
  get: async (id: number, params: DiagnosticsQuery, signal?: AbortSignal) => (await http.get<DiagnosticsPage>(`/devices/${id}/diagnostics`, { params, signal })).data,
  export: async (id: number) => (await http.get<Blob>(`/devices/${id}/diagnostics/export`, { responseType: 'blob' })).data,
  reconnect: async (id: number) => (await http.post<{ message: string }>(`/devices/${id}/diagnostics/reconnect`)).data
}
