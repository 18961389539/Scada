import type { LocalScreenDraft, StoredScreenDraft } from './drafts'

/**
 * Framework-free core of the designer's local draft backup. Everything here takes
 * an explicit Storage so the lifecycle can be unit tested without a browser.
 * The Vue composable in composables/useDesignerDraft.ts owns refs, timers and dialogs.
 */

export const draftKeyPrefix = 'scada:screen-draft:v1'

/** Backups are scoped per user and per screen; an anonymous editor gets none. */
export function draftPrefixFor(userId: string | number | null | undefined, screenId: number): string {
  if (userId == null || userId === '' || !Number.isFinite(screenId)) return ''
  return `${draftKeyPrefix}:${userId}:${screenId}:`
}

/** Each tab owns a distinct key, so a reload can tell its own backup from a stale one. */
export function newOwnDraftKey(prefix: string): string {
  return prefix + crypto.randomUUID()
}

/** True when the server moved on after the backup was written (revision or content). */
export function draftBaseChanged(draft: LocalScreenDraft | null | undefined, revision: number, savedSnapshot: string): boolean {
  return !!draft && (draft.baseRevision !== revision || draft.baseSnapshot !== savedSnapshot)
}

export type DraftSerialization = { raw: string } | { error: 'too-large' | 'unserializable' }

export function serializeDraft(draft: LocalScreenDraft, maxChars: number): DraftSerialization {
  let raw: string
  try { raw = JSON.stringify(draft) } catch { return { error: 'unserializable' } }
  if (raw.length > maxChars) return { error: 'too-large' }
  return { raw }
}

/** Returns false when storage rejected the write (quota exhausted, private mode, disabled). */
export function writeDraft(storage: Storage, key: string, raw: string): boolean {
  try { storage.setItem(key, raw); return true } catch { return false }
}

/** A recovered draft is only removed when nobody replaced its contents meanwhile. */
export function removeDraftIfUnchanged(storage: Storage, stored: StoredScreenDraft): boolean {
  try {
    if (storage.getItem(stored.key) === stored.raw) storage.removeItem(stored.key)
    return true
  } catch { return false }
}

/** Clears this tab's backup plus the recovered draft it adopted; other tabs stay intact. */
export function clearDraft(storage: Storage, ownKey: string, recovered: StoredScreenDraft | null): boolean {
  try {
    storage.removeItem(ownKey)
    if (recovered && storage.getItem(recovered.key) === recovered.raw) storage.removeItem(recovered.key)
    return true
  } catch { return false }
}
