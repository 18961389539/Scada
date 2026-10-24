import { http } from './http'

export interface HaPeer { nodeId: string; role: string; term: number; selfUrl: string | null; ts: number }
export interface HaLease { holder: string; term: number; expiresTs: number; handoverTo: string | null }
export interface HaEvent { ts: number; kind: string; message: string; term: number; actor: string | null }
export interface HaStatus {
  enabled: boolean; nodeId: string; role: 'Primary' | 'Standby' | 'Standalone'; term: number; generation: number; selfUrl: string | null; peerUrl: string | null
  peer: HaPeer | null; peerSeenTs: number; witness: string | null; lease: HaLease | null; witnessError: string | null; autoFailover: boolean; promotedTs: number
  configSyncTs: number; historySyncTs: number; historyRowsBehind: number; configApplied: number; historyRowsApplied: number; replicationError: string | null
  switchovers: number; events: HaEvent[]
}
export interface BackupDrill { fileName: string; ok: boolean; problems: string[]; durationMs: number; devices: number; tags: number; historyFiles: number; ts: string }
export interface BackupTargetResult { target: string; kind: string; ok: boolean; error: string | null; pruned: string[] }
export interface BackupRun {
  trigger: string; startedAt: string; finishedAt: string; fileName: string | null; bytes: number; ok: boolean; error: string | null
  drill: BackupDrill | null; targets: BackupTargetResult[]; pruned: string[]
}
export interface BackupSummary { fileName: string; bytes: number; createdAt: string }

export const opsApi = {
  role: async () => (await http.get<{ enabled: boolean; node: string | null; role: string; primaryUrl: string | null }>('/ha/role')).data,
  status: async () => (await http.get<HaStatus>('/ha/status')).data,
  switchover: async (reason: string) => (await http.post('/ha/switchover', { reason })).data,
  promote: async (reason: string, force: boolean) => (await http.post('/ha/promote', { reason, force })).data,
  backups: async () => (await http.get<BackupSummary[]>('/maintenance/backups')).data,
  runs: async () => (await http.get<BackupRun[]>('/maintenance/backups/runs')).data,
  targets: async () => (await http.get<{ name: string; kind: string; keepLatest: number }[]>('/maintenance/backups/targets')).data,
  run: async () => (await http.post<BackupRun>('/maintenance/backups/run', null, { timeout: 600000 })).data,
  drill: async (fileName: string) => (await http.post<BackupDrill>(`/maintenance/backups/${encodeURIComponent(fileName)}/drill`, null, { timeout: 600000 })).data
}
