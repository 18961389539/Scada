import { test, expect, type Page } from './fixtures'
import { writeFileSync } from 'node:fs'

/** 英文界面巡检：配置管理 / 报表 / 生产管理页面不应出现未翻译的中文文案。 */
const PAGES = [
  '/config/devices', '/config/gateways', '/config/hmi-library', '/config/layouts',
  '/config/object-templates', '/config/tags', '/config/project', '/config/calculations',
  '/config/oee', '/config/rules', '/config/notifications', '/config/annunciators',
  '/config/external-api', '/reports', '/report-center', '/recipes', '/traceability',
  '/oee', '/calc-jobs'
]

async function signIn(page: Page): Promise<string[]> {
  const response = await page.request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'en-US')
  }, auth)
  // 用户显示名与角色名属于业务数据，不参与界面文案检查。
  return [auth.user?.displayName, auth.user?.roleName].filter((value: unknown): value is string => typeof value === 'string' && value.length > 0)
}

interface I18nFinding {
  path: string
  text: string
}

test('english ui leaves no untranslated chinese copy', async ({ page }, testInfo) => {
  test.setTimeout(180000)
  const dataValues = await signIn(page)
  const findings: I18nFinding[] = []
  for (const path of PAGES) {
    await page.goto(path, { waitUntil: 'domcontentloaded' })
    await page.waitForTimeout(500)
    // 只采集界面静态文案；表格行、标签、输入回显与下拉项属于业务数据，可能本来就是中文。
    const lines = await page.evaluate(() => {
      const skip = 'table, .el-table, .el-tag, .el-descriptions, .el-select-dropdown, .el-pagination, .el-tree, .el-input__inner, .el-textarea__inner, .el-select__selected-item'
      const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT)
      const out: string[] = []
      while (walker.nextNode()) {
        const node = walker.currentNode
        const text = node.textContent?.trim() ?? ''
        if (!text) continue
        const element = node.parentElement
        if (!element) continue
        const style = getComputedStyle(element)
        if (style.display === 'none' || style.visibility === 'hidden') continue
        if (element.closest(skip)) continue
        out.push(text)
      }
      return out
    })
    for (const line of lines) {
      // 语言名称按惯例始终以自身语言显示。
      const isLanguageName = line === '简体中文'
      if (/[\u4e00-\u9fa5]/.test(line) && !isLanguageName && !dataValues.some(value => line.includes(value))) findings.push({ path, text: line.slice(0, 160) })
    }
  }
  writeFileSync('test-results/ui-i18n-audit.json', JSON.stringify(findings, null, 2))
  await testInfo.attach('ui-i18n-audit.json', { body: JSON.stringify(findings, null, 2), contentType: 'application/json' })
  const unique = [...new Map(findings.map(item => [`${item.path}|${item.text}`, item])).values()]
  expect(unique, JSON.stringify(unique, null, 2)).toHaveLength(0)
})