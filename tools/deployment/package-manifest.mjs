import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { join, relative, resolve, sep } from 'node:path'

const root = resolve(process.argv[2] ?? '')
if (!process.argv[2]) throw new Error('Usage: node package-manifest.mjs <package-directory>')
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
if (!files['Scada.Server.dll'] || !files['wwwroot/index.html']) throw new Error('Not a complete server package')
const revision = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim()
const trackedDirty = execFileSync('git', ['status', '--porcelain', '--untracked-files=no'], { encoding: 'utf8' }).trim().length > 0
const untracked = execFileSync('git', ['ls-files', '--others', '--exclude-standard', '-z'], { encoding: 'utf8' }).split('\0').filter(Boolean)
const dirty = trackedDirty || untracked.some(file => !resolve(file).startsWith(root + sep))
const build = process.env.GITHUB_RUN_NUMBER ?? 'local'
writeFileSync(join(root, 'release-manifest.json'), JSON.stringify({
  format: 1, product: 'Scada.Server', revision, dirty,
  version: `${build}-${revision.slice(0, 12)}${dirty ? '-dirty' : ''}`,
  build, createdUtc: new Date().toISOString(), algorithm: 'SHA256', files
}, null, 2))
console.log(`Manifest: ${Object.keys(files).length} files, revision ${revision}, dirty=${dirty}`)
