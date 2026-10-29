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

for (const [name, route, budget] of [
  ['login', 'src/views/LoginView.vue', 350 * 1024],
  ['overview', 'src/views/OverviewView.vue', 500 * 1024]
]) {
  const files = closure(entry)
  closure(route, files)
  if (name === 'overview') closure('src/layout/AppLayout.vue', files)
  const eagerHeavy = [...files].filter(file => /vendor-(monaco|echarts)/.test(file))
  if (eagerHeavy.length) throw new Error(`${name} eagerly loads ${eagerHeavy.join(', ')}`)
  const bytes = [...files].reduce((sum, file) => sum + gzipSync(readFileSync(resolve(dist, file))).length, 0)
  console.log(`${name}: ${bytes} gzip bytes / ${budget} budget (${files.size} JS/CSS files)`)
  if (bytes > budget) throw new Error(`${name} exceeds its gzip JS/CSS budget`)
}
