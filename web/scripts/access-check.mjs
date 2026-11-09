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

/**
 * Routes that intentionally have no meta.policy. Anything else would be a page
 * reachable with just a valid token, so a new route must either declare a policy
 * or be added here explicitly (and the list is checked for stale entries too).
 * Public routes, the 404 catch-all and the AppLayout container are excluded by flags.
 */
export const ROUTE_POLICY_EXEMPTIONS = new Set([
  '/',                    // AppLayout container; children carry their own policies
  '/change-password',     // forced first stop for mustChangePassword sessions
  '/console',             // runtime workbench (normalized /console/:layoutId?); control actions guarded in-page
  '/kiosk/screens/:id',   // unattended read-only display window
  '/license',             // LicenseGuard must be reachable for every signed-in role
  '/calc-jobs',
  '/report-center',
  '/screens',
  '/screens/:id',
  '/overview',
  '/realtime',
  '/trends',
  '/alarms',
  '/alarm-analytics',
  '/oee',
  '/reports'
])

/** Read public / catch-all markers from the same source line. Kept separate so collect()'s shape stays stable. */
export function collectRouteFlags(source) {
  const flags = new Map()
  for (const line of source.split('\n')) {
    const match = line.match(/path: '([^']+)'/)
    if (!match) continue
    flags.set(match[1], {
      public: /public:\s*true/.test(line),
      catchAll: match[1].includes(':pathMatch')
    })
  }
  return flags
}

export function validateRoutePolicies(routes, exemptions = ROUTE_POLICY_EXEMPTIONS) {
  const unguarded = routes
    .filter(route => !route.policy && !route.public && !route.catchAll)
    .map(route => normalizedPath(route.path))
  const unexpected = unguarded.filter(path => !exemptions.has(path))
  if (unexpected.length) {
    throw new Error(`routes without meta.policy (add a policy or an explicit exemption): ${unexpected.join(', ')}`)
  }
  const stale = [...exemptions].filter(path => !unguarded.includes(path))
  if (stale.length) throw new Error(`stale route policy exemptions: ${stale.join(', ')}`)
  return unguarded
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const policyFile = readFileSync('src/access/policies.ts', 'utf8')
  const policyBlock = policyFile.slice(policyFile.indexOf('const POLICY_DEFS'), policyFile.indexOf('} as const satisfies'))
  const policyIds = new Set([...policyBlock.matchAll(/^\s{2}'([^']+)':/gm)].map(m => m[1]))
  if (!policyIds.size) throw new Error('No access policies found')
  const nav = collect(readFileSync('src/nav/catalog.ts', 'utf8'))
  const routerSource = readFileSync('src/router/index.ts', 'utf8')
  const routes = collect(routerSource)
  if (!nav.length || !routes.length) throw new Error('Navigation or routes could not be parsed')
  validateAccess(nav, routes, policyIds)
  const flags = collectRouteFlags(routerSource)
  const exempted = validateRoutePolicies(routes.map(route => ({ ...route, ...(flags.get(route.path) ?? {}) })))
  console.log(`access ok: ${policyIds.size} policies, ${nav.filter(item => item.policy).length} guarded nav items, ${routes.filter(item => item.policy).length} guarded routes, ${exempted.length} exempted unguarded routes`)
}
