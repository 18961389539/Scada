import { http } from './http'

export type ConflictResolution = 'Overwrite' | 'Skip' | 'Rename'
export interface FieldChange { field: string; old: string | null; new: string | null }
export interface PlanEntry { section: string; sectionLabel: string; key: string; finalKey: string; action: 'add' | 'update' | 'rename' | 'skip' | 'delete'; areas: string[]; changes: FieldChange[] }
export interface PlanView {
  baseSha: string | null; versionId: number | null; counts: Record<string, number>; entries: PlanEntry[]; hidden: number
  errors: string[]; forbidden: string[]; warnings: string[]
}
export interface ConfigVersion { id: number; kind: string; note: string; createdTs: number; createdBy: string; contentSha256: string; summary: Record<string, number> }
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export type ProjectPackage = Record<string, any>

export const projectApi = {
  versions: async () => (await http.get<ConfigVersion[]>('/project/versions')).data,
  snapshot: async (note: string) => (await http.post<ConfigVersion>('/project/versions', { note })).data,
  diff: async (from: string, to: string) => (await http.get<PlanView>('/project/diff', { params: { from, to } })).data,
  exportPackage: async () => (await http.get('/project/export', { responseType: 'blob' })).data as Blob,
  downloadVersion: async (id: number) => (await http.get(`/project/versions/${id}/download`, { responseType: 'blob' })).data as Blob,
  rollbackPreview: async (id: number) => (await http.post<PlanView>(`/project/versions/${id}/rollback/preview`)).data,
  rollback: async (id: number, baseSha: string) => (await http.post<PlanView>(`/project/versions/${id}/rollback`, { baseSha }, { timeout: 120000 })).data,
  importPreview: async (pkg: ProjectPackage, resolution: ConflictResolution, resolutions: Record<string, ConflictResolution>) =>
    (await http.post<PlanView>('/project/import/preview', { package: pkg, resolution, resolutions }, { timeout: 120000 })).data,
  importApply: async (pkg: ProjectPackage, resolution: ConflictResolution, resolutions: Record<string, ConflictResolution>, baseSha: string) =>
    (await http.post<PlanView>('/project/import', { package: pkg, resolution, resolutions, baseSha }, { timeout: 120000 })).data
}
