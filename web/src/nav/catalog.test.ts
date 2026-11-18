import { describe, expect, it } from 'vitest'
import zhJson from '../i18n/locales/zh-CN.json'
import enJson from '../i18n/locales/en-US.json'
import zhFlow from '../i18n/locales/zh-CN'
import enFlow from '../i18n/locales/en-US'
import { NAV_ITEMS } from './catalog'

describe('navigation labels', () => {
  it('names the three screen paths distinctly, and uses permit wording for lockouts', () => {
    expect(NAV_ITEMS.find(item => item.id === 'screens')?.titleKey).toBe('navMenu.k8a1840')
    expect(NAV_ITEMS.find(item => item.id === 'workspace')?.titleKey).toBe('workspace.title')
    expect(NAV_ITEMS.find(item => item.id === 'console')?.titleKey).toBe('workbench.navTitle')
    expect(NAV_ITEMS.find(item => item.id === 'control')?.titleKey).toBe('navMenu.k4eb87f')

    expect(enJson.navMenu.k8a1840).toBe('Screen catalog')
    expect(enJson.workspace.title).toBe('Multi-tab monitor')
    expect(enFlow.workbench.navTitle).toBe('Control room layout')
    expect(enJson.navMenu.k4eb87f).toBe('Operating permits')

    expect(zhJson.navMenu.k8a1840).not.toBe(zhJson.index.k8a1840)
    expect(zhJson.workspace.title).not.toBe(zhFlow.workbench.runtimeTitle)
    expect(zhFlow.workbench.navTitle).not.toBe(zhFlow.workbench.runtimeTitle)
    expect(zhJson.navMenu.k4eb87f).not.toBe(zhJson.index.k8a1840)
    expect(zhJson.navMenu.k4eb87f).toBeTruthy()
    expect(zhFlow.workbench.navTitle).toBeTruthy()
  })
})
