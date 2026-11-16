import { beforeEach, describe, expect, it, vi } from 'vitest'
const { get } = vi.hoisted(() => ({ get: vi.fn() }))
vi.mock('./api/http', () => ({ http: { get } }))
vi.mock('./i18n', () => ({ tr: (key: string) => key }))
import { defaultOptions, ensureProtocolCatalog, parseOptions, protocolOptionsComplete, protocolCatalogError, resetProtocolCatalog, textProtocols } from './protocols'
const catalog = [{ value: 'BacnetIp', defaultPort: 47808, hostPort: true, credentials: false, fields: [{ key: 'deviceInstance', type: 'number' as const, default: -1, min: 0, max: 4194302 }], addressPlaceholder: '' }]
beforeEach(() => { resetProtocolCatalog(); get.mockReset() })
describe('protocol catalog recovery', () => {
  it('makes failure visible, then retries to a complete catalog', async () => {
    get.mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce({ data: catalog })
    await expect(ensureProtocolCatalog()).rejects.toThrow('offline')
    expect(protocolCatalogError.value).toBe(true)
    expect(textProtocols.value).toEqual([])
    await ensureProtocolCatalog()
    expect(protocolCatalogError.value).toBe(false)
    expect(textProtocols.value[0]?.value).toBe('BacnetIp')
  })
  it('rejects empty success responses instead of enabling incomplete forms', async () => {
    get.mockResolvedValueOnce({ data: [] })
    await expect(ensureProtocolCatalog()).rejects.toThrow('Empty protocol catalog')
    expect(protocolCatalogError.value).toBe(true)
  })
  it('does not accept a response from a previous session', async () => {
    let resolve!: (value: { data: typeof catalog }) => void
    get.mockReturnValueOnce(new Promise(r => { resolve = r }))
    const pending = ensureProtocolCatalog()
    resetProtocolCatalog()
    resolve({ data: catalog }); await pending
    expect(textProtocols.value).toEqual([])
  })
  it('leaves required instance blank, preserving an explicit zero', async () => {
    get.mockResolvedValueOnce({ data: catalog }); await ensureProtocolCatalog()
    const def = textProtocols.value[0]!
    expect(defaultOptions(def)).not.toHaveProperty('deviceInstance')
    expect(protocolOptionsComplete(def, {})).toBe(false)
    expect(protocolOptionsComplete(def, { deviceInstance: 0 })).toBe(true)
    expect(protocolOptionsComplete(def, { deviceInstance: 0.5 })).toBe(false)
    expect(parseOptions(def, '{"deviceInstance":0}').deviceInstance).toBe(0)
  })
})
