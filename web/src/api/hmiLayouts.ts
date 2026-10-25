import { http } from './http'
export type RegionKind = 'screen' | 'alarmBanner' | 'alarmList' | 'navigation'
export interface HmiRegion { id: string; kind: RegionKind; screenId?: number | null; title?: string | null; row: number; col: number; rowSpan: number; colSpan: number }
export interface HmiMonitor { name: string; columns: number; rows: number; regions: HmiRegion[] }
export interface HmiLayoutDocument { monitors: HmiMonitor[] }
export interface HmiLayout { id: number; name: string; revision: number; updatedTs: number; updatedBy: string; document: HmiLayoutDocument }
export interface HmiWorkstation { id: number; name: string; description: string | null; layoutId: number | null; allowControl: boolean; updatedTs: number; updatedBy: string }
export const WORKSTATION_KEY = 'scada_workstation'
export const hmiLayoutApi = {
  list: async () => (await http.get<HmiLayout[]>('/hmi/layouts')).data,
  save: async (l: { id?: number; name: string; document: HmiLayoutDocument; revision?: number }) =>
    (l.id ? await http.put<HmiLayout>(`/hmi/layouts/${l.id}`, l) : await http.post<HmiLayout>('/hmi/layouts', l)).data,
  remove: async (id: number) => http.delete(`/hmi/layouts/${id}`),
  workstations: async () => (await http.get<HmiWorkstation[]>('/hmi/workstations')).data,
  saveWorkstation: async (w: { id?: number; name: string; description: string | null; layoutId: number | null; allowControl: boolean }) =>
    (w.id ? await http.put<HmiWorkstation>(`/hmi/workstations/${w.id}`, w) : await http.post<HmiWorkstation>('/hmi/workstations', w)).data,
  removeWorkstation: async (id: number) => http.delete(`/hmi/workstations/${id}`),
  resolve: async (name: string) => (await http.get<{ workstation: HmiWorkstation; layout: HmiLayout | null }>('/hmi/workstations/resolve', { params: { name } })).data
}
