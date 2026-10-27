import type { ScreenDocument } from './model'
import { palette } from './model'

export interface EditorState { name: string; document: ScreenDocument }
export interface LocalScreenDraft { version: 1; baseRevision: number; baseSnapshot: string; snapshot: string; savedAt: number }
export interface StoredScreenDraft { key: string; raw: string; draft: LocalScreenDraft }
export const maxDraftChars = 1_500_000

export function parseEditorState(source: string): EditorState | null {
  try {
    if (source.length > maxDraftChars) return null
    const state = JSON.parse(source) as EditorState
    const document = state?.document
    if (typeof state?.name !== 'string' || state.name.length > 100 || !document || document.schemaVersion !== 1
      || !Number.isFinite(document.width) || document.width < 320 || document.width > 7680
      || !Number.isFinite(document.height) || document.height < 200 || document.height > 4320
      || typeof document.background !== 'string' || !Array.isArray(document.nodes) || document.nodes.length > 1000) return null
    const types = new Set([...palette.map(item => item.type), 'symbol', 'template'])
    if (!document.nodes.every(node => node && typeof node.id === 'string' && types.has(node.type)
      && [node.x, node.y, node.width, node.height, node.rotation, node.min, node.max, node.activeThreshold, node.decimals].every(value => value == null || Number.isFinite(value))
      && (node.decimals == null || node.decimals >= 0 && node.decimals <= 6)
      && [node.text, node.color, node.activeColor, node.unit].every(value => typeof value === 'string')
      && node.action && ['none', 'navigate', 'popup', 'command'].includes(node.action.type)
      && (node.bindings == null || Array.isArray(node.bindings) && node.bindings.every(binding => binding && typeof binding.key === 'string'))
      && (node.tags == null || Array.isArray(node.tags) && node.tags.every(Number.isInteger))
      && (node.texts == null || typeof node.texts === 'object' && !Array.isArray(node.texts) && Object.values(node.texts).every(text => typeof text === 'string')))) return null
    if (new Set(document.nodes.map(node => node.id)).size !== document.nodes.length) return null
    return state
  } catch { return null }
}

export function latestScreenDraft(prefix: string, serverSnapshot: string, excludeKey: string, storage: Storage = localStorage): StoredScreenDraft | null {
  let latest: StoredScreenDraft | null = null
  for (let index = 0; index < storage.length; index++) {
    const key = storage.key(index)
    if (!key?.startsWith(prefix) || key === excludeKey) continue
    const raw = storage.getItem(key)
    if (!raw || raw.length > maxDraftChars) continue
    let draft: LocalScreenDraft
    try { draft = JSON.parse(raw) as LocalScreenDraft } catch { continue }
    if (draft?.version !== 1 || !Number.isInteger(draft.baseRevision) || draft.baseRevision < 1
      || !Number.isFinite(draft.savedAt) || typeof draft.baseSnapshot !== 'string' || typeof draft.snapshot !== 'string'
      || draft.snapshot === serverSnapshot || !parseEditorState(draft.snapshot) || !parseEditorState(draft.baseSnapshot)) continue
    if (!latest || draft.savedAt > latest.draft.savedAt) latest = { key, raw, draft }
  }
  return latest
}