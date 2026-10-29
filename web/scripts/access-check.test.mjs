import { test } from 'node:test'
import assert from 'node:assert/strict'
import { collect, validateAccess } from './access-check.mjs'

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
