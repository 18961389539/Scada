import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

export function collect(source, pathPattern = /path: '([^']+)'/) {
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

const normalizedPath = path => '/' + path.replace(/^\/|\/$/g, '').replace(/\/:[^/]+\?$/, '')

export function validateAccess(nav, routes, policyIds) {
  for (const item of [...nav, ...routes]) {
    if (item.policy && !policyIds.has(item.policy)) throw new Error(`unknown policy '${item.policy}' for ${item.path}`)
  }
  const routeByPath = new Map(routes.map(route => [normalizedPath(route.path), route.policy]))
  for (const item of nav) {
    const path = normalizedPath(item.path)
    if (!routeByPath.has(path)) throw new Error(`navigation has no route: ${item.path}`)
    const routePolicy = routeByPath.get(path)
    // LicenseGuard sends every signed-in role here; only the navigation entry is administrative.
    if (path === '/license' && item.policy === 'system-admin' && routePolicy === '') continue
    if (routePolicy !== item.policy) {
      throw new Error(`policy drift: nav ${item.path}=${item.policy || '(none)'}, route=${routePolicy || '(none)'}`)
    }
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const policyFile = readFileSync('src/access/policies.ts', 'utf8')
  const policyBlock = policyFile.slice(policyFile.indexOf('const POLICY_DEFS'), policyFile.indexOf('} as const satisfies'))
  const policyIds = new Set([...policyBlock.matchAll(/^\s{2}'([^']+)':/gm)].map(m => m[1]))
  if (!policyIds.size) throw new Error('No access policies found')
  const nav = collect(readFileSync('src/nav/catalog.ts', 'utf8'))
  const routes = collect(readFileSync('src/router/index.ts', 'utf8'))
  if (!nav.length || !routes.length) throw new Error('Navigation or routes could not be parsed')
  validateAccess(nav, routes, policyIds)
  console.log(`access ok: ${policyIds.size} policies, ${nav.filter(item => item.policy).length} guarded nav items, ${routes.filter(item => item.policy).length} guarded routes`)
}