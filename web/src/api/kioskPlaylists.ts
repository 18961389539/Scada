import { http } from './http'

export interface KioskPlaylist {
  id: number
  name: string
  screenIds: number[]
  intervalSeconds: number
  monitorOnly: boolean
  revision: number
  updatedTs: number
  updatedBy: string
}

export interface KioskPlaylistRequest {
  name: string
  screenIds: number[]
  intervalSeconds: number
  monitorOnly: boolean
  revision?: number
}

export const kioskPlaylistApi = {
  list: async () => (await http.get<KioskPlaylist[]>('/kiosk-playlists')).data,
  get: async (id: number) => (await http.get<KioskPlaylist>(`/kiosk-playlists/${id}`)).data,
  create: async (request: KioskPlaylistRequest) => (await http.post<KioskPlaylist>('/kiosk-playlists', request)).data,
  save: async (id: number, request: KioskPlaylistRequest) => (await http.put<KioskPlaylist>(`/kiosk-playlists/${id}`, request)).data,
  remove: async (id: number, revision: number) => http.delete(`/kiosk-playlists/${id}`, { params: { revision } })
}
