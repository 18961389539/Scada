import { readFileSync } from 'node:fs'

const policyFile = readFileSync('src/access/policies.ts', 'utf8')
const policyBlock = policyFile.slice(policyFile.indexOf('const POLICY_DEFS'), policyFile.indexOf('} as const satisfies'))
const policyIds = new Set([...policyBlock.matchAll(/^\s{2}'([^']+)':/gm)].map(m => m[1]))

function collect(source, pathPattern) {
  const lines = source.split('\n')
  const found = []
  let path = ''
  let policy = ''
  const flush = () => {
    if (path) found.push({ path, policy })
    path = ''
    policy = ''
  }
  for (const line of lines) {
    const pathMatch = line.match(pathPattern)
    if (pathMatch) {
      flush()
      path = pathMatch[1]
    }
    const policyMatch = line.match(/policy: '([^']+)'/)
    if (policyMatch) policy = policyMatch[1]
  }
  flush()
  return found
}

const nav = collect(readFileSync('src/nav/catalog.ts', 'utf8'), /path: '([^']+)'/)
  .filter(item => item.policy)
const routes = collect(readFileSync('src/router/index.ts', 'utf8'), /path: '([^']+)'/)
  .filter(route => route.policy)

for (const item of [...nav, ...routes]) {
  if (!policyIds.has(item.policy)) throw new Error(`unknown policy '${item.policy}' for ${item.path}`)
}

const routeByPath = new Map(routes.map(route => [route.path, route.policy]))
for (const item of nav) {
  const routePolicy = routeByPath.get(item.path)
  if (routePolicy && routePolicy !== item.policy) {
    throw new Error(`policy drift: nav ${item.path}=${item.policy}, route=${routePolicy}`)
  }
}

console.log(`access ok: ${policyIds.size} policies, ${nav.length} guarded nav items, ${routes.length} guarded routes`)