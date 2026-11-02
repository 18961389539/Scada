import { http } from './http'

export type LicenseStatus = 'Trial' | 'Active' | 'ExpiringSoon' | 'Expired' | 'Invalid' | 'Tampered'

export interface LicenseSnapshot {
  status: LicenseStatus
  machineId: string
  message: string
  licenseId: string | null
  customer: string | null
  expiresAt: string | null
  daysRemaining: number
  maxDevices: number
  maxTags: number
  features: string[]
  trialEndsAt: string | null
  trialDays: number
  blocked: boolean
}

export interface LicensePreview {
  licenseId: string
  customer: string
  expiresAt: string
  daysRemaining: number
  maxDevices: number
  maxTags: number
  features: string[]
  devices: number
  tags: number
}

export const licenseApi = {
  get: async (): Promise<LicenseSnapshot> => (await http.get<LicenseSnapshot>('/license')).data,
  preview: async (code: string): Promise<LicensePreview> => (await http.post<LicensePreview>('/license/preview', { code })).data,
  activate: async (code: string): Promise<LicenseSnapshot> =>
    (await http.post<LicenseSnapshot>('/license/activate', { code })).data
}
