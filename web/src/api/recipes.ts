import { http } from './http'

export interface RecipeParameter { key: string; name: string; tagId: number; unit: string | null; min: number | null; max: number | null; defaultValue: number | null }
export interface RecipeTemplate { id: number; name: string; areaId: number; description: string | null; dualApproval: boolean; revision: number; updatedTs: number; updatedBy: string | null; parameters: RecipeParameter[] }
export interface RecipeValue { key: string; value: number }
export interface Recipe { id: number; templateId: number; templateName: string; areaId: number; name: string; description: string | null; version: number; updatedTs: number; updatedBy: string | null; values: RecipeValue[]; note: string | null; source: string | null }
export interface RecipeVersion { version: number; note: string | null; source: string; createdTs: number; createdBy: string; values: RecipeValue[] }
export interface RecipeParamResult { key: string; name: string; tagId: number; target: number; before: number | null; readBack: number | null; status: string; message: string | null }
export interface RecipeDownload {
  id: number; recipeId: number; recipeName: string; version: number; templateId: number; areaId: number; status: string; statusLabel: string; requestedBy: string; approvedBy: string | null
  approvalId: string | null; createdTs: number; startedTs: number | null; endedTs: number | null; written: number; message: string | null; results: RecipeParamResult[]
}
export interface RecipeParamCheck { key: string; name: string; tagId: number; tagName: string; unit: string | null; target: number; current: number | null; refusals: string[]; dualApproval: boolean }
export interface RecipeCheck { recipeId: number; version: number; dualApproval: boolean; ok: boolean; refusals: string[]; parameters: RecipeParamCheck[] }
export interface RecipeCompare { leftLabel: string; rightLabel: string; differences: number; rows: { key: string; name: string; unit: string | null; tagId: number; left: number | null; right: number | null; different: boolean }[] }

export const recipeApi = {
  templates: async () => (await http.get<RecipeTemplate[]>('/recipes/templates')).data,
  saveTemplate: async (t: Omit<RecipeTemplate, 'id' | 'updatedTs' | 'updatedBy'> & { id?: number }) =>
    (t.id ? await http.put<RecipeTemplate>(`/recipes/templates/${t.id}`, t) : await http.post<RecipeTemplate>('/recipes/templates', t)).data,
  removeTemplate: async (id: number) => http.delete(`/recipes/templates/${id}`),
  list: async (templateId?: number) => (await http.get<Recipe[]>('/recipes', { params: { templateId } })).data,
  save: async (r: { id?: number; templateId: number; name: string; description: string | null; values: RecipeValue[]; note: string | null; version: number }) =>
    (r.id ? await http.put<{ recipe: Recipe; warnings: string[] }>(`/recipes/${r.id}`, r) : await http.post<{ recipe: Recipe; warnings: string[] }>('/recipes', r)).data,
  remove: async (id: number) => http.delete(`/recipes/${id}`),
  versions: async (id: number) => (await http.get<RecipeVersion[]>(`/recipes/${id}/versions`)).data,
  compare: async (left: number, leftVersion: number | null, right: string, rightVersion: number | null) =>
    (await http.get<RecipeCompare>('/recipes/compare', { params: { left, leftVersion: leftVersion ?? undefined, right, rightVersion: rightVersion ?? undefined } })).data,
  upload: async (templateId: number, recipeId: number | null, name: string | null, note: string | null) =>
    (await http.post<{ recipe: Recipe; warnings: string[] }>('/recipes/upload', { templateId, recipeId, name, note })).data,
  check: async (id: number, version: number) => (await http.post<RecipeCheck>(`/recipes/${id}/check`, { version })).data,
  download: async (id: number, version: number) => (await http.post<RecipeDownload>(`/recipes/${id}/download`, { version, confirmed: true })).data,
  downloads: async (recipeId?: number) => (await http.get<RecipeDownload[]>('/recipes/downloads', { params: { recipeId } })).data,
  abort: async (id: number) => http.post(`/recipes/downloads/${id}/abort`)
}
