import { http } from './http'
import type { LibraryItem, ScreenDocument } from '../screens/model'
export interface LibraryInput { key: string; name: string; category: string; kind: 'symbol' | 'template'; svg?: string | null; document?: ScreenDocument | null; placeholders?: string[]; width: number; height: number; revision?: number }
export const hmiLibraryApi = {
  list: async () => (await http.get<LibraryItem[]>('/hmi/library')).data,
  create: async (item: LibraryInput) => (await http.post<{ item: LibraryItem; usedBy: string[] }>('/hmi/library', item)).data,
  update: async (id: number, item: LibraryInput) => (await http.put<{ item: LibraryItem; usedBy: string[] }>(`/hmi/library/${id}`, item)).data,
  remove: async (id: number) => http.delete(`/hmi/library/${id}`),
  import: async (items: LibraryInput[]) => (await http.post<{ added: number; updated: number }>('/hmi/library/import', items)).data
}
