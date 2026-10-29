import type { HmiLayoutDocument, HmiMonitor, HmiRegion } from '../../api/hmiLayouts'

export interface LayoutDraftContent { name: string; document: HmiLayoutDocument }
export interface LocalLayoutDraft {
  schema: 1
  id: string
  layoutId: number | null
  baseRevision: number
  base: LayoutDraftContent
  content: LayoutDraftContent
  savedAt: number
  copiedFrom?: string
}
const prefix = (owner: string) => `scada_layout_recovery:v1:${encodeURIComponent(owner)}:`
const finite = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value)
const text = (value: unknown, length: number): value is string => typeof value === 'string' && value.length <= length
const integer = (value: unknown): value is number => finite(value) && Number.isInteger(value) && value >= -2_147_483_648 && value <= 2_147_483_647
const optionalNumber = (value: unknown) => value == null || integer(value)
const grid = (value: unknown) => integer(value) && value >= 1 && value <= 24
function contentValid(value: unknown): value is LayoutDraftContent {
  if (!value || typeof value !== 'object') return false
  const content = value as LayoutDraftContent, monitors = content.document?.monitors
  return text(content.name, 100) && Array.isArray(monitors) && monitors.length >= 1 && monitors.length <= 8 && monitors.every((monitor: HmiMonitor) =>
    monitor && text(monitor.name, 40) && grid(monitor.rows) && grid(monitor.columns)
    && (monitor.previewWidth == null && monitor.previewHeight == null || integer(monitor.previewWidth) && monitor.previewWidth >= 320 && monitor.previewWidth <= 7680 && integer(monitor.previewHeight) && monitor.previewHeight >= 240 && monitor.previewHeight <= 4320)
    && Array.isArray(monitor.regions) && monitor.regions.length <= 16 && monitor.regions.every((region: HmiRegion) =>
      region && text(region.id, 40) && ['screen', 'navigation', 'alarmList', 'alarmBanner'].includes(region.kind) && (region.title == null || text(region.title, 60))
      && optionalNumber(region.screenId) && [region.row, region.col, region.rowSpan, region.colSpan].every(integer)))
}
export function readLocalLayoutDrafts(owner: string): { drafts: LocalLayoutDraft[]; failed: boolean } {
  const drafts: LocalLayoutDraft[] = []
  let failed = false
  if (!owner) return { drafts, failed }
  try {
    for (let index = 0; index < localStorage.length; index++) {
      const key = localStorage.key(index)
      if (!key?.startsWith(prefix(owner))) continue
      try {
        const raw = localStorage.getItem(key)
        if (!raw || raw.length > 1_000_000) throw new Error('Invalid local layout draft')
        const draft = JSON.parse(raw) as LocalLayoutDraft
        if (draft.schema !== 1 || !text(draft.id, 100) || !draft.id || key !== prefix(owner) + draft.id
          || !(draft.layoutId === null || Number.isSafeInteger(draft.layoutId) && draft.layoutId > 0)
          || !Number.isSafeInteger(draft.baseRevision) || draft.baseRevision < 0 || !Number.isSafeInteger(draft.savedAt) || draft.savedAt < 0 || draft.savedAt > 8_640_000_000_000_000
          || !contentValid(draft.base) || !contentValid(draft.content) || (draft.copiedFrom != null && !text(draft.copiedFrom, 100))) throw new Error('Invalid local layout draft')
        drafts.push(draft)
      } catch { failed = true }
    }
  } catch { failed = true }
  return { drafts: drafts.sort((a, b) => b.savedAt - a.savedAt), failed }
}
export function writeLocalLayoutDraft(owner: string, draft: LocalLayoutDraft): boolean {
  if (!contentValid(draft.base) || !contentValid(draft.content)) return false
  try { localStorage.setItem(prefix(owner) + draft.id, JSON.stringify(draft)); return true } catch { return false }
}
export function removeLocalLayoutDraft(owner: string, id: string, savedAt?: number): boolean {
  try {
    const key = prefix(owner) + id
    if (savedAt != null) { const raw = localStorage.getItem(key); if (raw && (JSON.parse(raw) as LocalLayoutDraft).savedAt !== savedAt) return true }
    localStorage.removeItem(key); return true
  } catch { return false }
}
export function normalizeDraftContent(content: LayoutDraftContent): LayoutDraftContent {
  return { name: content.name, document: { monitors: content.document.monitors.map(monitor => ({ ...monitor, name: monitor.name ?? '', regions: monitor.regions.map(region => ({ ...region, id: region.id ?? '' })) })) } }
}
// Normalize optional server fields before comparing a cached base with a fresh response.
export function layoutContentKey(content: LayoutDraftContent): string {
  return JSON.stringify([content.name, content.document.monitors.map(monitor => [monitor.name ?? '', monitor.columns, monitor.rows, monitor.previewWidth ?? null, monitor.previewHeight ?? null,
    monitor.regions.map(region => [region.id ?? '', region.kind, region.title ?? '', region.screenId ?? null, region.row, region.col, region.rowSpan, region.colSpan])])])
}
