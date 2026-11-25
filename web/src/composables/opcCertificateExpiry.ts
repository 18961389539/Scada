export const CERT_EXPIRY_SOON_MS = 30 * 24 * 60 * 60 * 1000

export type CertificateExpiryKind = 'expired' | 'expiring' | 'ok'

export function certificateExpiry(notAfter: string, now = Date.now()): CertificateExpiryKind {
  const end = Date.parse(notAfter)
  if (!Number.isFinite(end)) return 'ok'
  if (end <= now) return 'expired'
  if (end - now <= CERT_EXPIRY_SOON_MS) return 'expiring'
  return 'ok'
}

export type ExpiryCert = { subject: string; notAfter: string; store: string }

export function summarizeCertificateExpiry(certs: ExpiryCert[], now = Date.now()) {
  const relevant = certs.filter(cert => cert.store === 'trusted' || cert.store === 'own')
  const expired = relevant.filter(cert => certificateExpiry(cert.notAfter, now) === 'expired')
  const expiring = relevant.filter(cert => certificateExpiry(cert.notAfter, now) === 'expiring')
  return { expired, expiring }
}
