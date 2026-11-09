import { test } from 'node:test'
import assert from 'node:assert/strict'
import { build } from 'esbuild'
import { fileURLToPath } from 'node:url'
import { dirname, resolve } from 'node:path'

// The module under test is TypeScript and type-only imports are erased, so bundling it
// yields a self-contained ESM chunk that can run in plain node without a DOM.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const result = await build({
  entryPoints: [resolve(root, 'src/screens/draftBackup.ts')],
  bundle: true, format: 'esm', platform: 'node', write: false, logLevel: 'silent'
})
const code = result.outputFiles[0].text
const draft = await import(`data:text/javascript,${encodeURIComponent(code)}`)

class MemoryStorage {
  constructor() { this.map = new Map() }
  get length() { return this.map.size }
  key(index) { return [...this.map.keys()][index] ?? null }
  getItem(key) { return this.map.has(key) ? this.map.get(key) : null }
  setItem(key, value) { this.map.set(key, String(value)) }
  removeItem(key) { this.map.delete(key) }
  keys() { return [...this.map.keys()] }
}

class FailingStorage extends MemoryStorage {
  constructor(method) { super(); this.method = method }
  getItem(key) { if (this.method === 'getItem') throw new Error('blocked'); return super.getItem(key) }
  setItem(key, value) { if (this.method === 'setItem') throw new Error('quota'); super.setItem(key, value) }
  removeItem(key) { if (this.method === 'removeItem') throw new Error('blocked'); super.removeItem(key) }
}

function stored(key, raw, overrides = {}) {
  return {
    key, raw,
    draft: { version: 1, baseRevision: 3, baseSnapshot: 'server-v3', snapshot: 'local-v3', savedAt: 1000, ...overrides }
  }
}

function sampleDraft(overrides = {}) {
  return { version: 1, baseRevision: 3, baseSnapshot: 'server-v3', snapshot: 'local-v3', savedAt: 1000, ...overrides }
}

test('draft keys are scoped per user and screen, and anonymous editors get none', () => {
  assert.equal(draft.draftKeyPrefix, 'scada:screen-draft:v1')
  assert.equal(draft.draftPrefixFor('u-7', 12), 'scada:screen-draft:v1:u-7:12:')
  assert.equal(draft.draftPrefixFor(7, 12), 'scada:screen-draft:v1:7:12:')
  assert.equal(draft.draftPrefixFor(null, 12), '')
  assert.equal(draft.draftPrefixFor(undefined, 12), '')
  assert.equal(draft.draftPrefixFor('', 12), '')
  assert.equal(draft.draftPrefixFor('u-7', Number.NaN), '')
})

test('each tab gets a distinct key under the same prefix', () => {
  const prefix = draft.draftPrefixFor('u-7', 12)
  const a = draft.newOwnDraftKey(prefix)
  const b = draft.newOwnDraftKey(prefix)
  assert.notEqual(a, b)
  assert.ok(a.startsWith(prefix) && b.startsWith(prefix))
})

test('draftBaseChanged detects both a newer revision and changed server content', () => {
  const local = sampleDraft()
  assert.equal(draft.draftBaseChanged(null, 3, 'server-v3'), false)
  assert.equal(draft.draftBaseChanged(undefined, 3, 'server-v3'), false)
  assert.equal(draft.draftBaseChanged(local, 3, 'server-v3'), false)
  assert.equal(draft.draftBaseChanged(local, 4, 'server-v3'), true)
  assert.equal(draft.draftBaseChanged(local, 3, 'server-v4'), true)
})

test('serializeDraft enforces the size cap and survives unserializable input', () => {
  const ok = draft.serializeDraft(sampleDraft(), 1_500_000)
  assert.ok('raw' in ok)
  assert.deepEqual(JSON.parse(ok.raw), sampleDraft())
  assert.deepEqual(draft.serializeDraft(sampleDraft(), 10), { error: 'too-large' })
  const circular = sampleDraft()
  circular.extra = circular
  assert.deepEqual(draft.serializeDraft(circular, 1_500_000), { error: 'unserializable' })
})

test('writeDraft reports storage failures instead of throwing', () => {
  const storage = new MemoryStorage()
  const { raw } = draft.serializeDraft(sampleDraft(), 1_500_000)
  assert.equal(draft.writeDraft(storage, 'own', raw), true)
  assert.equal(storage.getItem('own'), raw)
  assert.equal(draft.writeDraft(new FailingStorage('setItem'), 'own', raw), false)
})

test('clearDraft removes only this tab and the recovered draft it adopted', () => {
  const storage = new MemoryStorage()
  const prefix = draft.draftPrefixFor('u-7', 12)
  const own = draft.newOwnDraftKey(prefix)
  const otherTab = draft.newOwnDraftKey(prefix)
  const recovered = stored(draft.newOwnDraftKey(prefix), JSON.stringify(sampleDraft()))
  storage.setItem(own, 'own-backup')
  storage.setItem(otherTab, 'other-backup')
  storage.setItem(recovered.key, recovered.raw)
  assert.equal(draft.clearDraft(storage, own, recovered), true)
  assert.deepEqual(storage.keys(), [otherTab])
})

test('clearDraft keeps a recovered draft whose contents were replaced by another tab', () => {
  const storage = new MemoryStorage()
  const own = 'own'
  const recovered = stored('recovered', JSON.stringify(sampleDraft()))
  storage.setItem(own, 'own-backup')
  storage.setItem(recovered.key, 'rewritten-by-another-tab')
  assert.equal(draft.clearDraft(storage, own, recovered), true)
  assert.equal(storage.getItem(recovered.key), 'rewritten-by-another-tab')
  assert.equal(storage.getItem(own), null)
})

test('clearDraft and removeDraftIfUnchanged report storage failures', () => {
  const recovered = stored('recovered', JSON.stringify(sampleDraft()))
  assert.equal(draft.clearDraft(new FailingStorage('removeItem'), 'own', null), false)
  assert.equal(draft.clearDraft(new FailingStorage('getItem'), 'own', recovered), false)
  assert.equal(draft.removeDraftIfUnchanged(new FailingStorage('getItem'), recovered), false)
  const storage = new MemoryStorage()
  storage.setItem(recovered.key, recovered.raw)
  assert.equal(draft.removeDraftIfUnchanged(storage, recovered), true)
  assert.equal(storage.getItem(recovered.key), null)
})

test('two open tabs keep independent backups when one of them saves', () => {
  const storage = new MemoryStorage()
  const prefix = draft.draftPrefixFor('u-7', 12)
  const tabA = draft.newOwnDraftKey(prefix)
  const tabB = draft.newOwnDraftKey(prefix)
  const a = draft.serializeDraft(sampleDraft({ snapshot: 'tab-a' }), 1_500_000)
  const b = draft.serializeDraft(sampleDraft({ snapshot: 'tab-b', savedAt: 2000 }), 1_500_000)
  assert.equal(draft.writeDraft(storage, tabA, a.raw), true)
  assert.equal(draft.writeDraft(storage, tabB, b.raw), true)
  assert.equal(storage.length, 2)
  // Tab A saved, so it clears its own key only. Tab B's recovery candidate survives.
  assert.equal(draft.clearDraft(storage, tabA, null), true)
  assert.equal(storage.getItem(tabA), null)
  assert.equal(JSON.parse(storage.getItem(tabB)).snapshot, 'tab-b')
  assert.ok(storage.keys().every(key => key.startsWith(prefix)))
})
