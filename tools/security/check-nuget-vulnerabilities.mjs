#!/usr/bin/env node
// NuGet 依赖漏洞门禁：读取 `dotnet list package --vulnerable --include-transitive --format json`
// 的输出，任何未在 allowlist 中登记的漏洞都让构建失败。
// 为什么不用 dotnet list 的退出码：不同 SDK 版本对有漏洞时的退出码不一致，
// 且命令本身失败时也可能留下不完整 JSON。这里只认报告内容，报告为空或结构不对一律失败。
import { existsSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'

const args = process.argv.slice(2)
const argOf = (name, fallback) => {
  const i = args.indexOf(`--${name}`)
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback
}

const reportPath = resolve(argOf('file', 'TestResults/nuget-vuln.json'))
const allowlistPath = resolve(argOf('allowlist', 'tools/security/nuget-vulnerability-allowlist.json'))

if (!existsSync(reportPath)) {
  console.error(`[nuget-audit] 找不到漏洞报告 ${reportPath}；请先运行 dotnet list package --vulnerable --format json`)
  process.exit(1)
}

let report
try {
  report = JSON.parse(readFileSync(reportPath, 'utf8'))
} catch (error) {
  console.error(`[nuget-audit] ${reportPath} 不是合法 JSON：${error.message}`)
  process.exit(1)
}

const projects = Array.isArray(report.projects) ? report.projects : []
if (projects.length === 0) {
  console.error('[nuget-audit] 报告里没有任何项目，无法判断依赖是否安全；按失败处理')
  process.exit(1)
}

const allowlist = existsSync(allowlistPath) ? JSON.parse(readFileSync(allowlistPath, 'utf8')) : []
if (!Array.isArray(allowlist)) {
  console.error(`[nuget-audit] ${allowlistPath} 必须是数组`)
  process.exit(1)
}
const allowed = new Map(
  allowlist
    .filter((item) => item && typeof item.package === 'string' && typeof item.advisory === 'string'
      && typeof item.reason === 'string' && item.reason.trim().length >= 8)
    .map((item) => [`${item.package}|${item.advisory}`, item])
)
for (const item of allowlist) {
  if (!item || typeof item.package !== 'string' || typeof item.advisory !== 'string'
    || typeof item.reason !== 'string' || item.reason.trim().length < 8) {
    console.error('[nuget-audit] 白名单条目必须包含 package、advisory 与至少 8 个字符的 reason')
    process.exit(1)
  }
}

const findings = []
const stale = new Set(allowed.keys())
for (const project of projects) {
  const projectName = String(project.path ?? '').split(/[\\/]/).pop()?.replace(/\.csproj$/i, '') ?? 'unknown'
  for (const framework of project.frameworks ?? []) {
    for (const group of ['topLevelPackages', 'transitivePackages']) {
      for (const pkg of framework[group] ?? []) {
        for (const vulnerability of pkg.vulnerabilities ?? []) {
          const key = `${pkg.id}|${vulnerability.advisoryurl}`
          const exception = allowed.get(key)
          if (exception) {
            stale.delete(key)
            continue
          }
          findings.push({
            project: projectName,
            package: pkg.id,
            resolved: pkg.resolvedVersion,
            severity: vulnerability.severity,
            advisory: vulnerability.advisoryurl
          })
        }
      }
    }
  }
}

if (findings.length > 0) {
  console.error(`[nuget-audit] 发现 ${findings.length} 条未登记的依赖漏洞：`)
  for (const item of findings) {
    console.error(`  - [${item.severity}] ${item.package} ${item.resolved} @ ${item.project}`)
    console.error(`    ${item.advisory}`)
  }
  console.error('升级依赖；确需保留时在 tools/security/nuget-vulnerability-allowlist.json 写明 package、advisory 与原因。')
  process.exit(1)
}

if (stale.size > 0) {
  console.error('[nuget-audit] 白名单里有过期条目（漏洞已不存在），请删除：')
  for (const key of stale) console.error(`  - ${key}`)
  process.exit(1)
}

const packageCount = new Set(
  projects.flatMap((project) => (project.frameworks ?? [])
    .flatMap((framework) => [...(framework.topLevelPackages ?? []), ...(framework.transitivePackages ?? [])])
    .map((pkg) => `${pkg.id}@${pkg.resolvedVersion}`))
).size
console.log(`[nuget-audit] 通过：${projects.length} 个项目，未发现未登记的漏洞${packageCount > 0 ? `（${packageCount} 个包）` : ''}`)