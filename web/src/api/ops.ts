import { http } from './http'

export interface HaPeer { nodeId: string; role: string; term: number; selfUrl: string | null; ts: number }
export interface HaLease { holder: string; term: number; expiresTs: number; handoverTo: string | null }
export interface HaEvent { ts: number; kind: string; message: string; term: number; actor: string | null }
export interface HaStatus {
  enabled: boolean; nodeId: string; role: 'Primary' | 'Standby' | 'Standalone'; term: number; generation: number; selfUrl: string | null; peerUrl: string | null
  peer: HaPeer | null; peerSeenTs: number; witness: string | null; lease: HaLease | null; witnessError: string | null; autoFailover: boolean; promotedTs: number
  configSyncTs: number; historySyncTs: number; historyRowsBehind: number; configApplied: number; historyRowsApplied: number; replicationError: string | null
  runtimeSyncTs?: number; runtimeRowsApplied?: number; runtimeRowsBehind?: number; peerSpoolBytes?: number; peerSpoolApproxCount?: number
  switchovers: number; events: HaEvent[]
}
export interface BackupDrill { fileName: string; ok: boolean; problems: string[]; durationMs: number; devices: number; tags: number; historyFiles: number; ts: string }
export interface BackupTargetResult { target: string; kind: string; ok: boolean; error: string | null; pruned: string[] }
export interface BackupRun {
  trigger: string; startedAt: string; finishedAt: string; fileName: string | null; bytes: number; ok: boolean; error: string | null
  drill: BackupDrill | null; targets: BackupTargetResult[]; pruned: string[]
}
export interface BackupSummary { fileName: string; bytes: number; createdAt: string; encrypted?: boolean; path?: string }
export interface BackupSecurity { development: boolean; passwordConfigured: boolean; plaintextRisk: boolean; warning?: string | null }
export interface BackupVerification { valid: boolean; problems: string[]; manifest?: { createdAt?: string; devices?: number; tags?: number; historyFiles?: number } | null }
export interface DesktopClient {
  clientId: string; machineName: string; windowsUser: string; userId: number | null; userName: string | null
  targetKind: string | null; targetId: number | null; targetName: string | null
  appVersion: string; webView2Version: string; configScope: string | null; nodeId: string; remoteAddress: string | null
  firstSeenTs: number; lastSeenTs: number; online: boolean; blockedReason: string | null
  conflict: boolean; previousMachineName: string | null
}
export interface DesktopClientPolicy {
  minimumVersion: string | null; latestVersion: string | null; downloadUrl: string | null
  updateRequired: boolean; updateAvailable: boolean
}
export interface DesktopClientList {
  node: string; haRole: string; onlineThresholdSeconds: number; total: number
  /** 全量口径：列表最多返回最近上报的 500 条，计数不受截断影响。 */
  online: number; blocked: number; outdated: number
  /** true 表示 clients 只包含最近 500 条，total 是全场数量。 */
  truncated: boolean; policy: DesktopClientPolicy
  clients: DesktopClient[]
}

export const opsApi = {
  role: async () => (await http.get<{ enabled: boolean; node: string | null; role: string; primaryUrl: string | null }>('/ha/role')).data,
  status: async () => (await http.get<HaStatus>('/ha/status')).data,
  switchover: async (reason: string) => (await http.post('/ha/switchover', { reason })).data,
  promote: async (reason: string, force: boolean) => (await http.post('/ha/promote', { reason, force })).data,
  backups: async () => (await http.get<BackupSummary[]>('/maintenance/backups')).data,
  backupSecurity: async () => (await http.get<BackupSecurity>('/maintenance/backups/security')).data,
  verify: async (fileName: string) => (await http.post<BackupVerification>(`/maintenance/backups/${encodeURIComponent(fileName)}/verify`, null, { timeout: 600000 })).data,
  downloadUrl: (fileName: string) => `/api/maintenance/backups/${encodeURIComponent(fileName)}`,
  diagnosticsUrl: () => '/api/maintenance/diagnostics',
  runs: async () => (await http.get<BackupRun[]>('/maintenance/backups/runs')).data,
  targets: async () => (await http.get<{ name: string; kind: string; keepLatest: number }[]>('/maintenance/backups/targets')).data,
  run: async () => (await http.post<BackupRun>('/maintenance/backups/run', null, { timeout: 600000 })).data,
  drill: async (fileName: string) => (await http.post<BackupDrill>(`/maintenance/backups/${encodeURIComponent(fileName)}/drill`, null, { timeout: 600000 })).data,
  desktopClients: async () => (await http.get<DesktopClientList>('/desktop/clients')).data
}
