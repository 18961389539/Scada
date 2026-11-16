import { computed, type ComputedRef, type Ref } from 'vue'
import { createNode } from '../screens/model'
import type { ScreenAction, ScreenAnimation, ScreenNode } from '../screens/model'

interface NodeEditingOptions {
  selected: ComputedRef<ScreenNode | undefined>
  selectedNodes: ComputedRef<ScreenNode[]>
  selectedIds: Ref<string[]>
  busy: Ref<boolean>
  commit: (key?: unknown) => void
  commitField: (field: string) => void
}

/** 选中集合的通用属性、批量动作与样式编辑。 */
export function useDesignerNodeEditing(options: NodeEditingOptions) {
  const { selected, selectedNodes, selectedIds, busy, commit, commitField } = options

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

  return {
    commonOf, mixedOf, commonAction, mixedAction, applyAll, applyNumber, applyAction,
    clearActionTargets, setActionType, setBatchActionType, batchActionType, batchShowsTarget, batchShowsCommand,
    setAlign, setStyleFlag, setStyleNumber,
    resetAppearance, resetData, resetAction, resetAnimation, setAnimType, setAnimField,
    groupedSelection, groupSelection, ungroupSelection, toggleNodeFlag, toggleSelectionFlag
  }
}
