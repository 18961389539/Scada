import { http } from './http'
import type { AlarmRule, Tag } from '../types'
import type { ScreenDocument } from '../screens/model'
export interface ObjectTemplateDocument {
  schemaVersion: number
  tags: { key: string; addressOffset: number; properties: Tag }[]
  alarms: { key: string; tagKey: string; properties: AlarmRule }[]
  panel: ScreenDocument; bindings: Record<string, string>; actionBindings: Record<string, string>
}
export interface TemplateSummary { id: number; name: string; revision: number; latestVersion: number; instanceCount: number; updatedTs: number; updatedBy: string }
export interface TemplateDetail extends TemplateSummary { document: ObjectTemplateDocument; versions: { version: number; createdTs: number; createdBy: string }[] }
export interface ObjectInstance { id: number; name: string; templateId: number; templateName: string; appliedVersion: number; latestVersion: number; deviceId: number; deviceName: string; baseAddress: number; screenId: number; revision: number; updatedTs: number }
export interface InstanceParameters { name: string; deviceId: number; baseAddress: number }
export interface Conflict { key: string; label: string; baseline: string; local: string; incoming: string; choices: ('keepLocal' | 'useTemplate')[] }
export interface ObjectPlan { instanceId: number | null; name: string; version: number; revision: number; state: string; tagCount: number; alarmCount: number; panelNodeCount: number; changes: { key: string; action: string; detail: string }[]; conflicts: Conflict[]; errors: string[]; canApply: boolean }
export interface BatchPlan { plans: ObjectPlan[]; errors: string[]; canApply: boolean }
export interface UpgradeInput { id: number; version: number; revision: number; expectedState: string; resolutions: Record<string, string> }
export const objectTemplateApi = {
  list: async () => (await http.get<TemplateSummary[]>('/object-templates')).data,
  get: async (id: number) => (await http.get<TemplateDetail>(`/object-templates/${id}`)).data,
  version: async (id: number, version: number) => (await http.get<{ version: number; document: ObjectTemplateDocument }>(`/object-templates/${id}/versions/${version}`)).data,
  pump: async () => (await http.get<ObjectTemplateDocument>('/object-templates/examples/pump')).data,
  create: async (name: string, document: ObjectTemplateDocument) => (await http.post<{ id: number; revision: number }>('/object-templates', { name, revision: 0, document })).data,
  save: async (id: number, name: string, revision: number, document: ObjectTemplateDocument) => (await http.put<{ revision: number }>(`/object-templates/${id}`, { name, revision, document })).data,
  validate: async (document: ObjectTemplateDocument) => (await http.post<{ errors: string[] }>('/object-templates/validate', document)).data.errors,
  publish: async (id: number, revision: number) => (await http.post<{ revision: number; latestVersion: number }>(`/object-templates/${id}/publish`, { revision })).data,
  remove: async (id: number, revision: number) => http.delete(`/object-templates/${id}`, { params: { revision } }),
  previewCreate: async (id: number, version: number, instances: InstanceParameters[]) => (await http.post<BatchPlan>(`/object-templates/${id}/instantiate/preview`, { version, instances })).data,
  instantiate: async (id: number, version: number, instances: InstanceParameters[]) => (await http.post<{ id: number; name: string; screenId: number }[]>(`/object-templates/${id}/instantiate`, { version, instances })).data,
  instances: async () => (await http.get<ObjectInstance[]>('/object-instances')).data,
  previewUpgrade: async (instance: ObjectInstance, version: number, resolutions: Record<string, string>) => (await http.post<ObjectPlan>(`/object-instances/${instance.id}/upgrade/preview`, { version, revision: instance.revision, resolutions })).data,
  upgrade: async (items: UpgradeInput[]) => (await http.post('/object-instances/upgrade', { items })).data,
  detach: async (id: number, revision: number) => http.delete(`/object-instances/${id}`, { params: { revision } })
}
