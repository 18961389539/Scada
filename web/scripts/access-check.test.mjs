import { test } from 'node:test'
import assert from 'node:assert/strict'
import { collect, validateAccess, collectRouteFlags, validateRoutePolicies } from './access-check.mjs'

const policies = new Set(['configure', 'system'])
const nav = [{ path: '/config/devices', policy: 'configure' }]
test('relative child route paths match absolute navigation paths', () => {
  validateAccess(nav, [{ path: 'config/devices', policy: 'configure' }], policies)
  validateAccess([{ path: '/console', policy: '' }], [{ path: '/console/:layoutId?', policy: '' }], policies)
})
test('missing route and unknown policy are rejected', () => {
  assert.throws(() => validateAccess(nav, [], policies), /no route/)
  assert.throws(() => validateAccess(nav, [{ path: 'config/devices', policy: 'typo' }], policies), /unknown policy/)
})
test('policy drift and removal on either side are rejected', () => {
  for (const policy of ['system', '']) {
    assert.throws(() => validateAccess(nav, [{ path: 'config/devices', policy }], policies), /policy drift/)
  }
  assert.throws(() => validateAccess([{ path: '/config/devices', policy: '' }],
    [{ path: 'config/devices', policy: 'configure' }], policies), /policy drift/)
})
test('license status stays reachable from the blocking license dialog for every role', () => {
  const licensePolicies = new Set(['system-admin', 'configure'])
  validateAccess([{ path: '/license', policy: 'system-admin' }], [{ path: 'license', policy: '' }], licensePolicies)
  assert.throws(() => validateAccess([{ path: '/license', policy: 'configure' }],
    [{ path: 'license', policy: '' }], licensePolicies), /policy drift/)
})
test('single-line and multiline route declarations preserve policy boundaries', () => {
  assert.deepEqual(collect(`{ path: '/overview', component: load },\r\n{
    path: 'config/devices',
    meta: { policy: 'configure' }
  },
  { path: '/login', component: login }`), [
    { path: '/overview', policy: '' },
    { path: 'config/devices', policy: 'configure' },
    { path: '/login', policy: '' }
  ])
})
test('a new route without a policy is rejected until it is exempted', () => {
  const routes = [
    { path: '/overview', policy: '' },
    { path: '/login', policy: '', public: true },
    { path: ':pathMatch(.*)*', policy: '', catchAll: true },
    { path: 'config/new-page', policy: '' }
  ]
  const exemptions = new Set(['/overview'])
  assert.throws(() => validateRoutePolicies(routes, exemptions), /config\/new-page/)
  assert.deepEqual(validateRoutePolicies(routes.slice(0, 3), exemptions), ['/overview'])
  assert.throws(() => validateRoutePolicies(routes.slice(0, 3), new Set(['/overview', '/removed'])), /stale/)
})
test('public and catch-all markers come from the same source line', () => {
  const flags = collectRouteFlags("{ path: '/login', meta: { public: true } }\n{ path: ':pathMatch(.*)*', component: notFound }")
  assert.deepEqual(flags.get('/login'), { public: true, catchAll: false })
  assert.deepEqual(flags.get(':pathMatch(.*)*'), { public: false, catchAll: true })
})
