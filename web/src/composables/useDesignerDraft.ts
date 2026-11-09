import { computed, ref, watch, type ComputedRef, type Ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { tr } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { latestScreenDraft, maxDraftChars, parseEditorState, type LocalScreenDraft, type StoredScreenDraft } from '../screens/drafts'
import {
  clearDraft, draftBaseChanged, draftPrefixFor, newOwnDraftKey,
  removeDraftIfUnchanged, serializeDraft, writeDraft
} from '../screens/draftBackup'
import type { ScreenDocument } from '../screens/model'

export interface DesignerDraftContext {
  embedded: boolean
  screenId: number
  snapshot: ComputedRef<string>
  savedSnapshot: Ref<string>
  revision: Ref<number>
  loading: Ref<boolean>
  loadError: Ref<string>
  dirty: ComputedRef<boolean>
  busy: Ref<boolean>
  document: Ref<ScreenDocument>
  name: Ref<string>
  selectedIds: Ref<string[]>
  commit: () => void
  fitCanvas: () => Promise<void>
}

/**
 * Vue wrapper around screens/draftBackup.ts. The owning view keeps the document
 * state; this composable owns refs, the debounce timer and the recovery dialog.
 * Keep the exported function names stable: the view binds them to pagehide and
 * beforeunload listeners as well as to template buttons.
 */
export function useDesignerDraft(ctx: DesignerDraftContext) {
  const auth = useAuthStore()
  const draftPrefix = draftPrefixFor(auth.user?.id, ctx.screenId)
  const ownDraftKey = draftPrefix ? newOwnDraftKey(draftPrefix) : ''
  const recoverableDraft = ref<StoredScreenDraft | null>(null)
  const draftError = ref('')
  const localSavedAt = ref(0)
  const draftBaseChangedFlag = computed(() =>
    draftBaseChanged(recoverableDraft.value?.draft, ctx.revision.value, ctx.savedSnapshot.value))
  let recoveredDraft: StoredScreenDraft | null = null
  let backupTimer: number | undefined
  let leavingDiscarded = false

  const unavailable = (): string => tr('screenDesignerView.localDraftUnavailable')

  function findLocalDraft(): void {
    if (ctx.embedded || !draftPrefix) return
    try {
      const found = latestScreenDraft(draftPrefix, ctx.savedSnapshot.value, ownDraftKey)
      recoverableDraft.value = found.draft
      draftError.value = found.unreadable ? unavailable() : ''
    } catch { draftError.value = unavailable() }
  }

  function clearLocalBackup(): void {
    window.clearTimeout(backupTimer)
    if (ctx.embedded || !draftPrefix) return
    if (!clearDraft(localStorage, ownDraftKey, recoveredDraft)) { draftError.value = unavailable(); return }
    recoveredDraft = null; localSavedAt.value = 0; draftError.value = ''
  }

  function persistLocalDraft(): void {
    if (ctx.embedded || !draftPrefix || ctx.loading.value || ctx.loadError.value || leavingDiscarded) return
    if (!ctx.dirty.value) { clearLocalBackup(); return }
    const savedAt = Date.now()
    const backup: LocalScreenDraft = {
      version: 1,
      baseRevision: ctx.revision.value,
      baseSnapshot: ctx.savedSnapshot.value,
      snapshot: ctx.snapshot.value,
      savedAt
    }
    const serialized = serializeDraft(backup, maxDraftChars)
    if ('error' in serialized || !writeDraft(localStorage, ownDraftKey, serialized.raw)) {
      draftError.value = unavailable(); return
    }
    localSavedAt.value = savedAt; draftError.value = ''
  }

  watch(ctx.snapshot, () => {
    if (ctx.embedded || ctx.loading.value || leavingDiscarded) return
    window.clearTimeout(backupTimer)
    backupTimer = window.setTimeout(persistLocalDraft, 600)
  })

  async function restoreLocalDraft(): Promise<void> {
    const candidate = recoverableDraft.value
    if (!candidate || ctx.busy.value) return
    if (draftBaseChangedFlag.value || ctx.dirty.value) {
      try {
        await ElMessageBox.confirm(
          tr('screenDesignerView.localDraftReplace'), tr('screenDesignerView.restoreLocalDraft'),
          {
            type: 'warning',
            confirmButtonText: tr('screenDesignerView.restoreLocalDraft'),
            cancelButtonText: tr('screenDesignerView.cancel')
          }
        )
      } catch { return }
    }
    const state = parseEditorState(candidate.draft.snapshot)
    if (!state) return
    ctx.commit()
    ctx.name.value = state.name
    ctx.document.value = state.document
    ctx.selectedIds.value = []
    ctx.commit()
    recoveredDraft = candidate
    recoverableDraft.value = null
    persistLocalDraft()
    await ctx.fitCanvas()
  }

  function discardLocalDraft(): void {
    const candidate = recoverableDraft.value
    if (!candidate || ctx.busy.value) return
    if (!removeDraftIfUnchanged(localStorage, candidate)) { draftError.value = unavailable(); return }
    findLocalDraft()
  }

  function setLeavingDiscarded(value: boolean): void { leavingDiscarded = value }
  function disposeDraftTimer(): void { window.clearTimeout(backupTimer) }

  return {
    recoverableDraft, draftError, localSavedAt, draftBaseChanged: draftBaseChangedFlag,
    findLocalDraft, clearLocalBackup, persistLocalDraft,
    restoreLocalDraft, discardLocalDraft,
    setLeavingDiscarded, disposeDraftTimer
  }
}
