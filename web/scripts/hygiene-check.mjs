#!/usr/bin/env node
// 轻量卫生检查：不引入 lint 依赖，只守住几条“只会单向变坏”的指标。
// 阈值是棘轮：可以往下调，往上调必须在 PR 里说明原因。
// 等 ESLint 依赖可以正常安装后，这个脚本由 eslint 接管（规则见 docs/quality/full-review-2026-11.md）。
import { readdirSync, readFileSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../src', import.meta.url))

// 当前实测值：30 处 any（集中在 ScreenPropertiesPanel / AlarmAnalyticsView），0 处 console.log。
const BUDGET = { any: 30, consoleLog: 0 }

const files = []
const walk = (dir) => {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name)
    if (statSync(path).isDirectory()) walk(path)
    else if (/\.(vue|ts)$/.test(name) && !name.endsWith('.d.ts')) files.push(path)
  }
}
walk(root)

let any = 0
let consoleLog = 0
const offenders = []
for (const file of files) {
  const source = readFileSync(file, 'utf8')
  const anyHits = source.match(/:\s*any\b|\bas any\b|<any>/g) ?? []
  const logHits = source.match(/console\.(log|debug)\(/g) ?? []
  if (anyHits.length) offenders.push([anyHits.length, 'any', relative(root, file)])
  any += anyHits.length
  consoleLog += logHits.length
}

const problems = []
if (any > BUDGET.any) problems.push(`any: ${any} > ${BUDGET.any}`)
if (consoleLog > BUDGET.consoleLog) problems.push(`console.log: ${consoleLog} > ${BUDGET.consoleLog}`)
if (problems.length) {
  console.error('卫生检查未通过：' + problems.join('；'))
  console.error('超出定额的文件：')
  for (const [count, kind, file] of offenders.sort((a, b) => b[0] - a[0]).slice(0, 10)) {
    console.error(`  ${String(count).padStart(3)} ${kind}  ${file}`)
  }
  console.error('\n要么改掉新增的用法，要么在 scripts/hygiene-check.mjs 里写明为什么放宽定额。')
  process.exit(1)
}

console.log(`hygiene ok: any=${any}/${BUDGET.any}, console.log=${consoleLog}/${BUDGET.consoleLog}, ${files.length} files`)
