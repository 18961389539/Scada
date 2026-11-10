import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { onBeforeRouteLeave, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { getLocalWorkstation, hmiLayoutApi, isLocalWorkstation, setLocalWorkstation, WORKSTATION_ID_KEY, type HmiLayout, type HmiLayoutDocument, type HmiMonitor, type HmiRegion, type HmiWorkstation, type RegionKind } from '../api/hmiLayouts'
import { areaApi, deviceApi } from '../api'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { useActionError } from '../composables/useActionError'
import type { ScreenSummary } from '../screens/model'
import type { Area, Device } from '../types'
import { tr } from '../i18n'
import { formatTime } from '../utils'
import { contentMonitor, misplacedAlarmBanners, previewSize } from '../components/console/layoutGeometry'
import { copiedRegionId, findRegionSpace, fitRegionsToGrid, geometryChanged, type RegionGeometry } from '../components/console/layoutEditing'
import { isMobile } from '../mobile'
import { createLayoutTemplate, layoutTemplateKeys, type LayoutTemplateKey } from '../components/console/layoutTemplates'
import { useAuthStore } from '../stores/auth'
import { layoutContentKey, normalizeDraftContent, readLocalLayoutDrafts, removeLocalLayoutDraft, writeLocalLayoutDraft, type LayoutDraftContent, type LocalLayoutDraft } from '../components/console/layoutRecovery'
import LayoutVersionHistory from '../components/console/LayoutVersionHistory.vue'
import QuickLayoutCreate from '../components/console/QuickLayoutCreate.vue'

export function useLayoutsView() {
  const router = useRouter()
  const popups = new Map<string, { handleClose?: () => void; hide?: () => void }>()
  function rememberPopup(key: string, instance: unknown): void { if (instance) popups.set(key, instance as { handleClose?: () => void; hide?: () => void }); else popups.delete(key) }
  function closePopups(): void { for (const popup of popups.values()) { popup.handleClose?.(); popup.hide?.() } }
  onMounted(() => window.addEventListener('resize', closePopups))
  onBeforeUnmount(() => { window.removeEventListener('resize', closePopups); popups.clear() })
  const lifecycle = ref<InstanceType<typeof LayoutVersionHistory> | null>(null)
  const quickCreate = ref<InstanceType<typeof QuickLayoutCreate>>()
  const layouts = ref<HmiLayout[]>([]), stations = ref<HmiWorkstation[]>([]), screens = ref<ScreenSummary[]>([]), areas = ref<Area[]>([]), devices = ref<Device[]>([]), loadError = ref(''), layoutError = ref(''), stationError = ref('')
  const scopeCatalogError = ref(''), scopeCatalogReady = ref(false)
  const lastSavedId = ref<number | null>(null)
  const savedDraft = computed(() => layouts.value.find(layout => layout.id === lastSavedId.value && (layout.publishedRevision == null || layout.hasDraftChanges !== false)))
  function rememberSavedDraft(layout: HmiLayout): void { lastSavedId.value = layout.id; void load() }
  const { actionError, guard: runGuarded } = useActionError()
  const deleteConflict = ref(false), stationConflict = ref(false)
  const isConflict = (reason: unknown) => (reason as { response?: { status?: number } })?.response?.status === 409
  async function guardDelete(action: () => Promise<unknown>): Promise<boolean> {
    deleteConflict.value = false
    return runGuarded(async () => { try { await action() } catch (reason) { deleteConflict.value = isConflict(reason); throw reason } }, tr('layoutsView.k72250c'))
  }
  async function reloadAfterDeleteConflict(): Promise<void> { await load(); if (!loadError.value && !stationsError.value) { deleteConflict.value = false; actionError.value = '' } }
  const stationsError = ref(''), screensError = ref(''), loading = ref(false), saving = ref(false), stationSaving = ref(false)
  const kinds: Record<RegionKind, string> = { screen: tr('layoutsView.k8a1840'), alarmBanner: tr('layoutsView.k77911d'), alarmList: tr('layoutsView.k459887'), navigation: tr('layoutsView.k6be0ed') }
  const editableKinds = { screen: kinds.screen, alarmList: kinds.alarmList, navigation: kinds.navigation }
  const getBinding = () => ({ ...getLocalWorkstation(), hasId: localStorage.getItem(WORKSTATION_ID_KEY) != null })
  const local = ref(getBinding())
  const localName = computed(() => local.value.name || (local.value.id ? `#${local.value.id}` : local.value.hasId ? '—' : ''))
  const localInvalid = computed(() => !!localName.value && !loading.value && !stationsError.value && !stations.value.some(s => local.value.hasId ? s.id === local.value.id : s.name === local.value.name))
  const layoutStations = (id: number) => stations.value.filter(s => s.layoutId === id)
  const publishedLayouts = computed(() => layouts.value.filter(layout => layout.publishedRevision != null))
  const screenNames = computed(() => new Map(screens.value.map(screen => [screen.id, screen.name])))
  const localStation = computed(() => stations.value.find(station => local.value.hasId ? station.id === local.value.id : station.name === local.value.name))
  const localLayout = computed(() => layouts.value.find(layout => layout.id === localStation.value?.layoutId))
  const localReady = computed(() => !loading.value && !stationsError.value && !loadError.value && !!localStation.value && localLayout.value?.publishedRevision != null)
  const localSummaryMessage = computed(() => tr(`layoutCatalog.local_${loading.value ? 'loading' : stationsError.value || loadError.value ? 'error' : localInvalid.value ? 'invalid' : !localStation.value ? 'unregistered' : localStation.value.layoutId == null ? 'unassigned' : !localLayout.value ? 'missing' : localLayout.value.publishedRevision == null ? 'unpublished' : 'ready'}`))
  const layoutSearch = ref(''), statusFilter = ref('all'), usageFilter = ref('all')
  const filteredLayouts = computed(() => {
    const query = layoutSearch.value.trim().toLocaleLowerCase()
    return layouts.value.filter(layout => (!query || [layout.name, layout.publishedName].some(name => name?.toLocaleLowerCase().includes(query)))
      && (statusFilter.value === 'all' || (statusFilter.value === 'published' ? layout.publishedRevision != null : statusFilter.value === 'unpublished' ? layout.publishedRevision == null : layout.publishedRevision != null && layout.hasDraftChanges))
      && (stationsError.value || usageFilter.value === 'all' || (usageFilter.value === 'used' ? layoutStations(layout.id).length > 0 : !layoutStations(layout.id).length)))
  })
  function resetFilters(): void { layoutSearch.value = ''; statusFilter.value = 'all'; usageFilter.value = 'all' }
  function monitorSummary(layout: HmiLayout): string { return tr('layoutCatalog.monitorSummary', [layout.document.monitors.length, layout.document.monitors.reduce((total, monitor) => total + contentMonitor(monitor).regions.length, 0)]) }
  const stationsPanel = ref<HTMLElement | null>(null)
  function focusStations(): void { stationsPanel.value?.scrollIntoView({ behavior: 'smooth', block: 'start' }); stationsPanel.value?.focus({ preventScroll: true }) }
  function stationLayoutLabel(id: number | null): string {
    const layout = layouts.value.find(item => item.id === id)
    return !layout ? id == null ? '—' : `#${id}` : layout.publishedRevision == null ? tr('layoutLifecycle.unpublishedBinding', [layout.name]) : layout.publishedName || layout.name
  }

  async function load(): Promise<void> {
    if (loading.value) return
    loading.value = true
    const [layoutResult, stationResult, screenResult] = await Promise.allSettled([hmiLayoutApi.list(true), hmiLayoutApi.workstations(), screenApi.list()])
    if (layoutResult.status === 'fulfilled') { layouts.value = layoutResult.value; loadError.value = '' }
    else loadError.value = errorMessage(layoutResult.reason, tr('layoutsView.kf6b7a4'))
    if (stationResult.status === 'fulfilled') {
      stations.value = stationResult.value; stationsError.value = ''
      const match = stations.value.find(isLocalWorkstation)
      if (match) setLocalWorkstation(match)
      syncLocal()
    }
    else { stationsError.value = errorMessage(stationResult.reason, tr('layoutMonitor.stationsFailed')); usageFilter.value = 'all' }
    if (screenResult.status === 'fulfilled') { screens.value = screenResult.value.filter(s => s.publishedRevision != null); screensError.value = '' }
    else screensError.value = errorMessage(screenResult.reason, tr('layoutMonitor.screensFailed'))
    loading.value = false
  }

  const visible = ref(false), monitorTab = ref('0')
  const editorAdvanced = ref(false)
  function toggleAdvancedSettings(): void { endDrag(); editorAdvanced.value = !editorAdvanced.value }
  const layoutSettingsOpen = ref(false), monitorSettingsOpen = ref(false), regionPropertiesOpen = ref(false), issuesOpen = ref(false)
  function collapseMobilePanels(): void { layoutSettingsOpen.value = false; monitorSettingsOpen.value = false; regionPropertiesOpen.value = false; issuesOpen.value = false }
  watch(isMobile, mobile => { if (mobile) collapseMobilePanels() })
  const layoutConflict = ref(false)
  const form = reactive<{ id?: number; name: string; revision: number; document: HmiLayoutDocument }>({ name: '', revision: 0, document: { monitors: [] } })
  const selectedRegionIndex = ref(0), runtimePreviewVisible = ref(false), legacyConverted = ref(0)
  const regionClipboard = ref<HmiRegion | null>(null)
  const copiedFrom = ref(''), templateKey = ref<LayoutTemplateKey>('dual')
  const templateDocument = computed(() => createLayoutTemplate(templateKey.value, screens.value[0]?.id ?? null))
  const ratioSizes: Record<string, [number, number]> = { '16:9': [1920, 1080], '16:10': [1920, 1200], '4:3': [1280, 960], '9:16': [1080, 1920] }
  const previewRatio = computed({
    get: () => {
      const monitor = form.document.monitors[Number(monitorTab.value)]
      if (!monitor) return '16:9'
      const size = previewSize(monitor)
      return Object.entries(ratioSizes).find(([, [width, height]]) => Math.abs(width / height - size.width / size.height) < .001)?.[0] ?? `${size.width}:${size.height}`
    },
    set: (ratio: string) => {
      const monitor = form.document.monitors[Number(monitorTab.value)], size = ratioSizes[ratio]
      if (monitor && size) setPreviewSize(monitor, size[0], size[1])
    }
  })
  function setPreviewSize(monitor: HmiMonitor, width: number | undefined, height: number | undefined): void {
    if (width == null || height == null) return
    monitor.previewWidth = width; monitor.previewHeight = height; recordHistory()
  }
  const gridControlVersion = ref(0)
  interface GridResize { monitor: HmiMonitor; columns: number; rows: number; affected: string[]; fitted: RegionGeometry[] | null; baseline: string }
  const pendingGrid = ref<GridResize | null>(null)
  const pendingGridPreview = computed<HmiLayoutDocument | null>(() => {
    const pending = pendingGrid.value
    if (!pending?.fitted) return null
    return { monitors: [{ ...pending.monitor, columns: pending.columns, rows: pending.rows, regions: pending.monitor.regions.map((region, index) => ({ ...region, ...pending.fitted![index] })) }] }
  })
  function cancelGridResize(): void { pendingGrid.value = null; gridControlVersion.value++ }
  function changeGridSize(monitor: HmiMonitor, columns: number | undefined, rows: number | undefined): void {
    if (pendingGrid.value || columns == null || rows == null || ![columns, rows].every(value => Number.isInteger(value) && value >= 1 && value <= 24)) { gridControlVersion.value++; return }
    if (monitor.columns === columns && monitor.rows === rows) return
    endDrag(); recordHistory()
    const affected = monitor.regions.filter(region => outOfBounds({ ...monitor, columns, rows }, region)).map(region => region.id || '?')
    if (affected.length) {
      pendingGrid.value = { monitor, columns, rows, affected, fitted: fitRegionsToGrid(monitor, columns, rows), baseline: JSON.stringify(monitor) }
    } else { monitor.columns = columns; monitor.rows = rows; recordHistory(); gridControlVersion.value++ }
  }
  function applyGridResize(fit: boolean): void {
    const pending = pendingGrid.value
    if (!pending || fit && !pending.fitted) return
    if (form.document.monitors.includes(pending.monitor) && JSON.stringify(pending.monitor) === pending.baseline) {
      endDrag(); recordHistory()
      pending.monitor.columns = pending.columns; pending.monitor.rows = pending.rows
      if (fit) pending.fitted!.forEach((geometry, index) => Object.assign(pending.monitor.regions[index], geometry))
      recordHistory()
    }
    cancelGridResize()
  }
  function applyGeometry(monitor: HmiMonitor, geometry: RegionGeometry[]): void {
    if (!geometryChanged(monitor.regions, geometry)) return
    endDrag(); recordHistory()
    geometry.forEach((position, index) => Object.assign(monitor.regions[index], { row: position.row, col: position.col, rowSpan: position.rowSpan, colSpan: position.colSpan }))
    recordHistory()
  }
  const selectedRegion = computed(() => form.document.monitors[Number(monitorTab.value)]?.regions[selectedRegionIndex.value])
  watch(monitorTab, () => { selectedRegionIndex.value = 0 }, { flush: 'sync' })
  interface EditorHistoryEntry { content: string; monitorIndex: number; regionIndex: number }
  const history = ref<EditorHistoryEntry[]>([]), historyIndex = ref(0)
  const snapshot = () => JSON.stringify({ name: form.name, document: form.document })
  const historyEntry = (): EditorHistoryEntry => ({ content: snapshot(), monitorIndex: Number(monitorTab.value), regionIndex: selectedRegionIndex.value })
  function recordHistory(): void {
    if (!visible.value || drag.value) return
    const current = snapshot()
    if (current === history.value[historyIndex.value]?.content) { Object.assign(history.value[historyIndex.value], historyEntry()); return }
    history.value.splice(historyIndex.value + 1)
    history.value.push(historyEntry())
    if (history.value.length > 100) history.value.shift()
    historyIndex.value = history.value.length - 1
  }
  function restoreHistory(index: number): void {
    endDrag()
    const entry = history.value[index]
    const state = JSON.parse(entry.content) as { name: string; document: HmiLayoutDocument }
    form.name = state.name; form.document = state.document; historyIndex.value = index
    monitorTab.value = String(Math.min(entry.monitorIndex, form.document.monitors.length - 1))
    selectedRegionIndex.value = Math.max(0, Math.min(entry.regionIndex, (form.document.monitors[Number(monitorTab.value)]?.regions.length ?? 0) - 1))
    if (!layoutConflict.value) layoutError.value = ''
    void selectRegion(selectedRegionIndex.value)
  }
  function undo(): void { endDrag(); recordHistory(); if (historyIndex.value > 0) restoreHistory(historyIndex.value - 1) }
  function redo(): void { endDrag(); recordHistory(); if (historyIndex.value < history.value.length - 1) restoreHistory(historyIndex.value + 1) }
  function editorShortcut(event: KeyboardEvent): void {
    if (event.defaultPrevented || event.isComposing || event.altKey || saving.value || runtimePreviewVisible.value) return
    const target = event.target as HTMLElement, key = event.key.toLowerCase(), modifier = event.ctrlKey || event.metaKey
    if (modifier && key === 's') { event.preventDefault(); target.closest<HTMLElement>('input, textarea')?.blur(); void nextTick().then(() => save()); return }
    if (target.closest('input, textarea, [contenteditable]:not([contenteditable="false"]), [role="combobox"], [role="listbox"], [role="menu"]')) return
    if (modifier && key === 'z') { event.preventDefault(); event.shiftKey ? redo() : undo(); return }
    if (modifier && key === 'y') { event.preventDefault(); redo(); return }
    const monitor = form.document.monitors[Number(monitorTab.value)]
    if (!monitor) return
    if (modifier && !event.shiftKey && ['c', 'v', 'd'].includes(key)) {
      if (key === 'c' && document.getSelection()?.toString()) return
      event.preventDefault()
      if (key === 'c') copyRegion()
      else if (key === 'v') pasteRegion(monitor)
      else duplicateRegion(monitor)
    } else if (!modifier && !event.shiftKey && ['Delete', 'Backspace'].includes(event.key) && selectedRegion.value) {
      event.preventDefault()
      if (monitor.regions.length <= 1) ElMessage.warning(tr('layoutEditing.keepRegion'))
      else removeRegion(monitor, selectedRegionIndex.value)
    }
  }
  const layoutBaseline = ref(''), stationBaseline = ref('')
  const layoutDirty = computed(() => visible.value && JSON.stringify(form) !== layoutBaseline.value)
  const stationDirty = computed(() => stationVisible.value && JSON.stringify(stationForm) !== stationBaseline.value)
  const auth = useAuthStore()
  const recoveryOwner = computed(() => auth.user ? `${auth.user.id}:${auth.user.userName}` : '')
  const recoveries = ref<LocalLayoutDraft[]>([]), recoveryError = ref(''), recoverySavedAt = ref(0), restoring = ref(false)
  let recoveryTimer: ReturnType<typeof setTimeout> | undefined, recoveryId = '', sessionOwner = '', recoveryBase: LayoutDraftContent | null = null, lastRecoveryContent = ''
  let restoredSource: LocalLayoutDraft | null = null
  function refreshRecoveries(): void {
    const result = readLocalLayoutDrafts(recoveryOwner.value)
    recoveries.value = result.drafts.filter(draft => draft.id !== recoveryId)
    if (result.failed) recoveryError.value = tr('layoutRecovery.storageFailed')
  }
  function persistLocalDraft(): boolean {
    clearTimeout(recoveryTimer)
    if (!visible.value || !layoutDirty.value || !recoveryBase || !sessionOwner || sessionOwner !== recoveryOwner.value) return false
    const content = snapshot()
    if (content === lastRecoveryContent) return true
    const savedAt = Date.now()
    const stored = writeLocalLayoutDraft(sessionOwner, { schema: 1, id: recoveryId, layoutId: form.id ?? null, baseRevision: form.revision, base: recoveryBase, content: normalizeDraftContent(JSON.parse(content) as LayoutDraftContent), savedAt, copiedFrom: copiedFrom.value })
    if (stored) { recoverySavedAt.value = savedAt; lastRecoveryContent = content }
    else recoveryError.value = tr('layoutRecovery.storageFailed')
    return stored
  }
  function forgetLocalDraft(): void {
    clearTimeout(recoveryTimer)
    if (sessionOwner && recoveryId && !removeLocalLayoutDraft(sessionOwner, recoveryId)) recoveryError.value = tr('layoutRecovery.storageFailed')
    if (sessionOwner && restoredSource && !removeLocalLayoutDraft(sessionOwner, restoredSource.id, restoredSource.savedAt)) recoveryError.value = tr('layoutRecovery.storageFailed')
    recoveryId = ''; sessionOwner = ''; restoredSource = null; recoverySavedAt.value = 0; lastRecoveryContent = ''; refreshRecoveries()
  }
  watch([visible, snapshot], () => {
    clearTimeout(recoveryTimer)
    if (!visible.value) return
    if (layoutDirty.value) recoveryTimer = setTimeout(persistLocalDraft, 400)
    else { if (sessionOwner && recoveryId && !removeLocalLayoutDraft(sessionOwner, recoveryId)) recoveryError.value = tr('layoutRecovery.storageFailed'); recoverySavedAt.value = 0; lastRecoveryContent = '' }
  }, { flush: 'post' })
  watch(recoveryOwner, refreshRecoveries, { immediate: true })
  async function discardLocalDraft(draft: LocalLayoutDraft): Promise<void> {
    if (!await confirmDiscard()) return
    if (!removeLocalLayoutDraft(recoveryOwner.value, draft.id, draft.savedAt)) recoveryError.value = tr('layoutRecovery.storageFailed')
    refreshRecoveries()
  }
  async function restoreLocalDraft(draft: LocalLayoutDraft): Promise<void> {
    if (visible.value || restoring.value) return
    restoring.value = true; recoveryError.value = ''
    try {
      let current: HmiLayout | undefined
      if (draft.layoutId != null) {
        try { current = await hmiLayoutApi.get(draft.layoutId, true) }
        catch (reason) { if ((reason as { response?: { status?: number } }).response?.status !== 404) throw reason }
      }
      const changed = draft.layoutId != null && (!current || current.revision !== draft.baseRevision || layoutContentKey(current) !== layoutContentKey(draft.base))
      if (changed) {
        try { await ElMessageBox.confirm(tr(current ? 'layoutRecovery.serverChanged' : 'layoutRecovery.sourceDeleted', [draft.baseRevision, current?.revision ?? '—']), tr('layoutRecovery.restoreCopy'), { type: 'warning', confirmButtonText: tr('layoutRecovery.restoreCopy') }) } catch { return }
      }
      edit(current, changed)
      form.name = changed ? copyName(draft.content.name || tr('layoutRecovery.unnamed'), layouts.value.map(layout => layout.name), 100) : draft.content.name
      form.document = { monitors: JSON.parse(JSON.stringify(draft.content.document.monitors)).map(contentMonitor) }
      copiedFrom.value = changed ? draft.base.name : draft.copiedFrom ?? ''
      restoredSource = draft; recordHistory()
      if (persistLocalDraft()) {
        if (!removeLocalLayoutDraft(sessionOwner, draft.id, draft.savedAt)) recoveryError.value = tr('layoutRecovery.storageFailed')
        restoredSource = null
      }
      refreshRecoveries()
    } catch (reason) { recoveryError.value = errorMessage(reason, tr('layoutRecovery.restoreFailed')) }
    finally { restoring.value = false }
  }
  function flushHiddenDraft(): void { if (document.visibilityState === 'hidden') persistLocalDraft() }
  onMounted(() => { window.addEventListener('storage', refreshRecoveries); document.addEventListener('visibilitychange', flushHiddenDraft) })
  onBeforeUnmount(() => { persistLocalDraft(); clearTimeout(recoveryTimer); window.removeEventListener('storage', refreshRecoveries); document.removeEventListener('visibilitychange', flushHiddenDraft) })
  async function confirmDiscard(): Promise<boolean> {
    try { await ElMessageBox.confirm(tr('layoutMonitor.discardChanges'), tr('layoutMonitor.unsaved'), { type: 'warning' }); return true } catch { return false }
  }
  async function closeLayout(done?: () => void): Promise<void> {
    if (saving.value || (layoutDirty.value && !await confirmDiscard())) return
    endDrag(); forgetLocalDraft(); if (done) done(); else visible.value = false
  }
  async function closeStation(done?: () => void): Promise<void> {
    if (stationSaving.value || (stationDirty.value && !await confirmDiscard())) return
    if (done) done(); else stationVisible.value = false
  }
  onBeforeRouteLeave(async () => { if (!(layoutDirty.value || stationDirty.value)) return true; if (!await confirmDiscard()) return false; forgetLocalDraft(); return true })
  function beforeUnload(event: BeforeUnloadEvent): void { persistLocalDraft(); if (layoutDirty.value || stationDirty.value) { event.preventDefault(); event.returnValue = '' } }
  function edit(row?: HmiLayout, asCopy = false): void {
    endDrag()
    editorAdvanced.value = false
    cancelGridResize(); collapseMobilePanels()
    copiedFrom.value = asCopy && row ? row.name : ''; templateKey.value = 'dual'
    Object.assign(form, row ? { id: asCopy ? undefined : row.id, name: asCopy ? copyName(row.name, layouts.value.map(layout => layout.name), 100) : row.name, revision: asCopy ? 0 : row.revision, document: JSON.parse(JSON.stringify(row.document)) } : { id: undefined, name: '', revision: 0, document: { monitors: [] } })
    if (!form.document.monitors.length) preset()
    layoutError.value = ''; layoutConflict.value = false
    layoutBaseline.value = JSON.stringify(form)
    clearTimeout(recoveryTimer); recoveryId = window.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`; sessionOwner = recoveryOwner.value
    recoveryBase = normalizeDraftContent(JSON.parse(snapshot()) as LayoutDraftContent); recoverySavedAt.value = 0; lastRecoveryContent = ''; restoredSource = null
    legacyConverted.value = form.document.monitors.filter(m => m.regions.some(r => r.kind === 'alarmBanner')).length
    if (legacyConverted.value) form.document.monitors = form.document.monitors.map(contentMonitor)
    void loadScopeCatalog()
    monitorTab.value = '0'; selectedRegionIndex.value = 0; regionClipboard.value = null; visible.value = true
    history.value = [historyEntry()]; historyIndex.value = 0
  }
  async function reloadSavedDraft(): Promise<void> {
    if (!form.id || saving.value || (layoutDirty.value && !await confirmDiscard())) return
    try { const current = await hmiLayoutApi.get(form.id, true); forgetLocalDraft(); edit(current) }
    catch (reason) { layoutError.value = errorMessage(reason, tr('layoutLifecycle.loadFailed')) }
  }
  async function replacePreset(): Promise<void> {
    try { await ElMessageBox.confirm(tr('layoutMonitor.replacePreset'), tr('layoutCatalog.applyTemplate'), { type: 'warning' }) } catch { return }
    endDrag(); preset(); monitorTab.value = '0'; selectedRegionIndex.value = 0; recordHistory()
  }
  async function removeMonitor(index: number): Promise<void> {
    try { await ElMessageBox.confirm(tr('layoutMonitor.removeMonitor'), tr('layoutsView.k33160a'), { type: 'warning' }) } catch { return }
    endDrag(); form.document.monitors.splice(index, 1); monitorTab.value = '0'; selectedRegionIndex.value = 0; recordHistory()
  }
  function preset(): void {
    legacyConverted.value = 0
    form.document = createLayoutTemplate(templateKey.value, screens.value[0]?.id ?? null)
  }
  function copyName(base: string, existing: string[], limit: number): string {
    for (let index = 1; ; index++) {
      const suffix = ` ${tr('layoutCatalog.copySuffix')}${index === 1 ? '' : ` ${index}`}`
      const name = base.slice(0, Math.max(0, limit - suffix.length)) + suffix
      if (!existing.includes(name)) return name
    }
  }
  function duplicateMonitor(index: number): void {
    if (form.document.monitors.length >= 8) return
    endDrag()
    const monitor = JSON.parse(JSON.stringify(form.document.monitors[index])) as HmiMonitor
    monitor.name = copyName(monitor.name, form.document.monitors.map(item => item.name), 40)
    form.document.monitors.push(monitor); monitorTab.value = String(form.document.monitors.length - 1); selectedRegionIndex.value = 0; recordHistory()
  }
  function moveMonitor(index: number, direction: -1 | 1): void {
    const destination = index + direction
    if (destination < 0 || destination >= form.document.monitors.length) return
    endDrag(); recordHistory()
    const regionIndex = selectedRegionIndex.value
    const [monitor] = form.document.monitors.splice(index, 1)
    form.document.monitors.splice(destination, 0, monitor)
    monitorTab.value = String(destination); selectedRegionIndex.value = regionIndex; recordHistory()
  }
  function pasteIssue(monitor: HmiMonitor, region: HmiRegion | null | undefined): string {
    if (!region) return tr('layoutEditing.clipboardEmpty')
    if (monitor.regions.length >= 16) return tr('layoutEditing.regionLimit')
    return findRegionSpace(monitor, region) ? '' : tr('layoutEditing.noRoom')
  }
  function copyRegion(): void {
    if (!selectedRegion.value) return
    endDrag(); regionClipboard.value = JSON.parse(JSON.stringify(selectedRegion.value)) as HmiRegion
  }
  function insertRegionCopy(monitor: HmiMonitor, source: HmiRegion | null | undefined): void {
    endDrag(); recordHistory()
    const issue = pasteIssue(monitor, source)
    if (issue || !source) { ElMessage.warning(issue); return }
    const position = findRegionSpace(monitor, source)!
    const region = JSON.parse(JSON.stringify(source)) as HmiRegion
    Object.assign(region, position, { id: copiedRegionId(region.id, monitor) })
    monitor.regions.push(region); selectedRegionIndex.value = monitor.regions.length - 1; recordHistory()
    void selectRegion(selectedRegionIndex.value)
  }
  function pasteRegion(monitor: HmiMonitor): void { insertRegionCopy(monitor, regionClipboard.value) }
  function duplicateRegion(monitor: HmiMonitor): void { insertRegionCopy(monitor, selectedRegion.value) }
  function addMonitor(): void {
    if (form.document.monitors.length >= 8) return
    endDrag(); form.document.monitors.push({ name: '', columns: 12, rows: 12, regions: [{ id: 'main', kind: 'screen', screenId: screens.value[0]?.id ?? null, row: 1, col: 1, rowSpan: 12, colSpan: 12 }] })
    monitorTab.value = String(form.document.monitors.length - 1); selectedRegionIndex.value = 0; recordHistory()
  }
  function freeCell(m: HmiMonitor): { row: number; col: number } | null {
    for (let row = 1; row <= m.rows; row++) for (let col = 1; col <= m.columns; col++) {
      if (!m.regions.some(r => row >= r.row && row < r.row + r.rowSpan && col >= r.col && col < r.col + r.colSpan)) return { row, col }
    }
    return null
  }
  function addRegion(m: HmiMonitor): void {
    const position = freeCell(m)
    if (m.regions.length >= 16 || !position) return
    let n = m.regions.length + 1; while (m.regions.some(r => r.id === `r${n}`)) n++
    m.regions.push({ id: `r${n}`, kind: 'screen', screenId: screens.value[0]?.id ?? null, ...position, rowSpan: 1, colSpan: 1 })
    selectedRegionIndex.value = m.regions.length - 1; recordHistory()
    void selectRegion(selectedRegionIndex.value)
  }
  function removeRegion(m: HmiMonitor, index: number): void {
    if (m.regions.length <= 1) return
    endDrag(); m.regions.splice(index, 1); selectedRegionIndex.value = Math.min(index, m.regions.length - 1); recordHistory()
  }
  async function selectRegion(index: number): Promise<void> {
    selectedRegionIndex.value = index
    await nextTick()
    const region = document.querySelectorAll<HTMLElement>('[data-test="layout-preview"] [data-test="layout-region"]')[index]
    ;(region ?? document.querySelector<HTMLElement>('.layout-editor'))?.focus({ preventScroll: true })
  }

  type DragMode = 'move' | 'resize'
  interface DragState {
    mode: DragMode
    monitor: HmiMonitor
    region: HmiRegion
    startX: number
    startY: number
    row: number
    col: number
    rowSpan: number
    colSpan: number
    cellW: number
    cellH: number
  }
  const drag = ref<DragState | null>(null)
  const clamp = (value: number, min: number, max: number) => Math.min(Math.max(value, min), max)

  function gridStyle(m: HmiMonitor): Record<string, string> {
    return { gridTemplateColumns: `repeat(${Math.max(1, m.columns)}, minmax(0, 1fr))`, gridTemplateRows: `repeat(${Math.max(1, m.rows)}, minmax(0, 1fr))`, '--grid-cols': String(Math.max(1, m.columns)), '--grid-rows': String(Math.max(1, m.rows)) }
  }
  function previewFrameStyle(m: HmiMonitor): Record<string, string> {
    const { width, height } = previewSize(m)
    return { aspectRatio: `${width} / ${height}`, maxWidth: `${(isMobile.value ? 34 : 62) * width / height}dvh` }
  }
  function regionStyle(r: HmiRegion): Record<string, string> {
    return { gridColumn: `${r.col} / span ${r.colSpan}`, gridRow: `${r.row} / span ${r.rowSpan}` }
  }
  function overlaps(a: HmiRegion, b: HmiRegion): boolean {
    return a.row < b.row + b.rowSpan && b.row < a.row + a.rowSpan && a.col < b.col + b.colSpan && b.col < a.col + a.colSpan
  }
  function outOfBounds(m: HmiMonitor, r: HmiRegion): boolean {
    return ![r.row, r.col, r.rowSpan, r.colSpan].every(n => Number.isInteger(n) && n > 0) || r.row + r.rowSpan - 1 > m.rows || r.col + r.colSpan - 1 > m.columns
  }
  function regionOverlaps(m: HmiMonitor, index: number): boolean {
    return m.regions.some((other, i) => i !== index && overlaps(m.regions[index], other))
  }
  interface LayoutIssue { monitorIndex: number; regionIndex?: number; target?: string; message: string }
  function alarmScope(region: HmiRegion): 'all' | 'area' | 'device' | 'screen' {
    if (region.alarmDeviceId) return 'device'
    if (region.alarmScreenId) return 'screen'
    if (region.alarmAreaId) return 'area'
    return 'all'
  }
  function setAlarmScope(region: HmiRegion, scope: string): void {
    region.alarmAreaId = scope === 'area' ? region.alarmAreaId ?? areas.value[0]?.id ?? null : null
    region.alarmDeviceId = scope === 'device' ? region.alarmDeviceId ?? devices.value[0]?.id ?? null : null
    region.alarmScreenId = scope === 'screen' ? region.alarmScreenId ?? screens.value[0]?.id ?? null : null
    recordHistory()
  }
  function onRegionKind(region: HmiRegion): void {
    if (region.kind !== 'alarmList') { region.alarmAreaId = null; region.alarmDeviceId = null; region.alarmScreenId = null }
    recordHistory()
  }
  async function loadScopeCatalog(): Promise<void> {
    scopeCatalogReady.value = false
    const [areaResult, deviceResult] = await Promise.allSettled([areaApi.list(), deviceApi.list()])
    if (areaResult.status === 'fulfilled') areas.value = areaResult.value
    if (deviceResult.status === 'fulfilled') devices.value = deviceResult.value
    scopeCatalogError.value = areaResult.status === 'rejected' || deviceResult.status === 'rejected' ? tr('layoutScope.catalogFailed') : ''
    scopeCatalogReady.value = true
  }
  function validScreen(r: HmiRegion): boolean { return screens.value.some(s => s.id === r.screenId) }
  function screenLabel(r: HmiRegion): string { return screens.value.find(s => s.id === r.screenId)?.name || tr('layoutImprovements.unavailableScreen', [r.screenId ?? '—']) }
  function monitorIssues(m: HmiMonitor, monitorIndex: number): LayoutIssue[] {
    const issues: LayoutIssue[] = []
    const add = (message: string, regionIndex?: number, target?: string) => issues.push({ monitorIndex, regionIndex, target, message: tr('layoutsView.monitorIssue', [m.name || monitorIndex + 1, message]) })
    if ((m.name?.length ?? 0) > 40) add(tr('layoutImprovements.monitorNameLimit'), undefined, 'monitor-name')
    if (![m.columns, m.rows].every(n => Number.isInteger(n) && n >= 1 && n <= 24)) add(tr('layoutImprovements.gridLimit'), undefined, 'monitor-columns')
    if (!validPreviewSize(m)) add(tr('layoutPreview.sizeRule'), undefined, 'monitor-preview-width')
    if (m.regions.length < 1 || m.regions.length > 16) add(tr('layoutImprovements.regionLimit'))
    const ids = new Set<string>()
    m.regions.forEach((r, i) => {
      if (!r.id?.trim() || r.id.length > 40 || ids.has(r.id)) add(tr('layoutImprovements.regionIdRule', [r.id || '?']), i, 'region-id')
      ids.add(r.id)
      if (!Object.prototype.hasOwnProperty.call(kinds, r.kind)) add(tr('layoutImprovements.invalidKind', [r.id]), i, 'region-kind')
      if (outOfBounds(m, r)) add(tr('layoutsView.regionOut', [r.id, m.columns, m.rows]), i, 'region-row')
      if (r.kind === 'screen' && (!r.screenId || (!screensError.value && !validScreen(r)))) add(tr('layoutsView.regionScreen', [r.id]), i, 'region-screen')
      const scopes = [r.alarmAreaId, r.alarmDeviceId, r.alarmScreenId].filter(id => id != null)
      if (scopes.length > 1 || (r.kind !== 'alarmList' && scopes.length > 0)) add(tr('layoutScope.invalid', [r.id]), i, 'region-alarm-scope')
      if (r.alarmAreaId != null && scopeCatalogReady.value && !scopeCatalogError.value && !areas.value.some(area => area.id === r.alarmAreaId)) add(tr('layoutScope.areaMissing', [r.id]), i, 'region-alarm-area')
      if (r.alarmDeviceId != null && scopeCatalogReady.value && !scopeCatalogError.value && !devices.value.some(device => device.id === r.alarmDeviceId)) add(tr('layoutScope.deviceMissing', [r.id]), i, 'region-alarm-device')
      if (r.alarmScreenId != null && !screensError.value && !screens.value.some(screen => screen.id === r.alarmScreenId)) add(tr('layoutScope.screenMissing', [r.id]), i, 'region-alarm-screen')
      if ((r.title?.length ?? 0) > 60) add(tr('layoutImprovements.titleLimit', [r.id]), i, 'region-title')
      const previous = m.regions.findIndex((other, oi) => oi < i && overlaps(other, r))
      if (previous >= 0) add(tr('layoutsView.regionOverlap', [r.id, m.regions[previous].id]), i)
    })
    if (misplacedAlarmBanners(m)) add(tr('layoutScope.bannerTop'))
    return issues
  }
  const layoutIssues = computed<LayoutIssue[]>(() => {
    const issues = form.document.monitors.flatMap((m, i) => monitorIssues(m, i))
    if (form.document.monitors.length < 1 || form.document.monitors.length > 8) issues.unshift({ monitorIndex: 0, message: tr('layoutImprovements.monitorLimit') })
    return issues
  })
  const draftShapeError = computed(() => {
    if (!form.name.trim() || form.name.length > 100) return tr('layoutLifecycle.nameRule')
    if (form.document.monitors.length < 1 || form.document.monitors.length > 8) return tr('layoutImprovements.monitorLimit')
    for (const monitor of form.document.monitors) {
      if (!validPreviewSize(monitor)) return tr('layoutPreview.sizeRule')
      if ((monitor.name?.length ?? 0) > 40) return tr('layoutImprovements.monitorNameLimit')
      if (![monitor.rows, monitor.columns].every(n => Number.isInteger(n) && n >= 1 && n <= 24)) return tr('layoutImprovements.gridLimit')
      if (monitor.regions.length > 16) return tr('layoutLifecycle.draftRegionLimit')
      for (const region of monitor.regions) {
        if ((region.id?.length ?? 0) > 40 || (region.title?.length ?? 0) > 60 || !Object.prototype.hasOwnProperty.call(kinds, region.kind)) return tr('layoutLifecycle.draftShapeRule')
        if (![region.row, region.col, region.rowSpan, region.colSpan].every(n => Number.isInteger(n) && n >= -2147483648 && n <= 2147483647)) return tr('layoutLifecycle.geometryInteger')
      }
    }
    return ''
  })
  function validPreviewSize(monitor: HmiMonitor): boolean {
    if (monitor.previewWidth == null && monitor.previewHeight == null) return true
    return Number.isInteger(monitor.previewWidth) && Number.isInteger(monitor.previewHeight) && monitor.previewWidth! >= 320 && monitor.previewWidth! <= 7680 && monitor.previewHeight! >= 240 && monitor.previewHeight! <= 4320
  }
  async function focusIssue(issue: LayoutIssue): Promise<void> {
    monitorTab.value = String(issue.monitorIndex)
    if (issue.target?.startsWith('monitor-')) monitorSettingsOpen.value = true
    if (issue.regionIndex != null) regionPropertiesOpen.value = true
    if (issue.target && ['monitor-columns', 'monitor-preview-width', 'region-id', 'region-row'].includes(issue.target)) editorAdvanced.value = true
    await nextTick()
    if (issue.regionIndex != null) selectedRegionIndex.value = issue.regionIndex
    await nextTick()
    if (issue.target) {
      const control = [...document.querySelectorAll<HTMLElement>(`.layout-editor [data-test="${issue.target}"]`)].map(el => el.matches('input') ? el : el.querySelector<HTMLElement>('input')).find(el => el && el.getClientRects().length)
      if (control) { control.focus(); control.scrollIntoView({ block: 'nearest' }); return }
    }
    if (issue.regionIndex != null) await selectRegion(issue.regionIndex)
  }

  function beginDrag(e: PointerEvent, m: HmiMonitor, r: HmiRegion, mode: DragMode, index: number): void {
    if (e.button !== 0) return
    endDrag()
    selectedRegionIndex.value = index
    const preview = (e.currentTarget as HTMLElement).closest('.preview') as HTMLElement | null
    if (!preview) return
    const rect = preview.getBoundingClientRect()
    const css = getComputedStyle(preview)
    const gap = parseFloat(css.columnGap) || 0
    const rowGap = parseFloat(css.rowGap) || 0
    recordHistory()
    drag.value = {
      mode, monitor: m, region: r,
      startX: e.clientX, startY: e.clientY,
      row: r.row, col: r.col, rowSpan: r.rowSpan, colSpan: r.colSpan,
      cellW: (rect.width - parseFloat(css.paddingLeft) - parseFloat(css.paddingRight) + gap) / Math.max(1, m.columns),
      cellH: (rect.height - parseFloat(css.paddingTop) - parseFloat(css.paddingBottom) + rowGap) / Math.max(1, m.rows)
    }
    window.addEventListener('pointermove', onDragMove)
    window.addEventListener('pointerup', endDrag, { once: true })
    window.addEventListener('pointercancel', endDrag, { once: true })
    ;(e.currentTarget as HTMLElement).closest<HTMLElement>('[data-test="layout-region"]')?.focus({ preventScroll: true })
    e.preventDefault()
  }
  function startDrag(e: PointerEvent, m: HmiMonitor, r: HmiRegion, index: number): void { beginDrag(e, m, r, 'move', index) }
  function startResize(e: PointerEvent, m: HmiMonitor, r: HmiRegion, index: number): void { beginDrag(e, m, r, 'resize', index) }
  function onDragMove(e: PointerEvent): void {
    const d = drag.value
    if (!d) return
    const dx = Math.round((e.clientX - d.startX) / d.cellW)
    const dy = Math.round((e.clientY - d.startY) / d.cellH)
    if (d.mode === 'move') {
      d.region.col = clamp(d.col + dx, 1, d.monitor.columns - d.region.colSpan + 1)
      d.region.row = clamp(d.row + dy, 1, d.monitor.rows - d.region.rowSpan + 1)
    } else {
      d.region.colSpan = clamp(d.colSpan + dx, 1, d.monitor.columns - d.region.col + 1)
      d.region.rowSpan = clamp(d.rowSpan + dy, 1, d.monitor.rows - d.region.row + 1)
    }
  }
  function endDrag(): void {
    const wasDragging = !!drag.value
    drag.value = null
    window.removeEventListener('pointermove', onDragMove)
    window.removeEventListener('pointerup', endDrag)
    window.removeEventListener('pointercancel', endDrag)
    if (wasDragging) recordHistory()
  }
  function regionKeydown(event: KeyboardEvent, m: HmiMonitor, r: HmiRegion, index: number): void {
    selectedRegionIndex.value = index
    if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); return }
    const direction = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key]
    if (!direction || event.ctrlKey || event.metaKey || event.altKey) return
    event.preventDefault()
    if (event.shiftKey) {
      r.colSpan = clamp(r.colSpan + direction[0], 1, Math.max(1, m.columns - r.col + 1))
      r.rowSpan = clamp(r.rowSpan + direction[1], 1, Math.max(1, m.rows - r.row + 1))
    } else {
      r.col = clamp(r.col + direction[0], 1, Math.max(1, m.columns - r.colSpan + 1))
      r.row = clamp(r.row + direction[1], 1, Math.max(1, m.rows - r.rowSpan + 1))
    }
    recordHistory()
  }
  onBeforeUnmount(endDrag)
  async function save(reviewPublication = false): Promise<void> {
    if (saving.value || draftShapeError.value || layoutConflict.value) return
    saving.value = true
    try {
      const saved = await hmiLayoutApi.save({ ...form, name: form.name.trim(), document: JSON.parse(JSON.stringify(form.document)) })
      lastSavedId.value = saved.id
      forgetLocalDraft()
      ElMessage.success(tr('workbench.draftSaved'))
      visible.value = false
      await load()
      if (reviewPublication) await lifecycle.value?.openPublish(saved.id)
    } catch (reason) {
      layoutConflict.value = (reason as { response?: { status?: number } }).response?.status === 409
      layoutError.value = layoutConflict.value ? tr('layoutLifecycle.draftConflict') : errorMessage(reason, tr('layoutsView.k40525a'))
    } finally { saving.value = false }
  }
  async function remove(row: HmiLayout): Promise<void> {
    const assigned = layoutStations(row.id)
    const impact = stationsError.value ? tr('layoutImprovements.usageUnknown') : assigned.length ? tr('layoutImprovements.deleteImpact', [assigned.map(s => s.name).join('、')]) : ''
    try { await ElMessageBox.confirm([tr('layoutsView.kb8c7a3', [row.name]), impact].filter(Boolean).join('\n'), tr('layoutsView.k3755f5'), { type: 'warning' }) } catch { return }
    if (await guardDelete(() => hmiLayoutApi.remove(row.id, row.revision))) await load()
  }

  const stationVisible = ref(false)
  const stationForm = reactive<{ id?: number; name: string; description: string | null; layoutId: number | null; allowControl: boolean; expectedUpdatedTs?: number }>({ name: '', description: null, layoutId: null, allowControl: true })
  function editStation(row?: HmiWorkstation): void {
    Object.assign(stationForm, { id: row?.id, name: row?.name ?? '', description: row?.description ?? null, layoutId: row ? row.layoutId : publishedLayouts.value[0]?.id ?? null, allowControl: row?.allowControl ?? true, expectedUpdatedTs: row?.updatedTs })
    stationBaseline.value = JSON.stringify(stationForm); stationError.value = ''; stationConflict.value = false; stationVisible.value = true
  }
  async function reloadStation(): Promise<void> {
    if (!stationForm.id || stationSaving.value || (stationDirty.value && !await confirmDiscard())) return
    try {
      const current = await hmiLayoutApi.workstations(), station = current.find(item => item.id === stationForm.id)
      stations.value = current; stationsError.value = ''
      const match = current.find(isLocalWorkstation)
      if (match) { setLocalWorkstation(match); syncLocal() }
      if (!station) { stationError.value = tr('layoutSafety.stationDeleted'); return }
      editStation(station)
    } catch (reason) { stationError.value = errorMessage(reason, tr('layoutMonitor.stationsFailed')) }
  }
  async function saveStation(): Promise<void> {
    if (stationSaving.value || stationConflict.value) return
    stationForm.name = stationForm.name.trim()
    if (!/^[A-Za-z0-9_.-]{1,60}$/.test(stationForm.name)) { stationError.value = tr('layoutsView.stationNameRule'); return }
    if ((stationForm.description?.length ?? 0) > 200) { stationError.value = tr('layoutImprovements.descriptionLimit'); return }
    if (stationForm.layoutId != null && !publishedLayouts.value.some(l => l.id === stationForm.layoutId)) { stationError.value = tr('layoutPreview.legacyBinding'); return }
    stationSaving.value = true
    const wasLocal = stations.value.some(s => s.id === stationForm.id && isLocalWorkstation(s))
    let savedStation: HmiWorkstation | undefined
    const saved = await runGuarded(async () => {
      try { savedStation = await hmiLayoutApi.saveWorkstation({ ...stationForm, layoutId: stationForm.layoutId ?? null }) }
      catch (reason) { stationConflict.value = isConflict(reason); throw reason }
    }, tr('layoutsView.k40525a'), stationError)
    stationSaving.value = false
    if (saved) {
      if (wasLocal && savedStation) { setLocalWorkstation(savedStation); syncLocal() }
      ElMessage.success(tr('layoutsView.kcdfab9')); stationVisible.value = false; await load()
    }
  }
  async function removeStation(row: HmiWorkstation): Promise<void> {
    try { await ElMessageBox.confirm(tr('layoutsView.k54ad42', [row.name]), tr('layoutsView.k3755f5'), { type: 'warning' }) } catch { return }
    if (await guardDelete(() => hmiLayoutApi.removeWorkstation(row.id, row.updatedTs))) {
      if (isLocalWorkstation(row)) { setLocalWorkstation(null); syncLocal() }
      await load()
    }
  }
  function setLocal(station: HmiWorkstation | null): void { setLocalWorkstation(station); syncLocal(); ElMessage.success(station ? tr('layoutsView.k0a2885', [station.name]) : tr('layoutsView.k7507bc')) }
  function syncLocal(): void { local.value = getBinding() }
  onMounted(() => { void load(); window.addEventListener('beforeunload', beforeUnload) })
  onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload))
  onMounted(() => window.addEventListener('storage', syncLocal))
  onBeforeUnmount(() => window.removeEventListener('storage', syncLocal))
  return {
    router, popups, rememberPopup, closePopups, lifecycle, quickCreate, layouts, stations, screens, areas, devices, loadError, layoutError, stationError, scopeCatalogError, scopeCatalogReady, lastSavedId, savedDraft, rememberSavedDraft, actionError, runGuarded, deleteConflict, stationConflict, isConflict, guardDelete, reloadAfterDeleteConflict, stationsError, screensError, loading, saving, stationSaving, editableKinds, getBinding, local, localName, localInvalid, layoutStations, publishedLayouts, screenNames, localStation, localLayout, localReady, localSummaryMessage, layoutSearch, statusFilter, usageFilter, filteredLayouts, resetFilters, monitorSummary, stationsPanel, focusStations, stationLayoutLabel, load, visible, monitorTab, editorAdvanced, toggleAdvancedSettings, layoutSettingsOpen, monitorSettingsOpen, regionPropertiesOpen, issuesOpen, collapseMobilePanels, layoutConflict, form, selectedRegionIndex, runtimePreviewVisible, legacyConverted, regionClipboard, copiedFrom, templateKey, templateDocument, previewRatio, setPreviewSize, gridControlVersion, pendingGrid, pendingGridPreview, cancelGridResize, changeGridSize, applyGridResize, applyGeometry, selectedRegion, history, historyIndex, snapshot, historyEntry, recordHistory, restoreHistory, undo, redo, editorShortcut, layoutBaseline, stationBaseline, layoutDirty, stationDirty, auth, recoveryOwner, recoveries, recoveryError, recoverySavedAt, restoring, refreshRecoveries, persistLocalDraft, forgetLocalDraft, discardLocalDraft, restoreLocalDraft, flushHiddenDraft, confirmDiscard, closeLayout, closeStation, beforeUnload, edit, reloadSavedDraft, replacePreset, removeMonitor, preset, copyName, duplicateMonitor, moveMonitor, pasteIssue, copyRegion, insertRegionCopy, pasteRegion, duplicateRegion, addMonitor, freeCell, addRegion, removeRegion, selectRegion, drag, clamp, gridStyle, previewFrameStyle, regionStyle, overlaps, outOfBounds, regionOverlaps, alarmScope, setAlarmScope, onRegionKind, loadScopeCatalog, validScreen, screenLabel, monitorIssues, layoutIssues, draftShapeError, validPreviewSize, focusIssue, beginDrag, startDrag, startResize, onDragMove, endDrag, regionKeydown, save, remove, stationVisible, stationForm, editStation, reloadStation, saveStation, removeStation, setLocal, syncLocal, ref, isLocalWorkstation, formatTime, isMobile, layoutTemplateKeys, kinds
  }
}
