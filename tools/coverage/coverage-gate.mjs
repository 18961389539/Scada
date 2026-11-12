#!/usr/bin/env node
// 覆盖率门禁：把 dotnet test 产出的多份 cobertura 按程序集汇总，并与基线比较。
//
// 为什么需要它：CI 从 11 月起四个测试项目都带 coverlet.collector，但报告只是作为
// artifact 上传，没有人看，也没有任何东西阻止覆盖率下滑。基线文件不存在时只打印
// 汇总（首次接入），存在时按程序集逐个比对，低于阈值即失败。
//
// 用法：
//   node tools/coverage/coverage-gate.mjs --dir TestResults
//   node tools/coverage/coverage-gate.mjs --dir TestResults --update   # 重写基线
import { readdirSync, readFileSync, statSync, writeFileSync, existsSync, mkdirSync } from 'node:fs'
import { join, dirname, resolve } from 'node:path'

const args = process.argv.slice(2)
const argOf = (name, fallback) => {
  const i = args.indexOf(`--${name}`)
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback
}
const root = resolve(argOf('dir', 'TestResults'))
const baselinePath = resolve(argOf('baseline', 'tools/coverage/coverage-baseline.json'))
const update = args.includes('--update')

function walk(dir, out = []) {
  if (!existsSync(dir)) return out
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry)
    if (statSync(full).isDirectory()) walk(full, out)
    else if (entry === 'coverage.cobertura.xml') out.push(full)
  }
  return out
}

// coverlet 的 cobertura 里 <package> 只有 name / line-rate / branch-rate / complexity，
// 没有 lines-valid / lines-covered（那两个在根 <coverage> 上，是全量合计）。
// 所以按程序集的行数只能自己数 <line hits="...">，不能用属性。
function parsePackages(xml) {
  const list = []
  for (const block of xml.split(/<package\s+/).slice(1)) {
    const head = block.slice(0, block.indexOf('>'))
    const name = /name="([^"]+)"/.exec(head)?.[1]
    if (!name) continue
    const body = block.split('</package>')[0]
    const lines = [...body.matchAll(/<line\b[^>]*\bhits="(\d+)"/g)]
    if (lines.length === 0) {
      // 退化情况：没有逐行信息时退回 package 自己的 line-rate，行数记 0，不参与「取最大」比较。
      const rate = Number(/line-rate="([\d.]+)"/.exec(head)?.[1] ?? 0)
      list.push({ name, rate, valid: 0, covered: 0 })
      continue
    }
    const covered = lines.filter((m) => Number(m[1]) > 0).length
    list.push({ name, rate: covered / lines.length, valid: lines.length, covered })
  }
  return list
}

const files = walk(root)
// 同一程序集可能出现在多个包的 cobertura 里，取「行数最多」的那份代表它：
// 部分包只覆盖到少数类，行数少的报告会把整体覆盖率算低。
const byAssembly = new Map()
for (const file of files) {
  for (const entry of parsePackages(readFileSync(file, 'utf8'))) {
    const prev = byAssembly.get(entry.name)
    if (!prev || entry.valid > prev.valid) byAssembly.set(entry.name, entry)
  }
}

if (files.length === 0) {
  console.log(`[coverage] ${root} 下没有 cobertura 报告（未启用收集器？），跳过`)
  process.exit(0)
}

// 报告在但一条都解析不出来，说明格式变了或解析写错了。这里必须失败：
// 静默通过等于门禁悄悄失效。
if (byAssembly.size === 0) {
  console.error(`[coverage] 找到 ${files.length} 份报告但未解析出任何程序集，请检查 cobertura 格式`)
  process.exit(1)
}

const baseline = existsSync(baselinePath) ? JSON.parse(readFileSync(baselinePath, 'utf8')) : null
const rows = [...byAssembly.values()]
  .filter((x) => !x.name.startsWith('Scada.Tests'))
  .sort((a, b) => a.name.localeCompare(b.name))

const pad = (v, n) => String(v).padEnd(n)
console.log(pad('程序集', 28) + pad('行覆盖率', 10) + pad('行数', 10) + '基线')
const failures = []
const next = {}
for (const row of rows) {
  const pct = row.valid === 0 ? row.rate * 100 : (row.covered / row.valid) * 100
  next[row.name] = Number(pct.toFixed(2))
  const floor = baseline?.[row.name]
  const mark = floor === undefined ? '（新）' : pct + 0.05 < floor ? `!! ${floor}%` : `${floor}%`
  if (floor !== undefined && pct + 0.05 < floor) failures.push(`${row.name} ${pct.toFixed(2)}% < ${floor}%`)
  console.log(pad(row.name, 28) + pad(pct.toFixed(2) + '%', 10) + pad(row.valid, 10) + mark)
}

if (update) {
  mkdirSync(dirname(baselinePath), { recursive: true })
  writeFileSync(baselinePath, JSON.stringify(next, null, 2) + '\n')
  console.log(`[coverage] 已写入基线 ${baselinePath}`)
  process.exit(0)
}

if (!baseline) {
  console.log('[coverage] 尚无基线文件，只报告不拦截；确认数值后运行 --update 生成')
  process.exit(0)
}

if (failures.length > 0) {
  console.error('\n[coverage] 覆盖率低于基线：')
  for (const f of failures) console.error('  - ' + f)
  process.exit(1)
}
console.log('\n[coverage] 全部不低于基线')
