#!/usr/bin/env node
// 覆盖率门禁：把 dotnet test 产出的多份 cobertura 按程序集汇总，并与基线比较。
//
// 为什么需要它：测试项目已接入覆盖率采集，但报告如果只作为 artifact 上传，没有人看，
// 也没有任何东西阻止覆盖率下滑。基线文件不存在时只打印汇总（首次接入），
// 存在时按程序集逐个比对，低于阈值即失败。
//
// 用法：
//   node tools/coverage/coverage-gate.mjs --dir TestResults
//   node tools/coverage/coverage-gate.mjs --dir TestResults --update   # 重写基线
//   node tools/coverage/coverage-gate.mjs --check-ratchet --base-ref origin/main
//     # 棘轮：基线相对 base 提交下降时，必须在 coverage-drops.json 里登记 from/to/reason。
//     # 否则「门禁只比不低于基线」，谁都能顺手把基线改低让 CI 变绿。
import { readdirSync, readFileSync, statSync, writeFileSync, existsSync, mkdirSync } from 'node:fs'
import { execFileSync } from 'node:child_process'
import { join, dirname, resolve } from 'node:path'

const args = process.argv.slice(2)
const argOf = (name, fallback) => {
  const i = args.indexOf(`--${name}`)
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback
}
const root = resolve(argOf('dir', 'TestResults'))
const baselinePath = resolve(argOf('baseline', 'tools/coverage/coverage-baseline.json'))
const dropsPath = resolve(argOf('drops', 'tools/coverage/coverage-drops.json'))
const update = args.includes('--update')

const readBaseline = (text, label) => {
  try { return JSON.parse(text) }
  catch (e) { throw new Error(`${label} 不是合法 JSON：${e.message}`) }
}
const readDrops = () => existsSync(dropsPath) ? readBaseline(readFileSync(dropsPath, 'utf8'), dropsPath) : {}
const dropIsDocumented = (drops, name, from, to) => {
  const d = drops?.[name]
  return d && Math.abs(Number(d.from) - from) < 0.001 && Math.abs(Number(d.to) - to) < 0.001
    && typeof d.reason === 'string' && d.reason.trim().length >= 8
}
const dropHint = (name, from, to) =>
  `  "${name}": { "from": ${from}, "to": ${to}, "reason": "<说明为何允许下降>" }`

// 棘轮检查独立于覆盖率采集，可以单独跑（CI 在 dotnet test 之前执行）。
if (args.includes('--check-ratchet')) {
  const baseRef = argOf('base-ref', process.env.SCADA_BASELINE_REF || 'origin/main')
  const current = readBaseline(readFileSync(baselinePath, 'utf8'), baselinePath)
  let previous
  try {
    previous = readBaseline(
      execFileSync('git', ['show', `${baseRef}:tools/coverage/coverage-baseline.json`], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }),
      `${baseRef}:tools/coverage/coverage-baseline.json`)
  } catch (e) {
    console.error(`[coverage] 无法读取基线提交 ${baseRef}：${e.message}`)
    process.exit(1)
  }
  const drops = readDrops()
  const bad = []
  for (const [name, floor] of Object.entries(previous)) {
    const now = current[name]
    if (now === undefined) { bad.push(`${name} 的基线被删除`); continue }
    if (Number(now) + 0.05 >= Number(floor)) continue
    if (!dropIsDocumented(drops, name, Number(floor), Number(now)))
      bad.push(`${name} ${floor}% → ${now}% 下降未在 ${dropsPath} 登记原因：\n${dropHint(name, floor, now)}`)
  }
  if (bad.length > 0) {
    console.error('[coverage] 覆盖率基线棘轮失败：\n' + bad.map(x => '  - ' + x).join('\n'))
    process.exit(1)
  }
  console.log(`[coverage] 基线棘轮通过（对比 ${baseRef}，${Object.keys(previous).length} 个程序集，无未说明的下降）`)
  process.exit(0)
}

function walk(dir, out = []) {
  if (!existsSync(dir)) return out
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry)
    if (statSync(full).isDirectory()) walk(full, out)
    else if (entry === 'coverage.cobertura.xml') out.push(full)
  }
  return out
}

// 不同测试项目的 cobertura 对同一文件可能写绝对路径（D:\...\src\Scada.Core\...）
// 或相对路径（Scada.Core\...）。统一收敛到「程序集/文件内路径」，行级并集才能对上。
function canonicalFile(filename) {
  const normalized = filename.replace(/\\/g, '/')
  return /(?:^|\/)(Scada\.[A-Za-z0-9.]+)\/(.+)$/.exec(normalized)?.[0].replace(/^\//, '') ?? normalized
}

// 生成代码（obj/ 下的 SourceGenerator 产物、*.g.cs）不是产品源码，不参与覆盖率分母。
function isProductSource(file) {
  return !/\/obj\//i.test(file) && !/\.(g|generated)\.cs$/i.test(file)
}

// coverlet 的 cobertura 里 <package> 只有 name / line-rate / branch-rate / complexity，
// 没有 lines-valid / lines-covered（那两个在根 <coverage> 上，是全量合计）。
// 所以按程序集的行数只能自己数 <line number hits>，不能用属性。
function parsePackages(xml) {
  const list = []
  for (const block of xml.split(/<package\s+/).slice(1)) {
    const head = block.slice(0, block.indexOf('>'))
    const name = /name="([^"]+)"/.exec(head)?.[1]
    if (!name) continue
    const body = block.split('</package>')[0]
    // 行级并集：key = 规范化文件路径 + 行号，value = 该行是否被任一测试项目覆盖。
    const lines = new Map()
    for (const cls of body.matchAll(/<class\b[^>]*\bfilename="([^"]+)"[^>]*>([\s\S]*?)<\/class>/g)) {
      const file = canonicalFile(cls[1])
      if (!isProductSource(file)) continue
      for (const line of cls[2].matchAll(/<line\b[^>]*>/g)) {
        const number = /\bnumber="(\d+)"/.exec(line[0])?.[1]
        if (!number) continue
        const hits = Number(/\bhits="(\d+)"/.exec(line[0])?.[1] ?? 0)
        const key = `${file}:${number}`
        lines.set(key, (lines.get(key) ?? false) || hits > 0)
      }
    }
    list.push({
      name,
      // 退化情况：没有逐行信息时退回 package 自己的 line-rate，不参与行级并集。
      rate: Number(/line-rate="([\d.]+)"/.exec(head)?.[1] ?? 0),
      lines
    })
  }
  return list
}

const files = walk(root)
// 同一程序集可能出现在多个包的 cobertura 里（不同测试项目都会加载它）。
// 按 file+line 做并集：Server 套件顺带覆盖到的 Engine 行，与 Engine 套件自己的覆盖合并，
// 不再因为「单份报告取最大」而把新增用例的收益盖掉。
const byAssembly = new Map()
for (const file of files) {
  for (const entry of parsePackages(readFileSync(file, 'utf8'))) {
    const prev = byAssembly.get(entry.name)
    if (!prev) {
      byAssembly.set(entry.name, { name: entry.name, rate: entry.rate, lines: new Map(entry.lines) })
      continue
    }
    for (const [key, covered] of entry.lines) prev.lines.set(key, (prev.lines.get(key) ?? false) || covered)
    if (entry.rate > prev.rate) prev.rate = entry.rate
  }
}

if (files.length === 0) {
  if (process.env.SCADA_COVERAGE_ALLOW_MISSING === '1') {
    console.log(`[coverage] ${root} 下没有 cobertura 报告，已按 SCADA_COVERAGE_ALLOW_MISSING 跳过`)
    process.exit(0)
  }
  console.error(`[coverage] ${root} 下没有 cobertura 报告：门禁不能静默通过。本地调试可设 SCADA_COVERAGE_ALLOW_MISSING=1`)
  process.exit(1)
}

// 报告在但一条都解析不出来，说明格式变了或解析写错了。这里必须失败：
// 静默通过等于门禁悄悄失效。
if (byAssembly.size === 0) {
  console.error(`[coverage] 找到 ${files.length} 份报告但未解析出任何程序集，请检查 cobertura 格式`)
  process.exit(1)
}

const baseline = existsSync(baselinePath) ? JSON.parse(readFileSync(baselinePath, 'utf8')) : null
const rows = [...byAssembly.values()]
  // 只守产品程序集：测试程序集与第三方依赖（RBush、协议库等）不进基线。
  .filter((x) => x.name.startsWith('Scada.') && !x.name.endsWith('.Tests'))
  .sort((a, b) => a.name.localeCompare(b.name))

const missing = baseline ? Object.keys(baseline).filter(name => !rows.some(row => row.name === name)) : []
if (missing.length > 0) {
  console.error(`[coverage] 基线中的程序集在本次报告里整份缺失：${missing.join('、')}。请检查测试项目清单、采集器与 artifact 合并，门禁不能静默放行。`)
  process.exit(1)
}

const pad = (v, n) => String(v).padEnd(n)
console.log(pad('程序集', 28) + pad('行覆盖率', 10) + pad('行数', 10) + '基线')
const failures = []
const next = {}
for (const row of rows) {
  const valid = row.lines.size
  const covered = [...row.lines.values()].filter(Boolean).length
  const pct = valid === 0 ? row.rate * 100 : (covered / valid) * 100
  next[row.name] = Number(pct.toFixed(2))
  const floor = baseline?.[row.name]
  const mark = floor === undefined ? '（新）' : pct + 0.05 < floor ? `!! ${floor}%` : `${floor}%`
  if (floor !== undefined && pct + 0.05 < floor) failures.push(`${row.name} ${pct.toFixed(2)}% < ${floor}%`)
  console.log(pad(row.name, 28) + pad(pct.toFixed(2) + '%', 10) + pad(valid, 10) + mark)
}

if (update) {
  // 重写基线也不能顺手把门槛调低：stat 口径变化这类合理下降必须先在 drops 文件里写明原因。
  const drops = readDrops()
  const undocumented = baseline
    ? Object.entries(baseline)
        .filter(([name, floor]) => next[name] !== undefined && Number(next[name]) + 0.05 < Number(floor))
        .filter(([name, floor]) => !dropIsDocumented(drops, name, Number(floor), next[name]))
        .map(([name, floor]) => `${name} ${floor}% → ${next[name]}%：\n${dropHint(name, floor, next[name])}`)
    : []
  if (undocumented.length > 0) {
    console.error(`[coverage] 拒绝重写基线，以下下降未在 ${dropsPath} 登记原因：\n` +
      undocumented.map(x => '  - ' + x).join('\n'))
    process.exit(1)
  }
  mkdirSync(dirname(baselinePath), { recursive: true })
  writeFileSync(baselinePath, JSON.stringify(next, null, 2) + '\n')
  console.log(`[coverage] 已写入基线 ${baselinePath}`)
  process.exit(0)
}

if (!baseline) {
  console.error('[coverage] 缺少基线文件，门禁无法比较；确认数值后运行 --update 生成')
  process.exit(1)
}

if (failures.length > 0) {
  console.error('\n[coverage] 覆盖率低于基线：')
  for (const f of failures) console.error('  - ' + f)
  process.exit(1)
}
console.log('\n[coverage] 全部不低于基线')
