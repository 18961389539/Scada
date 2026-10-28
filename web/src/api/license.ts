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

export const licenseApi = {
  get: async (): Promise<LicenseSnapshot> => (await http.get<LicenseSnapshot>('/license')).data,
  activate: async (code: string): Promise<LicenseSnapshot> =>
    (await http.post<LicenseSnapshot>('/license/activate', { code })).data
}