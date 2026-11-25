import { describe, expect, it } from 'vitest'
import { helpKeyForPath, helpPathForKey } from './pages'
import en from './en.json'
import zh from './zh.json'

describe('help pages for handover, workspace and channels', () => {
  it('maps the three paths to help keys in both directions', () => {
    expect(helpKeyForPath('/handover')).toBe('handover')
    expect(helpKeyForPath('/workspace')).toBe('workspace')
    expect(helpKeyForPath('/config/channels')).toBe('channels')
    expect(helpPathForKey('handover')).toBe('/handover')
    expect(helpPathForKey('workspace')).toBe('/workspace')
    expect(helpPathForKey('channels')).toBe('/config/channels')
  })

  it('has Chinese and English titles and bodies', () => {
    for (const key of ['handover', 'workspace', 'channels'] as const) {
      expect(zh[key].title).toBeTruthy()
      expect(zh[key].body).toContain('■ 这个页面是做什么的')
      expect(en[key].title).toBeTruthy()
      expect(en[key].body).toContain('■ What this page is for')
    }
    expect(zh.handover.title).toBe('值班摘要')
    expect(zh.workspace.title).toBe('多标签监视')
    expect(zh.channels.title).toBe('通道')
    expect(en.handover.title).toBe('Shift summary')
    expect(en.workspace.title).toBe('Multi-tab monitor')
    expect(en.channels.title).toBe('Channels')
  })
})
