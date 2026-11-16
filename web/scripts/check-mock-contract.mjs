#!/usr/bin/env node
// e2e mock 契约门禁：spec 里按 URL 拦截的 mock 必须对应 src/api/contract.ts 中的真实路由。
// 背景：后端重命名或删除端点后，spec 仍按旧路径 fulfill，用例照样通过，真实页面却 404。
// 字段级校验不做静态推断（mock 数据多为变量/过滤结果），路由存在性是能稳定守住的底线。
import { readFileSync, readdirSync } from 'node:fs'
import { join, resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..')
const specDir = join(root, 'tests')
const contract = readFileSync(join(root, 'src/api/contract.ts'), 'utf8')

// contract.ts 的 paths 接口里，每条路由是 4 空格缩进的字符串键。
const templates = [...contract.matchAll(/^ {4}"(\/api\/[^"]+)": \{/gm)].map(match => match[1])
if (!templates.length) {
  console.error('[mock-contract] 未从 src/api/contract.ts 解析到任何 /api 路由')
  process.exit(1)
}

const asRegex = path => new RegExp(`^${path.split('/').map(segment => {
  if (/^\{[^}]+\}$/.test(segment)) return '[^/]+'
  return segment.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}).join('/')}$`)
const patterns = templates.map(path => ({ path, regex: asRegex(path) }))

const problems = []
let checked = 0
for (const file of readdirSync(specDir).filter(name => name.endsWith('.spec.ts'))) {
  const text = readFileSync(join(specDir, file), 'utf8')
  for (const match of text.matchAll(/url\.pathname === '([^']+)'/g)) {
    const path = match[1]
    if (!path.startsWith('/api/')) continue
    checked++
    if (!patterns.some(pattern => pattern.regex.test(path))) problems.push(`${file}: 未找到路由 ${path}`)
  }
  for (const match of text.matchAll(/url\.pathname\.startsWith\('([^']+)'\)/g)) {
    const prefix = match[1]
    if (!prefix.startsWith('/api/')) continue
    checked++
    if (!templates.some(path => path.startsWith(prefix))) problems.push(`${file}: 未找到前缀 ${prefix}`)
  }
}

if (problems.length) {
  console.error(`[mock-contract] ${problems.length} 处 e2e mock 指向不存在的路由：`)
  for (const problem of problems) console.error(`  ${problem}`)
  console.error('  如果后端路由确实改名，请同步更新 tests 下的 mock；若属新增端点，先更新 contract.ts。')
  process.exit(1)
}
console.log(`[mock-contract] ${checked} 处 e2e mock 路由均存在于 ${templates.length} 条契约路由中`)
