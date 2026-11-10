import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { gzipSync } from 'node:zlib'

const dist = resolve('dist')
const manifest = JSON.parse(readFileSync(resolve(dist, '.vite', 'manifest.json'), 'utf8'))
const entry = Object.keys(manifest).find(key => manifest[key].isEntry)
if (!entry) throw new Error('Build manifest has no entry')

function closure(key, files = new Set()) {
  const chunk = manifest[key]
  if (!chunk) throw new Error(`Missing manifest chunk: ${key}`)
  if (files.has(chunk.file)) return files
  files.add(chunk.file)
  for (const css of chunk.css ?? []) files.add(css)
  for (const dependency of chunk.imports ?? []) closure(dependency, files)
  return files
}

function gzipBytes(files) {
  return [...files].reduce((sum, file) => sum + gzipSync(readFileSync(resolve(dist, file))).length, 0)
}

const baseFiles = closure(entry)
const baseBytes = gzipBytes(baseFiles)

// 首屏两条路径保留绝对预算：数值来自 2026-11-04（登录 365.6 KB）与
// 2026-11-06（总览 499.7 KB）的实测，只防回归，不是目标值。
// 2026-11-09 因通道管理页新增约 80 条中英文文案（i18n 是静态打包，会进每条路由的闭包）
// 总览实测涨到 508.5 KB，预算上调到 512 KB。
const absoluteBudgets = [
  ['src/views/LoginView.vue', 372 * 1024],
  ['src/views/OverviewView.vue', 512 * 1024]
]
// 其余路由用「相对入口的增量」统一约束，新增路由自动纳入，不必手工登记。
// 2026-11-09 实测最大增量 606699 B（ObjectTemplateEditorView），留约 8% 漂移空间。
const routeDeltaBudget = 640 * 1024
const routes = Object.keys(manifest).filter(key => /^src\/views\/.*\.vue$/.test(key))
if (routes.length < 30) throw new Error(`Only ${routes.length} route chunks found; did the manifest layout change?`)

const measured = []
for (const route of routes) {
  const files = closure(entry)
  closure(route, files)
  if (route.endsWith('OverviewView.vue')) closure('src/layout/AppLayout.vue', files)
  // Monaco 只服务脚本编辑器，任何路由都不该在首屏静态闭包里带上它。
  const eagerHeavy = [...files].filter(file => /vendor-monaco/.test(file))
  if (eagerHeavy.length) throw new Error(`${route} eagerly loads ${eagerHeavy.join(', ')}`)
  const bytes = gzipBytes(files)
  measured.push({ route, bytes, delta: bytes - baseBytes, files: files.size })
}
measured.sort((a, b) => b.delta - a.delta)

console.log(`entry: ${baseBytes} gzip bytes / ${baseFiles.size} files`)
for (const row of measured.slice(0, 8)) {
  console.log(`  ${row.route.padEnd(46)} ${row.bytes} total / +${row.delta} delta / ${row.files} files`)
}
console.log(`checked ${measured.length} route chunks`)

for (const [route, budget] of absoluteBudgets) {
  const row = measured.find(item => item.route === route)
  if (!row) throw new Error(`Budgeted route missing from manifest: ${route}`)
  console.log(`${route}: ${row.bytes} gzip bytes / ${budget} absolute budget`)
  if (row.bytes > budget) throw new Error(`${route} exceeds its gzip JS/CSS budget by ${row.bytes - budget} bytes`)
}

const heaviest = measured[0]
console.log(`route delta ceiling: ${heaviest.delta} gzip bytes / ${routeDeltaBudget} budget (${heaviest.route})`)
if (heaviest.delta > routeDeltaBudget) {
  throw new Error(`${heaviest.route} exceeds the per-route delta budget by ${heaviest.delta - routeDeltaBudget} bytes`)
}
