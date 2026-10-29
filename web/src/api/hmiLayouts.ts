import { http } from './http'
export type RegionKind = 'screen' | 'alarmBanner' | 'alarmList' | 'navigation'
export interface HmiRegion { id: string; kind: RegionKind; screenId?: number | null; title?: string | null; row: number; col: number; rowSpan: number; colSpan: number }
export interface HmiMonitor { name: string; columns: number; rows: number; previewWidth?: number | null; previewHeight?: number | null; regions: HmiRegion[] }
export interface HmiLayoutDocument { monitors: HmiMonitor[] }
export interface HmiLayout { id: number; name: string; revision: number; updatedTs: number; updatedBy: string; document: HmiLayoutDocument; publishedRevision?: number | null; publishedName?: string | null; publishedTs?: number | null; publishedBy?: string | null; hasDraftChanges?: boolean }
export interface HmiLayoutVersionSummary { revision: number; name: string; publishedTs: number; publishedBy: string; note: string; restoredFromRevision?: number | null; kind: string }
export interface HmiLayoutVersion extends HmiLayoutVersionSummary { document: HmiLayoutDocument }
export interface HmiWorkstation { id: number; name: string; description: string | null; layoutId: number | null; allowControl: boolean; updatedTs: number; updatedBy: string }
export const WORKSTATION_KEY = 'scada_workstation'
export const WORKSTATION_ID_KEY = 'scada_workstation_id'
export function getLocalWorkstation(): { id: number | null; name: string | null } {
  const raw = localStorage.getItem(WORKSTATION_ID_KEY)
  const id = raw && /^[1-9]\d*$/.test(raw) && Number.isSafeInteger(Number(raw)) ? Number(raw) : null
  return { id, name: localStorage.getItem(WORKSTATION_KEY) }
}
export function setLocalWorkstation(station: Pick<HmiWorkstation, 'id' | 'name'> | null): void {
  if (station) {
    localStorage.setItem(WORKSTATION_ID_KEY, String(station.id))
    localStorage.setItem(WORKSTATION_KEY, station.name)
  } else {
    localStorage.removeItem(WORKSTATION_ID_KEY)
    localStorage.removeItem(WORKSTATION_KEY)
  }
}
export function isLocalWorkstation(station: Pick<HmiWorkstation, 'id' | 'name'>): boolean {
  const local = getLocalWorkstation()
  return localStorage.getItem(WORKSTATION_ID_KEY) != null ? local.id === station.id : local.name === station.name
}
export interface ResolvedWorkstation { workstation: HmiWorkstation; layout: HmiLayout | null }
export const hmiLayoutApi = {
  list: async (draft = false) => (await http.get<HmiLayout[]>('/hmi/layouts', { params: draft ? { draft: true } : undefined })).data,
  get: async (id: number, draft = false) => (await http.get<HmiLayout>(`/hmi/layouts/${id}`, { params: draft ? { draft: true } : undefined })).data,
  save: async (l: { id?: number; name: string; document: HmiLayoutDocument; revision?: number }) =>
    (l.id ? await http.put<HmiLayout>(`/hmi/layouts/${l.id}`, l) : await http.post<HmiLayout>('/hmi/layouts', l)).data,
  remove: async (id: number, revision: number) => http.delete(`/hmi/layouts/${id}`, { params: { revision } }),
  publish: async (id: number, revision: number, note: string) => (await http.post<HmiLayout>(`/hmi/layouts/${id}/publish`, { revision, note })).data,
  rollback: async (id: number, revision: number, targetRevision: number, note: string) => (await http.post<HmiLayout>(`/hmi/layouts/${id}/rollback`, { revision, targetRevision, note })).data,
  versions: async (id: number) => (await http.get<HmiLayoutVersionSummary[]>(`/hmi/layouts/${id}/versions`, { params: { limit: 200 } })).data,
  version: async (id: number, revision: number) => (await http.get<HmiLayoutVersion>(`/hmi/layouts/${id}/versions/${revision}`)).data,
  workstations: async () => (await http.get<HmiWorkstation[]>('/hmi/workstations')).data,
  saveWorkstation: async (w: { id?: number; name: string; description: string | null; layoutId: number | null; allowControl: boolean; expectedUpdatedTs?: number }) =>
    (w.id ? await http.put<HmiWorkstation>(`/hmi/workstations/${w.id}`, w) : await http.post<HmiWorkstation>('/hmi/workstations', w)).data,
  removeWorkstation: async (id: number, updatedTs: number) => http.delete(`/hmi/workstations/${id}`, { params: { updatedTs } }),
  resolve: async (name: string) => (await http.get<ResolvedWorkstation>('/hmi/workstations/resolve', { params: { name } })).data,
  resolveLocal: async (): Promise<ResolvedWorkstation | null> => {
    const id = localStorage.getItem(WORKSTATION_ID_KEY)
    const name = localStorage.getItem(WORKSTATION_KEY)
    if (id == null && !name) return null
    const resolved = (await http.get<ResolvedWorkstation>('/hmi/workstations/resolve', { params: id != null ? { id } : { name } })).data
    // A delayed response must not undo a newer binding made in another window.
    if (localStorage.getItem(WORKSTATION_ID_KEY) === id && localStorage.getItem(WORKSTATION_KEY) === name) setLocalWorkstation(resolved.workstation)
    return resolved
  }
}
