import { describe, expect, it } from 'vitest'
import { certificateExpiry, summarizeCertificateExpiry } from './opcCertificateExpiry'

describe('opcCertificateExpiry', () => {
  const now = Date.parse('2026-11-25T06:00:00Z')

  it('marks expired and soon-to-expire trusted or own certificates', () => {
    expect(certificateExpiry('2026-11-24T00:00:00Z', now)).toBe('expired')
    expect(certificateExpiry('2026-12-10T00:00:00Z', now)).toBe('expiring')
    expect(certificateExpiry('2027-11-25T00:00:00Z', now)).toBe('ok')
  })

  it('ignores rejected certificates when summarizing', () => {
    const summary = summarizeCertificateExpiry([
      { subject: 'old', notAfter: '2026-01-01T00:00:00Z', store: 'trusted' },
      { subject: 'soon', notAfter: '2026-12-01T00:00:00Z', store: 'own' },
      { subject: 'rejected-old', notAfter: '2026-01-01T00:00:00Z', store: 'rejected' }
    ], now)
    expect(summary.expired.map(x => x.subject)).toEqual(['old'])
    expect(summary.expiring.map(x => x.subject)).toEqual(['soon'])
  })
})
