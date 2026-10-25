#!/usr/bin/env node
// 未翻译检查：1) 英文缺键/多余键；2) 占位符不一致；3) 代码引用了不存在的键；4) 源码中残留未抽取的中文字面量。
// 有问题时以非零码退出。新增中文文案后先运行 `python3 scripts/i18n-extract.py` 再补 en-US.json。
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join, relative } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('../src', import.meta.url))
const load = (f) => JSON.parse(readFileSync(join(root, 'i18n/locales', f), 'utf8'))
const flat = (obj, prefix = '', out = {}) => { for (const [k, v] of Object.entries(obj)) typeof v === 'object' ? flat(v, `${prefix}${k}.`, out) : (out[prefix + k] = v); return out }
const zh = flat(load('zh-CN.json')), en = flat(load('en-US.json'))
const problems = []
for (const k of Object.keys(zh)) {
  if (!(k in en)) problems.push(`missing en-US: ${k} = ${zh[k]}`)
  else if (!en[k].trim()) problems.push(`empty en-US: ${k}`)
  else {
    const ph = (s) => (s.match(/\{\d+\}|\{'.'\}/g) ?? []).sort().join(',')
    if (ph(zh[k]) !== ph(en[k])) problems.push(`placeholder mismatch: ${k}: "${zh[k]}" vs "${en[k]}"`)
  }
}
for (const k of Object.keys(en)) if (!(k in zh)) problems.push(`stale en-US key: ${k}`)

const files = []
const walk = (d) => { for (const f of readdirSync(d)) { const p = join(d, f); if (statSync(p).isDirectory()) { if (f !== 'i18n') walk(p) } else if (/\.(vue|ts)$/.test(f) && !f.endsWith('.d.ts')) files.push(p) } }
walk(root)
const hand = new Set(Object.keys(flat((await import(join(root, 'i18n/locales/zh-CN.ts').replace(/^/, 'file://')).catch(() => ({ default: {} }))).default ?? {})))
for (const f of files) {
  const src = readFileSync(f, 'utf8')
  for (const m of src.matchAll(/(?:\$t|\btr|\bt)\(\s*'([\w.]+)'/g)) if (!(m[1] in zh) && !hand.has(m[1]) && !m[1].startsWith('flow.')) problems.push(`unknown key in ${relative(root, f)}: ${m[1]}`)
  src.split('\n').forEach((line, i) => {
    if (line.includes('i18n-ignore')) return
    const code = line.replace(/\/\/.*$/, '').replace(/<!--.*?-->/g, '').replace(/\/\*.*?\*\//g, '')
    if (/^\s*\*/.test(code)) return
    if (/[\u4e00-\u9fff]/.test(code)) problems.push(`untranslated literal ${relative(root, f)}:${i + 1}: ${line.trim().slice(0, 100)}`)
  })
}
if (problems.length) { console.error(problems.join('\n')); console.error(`\n${problems.length} i18n problem(s)`); process.exit(1) }
console.log(`i18n ok: ${Object.keys(zh).length} keys, ${files.length} files`)
