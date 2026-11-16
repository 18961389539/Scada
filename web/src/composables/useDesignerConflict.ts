import { computed, ref, type ComputedRef, type Ref } from 'vue'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { cleanNode, palette } from '../screens/model'
import type { ScreenDetail, ScreenDocument, ScreenNode } from '../screens/model'
import { tr } from '../i18n'

export interface ConflictRow { key: string; nodeId: string; kind: 'local' | 'server' | 'modified'; label: string; keep: boolean; choice: 'local' | 'server' | '' }

interface ConflictOptions {
  screenId: number
  document: Ref<ScreenDocument>
  name: Ref<string>
  revision: Ref<number>
  publishedRevision: Ref<number | null>
  selectedIds: Ref<string[]>
  savedSnapshot: Ref<string>
  snapshot: ComputedRef<string>
  setStableSnapshot: (value: string) => void
  actionError: Ref<string>
  save: () => Promise<boolean>
}

/** 保存冲突合并与发布前差异确认。 */
export function useDesignerConflict(options: ConflictOptions) {
  const { screenId, document, name, revision, publishedRevision, selectedIds, savedSnapshot, snapshot, actionError } = options
  const conflictVisible = ref(false), conflict = ref<{ server: ScreenDetail; rows: ConflictRow[] } | null>(null)
  const conflictRows = computed(() => conflict.value?.rows ?? [])
  const conflictNeedsChoice = computed(() => conflictRows.value.some(row => row.kind === 'modified' && !row.choice))
  const publishDiffVisible = ref(false)
  const publishDiff = ref<{ added: string[]; removed: string[]; changed: string[] }>({ added: [], removed: [], changed: [] })
  const publishDiffExtra = computed(() => Math.max(0, publishDiff.value.added.length + publishDiff.value.removed.length + publishDiff.value.changed.length - 24))
  let publishDiffResolve: ((accepted: boolean) => void) | null = null
  let publishDiffSettled = false

  function conflictLabel(node: ScreenNode): string {
    return `${node.text || palette.find(item => item.type === node.type)?.label || node.type} · #${node.id.slice(0, 6)}`
  }
  function sameNode(left: ScreenNode, right: ScreenNode): boolean {
    return JSON.stringify(cleanNode(left)) === JSON.stringify(cleanNode(right))
  }
  function conflictRowsFor(server: ScreenDocument): ConflictRow[] {
    const local = new Map(document.value.nodes.map(node => [node.id, node])), remote = new Map(server.nodes.map(node => [node.id, node]))
    const rows: ConflictRow[] = []
    for (const [id, node] of local) {
      const other = remote.get(id)
      if (!other) rows.push({ key: id, nodeId: id, kind: 'local', label: conflictLabel(node), keep: true, choice: 'local' })
      else if (!sameNode(node, other)) rows.push({ key: id, nodeId: id, kind: 'modified', label: conflictLabel(node), keep: true, choice: '' })
    }
    for (const [id, node] of remote) if (!local.has(id)) rows.push({ key: id, nodeId: id, kind: 'server', label: conflictLabel(node), keep: true, choice: 'server' })
    return rows
  }

  async function openConflict(): Promise<void> {
    try {
      const server = await screenApi.get(screenId, true)
      conflict.value = { server, rows: conflictRowsFor(server.document) }
      conflictVisible.value = true
      actionError.value = tr('screenDesignerView.conflictHint', [server.revision, revision.value])
    } catch { actionError.value = tr('screenDesignerView.conflictUnavailable') }
  }

  function reloadServerDraft(): void {
    const server = conflict.value?.server
    if (!server) return
    document.value = server.document
    name.value = server.name
    revision.value = server.revision
    const state = snapshot.value
    options.setStableSnapshot(state); savedSnapshot.value = state
    selectedIds.value = []; conflictVisible.value = false; actionError.value = ''
  }
  function overwriteServerDraft(): void {
    const server = conflict.value?.server
    if (!server) return
    revision.value = server.revision
    conflictVisible.value = false
    void options.save()
  }
  function mergeConflict(): void {
    const server = conflict.value?.server
    if (!server || conflictNeedsChoice.value) return
    const remote = new Map(server.document.nodes.map(node => [node.id, node]))
    const rows = new Map(conflict.value!.rows.map(row => [row.nodeId, row]))
    const next = document.value.nodes.flatMap(node => {
      const row = rows.get(node.id)
      if (!row) return [node]
      if (row.kind === 'local') return row.keep ? [node] : []
      if (row.kind === 'modified' && row.choice === 'server') { const other = remote.get(node.id); return other ? [JSON.parse(JSON.stringify(other)) as ScreenNode] : [node] }
      return [node]
    })
    for (const row of conflict.value!.rows) {
      if (row.kind === 'server' && row.keep) { const other = remote.get(row.nodeId); if (other) next.push(JSON.parse(JSON.stringify(other)) as ScreenNode) }
    }
    document.value = { ...document.value, nodes: next }
    revision.value = server.revision
    conflictVisible.value = false
    void options.save()
  }

  function diffLabels(local: ScreenDocument, remote: ScreenDocument): { added: string[]; removed: string[]; changed: string[] } {
    const left = new Map(local.nodes.map(node => [node.id, node])), right = new Map(remote.nodes.map(node => [node.id, node]))
    const added: string[] = [], removed: string[] = [], changed: string[] = []
    for (const [nodeId, node] of left) {
      const other = right.get(nodeId)
      if (!other) added.push(conflictLabel(node))
      else if (!sameNode(node, other)) changed.push(conflictLabel(node))
    }
    for (const [nodeId, node] of right) if (!left.has(nodeId)) removed.push(conflictLabel(node))
    if (local.width !== remote.width || local.height !== remote.height || local.background !== remote.background) changed.push(tr('screenDesignerView.canvas'))
    return { added, removed, changed }
  }
  function settlePublishDiff(accepted: boolean): void {
    if (publishDiffSettled) return
    publishDiffSettled = true
    publishDiffVisible.value = false
    publishDiffResolve?.(accepted)
    publishDiffResolve = null
  }
  async function confirmPublishDiff(): Promise<boolean> {
    if (!publishedRevision.value) return true
    let remote: ScreenDocument
    try { remote = (await screenApi.get(screenId, false)).document }
    catch (e) {
      actionError.value = tr('screenDesignerView.publishCheckDiffFailed', [errorMessage(e, tr('screenDesignerView.publishFailed'))])
      return false
    }
    const diff = diffLabels(document.value, remote)
    if (!diff.added.length && !diff.removed.length && !diff.changed.length) return true
    publishDiff.value = diff
    publishDiffSettled = false
    publishDiffVisible.value = true
    return await new Promise<boolean>(resolve => { publishDiffResolve = resolve })
  }

  return {
    conflictVisible, conflict, conflictRows, conflictNeedsChoice,
    publishDiffVisible, publishDiff, publishDiffExtra,
    conflictLabel, sameNode, conflictRowsFor,
    openConflict, reloadServerDraft, overwriteServerDraft, mergeConflict,
    diffLabels, settlePublishDiff, confirmPublishDiff
  }
}
