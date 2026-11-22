import { describe, expect, it } from 'vitest'
import en from './locales/en-US'
import zh from './locales/zh-CN'

describe('control room layout naming', () => {
  it('uses 控制室布局 on the runtime side and 布局配置 on the setup side', () => {
    expect(zh.workbench.navTitle).toBe('控制室布局')
    expect(zh.workbench.runtimeTitle).toBe('控制室布局')
    expect(zh.workbench.configurationTitle).toBe('布局配置')
    expect(zh.workbench.configure).toBe('配置布局')
    expect(zh.workbench.create).toBe('快速创建布局')
    expect(zh.workbench.createFromScreen).toBe('用此画面创建布局')
    expect(zh.workbench.emptyTitle).toBe('暂无可运行的已发布控制室布局')
    expect(zh.workbench.workbenchName).toBe('布局名称')
    expect(zh.workbench.quickTitle).toBe('快速创建布局')
  })

  it('keeps English names aligned with the control-room layout wording', () => {
    expect(en.workbench.navTitle).toBe('Control room layout')
    expect(en.workbench.runtimeTitle).toBe('Control room layout')
    expect(en.workbench.configurationTitle).toBe('Layout setup')
    expect(en.workbench.configure).toBe('Set up layout')
    expect(en.workbench.create).toBe('Quick-create layout')
    expect(en.workbench.createFromScreen).toBe('Create layout from this screen')
    expect(en.workbench.emptyTitle).toBe('No published control room layout is available')
    expect(en.workbench.workbenchName).toBe('Layout name')
  })
})
