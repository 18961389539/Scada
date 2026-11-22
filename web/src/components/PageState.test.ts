import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'

const source = readFileSync('src/components/PageState.vue', 'utf8')

describe('PageState 四态契约', () => {
  it('覆盖加载 / 空 / 错误 / 无权限四种状态', () => {
    for (const state of ['loading', 'empty', 'error', 'forbidden']) {
      expect(source).toContain(`state === '${state}'`)
      expect(source).toContain('page-state-${state}')
    }
  })

  it('状态变化会向读屏播报，错误态用 assertive', () => {
    expect(source).toContain('role="status"')
    expect(source).toContain("state === 'error' ? 'assertive' : 'polite'")
  })

  it('错误态按 retryText 决定是否渲染重试，并发出 retry 事件', () => {
    expect(source).toContain('v-if="retryText"')
    expect(source).toContain("emit('retry')")
  })

  it('不引入硬编码颜色，沿用设计 token', () => {
    expect(source).not.toMatch(/#[0-9a-fA-F]{6}/)
    expect(source).not.toMatch(/rgb\(/)
  })
})