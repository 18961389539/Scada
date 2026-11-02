import type { ComputedRef, Ref, WritableComputedRef } from 'vue'
import type { Device, Tag, TagValue } from '../types'
import type { Recipe, RecipeTemplate } from '../api/recipes'
import type { ControlSequence } from '../api/control'
import type { CalcJobInfo } from '../api/calcJobs'
import type { ArrangeOperation, LibraryItem, NodeType, ScreenAction, ScreenAnimation, ScreenDocument, ScreenNode, ScreenPort, ScreenSummary } from './model'
import type { BuiltinSymbol } from './symbols'

export interface PanelIssue { field: string; message: string }

// 属性面板通过单一接口拿设计器状态与命令，拆分组件时依赖集中在这里，便于类型检查兜底。
export interface DesignerPanelApi {
  selected: ComputedRef<ScreenNode | undefined>
  selectedNodes: ComputedRef<ScreenNode[]>
  selectedIds: Ref<string[]>
  document: Ref<ScreenDocument>
  embedded: boolean
  tags: Ref<Tag[]>
  devices: Ref<Device[]>
  tagValues: Ref<Record<number, TagValue>>
  tagValuesError: Ref<boolean>
  screens: Ref<ScreenSummary[]>
  sequences: Ref<ControlSequence[]>
  calcJobs: Ref<CalcJobInfo[]>
  recipes: Ref<Recipe[]>
  recipeTemplates: Ref<RecipeTemplate[]>
  typeLabel: (node: ScreenNode) => string
  tagLabel: (tag: Tag) => string
  selectedIssues: ComputedRef<PanelIssue[]>
  selectedChangedCount: ComputedRef<number>
  selectedNodeChanged: ComputedRef<boolean>
  focusField: (field: string) => Promise<void>
  fieldError: (field: string) => string
  closePanel: () => void
  commonOf: <K extends keyof ScreenNode>(key: K) => ScreenNode[K] | undefined
  mixedOf: <K extends keyof ScreenNode>(key: K) => boolean
  commonAction: <T>(get: (action: ScreenAction) => T) => T | undefined
  mixedAction: <T>(get: (action: ScreenAction) => T) => boolean
  applyAll: <K extends keyof ScreenNode>(key: K, value: ScreenNode[K]) => void
  applyNumber: (key: 'rotation' | 'decimals', value: number | undefined) => void
  applyAction: (patch: Partial<ScreenAction>) => void
  setBatchActionType: (type: ScreenAction['type']) => void
  setActionType: (type: ScreenAction['type']) => void
  syncBindings: (node: ScreenNode) => void
  batchActionType: ComputedRef<ScreenAction['type'] | undefined>
  batchShowsTarget: ComputedRef<boolean>
  batchShowsCommand: ComputedRef<boolean>
  batchShowFontSize: ComputedRef<boolean>
  arrangeOperations: ArrangeOperation[]
  arrange: (operation: ArrangeOperation) => void
  resetAppearance: () => void
  resetData: () => void
  resetAction: () => void
  resetAnimation: () => void
  showEnglishText: ComputedRef<boolean>
  englishText: WritableComputedRef<string>
  builtinSymbols: BuiltinSymbol[]
  userSymbols: ComputedRef<LibraryItem[]>
  symbolName: (symbol: BuiltinSymbol) => string
  showFontSize: ComputedRef<boolean>
  defaultFontSizes: Partial<Record<NodeType, number>>
  commit: (key?: unknown) => void
  commitField: (field: string) => void
  showStyleSection: ComputedRef<boolean>
  showTextStyle: ComputedRef<boolean>
  setAlign: (value: string | number | boolean | undefined) => void
  setStyleFlag: (key: 'wrap' | 'autoFit' | 'bold', value: string | number | boolean | undefined) => void
  showShapeStyle: ComputedRef<boolean>
  setStyleNumber: (key: 'opacity' | 'borderWidth' | 'radius', value: number | undefined) => void
  hasTypeSection: ComputedRef<boolean>
  hasDataSection: ComputedRef<boolean>
  hasActionSection: ComputedRef<boolean>
  isControlSelected: ComputedRef<boolean>
  tagLiveText: (tagId: number | null | undefined, unit?: string) => string
  tagLiveClass: (tagId: number | null | undefined) => string
  tagLiveTitle: (tagId: number | null | undefined) => string
  pipeEnds: readonly ['source', 'target']
  pipePorts: ScreenPort['port'][]
  pipeEndUpdates: Record<'source' | 'target', (value: string | null) => void>
  pipePortUpdates: Record<'source' | 'target', (value: ScreenPort['port']) => void>
  addPipePoint: () => void
  stateLimit: ComputedRef<number>
  addCommonStates: () => void
  addState: () => void
  setResetTag: (value: number | null | undefined) => void
  addPanelCommand: () => void
  multiTagTypes: NodeType[]
  animationSummary: (animation: ScreenAnimation) => string
  setAnimType: (value: string) => void
  setAnimField: <K extends keyof ScreenAnimation>(key: K, value: ScreenAnimation[K]) => void
  controlRangeTypes: NodeType[]
  fieldLabel: (base: string, unit?: string | null) => string
  setControlTag: (value: number | null | undefined) => void
  syncControlTag: () => void
  bindingChanged: () => void
  resizeCanvas: () => void
}
