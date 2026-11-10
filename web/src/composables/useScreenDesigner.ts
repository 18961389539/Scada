import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import Moveable from 'moveable'
import Selecto from 'selecto'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { screenApi, type ScreenPublicationResult } from '../api/screens'
import { deviceApi, tagApi } from '../api'
import { recipeApi, type Recipe, type RecipeTemplate } from '../api/recipes'
import { controlApi, type ControlSequence } from '../api/control'
import { calcJobApi, type CalcJobInfo } from '../api/calcJobs'
import { errorMessage } from '../api/http'
import { arrangeNodes, clampNode, cleanNode, controlTypes, createNode, createSymbolNode, createTemplateNode, defaultFontSizes, EditHistory, emptyDocument, fontSizeTypes, palette, pipeGeometry, portPoint, readableDocument, SEPARATOR, translateNodes } from '../screens/model'
import type { ArrangeOperation, LibraryItem, NodeType, ScreenAction, ScreenAnimation, ScreenDetail, ScreenDocument, ScreenIssue, ScreenNode, ScreenPort, ScreenSummary } from '../screens/model'
import type { DesignerPanelApi, PanelIssue } from '../screens/designerPanelApi'
import { builtinSymbols, symbolCategories, type BuiltinSymbol } from '../screens/symbols'
import { useLibraryStore } from '../stores/library'
import { runtimeReturnPath } from '../screens/navigation'
import { hmiLibraryApi } from '../api/hmiLibrary'
import { useI18n } from 'vue-i18n'
import type { Device, Tag } from '../types'
import { formatTime, formatValue, qualityLabels } from '../utils'
import { useDesignerTagValues } from '../composables/useDesignerTagValues'
import { useDesignerDraft } from '../composables/useDesignerDraft'
import { tr } from '../i18n'
import { openShortcutHelp, registerShortcuts } from '../shortcuts/registry'
import { ArrowDown, ArrowUp, Close, Hide, Lock, Menu as MenuIcon, Operation, Setting, Unlock, View } from '@element-plus/icons-vue'

export function useScreenDesigner(props: { embedded: boolean; modelValue?: ScreenDocument; bindingTags?: Tag[]; disabled: boolean }, emit: { (e: 'update:modelValue', document: ScreenDocument): void; (e: 'save'): void }) {
  const route = useRoute(), router = useRouter(), id = Number(route.params.id)
  const returnToRuntime = computed(() => runtimeReturnPath(route.query.returnTo, id))
  const runtimePath = computed(() => returnToRuntime.value ?? `/screens/${id}`)
  const document = ref<ScreenDocument>(emptyDocument()), name = ref(''), revision = ref(1), publishedRevision = ref<number | null>(null)
  const loading = ref(true), loadError = ref(''), actionError = ref(''), guardError = ref(''), busy = ref(false), zoom = ref(.75), snap = ref(true), selectedIds = ref<string[]>([])
  const tags = ref<Tag[]>([]), devices = ref<Device[]>([]), screens = ref<ScreenSummary[]>([]), stage = ref<HTMLDivElement>(), propertiesPanel = ref<HTMLElement>()
  const recipeTemplates = ref<RecipeTemplate[]>([]), recipes = ref<Recipe[]>([]), sequences = ref<ControlSequence[]>([]), calcJobs = ref<CalcJobInfo[]>([])
  const previewVisible = ref(false), issuesVisible = ref(false), issues = ref<ScreenIssue[]>([]), issuesError = ref(''), savedAt = ref('')
  interface ConflictRow { key: string; nodeId: string; kind: 'local' | 'server' | 'modified'; label: string; keep: boolean; choice: 'local' | 'server' | '' }
  const publishDiffVisible = ref(false)
  const publishDiff = ref<{ added: string[]; removed: string[]; changed: string[] }>({ added: [], removed: [], changed: [] })
  const publishDiffExtra = computed(() => Math.max(0, publishDiff.value.added.length + publishDiff.value.removed.length + publishDiff.value.changed.length - 24))
  let publishDiffResolve: ((accepted: boolean) => void) | null = null
  let publishDiffSettled = false
  const conflictVisible = ref(false), conflict = ref<{ server: ScreenDetail; rows: ConflictRow[] } | null>(null)
  const conflictRows = computed(() => conflict.value?.rows ?? [])
  const conflictNeedsChoice = computed(() => conflictRows.value.some(row => row.kind === 'modified' && !row.choice))
  const versionsVisible = ref(false), versionsSubmitting = ref(false), rollbackSucceeded = ref(false), rollbackWarning = ref('')
  function setVersionsBusy(value: boolean): void { versionsSubmitting.value = value; busy.value = value }
  function applyRollback(result: ScreenPublicationResult): void {
    revision.value = result.revision; publishedRevision.value = result.publishedRevision
    actionError.value = ''; rollbackSucceeded.value = true; rollbackWarning.value = result.warning ?? ''
  }
  const history = reactive(new EditHistory()), savedSnapshot = ref('')
  let stableSnapshot = '', clipboard: ScreenNode[] = [], drag: { x: number; y: number; nodes: ScreenNode[]; resize: boolean; moved: boolean } | null = null
  let moveable: Moveable | null = null, selecto: Selecto | null = null
  let disposed = false
  const selected = computed(() => document.value.nodes.find(n => n.id === selectedIds.value[0]))
  const selectedNodes = computed(() => document.value.nodes.filter(node => selectedIds.value.includes(node.id)))
  const isControlSelected = computed(() => !!selected.value && controlTypes.includes(selected.value.type))
  // 只在真正使用单测点的图元上显示数据/量程字段，避免文本、时钟、图表等出现无关配置。
  const dataBindingTypes: NodeType[] = ['value', 'symbol', 'pump', 'valve', 'tank', 'gauge', 'dial', 'motor', 'fan', 'led', 'progress', 'kpi', 'equipment-panel', 'pipe', 'polyline']
  const typeSettingTypes: NodeType[] = ['polyline', 'pipe', 'symbol', 'pump', 'valve', 'motor', 'fan', 'tank', 'gauge', 'dial', 'equipment-panel', 'image', 'chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal', 'chart-bar-stack', 'chart-area', 'chart-radar', 'chart-funnel', 'chart-gauge', 'table', 'history-table', 'oee-kpi', 'selector', 'knob', 'reset', 'silence', 'recipe']
  const labelTypes: NodeType[] = ['text', 'button', 'rectangle', 'ellipse', 'marquee', 'symbol', 'pump', 'valve', 'motor', 'fan', 'tank', 'equipment-panel', 'input', 'kpi']
  const hasDataSection = computed(() => !!selected.value && (isControlSelected.value || dataBindingTypes.includes(selected.value.type)))
  const hasActionSection = computed(() => !!selected.value && !isControlSelected.value && !['template', 'reset', 'silence', 'recipe'].includes(selected.value.type))
  const hasTypeSection = computed(() => !!selected.value && (selected.value.type === 'template' || typeSettingTypes.includes(selected.value.type)))
  const showEnglishText = computed(() => !!selected.value && labelTypes.includes(selected.value.type))
  const showFontSize = computed(() => !!selected.value && fontSizeTypes.includes(selected.value.type))
  const batchShowFontSize = computed(() => selectedNodes.value.some(node => fontSizeTypes.includes(node.type)))
  // 文字样式作用于主文字图元；形状样式作用于矩形/按钮/椭圆。
  const textStyleNodeTypes: NodeType[] = ['text', 'value', 'button', 'rectangle', 'ellipse']
  const shapeStyleNodeTypes: NodeType[] = ['rectangle', 'button', 'ellipse']
  const showTextStyle = computed(() => !!selected.value && textStyleNodeTypes.includes(selected.value.type))
  const showShapeStyle = computed(() => !!selected.value && shapeStyleNodeTypes.includes(selected.value.type))
  const showStyleSection = computed(() => showTextStyle.value || showShapeStyle.value)
  function setAlign(value: string | number | boolean | undefined): void {
    if (!selected.value) return
    selected.value.align = value === 'left' || value === 'right' ? value : null
    commitField('align')
  }
  function setStyleFlag(key: 'wrap' | 'autoFit' | 'bold', value: string | number | boolean | undefined): void {
    if (!selected.value) return
    selected.value[key] = value ? true : null
    commitField(key)
  }
  function setStyleNumber(key: 'opacity' | 'borderWidth' | 'radius', value: number | undefined): void {
    if (!selected.value) return
    selected.value[key] = value ?? null
    commitField(key)
  }
  const controlRangeTypes: NodeType[] = ['slider', 'spinner', 'input']
  const stateLimit = computed(() => selected.value && (selected.value.type === 'selector' || selected.value.type === 'knob') ? 8 : 16)
  const multiTagTypes: NodeType[] = ['chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal', 'chart-bar-stack', 'chart-area', 'chart-radar', 'chart-funnel', 'table', 'history-table']
  function sameValue<T>(values: T[]): T | undefined { return values.length > 0 && values.every(value => value === values[0]) ? values[0] : undefined }
  function commonOf<K extends keyof ScreenNode>(key: K): ScreenNode[K] | undefined { return sameValue(selectedNodes.value.map(node => node[key])) }
  function mixedOf<K extends keyof ScreenNode>(key: K): boolean {
    const list = selectedNodes.value
    return list.length > 1 && !list.every(node => node[key] === list[0][key])
  }
  function commonAction<T>(get: (action: ScreenAction) => T): T | undefined { return sameValue(selectedNodes.value.map(node => get(node.action))) }
  function mixedAction<T>(get: (action: ScreenAction) => T): boolean {
    const list = selectedNodes.value
    return list.length > 1 && !list.every(node => get(node.action) === get(list[0].action))
  }
  function applyAll<K extends keyof ScreenNode>(key: K, value: ScreenNode[K]): void {
    if (busy.value) return
    for (const node of selectedNodes.value) Object.assign(node, { [key]: value })
    commit(`batch:${selectedIds.value.join(',')}:${String(key)}`)
  }
  function applyNumber(key: 'rotation' | 'decimals', value: number | undefined): void {
    if (value === undefined) return
    applyAll(key, value)
  }
  function applyAction(patch: Partial<ScreenAction>): void {
    if (busy.value) return
    for (const node of selectedNodes.value) node.action = { ...node.action, ...patch }
    commit(`batch:${selectedIds.value.join(',')}:action:${Object.keys(patch).join(',')}`)
  }
  // 动作类型与目标字段的唯一对应表，切换类型时清掉无关目标，避免保存出指向错误对象的残留字段。
  const actionTargets: Record<ScreenAction['type'], (keyof ScreenAction)[]> = {
    none: [], navigate: ['screenId'], popup: ['screenId'], command: ['tagId', 'bind'], sequence: ['sequenceId'], recipe: ['recipeId'], script: ['jobId']
  }
  function clearActionTargets(action: ScreenAction, type: ScreenAction['type']): void {
    const keep = actionTargets[type]
    for (const field of ['screenId', 'tagId', 'sequenceId', 'recipeId', 'jobId', 'bind'] as const)
      if (!keep.includes(field)) action[field] = null
  }
  function setActionType(type: ScreenAction['type']): void {
    const action = selected.value?.action
    if (!action) return
    action.type = type
    clearActionTargets(action, type)
    commitField('action-type')
  }
  function setBatchActionType(type: ScreenAction['type']): void {
    if (busy.value) return
    for (const node of selectedNodes.value) { node.action = { ...node.action, type }; clearActionTargets(node.action, type) }
    commit(`batch:${selectedIds.value.join(',')}:action:type`)
  }
  const batchActionType = computed(() => commonAction(action => action.type))
  const batchShowsTarget = computed(() => batchActionType.value === 'navigate' || batchActionType.value === 'popup')
  const batchShowsCommand = computed(() => batchActionType.value === 'command')
  const pipeEnds = ['source', 'target'] as const, pipePorts: ScreenPort['port'][] = ['left', 'right', 'top', 'bottom']
  const pipeEndUpdates = { source: (value: string | null) => setPipeEnd('source', value), target: (value: string | null) => setPipeEnd('target', value) }
  const pipePortUpdates = { source: (value: ScreenPort['port']) => setPipePort('source', value), target: (value: ScreenPort['port']) => setPipePort('target', value) }
  let waypointGesture: AbortController | undefined
  function setPipePort(end: 'source' | 'target', port: ScreenPort['port']): void { const connection = selected.value?.[end]; if (connection) { connection.port = port; commit() } }
  function setPipeEnd(end: 'source' | 'target', value: string | null | undefined): void {
    const node = selected.value; if (!node) return
    Object.assign(node, pipeGeometry(node, document.value.nodes))
    node[end] = value ? { nodeId: value, port: end === 'source' ? 'right' : 'left' } : null
    node.rotation = 0; commit()
  }
  function addPipePoint(): void {
    const node = selected.value; if (!node?.points || node.points.length >= 64) return
    const end = node.points[node.points.length - 1], previous = node.points[node.points.length - 2]
    node.points.splice(node.points.length - 1, 0, { x: (previous.x + end.x) / 2, y: (previous.y + end.y) / 2 }); commit()
  }
  function addCommonStates(): void {
    if (!selected.value) return
    selected.value.states = [{ value: 0, label: tr('hmi.stopped'), color: '#7394b8' }, { value: 1, label: tr('hmi.running'), color: '#34d399' }, { value: 2, label: tr('hmi.moving'), color: '#fbbf24' }, { value: 3, label: tr('hmi.fault'), color: '#ef4444' }]; commit()
  }
  function addState(): void {
    const node = selected.value; if (!node || (node.states?.length ?? 0) >= stateLimit.value) return
    node.states ??= []; const value = Math.max(-1, ...node.states.map(item => item.value)) + 1
    node.states.push({ value, label: String(value), color: '#7394b8' }); commit()
  }
  function addPanelCommand(): void {
    const node = selected.value; if (!node || (node.commands?.length ?? 0) >= 8) return
    node.commands ??= []; const first = node.commands.length === 0
    node.commands.push({ key: `cmd${crypto.randomUUID().slice(0, 8)}`, label: tr(first ? 'hmi.start' : 'hmi.stop'), tagId: null, value: first ? 1 : 0 }); commit()
  }
  function beginWaypoint(rendered: ScreenNode, index: number, event: PointerEvent): void {
    if (busy.value || event.button !== 0 || rendered.rotation !== 0) return
    const node = document.value.nodes.find(item => item.id === rendered.id); if (!node?.points) return
    commit(); waypointGesture?.abort(); const controller = new AbortController(); waypointGesture = controller
    const move = (pointer: PointerEvent) => {
      const point = canvasPoint(pointer); if (!point || pointer.pointerId !== event.pointerId) return
      node.points![index] = { x: Math.max(0, Math.min(100, (point.x - node.x) / node.width * 100)), y: Math.max(0, Math.min(100, (point.y - node.y) / node.height * 100)) }
    }
    window.addEventListener('pointermove', move, { signal: controller.signal })
    window.addEventListener('pointerup', pointer => {
      if (pointer.pointerId !== event.pointerId) return
      move(pointer)
      if (index === 0 || index === node.points!.length - 1) {
        const point = canvasPoint(pointer)
        let nearest: ScreenPort | null = null, distance = 18 / zoom.value
        if (point) for (const target of document.value.nodes.filter(item => item.id !== node.id && !['polyline', 'template'].includes(item.type))) for (const port of pipePorts) {
          const connection = { nodeId: target.id, port }, anchor = portPoint(connection, document.value.nodes)!
          const next = Math.hypot(anchor.x - point.x, anchor.y - point.y)
          if (next < distance) { distance = next; nearest = connection }
        }
        if (nearest) node[index === 0 ? 'source' : 'target'] = nearest
      }
      controller.abort(); commit()
    }, { signal: controller.signal })
    window.addEventListener('pointercancel', () => { controller.abort(); commit() }, { signal: controller.signal, once: true })
  }
  const mobilePanel = ref<'palette' | 'properties' | null>(null)
  watch(selectedIds, () => {
    if (mobilePanel.value === 'palette') mobilePanel.value = null
    // 切换图元时重新展开分区，避免上一次折叠状态把相关字段藏起来。
    void nextTick(() => { for (const group of propertiesPanel.value?.querySelectorAll<HTMLDetailsElement>('details.prop-group') ?? []) group.open = true })
  })
  const guides = ref<{ vertical: number[]; horizontal: number[] } | null>(null)
  const marquee = ref<{ startX: number; startY: number; x: number; y: number; baseIds: string[]; originalIds: string[] } | null>(null)
  const selectionBox = computed(() => {
    const box = marquee.value
    if (!box || Math.max(Math.abs(box.x - box.startX), Math.abs(box.y - box.startY)) * zoom.value < 3) return null
    return { x: Math.min(box.x, box.startX), y: Math.min(box.y, box.startY), width: Math.abs(box.x - box.startX), height: Math.abs(box.y - box.startY) }
  })
  // 快照作为 computed 缓存：脏状态、本地备份和撤销共享同一次序列化，避免拖动时重复 stringify 整份文档。
  const snapshot = computed(() => JSON.stringify({ name: name.value, document: document.value }))
  const dirty = computed(() => savedSnapshot.value !== '' && snapshot.value !== savedSnapshot.value)
  const savedNodes = computed(() => {
    try {
      const parsed = JSON.parse(savedSnapshot.value)
      const nodes = parsed?.document?.nodes
      return Array.isArray(nodes) ? new Map<string, ScreenNode>(nodes.map((node: ScreenNode) => [node.id, node])) : new Map<string, ScreenNode>()
    } catch { return new Map<string, ScreenNode>() }
  })
  function nodeChanged(node: ScreenNode): boolean {
    const saved = savedNodes.value.get(node.id)
    return !saved || JSON.stringify(saved) !== JSON.stringify(node)
  }
  const selectedNodeChanged = computed(() => !!selected.value && nodeChanged(selected.value))
  const selectedChangedCount = computed(() => selectedNodes.value.length > 1 ? selectedNodes.value.filter(nodeChanged).length : 0)
  const {
    recoverableDraft, draftError, localSavedAt, draftBaseChanged,
    findLocalDraft, clearLocalBackup, persistLocalDraft, restoreLocalDraft, discardLocalDraft,
    setLeavingDiscarded, disposeDraftTimer
  } = useDesignerDraft({
    embedded: props.embedded, screenId: id,
    snapshot, savedSnapshot, revision, loading, loadError, dirty, busy,
    document, name, selectedIds, commit, fitCanvas
  })
  function notify(): void { if (props.embedded) emit('update:modelValue', JSON.parse(JSON.stringify(document.value))) }
  function commit(key: unknown = ''): void {
    const state = snapshot.value, mergeKey = typeof key === 'string' ? key : ''
    if (stableSnapshot && state !== stableSnapshot) { history.push(stableSnapshot, mergeKey); stableSnapshot = state; notify() }
  }
  function commitField(field: string): void { commit(`${selectedIds.value.join(',') || 'canvas'}:${field}`) }
  function fieldLabel(base: string, unit?: string | null): string { return unit ? tr('screenDesignerView.fieldWithUnit', [base, unit]) : base }
  function resetAppearance(): void {
    const node = selected.value
    if (!node) return
    const defaults = createNode(node.type, node.x, node.y)
    node.color = defaults.color
    node.activeColor = defaults.activeColor
    node.rotation = 0
    node.fontSize = null
    node.align = null
    node.wrap = null
    node.autoFit = null
    node.bold = null
    node.opacity = null
    node.borderWidth = null
    node.radius = null
    commitField('reset-appearance')
  }
  function resetData(): void {
    const node = selected.value
    if (!node) return
    const defaults = createNode(node.type, node.x, node.y)
    node.unit = ''
    node.min = defaults.min
    node.max = defaults.max
    node.decimals = defaults.decimals
    node.activeThreshold = defaults.activeThreshold
    node.releaseValue = defaults.releaseValue
    node.pulseMs = defaults.pulseMs
    node.maxHoldMs = defaults.maxHoldMs
    node.resetMode = defaults.resetMode
    node.silenceMinutes = defaults.silenceMinutes
    node.recipeTemplateId = defaults.recipeTemplateId
    commitField('reset-data')
  }
  function resetAction(): void {
    if (!selected.value) return
    selected.value.action = { type: 'none', screenId: null, tagId: null, value: 1 }
    commitField('reset-action')
  }
  function resetAnimation(): void {
    if (!selected.value) return
    selected.value.animation = null
    commitField('reset-animation')
  }
  // 矢量动画配置：类型切换时给出合理默认区间（rotate 角度、level 比例、flow 状态触发）。
  function setAnimType(value: string): void {
    if (!selected.value) return
    if (value === 'none') { selected.value.animation = null; commitField('animation-type'); return }
    const type = value as ScreenAnimation['type']
    const outTo = type === 'level' ? 1 : selected.value.type === 'valve' ? 90 : 360
    selected.value.animation = { type, source: type === 'flow' ? 'state' : 'value', tagId: selected.value.animation?.tagId ?? null,
      from: 0, to: 100, outFrom: 0, outTo }
    commitField('animation-type')
  }
  function setAnimField<K extends keyof ScreenAnimation>(key: K, value: ScreenAnimation[K]): void {
    if (!selected.value?.animation) return
    selected.value.animation = { ...selected.value.animation, [key]: value }
    commitField(`animation-${key}`)
  }
  function restore(state: string | undefined): void { if (!state) return; const parsed = JSON.parse(state); name.value = parsed.name; document.value = parsed.document; stableSnapshot = state; selectedIds.value = []; notify() }
  function undo(): void { commit(); restore(history.undo(snapshot.value)) }
  function redo(): void { restore(history.redo(snapshot.value)) }
  function tagLabel(tag: Tag): string { return props.embedded ? tag.name : tr('screenDesignerView.tagLabelWithId', [tag.deviceId === null ? tr('screenDesignerView.calculatedTags') : devices.value.find(d => d.id === tag.deviceId)?.name ?? tag.deviceId, tag.name, tag.id]) }
  function typeLabel(node: ScreenNode): string { return palette.find(item => item.type === node.type)?.label ?? node.type }
  function bindingChanged(): void { if (selected.value) { selected.value.tagId ??= null; selected.value.unit = tags.value.find(t => t.id === selected.value?.tagId)?.unit ?? '' }; commit() }
  function setControlTag(value: number | null | undefined): void {
    const node = selected.value
    if (!node) return
    const tagId = value ?? null
    node.action = { ...node.action, type: 'command', tagId }
    node.tagId = tagId
    node.unit = tagId == null ? '' : tags.value.find(tag => tag.id === tagId)?.unit ?? ''
    commit()
  }
  function setResetTag(value: number | null | undefined): void {
    const node = selected.value
    if (!node) return
    node.action = { ...node.action, type: 'command', tagId: value ?? null }
    commit()
  }
  function syncControlTag(): void {
    if (!selected.value || selected.value.action.tagId == null) return
    selected.value.tagId = selected.value.action.tagId
    commit()
  }
  function groupMembers(nodeId: string): string[] {
    const node = document.value.nodes.find(item => item.id === nodeId)
    return node?.groupId ? document.value.nodes.filter(item => item.groupId === node.groupId).map(item => item.id) : [nodeId]
  }
  function select(nodeId: string, additive = false): void {
    commit()
    const members = groupMembers(nodeId)
    if (additive) selectedIds.value = selectedIds.value.includes(nodeId) ? selectedIds.value.filter(id => !members.includes(id)) : [...new Set([...selectedIds.value, ...members])]
    else selectedIds.value = members
  }
  function block(message: string): void { guardError.value = message }
  function add(type: NodeType, x = 60 + document.value.nodes.length % 8 * 20, y = 60 + document.value.nodes.length % 8 * 20): void {
    if (busy.value) return
    if (document.value.nodes.length >= 1000) { block(tr('screenDesignerView.atMost1000ElementsPerScreen')); return }
    const node = clampNode(createNode(type, x, y), document.value); document.value.nodes.push(node); selectedIds.value = [node.id]; guardError.value = ''; commit()
  }
  const library = useLibraryStore()
  const { locale } = useI18n()
  const userSymbols = computed(() => library.items.filter(x => x.kind === 'symbol'))
  const templates = computed(() => library.items.filter(x => x.kind === 'template'))
  const symbolName = (s: BuiltinSymbol) => (locale.value === 'en-US' ? s.nameEn : s.name)
  const symbolCategoryLabel = (c: string | number) => (locale.value === 'en-US' ? symbolCategories[c]?.en : symbolCategories[c]?.zh) ?? String(c)
  const componentQuery = ref(''), layerQuery = ref(''), expandedGroups = ref<string[]>([])
  const paletteTab = ref<'both' | 'components' | 'layers'>('both')
  const collapsedGroups = ref<Record<string, boolean>>({})
  const renamingId = ref<string | null>(null), renameDraft = ref('')
  interface LayerEntry { key: string; kind: 'node' | 'group'; id: string; nodes: ScreenNode[] }
  function layerTitle(node: ScreenNode): string { return node.text || typeLabel(node) }
  function toggleGroup(id: string): void { collapsedGroups.value = { ...collapsedGroups.value, [id]: !collapsedGroups.value[id] } }
  function startRename(node: ScreenNode): void { renamingId.value = node.id; renameDraft.value = node.text }
  function commitRename(node: ScreenNode): void {
    if (renamingId.value !== node.id) return
    if (node.text !== renameDraft.value) { commit(); node.text = renameDraft.value; commit() }
    renamingId.value = null
  }
  function openCanvasSettings(): void {
    mobilePanel.value = 'properties'
    void nextTick(() => propertiesPanel.value?.querySelector('[data-test="canvas-settings"]')?.scrollIntoView({ block: 'nearest' }))
  }
  function matches(query: string, ...values: unknown[]): boolean {
    const text = values.filter(v => v != null).join(' ').toLocaleLowerCase()
    return query.trim().toLocaleLowerCase().split(/\s+/).every(word => text.includes(word))
  }
  const filteredPalette = computed(() => palette.filter(p => matches(componentQuery.value, p.type, p.label)))
  const filteredSymbols = computed(() => builtinSymbols.filter(s => matches(componentQuery.value, s.key, s.name, s.nameEn, s.category, symbolCategoryLabel(s.category))))
  const filteredUserSymbols = computed(() => userSymbols.value.filter(s => matches(componentQuery.value, s.key, s.name, s.category)))
  const filteredTemplates = computed(() => templates.value.filter(t => matches(componentQuery.value, t.key, t.name, t.category, ...t.placeholders)))
  const componentTotal = computed(() => palette.length + builtinSymbols.length + userSymbols.value.length + (props.embedded ? 0 : templates.value.length))
  const componentCount = computed(() => filteredPalette.value.length + filteredSymbols.value.length + filteredUserSymbols.value.length + (props.embedded ? 0 : filteredTemplates.value.length))
  const symbolGroups = computed(() => filteredSymbols.value.reduce<Record<string, BuiltinSymbol[]>>((acc, s) => { (acc[s.category] ??= []).push(s); return acc }, {}))
  watch(componentQuery, query => { if (query.trim()) expandedGroups.value = Object.keys(symbolGroups.value) })
  const tagNames = computed(() => new Map(tags.value.map(tag => [tag.id, tag.name])))
  const tagsById = computed(() => new Map(tags.value.map(tag => [tag.id, tag])))
  // 客户端先做一轮可判定的格式与引用校验，避免每次都要保存/发布后才在服务端报错。
  const selectedIssues = computed<PanelIssue[]>(() => {
    const node = selected.value
    if (!node) return []
    void locale.value
    const issues: PanelIssue[] = []
    const add = (field: string, message: string) => issues.push({ field, message })
    if (!(Number.isFinite(node.min) && Number.isFinite(node.max) && node.min < node.max)) add('range', tr('screenDesignerView.issueRange'))
    if (!Number.isInteger(node.decimals) || node.decimals < 0 || node.decimals > 6) add('decimals', tr('screenDesignerView.issueDecimals'))
    if (node.fontSize != null && (!Number.isFinite(node.fontSize) || node.fontSize < 8 || node.fontSize > 96)) add('fontSize', tr('screenDesignerView.issueFontSize'))
    if (node.type === 'image' && node.text && !/^(\/|\.{1,2}\/|https?:\/\/)/i.test(node.text)) add('image', tr('screenDesignerView.issueImageUrl'))
    if (node.tagId != null && tags.value.length && !tagsById.value.has(node.tagId)) add('tags', tr('screenDesignerView.issueTagMissing'))
    if (node.alarmTagId != null && tags.value.length && !tagsById.value.has(node.alarmTagId)) add('alarm', tr('screenDesignerView.issueAlarmTagMissing'))
    if (node.states?.length) {
      if (node.states.length > 16) add('states', tr('screenDesignerView.issueStateLimit'))
      const seen = new Map<number, number>()
      node.states.forEach((state, index) => {
        const count = (seen.get(state.value) ?? 0) + 1
        seen.set(state.value, count)
        if (count > 1) add('states', tr('screenDesignerView.issueStateDuplicate', [state.value]))
        if (!state.label.trim()) add('states', tr('screenDesignerView.issueStateLabel', [index + 1]))
      })
    }
    if (node.commands?.length) {
      if (node.commands.length > 8) add('commands', tr('screenDesignerView.issueCommandCount'))
      node.commands.forEach((command, index) => {
        if (!command.label.trim()) add('commands', tr('screenDesignerView.issueCommandLabel', [index + 1]))
        const tag = command.tagId == null ? undefined : tagsById.value.get(command.tagId)
        if (!tag?.writable) add('commands', tr('screenDesignerView.issueCommandTag', [index + 1]))
      })
    }
    if (node.animation) {
      if (!(Number.isFinite(node.animation.from) && Number.isFinite(node.animation.to) && node.animation.from < node.animation.to)) add('animation', tr('screenDesignerView.issueAnimationInput'))
      if (!(Number.isFinite(node.animation.outFrom) && Number.isFinite(node.animation.outTo) && node.animation.outFrom < node.animation.outTo)) add('animation', tr('screenDesignerView.issueAnimationOutput'))
      if (node.animation.source === 'value' && node.animation.tagId != null && !tagsById.value.has(node.animation.tagId)) add('animation', tr('screenDesignerView.issueTagMissing'))
    }
    if (node.type === 'template') {
      if (library.items.length && !library.error && node.templateKey && !templates.value.some(item => item.key === node.templateKey)) add('bindings', tr('screenDesignerView.templateDoesNotExist'))
      for (const binding of node.bindings ?? []) {
        if (binding.tagId == null) add('bindings', tr('screenDesignerView.issuePlaceholder', [binding.key]))
        else if (!tagsById.value.has(binding.tagId)) add('bindings', tr('screenDesignerView.issueTagMissing'))
      }
    }
    if (multiTagTypes.includes(node.type)) {
      if ((node.tags?.length ?? 0) > 12) add('charts', tr('screenDesignerView.issueChartCount'))
      if (node.tags?.some(id => !tagsById.value.has(id))) add('charts', tr('screenDesignerView.issueChartTag'))
    }
    if (node.windowSec != null && (!Number.isFinite(node.windowSec) || node.windowSec < 10 || node.windowSec > 86400)) add('window', tr('screenDesignerView.issueWindow'))
    if (node.type === 'oee-kpi' && node.lineId != null && node.lineId <= 0) add('oee', tr('screenDesignerView.issueLine'))
    if (node.type === 'polyline' && ((node.points?.length ?? 0) < 2 || (node.points?.length ?? 0) > 64)) add('polyline', tr('screenDesignerView.issuePolyline'))
    const action = node.action
    if (controlTypes.includes(node.type)) {
      if (action.type !== 'command') add('control', tr('screenDesignerView.controlAction'))
      else if (action.tagId == null) add('control', tr('screenDesignerView.controlTagRequired'))
      else {
        const tag = tagsById.value.get(action.tagId)
        if (!tag) add('control', tr('screenDesignerView.issueTagMissing'))
        else if (!tag.writable) add('control', tr('screenDesignerView.controlTagReadOnly'))
      }
    } else if (action.type === 'navigate' || action.type === 'popup') {
      const target = action.screenId == null ? undefined : screens.value.find(item => item.id === action.screenId)
      if (!target) add('action', tr('screenDesignerView.issueActionTarget'))
      else if (!target.publishedRevision) add('action', tr('screenDesignerView.issueActionUnpublished'))
    } else if (action.type === 'command' && node.type !== 'reset') {
      const tag = action.tagId == null ? undefined : tagsById.value.get(action.tagId)
      if (!tag?.writable) add('action', tr('screenDesignerView.issueActionTag'))
    } else if (action.type === 'sequence') {
      if (action.sequenceId == null) add('action', tr('hmi.issueActionSequence'))
      else if (sequences.value.length && !sequences.value.some(item => item.id === action.sequenceId)) add('action', tr('hmi.issueActionSequenceMissing'))
    } else if (action.type === 'recipe') {
      if (action.recipeId == null) add('action', tr('hmi.issueActionRecipe'))
      else if (recipes.value.length && !recipes.value.some(item => item.id === action.recipeId)) add('action', tr('hmi.issueActionRecipeMissing'))
    } else if (action.type === 'script') {
      const job = action.jobId == null ? undefined : calcJobs.value.find(item => item.job.id === action.jobId)?.job
      if (action.jobId == null) add('action', tr('hmi.issueActionScript'))
      else if (calcJobs.value.length && !job) add('action', tr('hmi.issueActionScriptMissing'))
      else if (job && !job.publishedRevision) add('action', tr('hmi.issueActionScriptUnpublished'))
    }
    if (node.type === 'jog' || node.type === 'pulse') {
      const release = node.releaseValue
      if (release == null || !Number.isFinite(release)) add('momentary', tr('screenDesignerView.issueReleaseValue'))
      else if (Number.isFinite(action.value) && release === action.value) add('momentary', tr('screenDesignerView.issueReleaseSame'))
      if (node.type === 'jog' && (!Number.isInteger(node.maxHoldMs) || (node.maxHoldMs ?? 0) < 500 || (node.maxHoldMs ?? 0) > 30000)) add('momentary', tr('screenDesignerView.issueHoldRange'))
      if (node.type === 'pulse' && (!Number.isInteger(node.pulseMs) || (node.pulseMs ?? 0) < 100 || (node.pulseMs ?? 0) > 5000)) add('momentary', tr('screenDesignerView.issuePulseRange'))
    }
    if (node.type === 'selector' || node.type === 'knob') {
      const count = node.states?.length ?? 0
      if (count < 2) add('states', tr('screenDesignerView.issuePositionMin'))
      else if (count > 8) add('states', tr('screenDesignerView.issuePositionMax'))
    }
    if (node.type === 'reset') {
      if (node.resetMode !== 'alarms' && node.resetMode !== 'tag') add('reset', tr('screenDesignerView.issueResetMode'))
      else if (node.resetMode === 'tag') {
        const tag = action.tagId == null ? undefined : tagsById.value.get(action.tagId)
        if (action.type !== 'command' || action.tagId == null) add('reset', tr('screenDesignerView.controlTagRequired'))
        else if (!tag) add('reset', tr('screenDesignerView.issueTagMissing'))
        else if (!tag.writable) add('reset', tr('screenDesignerView.controlTagReadOnly'))
      }
    }
    if (node.type === 'silence' && (!Number.isInteger(node.silenceMinutes) || (node.silenceMinutes ?? 0) < 1 || (node.silenceMinutes ?? 0) > 120)) add('silence', tr('screenDesignerView.issueSilenceMinutes'))
    if (node.type === 'recipe') {
      if (node.recipeTemplateId == null) add('recipe', tr('hmi.issueRecipeTemplate'))
      else if (recipeTemplates.value.length && !recipeTemplates.value.some(template => template.id === node.recipeTemplateId)) add('recipe', tr('hmi.issueRecipeTemplateMissing'))
    }
    return issues
  })
  function fieldError(field: string): string { return selectedIssues.value.filter(issue => issue.field === field).map(issue => issue.message).join(' · ') }
  async function focusField(field: string): Promise<void> {
    if (!field) return
    if (window.matchMedia('(max-width: 900px)').matches) mobilePanel.value = 'properties'
    await nextTick()
    const target = propertiesPanel.value?.querySelector<HTMLElement>(`[data-field="${CSS.escape(field)}"]`)
    if (!target) return
    const group = target.closest('details.prop-group') as HTMLDetailsElement | null
    if (group && !group.open) { group.open = true; await nextTick() }
    target.scrollIntoView({ block: 'center', behavior: 'smooth' })
    target.classList.add('field-focus')
    window.setTimeout(() => target.classList.remove('field-focus'), 1400)
    target.querySelector<HTMLInputElement>('input:not([type="color"]), textarea')?.focus({ preventScroll: true })
  }
  // 服务端问题只带图元编号；按文案关键词尽量定位到属性分区。
  function fieldForIssue(message: string): string {
    if (/位置|旋钮|选择开关|position|selector|knob/i.test(message)) return 'states' // i18n-ignore: server issue vocabulary
    if (/点动|瞬时|脉冲|释放值|保持|momentary|release|pulse|jog/i.test(message)) return 'momentary' // i18n-ignore: server issue vocabulary
    if (/复位|reset/i.test(message)) return 'reset' // i18n-ignore: server issue vocabulary
    if (/消音|silence/i.test(message)) return 'silence' // i18n-ignore: server issue vocabulary
    if (/配方模板|recipe template/i.test(message)) return 'recipe' // i18n-ignore: server issue vocabulary
    if (/配方|recipe/i.test(message)) return selected.value?.type === 'recipe' ? 'recipe' : 'action' // i18n-ignore: server issue vocabulary
    if (/顺控|sequence/i.test(message)) return 'action' // i18n-ignore: server issue vocabulary
    if (/控制|control/i.test(message)) return selected.value?.type === 'reset' ? 'reset' : controlTypes.includes(selected.value?.type ?? 'text') ? 'control' : 'action' // i18n-ignore: server issue vocabulary
    if (/量程|精度|range|decimals/i.test(message)) return 'range' // i18n-ignore: server issue vocabulary
    if (/字号|fontsize/i.test(message)) return 'fontSize' // i18n-ignore: server issue vocabulary
    if (/图片|image|url/i.test(message)) return 'image' // i18n-ignore: server issue vocabulary
    if (/状态|state/i.test(message)) return 'states' // i18n-ignore: server issue vocabulary
    if (/命令|command/i.test(message)) return 'commands' // i18n-ignore: server issue vocabulary
    if (/动画|animation/i.test(message)) return 'animation' // i18n-ignore: server issue vocabulary
    if (/占位符|模板|placeholder|template/i.test(message)) return 'bindings' // i18n-ignore: server issue vocabulary
    if (/管道|polyline|waypoint/i.test(message)) return 'polyline' // i18n-ignore: server issue vocabulary
    if (/窗口|window/i.test(message)) return 'window' // i18n-ignore: server issue vocabulary
    if (/产线|line/i.test(message)) return 'oee' // i18n-ignore: server issue vocabulary
    if (/图表|chart/i.test(message)) return 'charts' // i18n-ignore: server issue vocabulary
    if (/动作|action/i.test(message)) return 'action' // i18n-ignore: server issue vocabulary
    if (/测点|tag/i.test(message)) return 'tags' // i18n-ignore: server issue vocabulary
    return ''
  }
  const displayedTagIds = computed(() => {
    const node = selected.value
    if (!node) return []
    return [...new Set([
      node.tagId, node.alarmTagId, node.action.tagId, node.animation?.tagId,
      ...(node.tags ?? []), ...(node.bindings?.map(binding => binding.tagId) ?? []), ...(node.commands?.map(command => command.tagId) ?? [])
    ].filter((id): id is number => id != null))]
  })
  // 编辑态只订阅当前图元相关测点；实时值只作预览，不参与保存或发布校验。
  const {
    values: tagValues,
    failed: tagValuesError,
    start: startTagValues
  } = useDesignerTagValues({ tagIds: displayedTagIds, disabled: () => props.embedded })
  // 配方控件与配方/顺控动作的目标候选；目录失败时保留空列表，校验只提示未选择。
  async function loadRecipeCatalog(): Promise<void> {
    try {
      const [templates, items] = await Promise.all([recipeApi.templates(), recipeApi.list()])
      recipeTemplates.value = templates; recipes.value = items
    } catch { recipeTemplates.value = []; recipes.value = [] }
  }
  async function loadSequenceCatalog(): Promise<void> {
    try { sequences.value = await controlApi.sequences() } catch { sequences.value = [] }
  }
  async function loadScriptCatalog(): Promise<void> {
    try { calcJobs.value = (await calcJobApi.list()).filter(item => item.job.kind === 'Script' || item.job.kind === 'CSharp') } catch { calcJobs.value = [] }
  }
  function tagLiveText(tagId: number | null | undefined, unit = ''): string {
    const value = tagId == null ? undefined : tagValues.value[tagId]
    if (!value) return ''
    return `${formatValue(value.value)}${unit ? ` ${unit}` : ''} · ${qualityLabels[value.quality]}`
  }
  function tagLiveClass(tagId: number | null | undefined): string {
    const quality = tagId == null ? undefined : tagValues.value[tagId]?.quality
    return quality ? `q-${quality}` : ''
  }
  function tagLiveTitle(tagId: number | null | undefined): string {
    const value = tagId == null ? undefined : tagValues.value[tagId]
    return value ? formatTime(value.ts) : ''
  }
  function animationSummary(animation: ScreenAnimation): string {
    if (animation.type === 'flow') return `${tr('screenDesignerView.flow')} · ${tr(animation.source === 'state' ? 'screenDesignerView.stateTrigger' : 'screenDesignerView.valueMapping')}`
    const type = tr(animation.type === 'rotate' ? 'screenDesignerView.rotate' : 'screenDesignerView.level')
    const input = animation.source === 'value' ? `${animation.from} → ${animation.to}` : tr('screenDesignerView.stateTrigger')
    return `${type} · ${input}${animation.source === 'value' ? ` · ${animation.outFrom} → ${animation.outTo}` : ''}`
  }
  const filteredLayers = computed(() => document.value.nodes.filter(n => {
    const ids = [n.tagId, n.alarmTagId, n.action.tagId, ...(n.bindings?.map(b => b.tagId) ?? [])].filter((id): id is number => id != null)
    return matches(layerQuery.value, n.id, n.text, ...Object.values(n.texts ?? {}), n.type, palette.find(p => p.type === n.type)?.label,
      n.symbol, n.templateKey, ...ids.flatMap(id => [id, tagNames.value.get(id)]))
  }).reverse())
  // 图层列表默认只渲染前 100 项，避免上千图元时侧栏一次性创建大量节点。
  const layerLimit = ref(100)
  const visibleLayers = computed(() => filteredLayers.value.slice(0, layerLimit.value))
  const layerEntries = computed<LayerEntry[]>(() => {
    const order: LayerEntry[] = []
    const placed = new Set<string>()
    for (const node of visibleLayers.value) {
      if (!node.groupId) { order.push({ key: node.id, kind: 'node', id: node.id, nodes: [node] }); continue }
      if (placed.has(node.groupId)) continue
      placed.add(node.groupId)
      const members = visibleLayers.value.filter(item => item.groupId === node.groupId)
      if (members.length < 2) order.push({ key: node.id, kind: 'node', id: node.id, nodes: [node] })
      else order.push({ key: node.groupId, kind: 'group', id: node.groupId, nodes: members })
    }
    return order
  })
  watch(layerQuery, () => { layerLimit.value = 100 })
  const zoomOptions = computed(() => [...new Set([.1, .25, .35, .5, .75, 1, 1.25, 1.5, zoom.value])].sort((a, b) => a - b))
  async function fitCanvas(): Promise<void> {
    const viewport = stage.value; if (!viewport) return
    zoom.value = Math.max(.01, Math.floor(Math.min(1.5, (viewport.clientWidth - 40) / document.value.width, (viewport.clientHeight - 40) / document.value.height) * 100) / 100)
    await nextTick(); if (!disposed) viewport.scrollTo(0, 0)
  }
  async function changeZoom(value: number): Promise<void> {
    const viewport = stage.value; if (!viewport || !Number.isFinite(value) || value <= 0) return
    const x = (viewport.scrollLeft + viewport.clientWidth / 2 - 20) / zoom.value
    const y = (viewport.scrollTop + viewport.clientHeight / 2 - 20) / zoom.value
    zoom.value = value
    await nextTick(); if (!disposed) viewport.scrollTo(x * value + 20 - viewport.clientWidth / 2, y * value + 20 - viewport.clientHeight / 2)
  }
  async function revealSelection(): Promise<void> {
    await nextTick()
    const viewport = stage.value; if (!viewport || disposed) return
    const selectedBounds = () => selectedIds.value.flatMap(id => {
      const node = viewport.querySelector(`[data-node-id="${CSS.escape(id)}"]`)
      return node ? [node.getBoundingClientRect()] : []
    })
    let boxes = selectedBounds()
    if (!boxes.length) return
    const width = Math.max(...boxes.map(b => b.right)) - Math.min(...boxes.map(b => b.left))
    const height = Math.max(...boxes.map(b => b.bottom)) - Math.min(...boxes.map(b => b.top))
    const scale = Math.min(1, (viewport.clientWidth - 40) / width, (viewport.clientHeight - 40) / height)
    if (scale < 1) {
      zoom.value = Math.max(.01, Math.floor(zoom.value * scale * 100) / 100)
      await nextTick(); if (disposed) return
      boxes = selectedBounds()
      if (!boxes.length) return
    }
    const bounds = viewport.getBoundingClientRect()
    const x = (Math.min(...boxes.map(b => b.left)) + Math.max(...boxes.map(b => b.right))) / 2
    const y = (Math.min(...boxes.map(b => b.top)) + Math.max(...boxes.map(b => b.bottom))) / 2
    viewport.scrollTo(viewport.scrollLeft + x - bounds.left - viewport.clientWidth / 2, viewport.scrollTop + y - bounds.top - viewport.clientHeight / 2)
  }
  function selectLayer(nodeId: string, additive: boolean): void { select(nodeId, additive); if (selectedIds.value.includes(nodeId)) void revealSelection() }
  const englishText = computed({
    get: () => selected.value?.texts?.['en-US'] ?? '',
    set: (v: string) => { if (!selected.value) return; const texts = { ...(selected.value.texts ?? {}) }; if (v.trim()) texts['en-US'] = v; else delete texts['en-US']; selected.value.texts = Object.keys(texts).length ? texts : null }
  })
  function pushNode(node: ScreenNode): void {
    if (busy.value) return
    if (document.value.nodes.length >= 1000) { block(tr('screenDesignerView.atMost1000ElementsPerScreen')); return }
    const n = clampNode(node, document.value); document.value.nodes.push(n); selectedIds.value = [n.id]; guardError.value = ''; commit()
  }
  function nextPos(): [number, number] { return [60 + document.value.nodes.length % 8 * 20, 60 + document.value.nodes.length % 8 * 20] }
  function addSymbol(key: string, x?: number, y?: number): void {
    const b = builtinSymbols.find(s => s.key === key), u = userSymbols.value.find(s => `user:${s.key}` === key)
    const [px, py] = x === undefined ? nextPos() : [x, y!]
    pushNode(createSymbolNode(key, b?.w ?? u?.width ?? 100, b?.h ?? u?.height ?? 100, b ? b.name : u?.name ?? key, px, py))
  }
  function addTemplate(item: LibraryItem, x?: number, y?: number): void { const [px, py] = x === undefined ? nextPos() : [x, y!]; pushNode(createTemplateNode(item, px, py)) }
  function syncBindings(node: ScreenNode): void {
    const item = templates.value.find(t => t.key === node.templateKey); if (!item) { block(tr('screenDesignerView.templateDoesNotExist')); return }
    guardError.value = ''
    const old = new Map((node.bindings ?? []).map(b => [b.key, b.tagId]))
    node.bindings = item.placeholders.map(key => ({ key, tagId: old.get(key) ?? null })); commit()
  }
  function startSymbolDrag(key: string, event: DragEvent): void { event.dataTransfer?.setData('application/scada-symbol', key); if (event.dataTransfer) event.dataTransfer.effectAllowed = 'copy' }

  // 存为模板：所选图元按包围盒归一化，具体测点替换为占位符（同一测点同一占位符）。
  const templateVisible = ref(false), templateError = ref('')
  const templateForm = reactive<{ key: string; name: string; category: string; replace: boolean; placeholders: { tagId: number; key: string }[] }>({ key: '', name: '', category: tr('screenDesignerView.faceplate'), replace: true, placeholders: [] })
  function openTemplateDialog(): void {
    const picked = document.value.nodes.filter(n => selectedIds.value.includes(n.id))
    if (picked.some(n => n.type === 'template')) { block(tr('screenDesignerView.nestedTemplateNotAllowed')); return }
    guardError.value = ''
    const ids = [...new Set(picked.flatMap(n => [n.tagId, n.alarmTagId, n.action.type === 'command' ? n.action.tagId : null, ...(n.commands ?? []).map(command => command.tagId), ...(n.tags ?? []), n.animation?.tagId ?? null]).filter((x): x is number => !!x))]
    Object.assign(templateForm, { key: '', name: '', category: tr('screenDesignerView.faceplate'), replace: true, placeholders: ids.map((tagId, i) => {
      const raw = (tags.value.find(t => t.id === tagId)?.name ?? '').replace(/[^a-zA-Z0-9_-]/g, '')
      return { tagId, key: /^[a-zA-Z]/.test(raw) ? raw.slice(0, 64) : `p${i + 1}` }
    }) })
    templateError.value = ''; templateVisible.value = true
  }
  async function createTemplate(): Promise<void> {
    const picked = document.value.nodes.filter(n => selectedIds.value.includes(n.id)).map(node => pipeGeometry(node, document.value.nodes)); if (!picked.length) return
    const keys = templateForm.placeholders.map(p => p.key.trim())
    templateError.value = ''
    if (new Set(keys).size !== keys.length) { templateError.value = tr('screenDesignerView.placeholdersMustBeUnique'); return }
    const keyOf = new Map(templateForm.placeholders.map(p => [p.tagId, p.key.trim()]))
    const minX = Math.min(...picked.map(n => n.x)), minY = Math.min(...picked.map(n => n.y))
    const width = Math.max(16, Math.max(...picked.map(n => n.x + n.width)) - minX), height = Math.max(16, Math.max(...picked.map(n => n.y + n.height)) - minY)
    const nodes = picked.map(n => cleanNode({ ...JSON.parse(JSON.stringify(n)), x: n.x - minX, y: n.y - minY, tagId: null, alarmTagId: null, bind: n.tagId ? keyOf.get(n.tagId) : null, alarmBind: n.alarmTagId ? keyOf.get(n.alarmTagId) : null,
      tags: null, tagBinds: n.tags?.length ? n.tags.map(tagId => keyOf.get(tagId)).filter((key): key is string => key != null) : null,
      animation: n.animation ? { ...n.animation, tagId: null } : null,
      animationBind: n.animation?.tagId ? keyOf.get(n.animation.tagId) ?? null : null,
      source: n.source && selectedIds.value.includes(n.source.nodeId) ? n.source : null,
      target: n.target && selectedIds.value.includes(n.target.nodeId) ? n.target : null,
      commands: n.commands?.map(command => ({ ...command, tagId: null, bind: command.tagId ? keyOf.get(command.tagId) : null })),
      action: { ...n.action, tagId: null, bind: n.action.type === 'command' && n.action.tagId ? keyOf.get(n.action.tagId) : null } }))
    try {
      const { item } = await hmiLibraryApi.create({ key: templateForm.key.trim(), name: templateForm.name.trim() || templateForm.key.trim(), category: templateForm.category, kind: 'template', width, height, placeholders: keys,
        document: { schemaVersion: 1, width: Math.max(320, width), height: Math.max(200, height), background: document.value.background, nodes } })
      await library.reload()
      if (templateForm.replace) {
        detachConnections(new Set(selectedIds.value))
        const instance = createTemplateNode(item, minX, minY)
        instance.bindings = item.placeholders.map(key => ({ key, tagId: templateForm.placeholders.find(p => p.key.trim() === key)?.tagId ?? null }))
        document.value.nodes = [...document.value.nodes.filter(n => !selectedIds.value.includes(n.id)), instance]; selectedIds.value = [instance.id]; commit()
      }
      templateVisible.value = false; ElMessage.success(tr('screenDesignerView.templateCreated', [item.name]))
    } catch (e) { templateError.value = errorMessage(e, tr('screenDesignerView.templateCreationFailed')) }
  }
  function startPaletteDrag(type: NodeType, event: DragEvent): void { event.dataTransfer?.setData('application/scada-node', type); if (event.dataTransfer) event.dataTransfer.effectAllowed = 'copy' }
  function drop(event: DragEvent): void {
    const type = event.dataTransfer?.getData('application/scada-node') as NodeType
    const rect = (event.currentTarget as SVGSVGElement).getBoundingClientRect()
    const symbol = event.dataTransfer?.getData('application/scada-symbol')
    if (symbol) { addSymbol(symbol, Math.round((event.clientX - rect.left) / zoom.value), Math.round((event.clientY - rect.top) / zoom.value)); return }
    if (!palette.some(p => p.type === type)) return
    add(type, Math.round((event.clientX - rect.left) / zoom.value), Math.round((event.clientY - rect.top) / zoom.value))
  }
  function beginDrag(node: ScreenNode, event: MouseEvent | TouchEvent): void {
    if ('button' in event && event.button !== 0 || busy.value) return
    const target = document.value.nodes.find(item => item.id === node.id)
    if (target?.locked) return
    event.preventDefault()
    stage.value?.focus({ preventScroll: true })
    if (event.shiftKey) { select(node.id, true); return }
    commit(); if (!selectedIds.value.includes(node.id)) selectedIds.value = [node.id]
    syncInteractionTargets(); moveable?.dragStart(event)
  }
  function canvasPoint(event: { clientX: number; clientY: number }): { x: number; y: number } | null {
    const bounds = stage.value?.querySelector('.screen-canvas')?.getBoundingClientRect()
    if (!bounds) return null
    return { x: Math.max(0, Math.min(document.value.width, (event.clientX - bounds.left) / zoom.value)),
      y: Math.max(0, Math.min(document.value.height, (event.clientY - bounds.top) / zoom.value)) }
  }
  function beginSelection(event: PointerEvent): void {
    if (busy.value || event.button !== 0) return
    stage.value?.focus({ preventScroll: true })
  }
  function startTransform(event: { clientX: number; clientY: number }, resize = false): void {
    if (busy.value) return
    commit()
    drag = { x: event.clientX, y: event.clientY, nodes: JSON.parse(JSON.stringify(document.value.nodes.filter(node => selectedIds.value.includes(node.id)))), resize, moved: false }
  }
  function snapAlignment(originals: ScreenNode[], dx: number, dy: number): { dx: number; dy: number } {
    const threshold = Math.max(2, 6 / zoom.value)
    const selected = new Set(originals.map(node => node.id))
    const others = document.value.nodes.filter(node => !selected.has(node.id) && !node.hidden)
    const xs: number[] = [0, document.value.width / 2, document.value.width]
    const ys: number[] = [0, document.value.height / 2, document.value.height]
    for (const node of others) { xs.push(node.x, node.x + node.width / 2, node.x + node.width); ys.push(node.y, node.y + node.height / 2, node.y + node.height) }
    let snapDx = dx, snapDy = dy, bestX = threshold + 1, bestY = threshold + 1
    for (const node of originals) {
      for (const value of [node.x + dx, node.x + dx + node.width / 2, node.x + dx + node.width]) for (const target of xs) {
        const delta = target - value
        if (Math.abs(delta) <= threshold && Math.abs(delta) < Math.abs(bestX)) { bestX = delta; snapDx = dx + delta }
      }
      for (const value of [node.y + dy, node.y + dy + node.height / 2, node.y + dy + node.height]) for (const target of ys) {
        const delta = target - value
        if (Math.abs(delta) <= threshold && Math.abs(delta) < Math.abs(bestY)) { bestY = delta; snapDy = dy + delta }
      }
    }
    return { dx: snapDx, dy: snapDy }
  }
  function updateGuides(moved: ScreenNode[]): void {
    const threshold = Math.max(1, 2 / zoom.value)
    const selected = new Set(drag?.nodes.map(node => node.id) ?? [])
    const others = document.value.nodes.filter(node => !selected.has(node.id) && !node.hidden)
    const vertical = new Set<number>(), horizontal = new Set<number>()
    for (const node of moved) {
      const xs = [node.x, node.x + node.width / 2, node.x + node.width]
      const ys = [node.y, node.y + node.height / 2, node.y + node.height]
      for (const other of others) {
        for (const x of xs) for (const ox of [other.x, other.x + other.width / 2, other.x + other.width]) if (Math.abs(x - ox) <= threshold) vertical.add(Number(x.toFixed(1)))
        for (const y of ys) for (const oy of [other.y, other.y + other.height / 2, other.y + other.height]) if (Math.abs(y - oy) <= threshold) horizontal.add(Number(y.toFixed(1)))
      }
    }
    guides.value = vertical.size || horizontal.size ? { vertical: [...vertical], horizontal: [...horizontal] } : null
  }
  function transformNodes(event: { clientX: number; clientY: number }): void {
    if (!drag || busy.value) return
    const grid = snap.value ? 10 : 1
    let dx = Math.round((event.clientX - drag.x) / zoom.value / grid) * grid, dy = Math.round((event.clientY - drag.y) / zoom.value / grid) * grid
    if (snap.value && !drag.resize) ({ dx, dy } = snapAlignment(drag.nodes, dx, dy))
    const moved = drag.resize
      ? drag.nodes.map(original => clampNode({ ...original, width: original.width + dx, height: original.height + dy }, document.value))
      : translateNodes(drag.nodes, document.value, dx, dy)
    for (const node of moved) {
      const index = document.value.nodes.findIndex(original => original.id === node.id)
      document.value.nodes[index] = node
    }
    if (snap.value && !drag.resize) updateGuides(moved); else guides.value = null
    if (moved.some((node, index) => { const original = drag!.nodes[index]; return node.x !== original.x || node.y !== original.y || node.width !== original.width || node.height !== original.height })) drag.moved = true
  }
  function finishTransform(): void { guides.value = null; if (drag?.moved) commit(); drag = null; syncInteractionTargets() }
  function syncInteractionTargets(): void {
    if (!moveable || !stage.value || disposed) return
    if (!drag) {
      const targets = selectedIds.value.flatMap(id => {
        const node = document.value.nodes.find(item => item.id === id)
        if (!node || node.locked || node.hidden) return []
        const element = stage.value!.querySelector<SVGElement>(`.screen-canvas > g[data-node-id="${CSS.escape(id)}"]`)
        return element ? [element] : []
      })
      moveable.setState({ target: busy.value ? [] : targets.length === 1 ? targets[0] : targets, resizable: !busy.value && targets.length === 1 })
    }
    moveable.updateRect()
  }
  function initializeInteractions(): void {
    const viewport = stage.value
    if (!viewport) return
    moveable = new Moveable(viewport, { container: viewport, draggable: true, resizable: true, origin: false,
      renderDirections: ['se'], throttleDrag: 0, throttleResize: 0, preventDefault: true })
    moveable.on('dragStart', event => { if (busy.value) event.stop(); else startTransform(event) })
      .on('drag', transformNodes).on('dragEnd', finishTransform)
      .on('dragGroupStart', event => { if (busy.value) event.stop(); else startTransform(event) })
      .on('dragGroup', transformNodes).on('dragGroupEnd', finishTransform)
      .on('resizeStart', event => { if (busy.value) event.stop(); else startTransform(event, true) })
      .on('resize', transformNodes).on('resizeEnd', finishTransform)
    selecto = new Selecto({ container: viewport, dragContainer: viewport, className: 'designer-selecto',
      selectableTargets: ['.screen-canvas > g[data-node-id]:not(.inner)'], selectByClick: false, selectFromInside: false,
      hitRate: 0, checkInput: true, boundContainer: viewport })
    selecto.on('dragStart', event => {
      const target = event.inputEvent.target as Element | null
      if (busy.value || !target?.closest('.screen-canvas') || target.closest('[data-node-id]')) { event.stop(); return }
      const point = canvasPoint(event.inputEvent); if (!point) { event.stop(); return }
      commit(); drag = null; viewport.focus({ preventScroll: true })
      marquee.value = { startX: point.x, startY: point.y, ...point, baseIds: event.inputEvent.shiftKey ? [...selectedIds.value] : [], originalIds: [...selectedIds.value] }
      selecto?.setSelectedTargets([])
      if (!event.inputEvent.shiftKey) selectedIds.value = []
    }).on('drag', event => {
      const point = canvasPoint(event.inputEvent)
      if (marquee.value && point) { marquee.value.x = point.x; marquee.value.y = point.y }
    }).on('select', event => {
      if (!marquee.value || busy.value) return
      selectedIds.value = [...new Set([...marquee.value.baseIds, ...event.selected.flatMap(element => {
        const id = element.getAttribute('data-node-id')
        const node = id ? document.value.nodes.find(item => item.id === id) : undefined
        return id && node && !node.locked && !node.hidden ? groupMembers(id) : []
      })])]
    }).on('dragEnd', () => { marquee.value = null })
    viewport.addEventListener('scroll', syncInteractionTargets)
    syncInteractionTargets()
  }
  watch([selectedIds, zoom, busy], () => syncInteractionTargets(), { flush: 'post' })
  // Moveable 只依赖几何与层级顺序；文字、颜色、测点等属性变化不再触发整份文档深度监听。
  watch(() => document.value.nodes.map(node => `${node.id}:${node.x}:${node.y}:${node.width}:${node.height}:${node.rotation}`), () => syncInteractionTargets(), { flush: 'post' })
  function detachConnections(removed: Set<string>): void {
    document.value.nodes = document.value.nodes.map(node => {
      if (!removed.has(node.source?.nodeId ?? '') && !removed.has(node.target?.nodeId ?? '')) return node
      return { ...pipeGeometry(node, document.value.nodes), source: removed.has(node.source?.nodeId ?? '') ? null : node.source, target: removed.has(node.target?.nodeId ?? '') ? null : node.target }
    })
  }
  function remove(): void {
    if (busy.value) return
    const ids = selectedIds.value.filter(id => !document.value.nodes.find(node => node.id === id)?.locked)
    if (!ids.length) return
    detachConnections(new Set(ids))
    document.value.nodes = document.value.nodes.filter(n => !ids.includes(n.id))
    selectedIds.value = []; commit()
  }
  function copy(): void { clipboard = JSON.parse(JSON.stringify(document.value.nodes.filter(n => selectedIds.value.includes(n.id)))) }
  function paste(): void {
    if (!clipboard.length) return
    if (document.value.nodes.length + clipboard.length > 1000) { block(tr('screenDesignerView.atMost1000ElementsPerScreen')); return }
    guardError.value = ''
    const ids = new Map(clipboard.map(node => [node.id, crypto.randomUUID()]))
    const clones = translateNodes(clipboard, document.value, 20, 20).map(node => ({ ...JSON.parse(JSON.stringify(node)), id: ids.get(node.id)!, source: node.source ? { ...node.source, nodeId: ids.get(node.source.nodeId) ?? node.source.nodeId } : null, target: node.target ? { ...node.target, nodeId: ids.get(node.target.nodeId) ?? node.target.nodeId } : null }))
    document.value.nodes.push(...clones); selectedIds.value = clones.map(n => n.id); clipboard = JSON.parse(JSON.stringify(clones)); commit()
  }
  function duplicate(): void { copy(); paste() }
  function reorder(front: boolean): void {
    const picked = document.value.nodes.filter(n => selectedIds.value.includes(n.id)), others = document.value.nodes.filter(n => !selectedIds.value.includes(n.id))
    document.value.nodes = front ? [...others, ...picked] : [...picked, ...others]; commit()
  }
  function moveLayer(forward: boolean): void {
    if (busy.value) return
    commit()
    const nodes = [...document.value.nodes], picked = new Set(selectedIds.value)
    for (let index = forward ? nodes.length - 2 : 1; forward ? index >= 0 : index < nodes.length; index += forward ? -1 : 1) {
      const neighbor = index + (forward ? 1 : -1)
      if (picked.has(nodes[index].id) && !picked.has(nodes[neighbor].id)) [nodes[index], nodes[neighbor]] = [nodes[neighbor], nodes[index]]
    }
    document.value.nodes = nodes; commit()
  }
  const groupedSelection = computed(() => selectedNodes.value.some(node => node.groupId))
  function groupSelection(): void {
    if (busy.value || selectedNodes.value.length < 2) return
    commit()
    const groupId = `g${crypto.randomUUID().slice(0, 12)}`
    for (const node of selectedNodes.value) node.groupId = groupId
    commit()
  }
  function ungroupSelection(): void {
    if (busy.value || !groupedSelection.value) return
    commit(); for (const node of selectedNodes.value) node.groupId = null; commit()
  }
  function toggleNodeFlag(node: ScreenNode, key: 'locked' | 'hidden'): void {
    if (busy.value) return
    commit(); node[key] = node[key] ? null : true; commit()
  }
  function toggleSelectionFlag(key: 'locked' | 'hidden'): void {
    if (busy.value || !selectedNodes.value.length) return
    const next = !selectedNodes.value.every(node => node[key])
    commit(); for (const node of selectedNodes.value) node[key] = next ? true : null; commit()
  }
  function startLayerDrag(nodeId: string, event: DragEvent): void { event.dataTransfer?.setData('application/scada-layer', nodeId); if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move' }
  function dropLayer(targetId: string, event: DragEvent): void {
    const from = event.dataTransfer?.getData('application/scada-layer')
    if (from && from !== targetId) reorderLayer(from, targetId)
  }
  function reorderLayer(fromId: string, toId: string): void {
    if (busy.value) return
    const display = [...document.value.nodes].reverse()
    const from = display.findIndex(node => node.id === fromId), to = display.findIndex(node => node.id === toId)
    if (from < 0 || to < 0 || from === to) return
    commit()
    const [moved] = display.splice(from, 1)
    display.splice(to, 0, moved)
    document.value.nodes = display.reverse(); commit()
  }
  const arrangeOperations: ArrangeOperation[] = ['left', 'center-x', 'right', 'top', 'center-y', 'bottom', 'space-x', 'space-y', 'width', 'height', 'size']
  function arrange(operation: ArrangeOperation): void {
    if (busy.value) return
    commit()
    const picked = selectedIds.value.flatMap(id => { const node = document.value.nodes.find(item => item.id === id); return node ? [node] : [] })
    const arranged = arrangeNodes(picked, document.value, operation)
    if (!arranged) { block(tr('screenDesignerView.distributionSpace')); return }
    const byId = new Map(arranged.map(node => [node.id, node]))
    document.value.nodes = document.value.nodes.map(node => byId.get(node.id) ?? node)
    guardError.value = ''; commit()
  }
  function resizeCanvas(): void { document.value.nodes = document.value.nodes.map(n => clampNode(n, document.value)); commit() }
  function closePanel(): void { mobilePanel.value = null }
  const panelApi: DesignerPanelApi = {
    selected, selectedNodes, selectedIds, document, embedded: props.embedded,
    tags, devices, tagValues, tagValuesError, screens, sequences, calcJobs, recipes, recipeTemplates,
    typeLabel, tagLabel, selectedIssues, selectedChangedCount, selectedNodeChanged, focusField, fieldError, closePanel,
    commonOf, mixedOf, commonAction, mixedAction, applyAll, applyNumber, applyAction,
    setBatchActionType, setActionType, syncBindings, batchActionType, batchShowsTarget, batchShowsCommand, batchShowFontSize,
    arrangeOperations, arrange,
    resetAppearance, resetData, resetAction, resetAnimation,
    showEnglishText, englishText, builtinSymbols, userSymbols, symbolName,
    showFontSize, defaultFontSizes, commit, commitField,
    showStyleSection, showTextStyle, setAlign, setStyleFlag, showShapeStyle, setStyleNumber,
    hasTypeSection, hasDataSection, hasActionSection, isControlSelected,
    tagLiveText, tagLiveClass, tagLiveTitle,
    pipeEnds, pipePorts, pipeEndUpdates, pipePortUpdates, addPipePoint,
    stateLimit, addCommonStates, addState, setResetTag, addPanelCommand,
    multiTagTypes, animationSummary, setAnimType, setAnimField,
    controlRangeTypes, fieldLabel, setControlTag, syncControlTag, bindingChanged, resizeCanvas
  }
  // 服务端 400 的响应体里带着逐条 issues；只显示概括的 message 会让用户无从下手。
  function failureIssues(error: unknown): ScreenIssue[] {
    const body = (error as { response?: { data?: { issues?: unknown } } }).response?.data?.issues
    return Array.isArray(body) ? body.filter((item): item is ScreenIssue => !!item && typeof (item as ScreenIssue).message === 'string') : []
  }
  // 模板展开后的内部图元（实例--内部）归属到可编辑的实例上。
  function issueOwner(nodeId: string | undefined): ScreenNode | undefined {
    if (!nodeId) return
    return document.value.nodes.find(node => node.id === nodeId)
      ?? document.value.nodes.find(node => node.type === 'template' && nodeId.startsWith(`${node.id}${SEPARATOR}`))
  }
  // 图元文字优先；没有文字时用“类型 · 编号前缀”，避免只显示一串 UUID。
  function issueLabel(nodeId: string | undefined): string {
    if (!nodeId) return tr('screenDesignerView.canvas')
    const node = issueOwner(nodeId)
    if (!node) return nodeId
    return node.text || `${palette.find(item => item.type === node.type)?.label ?? node.type} · ${node.id.slice(0, 6)}`
  }
  // 内联告警直接给出前两条问题，其余交给弹窗列表。
  function issueDigest(list: ScreenIssue[]): string {
    const head = list.slice(0, 2).map(issue => `${issueLabel(issue.nodeId)}：${issue.message}`).join('；')
    return list.length > 2 ? `${head}…` : head
  }
  function showIssues(list: ScreenIssue[], error = ''): void {
    issues.value = list; issuesError.value = list.length ? '' : error; issuesVisible.value = true
  }
  async function openConflict(): Promise<void> {
    try {
      const server = await screenApi.get(id, true)
      conflict.value = { server, rows: conflictRowsFor(server.document) }
      conflictVisible.value = true
      actionError.value = tr('screenDesignerView.conflictHint', [server.revision, revision.value])
    } catch { actionError.value = tr('screenDesignerView.conflictUnavailable') }
  }
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
  function reloadServerDraft(): void {
    const server = conflict.value?.server
    if (!server) return
    document.value = server.document
    name.value = server.name
    revision.value = server.revision
    stableSnapshot = snapshot.value; savedSnapshot.value = stableSnapshot
    selectedIds.value = []; conflictVisible.value = false; actionError.value = ''
  }
  function overwriteServerDraft(): void {
    const server = conflict.value?.server
    if (!server) return
    revision.value = server.revision
    conflictVisible.value = false
    void save()
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
    void save()
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
    let remote: ScreenDocument | null = null
    try { remote = (await screenApi.get(id, false)).document } catch { return true }
    const diff = diffLabels(document.value, remote)
    if (!diff.added.length && !diff.removed.length && !diff.changed.length) return true
    publishDiff.value = diff
    publishDiffSettled = false
    publishDiffVisible.value = true
    return await new Promise<boolean>(resolve => { publishDiffResolve = resolve })
  }
  async function save(): Promise<boolean> {
    if (props.embedded) { commit(); emit('save'); return false }
    if (busy.value || loadError.value) return false
    commit(); busy.value = true; actionError.value = ''
    const sent = snapshot.value
    try { const data = JSON.parse(sent); const result = await screenApi.save(id, data.name, revision.value, data.document); revision.value = result.revision; savedSnapshot.value = sent; clearLocalBackup(); savedAt.value = formatTime(Date.now()); actionError.value = ''; ElMessage.success(tr('screenDesignerView.draftSaved')); return true }
    catch (e) {
      if ((e as { response?: { status?: number } }).response?.status === 409) { busy.value = false; await openConflict(); return false }
      // 保存被服务端以“画面格式无效”拒绝时，把逐条问题摊开，而不是只留一句概括。
      const list = failureIssues(e)
      if (list.length) { showIssues(list); actionError.value = tr('screenDesignerView.saveRejectedIssueS', [list.length, issueDigest(list)]) }
      else actionError.value = errorMessage(e, tr('screenDesignerView.saveFailedYourChangesRemainOnTheCanvas'))
      return false
    }
    finally { busy.value = false }
  }
  async function validate(): Promise<void> {
    try { showIssues(await screenApi.validate(document.value, id)) }
    catch (e) { showIssues([], errorMessage(e, tr('screenDesignerView.validateFailed'))) }
  }
  async function confirmPublishUsage(): Promise<boolean> {
    let usage
    try { usage = await screenApi.usage(id) } catch { return true }
    const layouts = usage.layouts.filter(item => item.inPublished)
    if (!layouts.length && !usage.workstations.length) return true
    const layoutNames = layouts.map(item => item.publishedName || item.name).join(', ')
    const stationNames = usage.workstations.map(item => item.name).join(', ')
    try {
      await ElMessageBox.confirm(tr('screenDesignerView.publishImpactMessage', [layoutNames || '-', stationNames || '-']), tr('screenDesignerView.publishImpactTitle'),
        { type: 'warning', confirmButtonText: tr('screenDesignerView.publishImpactConfirm'), cancelButtonText: tr('screenDesignerView.cancel') })
      return true
    } catch { return false }
  }
  async function publish(): Promise<void> {
    if (busy.value || loadError.value) return
    if (dirty.value && !await save()) return
    let issues: ScreenIssue[] = []
    try { issues = await screenApi.validate(document.value, id) }
    catch { issues = [] }
    if (issues.length) { showIssues(issues); return }
    if (!await confirmPublishDiff()) return
    if (!await confirmPublishUsage()) return
    busy.value = true; actionError.value = ''
    try {
      const result = await screenApi.publish(id, revision.value); revision.value = result.revision; publishedRevision.value = result.publishedRevision
      actionError.value = ''; rollbackSucceeded.value = false; rollbackWarning.value = ''
      if (result.warning) ElMessage.warning({ message: result.warning, duration: 8000 }); else ElMessage.success(tr('screenDesignerView.publishedRuntimeScreenUpdated'))
    } catch (e) {
      const list = failureIssues(e)
      if (list.length) { showIssues(list); actionError.value = tr('screenDesignerView.publishRejectedIssueS', [list.length, issueDigest(list)]) }
      else actionError.value = errorMessage(e, tr('screenDesignerView.publishFailed'))
    }
    finally { busy.value = false }
  }
  async function focusIssue(issue: ScreenIssue): Promise<void> {
    // Expanded template children belong to the editable instance.
    const owner = issueOwner(issue.nodeId)
    selectedIds.value = owner ? [owner.id] : []; issuesVisible.value = false
    await revealSelection()
    await focusField(fieldForIssue(issue.message))
  }
  function keydown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement
    if (event.defaultPrevented || event.isComposing || event.altKey || loading.value || loadError.value || busy.value || previewVisible.value || issuesVisible.value || templateVisible.value || versionsVisible.value || conflictVisible.value || publishDiffVisible.value) return
    if (target.closest('[role="dialog"]') || (!target.closest('.designer') && target !== window.document.body)) return
    if (marquee.value) {
      if (event.key === 'Escape') { selectedIds.value = marquee.value.originalIds; marquee.value = null; event.preventDefault() }
      return
    }
    const modifier = event.ctrlKey || event.metaKey
    if (modifier && event.key.toLowerCase() === 's') { event.preventDefault(); void save(); return }
    if (target.closest('input, textarea, select, [contenteditable="true"], .el-select, [role="dialog"]')) return
    if (modifier && event.key.toLowerCase() === 'a') { event.preventDefault(); selectedIds.value = document.value.nodes.map(n => n.id); return }
    if (event.key === 'Escape') { selectedIds.value = []; return }
    if (modifier && event.key.toLowerCase() === 'z') { event.preventDefault(); event.shiftKey ? redo() : undo(); return }
    if (modifier && event.key.toLowerCase() === 'y') { event.preventDefault(); redo(); return }
    if (modifier && event.key.toLowerCase() === 'c') { event.preventDefault(); copy(); return }
    if (modifier && event.key.toLowerCase() === 'v') { event.preventDefault(); paste(); return }
    if (modifier && event.key.toLowerCase() === 'd') { event.preventDefault(); duplicate(); return }
    if (modifier && event.key.toLowerCase() === 'g') { event.preventDefault(); event.shiftKey ? ungroupSelection() : groupSelection(); return }
    if (event.key === '[' || event.key === ']') { event.preventDefault(); moveLayer(event.key === ']'); return }
    if (event.key === 'Delete' || event.key === 'Backspace') { event.preventDefault(); remove(); return }
    if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key) && selectedIds.value.length) {
      event.preventDefault(); const step = event.shiftKey ? 10 : 1
      const moved = translateNodes(document.value.nodes.filter(node => selectedIds.value.includes(node.id)), document.value,
        event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0, event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0)
      const positions = new Map(moved.map(node => [node.id, node]))
      document.value.nodes = document.value.nodes.map(node => positions.get(node.id) ?? node); commit()
    }
  }
  let unregisterShortcuts: (() => void) | undefined
  function beforeUnload(event: BeforeUnloadEvent): void { persistLocalDraft(); if (!props.embedded && dirty.value) { event.preventDefault(); event.returnValue = '' } }
  async function confirmLeave(): Promise<boolean> { if (versionsSubmitting.value) return false; if (props.embedded || !dirty.value) return true; try { await ElMessageBox.confirm(tr('screenDesignerView.theScreenHasUnsavedChangesLeaveAnyway'), tr('screenDesignerView.unsavedChanges'), { type: 'warning', confirmButtonText: tr('screenDesignerView.leave'), cancelButtonText: tr('screenDesignerView.keepEditing') }); clearLocalBackup(); setLeavingDiscarded(true); return true } catch { return false } }
  onBeforeRouteLeave(confirmLeave)
  onBeforeRouteUpdate(confirmLeave)
  // 画面文档存在库里是可演进的 JSON，字段缺失时不能直接塞进画布：
  // 模板里第一处 document.nodes.length 就会抛错，整页空白且没有任何提示。
  // 也不能退化成空画布——用户会以为画面本来没有元素，一保存就把原内容覆盖掉。
  // 顺带深拷贝：嵌入式面板直接改 props 里的对象会污染父组件。
  function requireDocument(value: unknown): ScreenDocument {
    const doc = readableDocument(value)
    if (!doc) throw new Error(tr('designerUx.documentUnreadable'))
    return JSON.parse(JSON.stringify(doc)) as ScreenDocument
  }
  onMounted(async () => {
    void library.ensure()
    void loadRecipeCatalog()
    void loadSequenceCatalog(); void loadScriptCatalog()
    try {
      if (props.embedded) {
        document.value = requireDocument(props.modelValue ?? emptyDocument()); tags.value = props.bindingTags ?? []
        name.value = tr('screenDesignerView.faceplateTemplate'); stableSnapshot = snapshot.value; savedSnapshot.value = stableSnapshot
        await nextTick(); await fitCanvas()
      } else {
      const [screen, allTags, allDevices, allScreens] = await Promise.all([screenApi.get(id, true), tagApi.list(), deviceApi.list(), screenApi.list(true)])
      document.value = requireDocument(screen.document); name.value = screen.name; revision.value = screen.revision; publishedRevision.value = screen.publishedRevision
      tags.value = allTags; devices.value = allDevices; screens.value = allScreens; stableSnapshot = snapshot.value; savedSnapshot.value = stableSnapshot
      findLocalDraft()
      await nextTick(); await fitCanvas()
      }
    } catch (e) { loadError.value = errorMessage(e, tr('screenDesignerView.failedToLoadScreen')) } finally { loading.value = false }
    // 加载失败时画布本身就是隐藏的（模板里 v-if="!loadError"），再初始化交互只会
    // 在空文档上挂一堆监听和测点轮询。
    if (disposed || loadError.value) return
    initializeInteractions()
    startTagValues()
    window.addEventListener('keydown', keydown); window.addEventListener('beforeunload', beforeUnload)
    window.addEventListener('pagehide', persistLocalDraft)
    unregisterShortcuts = registerShortcuts({
      id: 'screen-designer',
      titleKey: 'shortcuts.designer',
      items: [
        { keys: ['Ctrl', 'S'], descKey: 'shortcuts.designerSave' },
        { keys: ['Ctrl', 'Z'], descKey: 'shortcuts.designerUndo' },
        { keys: ['Ctrl', 'Shift', 'Z'], descKey: 'shortcuts.designerRedo' },
        { keys: ['Ctrl', 'Y'], descKey: 'shortcuts.designerRedo' },
        { keys: ['Ctrl', 'C'], descKey: 'shortcuts.designerCopy' },
        { keys: ['Ctrl', 'V'], descKey: 'shortcuts.designerPaste' },
        { keys: ['Ctrl', 'D'], descKey: 'shortcuts.designerDuplicate' },
        { keys: ['Ctrl', 'A'], descKey: 'shortcuts.designerSelectAll' },
        { keys: ['Ctrl', 'G'], descKey: 'shortcuts.designerGroup' },
        { keys: ['Ctrl', 'Shift', 'G'], descKey: 'shortcuts.designerUngroup' },
        { keys: ['['], descKey: 'shortcuts.designerLayerBack' },
        { keys: [']'], descKey: 'shortcuts.designerLayerForward' },
        { keys: ['Delete'], descKey: 'shortcuts.designerDelete' },
        { keys: ['Shift', 'Arrow'], descKey: 'shortcuts.designerNudge' }
      ]
    })
  })
  watch(() => props.bindingTags, tagsToUse => { if (props.embedded && tagsToUse) tags.value = tagsToUse })
  watch(() => props.disabled, disabled => { if (props.embedded) busy.value = disabled })
  onBeforeUnmount(() => { unregisterShortcuts?.(); waypointGesture?.abort(); persistLocalDraft(); disposeDraftTimer(); disposed = true; stage.value?.removeEventListener('scroll', syncInteractionTargets); moveable?.destroy(); selecto?.destroy(); window.removeEventListener('keydown', keydown); window.removeEventListener('beforeunload', beforeUnload); window.removeEventListener('pagehide', persistLocalDraft) })
  return {
    route, router, id, returnToRuntime, runtimePath, document, name, revision, publishedRevision, loading, loadError, actionError, guardError, busy, zoom, snap, selectedIds, tags, devices, screens, stage, propertiesPanel, recipeTemplates, recipes, sequences, calcJobs, previewVisible, issuesVisible, issues, issuesError, savedAt, publishDiffVisible, publishDiff, publishDiffExtra, conflictVisible, conflict, conflictRows, conflictNeedsChoice, versionsVisible, versionsSubmitting, rollbackSucceeded, rollbackWarning, setVersionsBusy, applyRollback, history, savedSnapshot, selected, selectedNodes, isControlSelected, dataBindingTypes, typeSettingTypes, labelTypes, hasDataSection, hasActionSection, hasTypeSection, showEnglishText, showFontSize, batchShowFontSize, textStyleNodeTypes, shapeStyleNodeTypes, showTextStyle, showShapeStyle, showStyleSection, setAlign, setStyleFlag, setStyleNumber, controlRangeTypes, stateLimit, multiTagTypes, applyNumber, applyAction, clearActionTargets, setActionType, setBatchActionType, batchActionType, batchShowsTarget, batchShowsCommand, pipeEnds, pipeEndUpdates, pipePortUpdates, setPipePort, setPipeEnd, addPipePoint, addCommonStates, addState, addPanelCommand, beginWaypoint, mobilePanel, guides, marquee, selectionBox, snapshot, dirty, savedNodes, nodeChanged, selectedNodeChanged, selectedChangedCount, recoverableDraft, draftError, localSavedAt, draftBaseChanged, findLocalDraft, clearLocalBackup, persistLocalDraft, restoreLocalDraft, discardLocalDraft, setLeavingDiscarded, disposeDraftTimer, notify, commit, commitField, fieldLabel, resetAppearance, resetData, resetAction, resetAnimation, setAnimType, restore, undo, redo, tagLabel, typeLabel, bindingChanged, setControlTag, setResetTag, syncControlTag, groupMembers, select, block, add, library, locale, userSymbols, templates, symbolName, symbolCategoryLabel, componentQuery, layerQuery, expandedGroups, paletteTab, collapsedGroups, renamingId, renameDraft, layerTitle, toggleGroup, startRename, commitRename, openCanvasSettings, matches, filteredPalette, filteredSymbols, filteredUserSymbols, filteredTemplates, componentTotal, componentCount, symbolGroups, tagNames, tagsById, selectedIssues, fieldError, focusField, fieldForIssue, displayedTagIds, tagValues, tagValuesError, startTagValues, loadRecipeCatalog, loadSequenceCatalog, loadScriptCatalog, tagLiveText, tagLiveClass, tagLiveTitle, animationSummary, filteredLayers, layerLimit, visibleLayers, layerEntries, zoomOptions, fitCanvas, changeZoom, revealSelection, selectLayer, englishText, pushNode, nextPos, addSymbol, addTemplate, syncBindings, startSymbolDrag, templateVisible, templateError, templateForm, openTemplateDialog, createTemplate, startPaletteDrag, drop, beginDrag, canvasPoint, beginSelection, startTransform, snapAlignment, updateGuides, transformNodes, finishTransform, syncInteractionTargets, initializeInteractions, detachConnections, remove, copy, paste, duplicate, reorder, moveLayer, groupedSelection, groupSelection, ungroupSelection, toggleNodeFlag, toggleSelectionFlag, startLayerDrag, dropLayer, reorderLayer, arrangeOperations, arrange, resizeCanvas, closePanel, panelApi, failureIssues, issueOwner, issueLabel, issueDigest, showIssues, openConflict, conflictLabel, sameNode, conflictRowsFor, reloadServerDraft, overwriteServerDraft, mergeConflict, diffLabels, settlePublishDiff, confirmPublishDiff, save, validate, confirmPublishUsage, publish, focusIssue, keydown, beforeUnload, confirmLeave, requireDocument, ref, palette, formatTime, openShortcutHelp
  }
}
