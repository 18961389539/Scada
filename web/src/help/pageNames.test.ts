import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import en from './en.json'
import zh from './zh.json'

const pageName = /现场操作(?!员)/
const enPageName = /Field control/i

describe('help page names', () => {
  it('uses 操作许可 / Operating permits as the control page title', () => {
    expect(zh.control.title).toBe('操作许可')
    expect(en.control.title).toBe('Operating permits')
  })

  it('updates help body paths to 操作许可 → 待复核 without renaming 现场操作员', () => {
    expect(zh.control.body).not.toMatch(pageName)
    expect(zh.recipes.body).not.toMatch(pageName)
    expect(zh.control.body).toContain('操作许可')
    expect(zh.recipes.body).toContain('与操作许可共用同一套控制闭环')
    expect(zh.recipes.body).toContain('操作许可 → 待复核')
    expect(zh.screens.body).toContain('现场操作员')
    expect(zh.screenRun.body).toContain('现场操作员')
    expect(en.control.body).not.toMatch(enPageName)
    expect(en.recipes.body).not.toMatch(enPageName)
    expect(en.recipes.body).toContain('Operating permits → Pending approval')
  })
})

describe('i18n glossary page names', () => {
  it('no longer stores the retired 现场操作 / Field control page name', () => {
    const glossary = readFileSync('scripts/i18n-glossary.json', 'utf8')
    expect(glossary).not.toMatch(pageName)
    expect(glossary).not.toMatch(enPageName)
    expect(glossary).toContain('操作许可')
    expect(glossary).toContain('Operating permits')
  })
})
