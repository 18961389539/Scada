#!/usr/bin/env node
// CI 测试清单门禁：解决方案里的每个测试项目必须恰好出现在一个覆盖率分片里。
import { readFileSync, existsSync } from 'node:fs'
import { resolve } from 'node:path'

const root = resolve(import.meta.dirname, '..', '..')
const solution = readFileSync(resolve(root, 'Scada.sln'), 'utf8')
const workflow = readFileSync(resolve(root, '.github', 'workflows', 'ci.yml'), 'utf8')

const solutionProjects = [...solution.matchAll(/tests\\([^"]+?)\.csproj/g)]
  .map(match => match[1].split('\\').at(-1))
const lists = [...workflow.matchAll(/foreach \(\$project in @\(([^)]*)\)\)/g)]
  .map(match => [...match[1].matchAll(/"([^"]+)"/g)].map(item => item[1]))

const problems = []
if (lists.length !== 2) problems.push(`预期 2 份 CI 测试项目清单，实际 ${lists.length} 份`)
const listed = lists.flat()
const duplicates = listed.filter((name, index) => listed.indexOf(name) !== index)
if (duplicates.length) problems.push(`CI 重复登记测试项目：${[...new Set(duplicates)].join('、')}`)

const missing = solutionProjects.filter(name => !listed.includes(name))
const extra = listed.filter(name => !solutionProjects.includes(name))
if (missing.length) problems.push(`CI 清单漏掉的测试项目：${missing.join('、')}`)
if (extra.length) problems.push(`CI 清单中的项目不在解决方案里：${extra.join('、')}`)
for (const name of listed)
  if (!existsSync(resolve(root, 'tests', name, `${name}.csproj`))) problems.push(`测试项目路径不存在：tests/${name}/${name}.csproj`)

if (problems.length) {
  console.error('[test-projects] CI 测试清单与解决方案不一致：')
  for (const problem of problems) console.error(`  - ${problem}`)
  process.exit(1)
}
console.log(`[test-projects] ${listed.length} 个测试项目与解决方案一致，分片无重复。`)