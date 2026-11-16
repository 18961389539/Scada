import { describe, expect, it } from 'vitest'
import { builtinSymbols, sanitizeSvg, sanitizeSvgForRender } from './symbols'

const wrap = (inner: string) => `<svg xmlns="http://www.w3.org/2000/svg">${inner}</svg>`

describe('sanitizeSvg', () => {
  it('drops elements outside the whitelist', () => {
    const out = sanitizeSvg(wrap('<foreignObject><div>x</div></foreignObject><rect width="10" height="10"/>'))
    expect(out.toLowerCase()).not.toContain('foreignobject')
    expect(out).toContain('<rect')
  })

  it('strips event handler attributes but keeps the element', () => {
    const out = sanitizeSvg(wrap('<rect width="10" height="10" onload="alert(1)"/>'))
    expect(out.toLowerCase()).not.toContain('onload')
    expect(out).toContain('<rect')
  })

  it('strips javascript urls', () => {
    const out = sanitizeSvg(wrap('<rect width="10" height="10" fill="url(javascript:alert(1))"/>'))
    expect(out.toLowerCase()).not.toContain('javascript:')
    expect(out).toContain('<rect')
  })

  it('strips non-local references', () => {
    const out = sanitizeSvg(wrap('<path d="M0 0" href="https://evil.example/x.svg"/>'))
    expect(out).not.toContain('evil.example')
  })

  it('keeps local gradient references intact', () => {
    const out = sanitizeSvg(wrap('<defs><linearGradient id="g"><stop offset="0" stop-color="#fff"/></linearGradient></defs><rect width="10" height="10" fill="url(#g)"/>'))
    expect(out).toContain('url(#g)')
  })
})

describe('sanitizeSvgForRender', () => {
  it('returns null instead of throwing on non-svg input', () => {
    expect(sanitizeSvgForRender('<div>not svg</div>')).toBeNull()
    expect(sanitizeSvgForRender('')).toBeNull()
    expect(sanitizeSvgForRender(null)).toBeNull()
    expect(sanitizeSvgForRender(undefined)).toBeNull()
  })

  it('returns cleaned markup for a valid svg', () => {
    const out = sanitizeSvgForRender(wrap('<script>alert(1)</script><rect width="10" height="10"/>'))
    expect(out).not.toBeNull()
    expect(out!.toLowerCase()).not.toContain('script')
    expect(out).toContain('<rect')
  })

  it('keeps every builtin symbol renderable', () => {
    for (const s of builtinSymbols) {
      const safe = sanitizeSvgForRender(s.svg)
      expect(safe, s.key).not.toBeNull()
      expect(safe!, s.key).toContain('<svg')
    }
  })

  it('preserves viewBox and preserveAspectRatio casing after serialization', () => {
    const pipe = builtinSymbols.find(s => s.key === 'isa:pipe')!
    const safe = sanitizeSvgForRender(pipe.svg)!
    expect(safe).toContain('viewBox="0 0 100 20"')
    expect(safe).toContain('preserveAspectRatio="none"')
  })

  it('keeps a viewBox on every builtin symbol', () => {
    for (const s of builtinSymbols) expect(sanitizeSvgForRender(s.svg)!, s.key).toMatch(/viewBox="[^"]+"/)
  })
})