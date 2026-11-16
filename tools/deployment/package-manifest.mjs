import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join, relative, resolve, sep } from 'node:path'

const args = process.argv.slice(2)
const option = name => {
  const index = args.indexOf(`--${name}`)
  return index >= 0 ? args[index + 1] : undefined
}
const rootArg = args.find(arg => !arg.startsWith('--') && !args[args.indexOf(arg) - 1]?.startsWith('--'))
if (!rootArg) {
  throw new Error('Usage: node package-manifest.mjs <package-directory> [--product name] [--require file1,file2] [--version version]')
}
const root = resolve(rootArg)
const product = option('product') ?? 'Scada.Server'
const defaultRequired = product === 'Scada.Server' ? 'Scada.Server.dll,wwwroot/index.html' : ''
const required = (option('require') ?? defaultRequired).split(',').map(value => value.trim()).filter(Boolean)
const hash = data => createHash('sha256').update(data).digest('hex')
const files = {}
function walk(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name))) {
    const path = join(directory, entry.name)
    if (entry.isSymbolicLink()) throw new Error(`Package must not contain links: ${path}`)
    if (entry.isDirectory()) walk(path)
    else if (path !== join(root, 'release-manifest.json')) files[relative(root, path).replaceAll('\\', '/')] = hash(readFileSync(path))
  }
}
walk(root)
const missing = required.filter(file => !files[file])
if (missing.length) throw new Error(`Not a complete ${product} package; missing: ${missing.join(', ')}`)
const revision = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim()
const trackedDirty = execFileSync('git', ['status', '--porcelain', '--untracked-files=no'], { encoding: 'utf8' }).trim().length > 0
const untracked = execFileSync('git', ['ls-files', '--others', '--exclude-standard', '-z'], { encoding: 'utf8' }).split('\0').filter(Boolean)
const dirty = trackedDirty || untracked.some(file => !resolve(file).startsWith(root + sep))
const build = process.env.GITHUB_RUN_NUMBER ?? 'local'
writeFileSync(join(root, 'release-manifest.json'), JSON.stringify({
  format: 1,
  product,
  revision,
  dirty,
  version: option('version') ?? `${build}-${revision.slice(0, 12)}${dirty ? '-dirty' : ''}`,
  build,
  createdUtc: new Date().toISOString(),
  algorithm: 'SHA256',
  files
}, null, 2))
console.log(`Manifest: ${Object.keys(files).length} files, product ${product}, revision ${revision}, dirty=${dirty}`)
