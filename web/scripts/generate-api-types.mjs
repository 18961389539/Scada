#!/usr/bin/env node
// 从 /openapi/v1.json 生成 src/api/contract.ts（REST 契约的单一来源）。
// 用法：node scripts/generate-api-types.mjs [--url http://127.0.0.1:5080] [--file openapi.json] [--out src/api/contract.ts]
import { readFile, writeFile, mkdir } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import openapiTS, { astToString } from 'openapi-typescript'

const args = process.argv.slice(2)
const arg = (name, fallback) => {
  const index = args.indexOf(`--${name}`)
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback
}
const file = arg('file', null)
const url = arg('url', process.env.OPENAPI_URL ?? 'http://127.0.0.1:5080')
const out = resolve(arg('out', 'src/api/contract.ts'))

let schema
if (file) {
  schema = JSON.parse(await readFile(resolve(file), 'utf8'))
} else {
  const response = await fetch(new URL('/openapi/v1.json', url))
  if (!response.ok) throw new Error(`GET ${url}/openapi/v1.json 返回 ${response.status}；先启动服务端或改用 --file`)
  schema = await response.json()
}

const ast = await openapiTS(schema)
await mkdir(dirname(out), { recursive: true })
await writeFile(out, `// 由 scripts/generate-api-types.mjs 从 /openapi/v1.json 生成，请勿手改。\n${astToString(ast)}`, 'utf8')
console.log(`[api-types] 写出 ${out}（${Object.keys(schema.paths ?? {}).length} 条路径，${Object.keys(schema.components?.schemas ?? {}).length} 个 schema）`)
