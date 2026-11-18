import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const source = readFileSync('src/components/CommandReceipt.vue', 'utf8')

describe('CommandReceipt theme tokens', () => {
  it('uses theme text tokens instead of light-theme hex colors', () => {
    expect(source).toContain('var(--scada-text)')
    expect(source).toContain('var(--scada-text-secondary)')
    expect(source).not.toContain('#1e293b')
    expect(source).not.toContain('#334155')
  })
})
