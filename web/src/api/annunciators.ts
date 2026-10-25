import { http } from './http'

export type AnnunciatorChannelKind = 'Horn' | 'Beacon' | 'TowerRed' | 'TowerYellow' | 'TowerBlue' | 'TowerGreen'
export type AnnunciatorLatchMode = 'Follow' | 'Latch'
export type AlarmLevel = 'Info' | 'Warning' | 'Critical'

export interface AnnunciatorOutput {
  id?: number
  kind: AnnunciatorChannelKind
  tagId: number
  activeValue: number
  inactiveValue: number
  enabled: boolean
}

export interface AnnunciatorLevelMap {
  id?: number
  level: AlarmLevel
  horn: boolean
  beacon: boolean
  towerRed: boolean
  towerYellow: boolean
  towerBlue: boolean
  towerGreen: boolean
  flash: boolean
  soundKey?: string | null
}

export interface AnnunciatorPanel {
  id: number
  name: string
  areaId: number | null
  enabled: boolean
  latchMode: AnnunciatorLatchMode
  silenceMinutes: number
  flashOnMs: number
  flashOffMs: number
  criticalFlashOnMs: number
  criticalFlashOffMs: number
  browserSound: boolean
  revision: number
  updatedTs: number
  updatedBy?: string | null
  outputs: AnnunciatorOutput[]
  levels: AnnunciatorLevelMap[]
}

export interface AnnunciatorStatus {
  panelId: number
  name: string
  areaId: number | null
  enabled: boolean
  drivingLevel: AlarmLevel | null
  horn: boolean
  beacon: boolean
  towerRed: boolean
  towerYellow: boolean
  towerBlue: boolean
  towerGreen: boolean
  flashing: boolean
  silenced: boolean
  silenceUntilTs: number | null
  soundKey: string | null
  lastError: string | null
}

export type AnnunciatorPanelInput = Omit<AnnunciatorPanel, 'id' | 'revision' | 'updatedTs' | 'updatedBy' | 'outputs' | 'levels'> & {
  outputs: AnnunciatorOutput[]
  levels: AnnunciatorLevelMap[]
}

export const annunciatorApi = {
  list: async () => (await http.get<AnnunciatorPanel[]>('/annunciators')).data,
  status: async () => (await http.get<AnnunciatorStatus[]>('/annunciators/status')).data,
  get: async (id: number) => (await http.get<AnnunciatorPanel>(`/annunciators/${id}`)).data,
  create: async (input: AnnunciatorPanelInput) => (await http.post<AnnunciatorPanel>('/annunciators', input)).data,
  update: async (id: number, input: AnnunciatorPanelInput) => (await http.put<AnnunciatorPanel>(`/annunciators/${id}`, input)).data,
  remove: async (id: number) => { await http.delete(`/annunciators/${id}`) },
  silence: async (id: number, minutes?: number, reason?: string) =>
    (await http.post<AnnunciatorStatus>(`/annunciators/${id}/silence`, { minutes, reason })).data,
  clearSilence: async (id: number) => (await http.post<AnnunciatorStatus>(`/annunciators/${id}/clear-silence`)).data,
  test: async (id: number) => (await http.post<AnnunciatorStatus>(`/annunciators/${id}/test`)).data,
  ackVisible: async (note?: string) => (await http.post<{ acked: number; total: number }>('/annunciators/ack-visible', { note })).data
}
