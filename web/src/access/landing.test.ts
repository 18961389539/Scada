import { describe, expect, it } from 'vitest'
import { defaultLandingPath } from './policies'
import { resolveLandingPath } from './landing'

const operator = { role: 'Operator' as const }
const viewer = { role: 'Viewer' as const }
const engineer = { role: 'None' as const, access: { global: [], areas: [{ areaId: 7, permissions: ['View' as const, 'Configure' as const] }] } }

describe('resolveLandingPath', () => {
  it('keeps engineering accounts on the overview', async () => {
    expect(defaultLandingPath(engineer)).toBe('/overview')
    expect(await resolveLandingPath(engineer, async () => true)).toBe('/overview')
  })

  it('sends operators and viewers to the console when a workstation or published layout exists', async () => {
    expect(await resolveLandingPath(operator, () => true)).toBe('/console')
    expect(await resolveLandingPath(viewer, async () => true)).toBe('/console')
  })

  it('falls back to the screen catalog when no runtime layout is available', async () => {
    expect(await resolveLandingPath(operator, () => false)).toBe('/screens')
  })

  it('falls back to the screen catalog when the layout check fails', async () => {
    expect(await resolveLandingPath(operator, async () => { throw new Error('unavailable') })).toBe('/screens')
  })
})
