import { tr } from '../i18n'
export type NodeType = 'text' | 'value' | 'pump' | 'valve' | 'tank' | 'pipe' | 'rectangle' | 'ellipse' | 'button' | 'gauge' | 'motor' | 'fan' | 'symbol' | 'template' | 'led' | 'progress' | 'kpi' | 'clock' | 'image' | 'marquee' | 'dial' | 'chart-bar' | 'chart-pie' | 'chart-line' | 'chart-bar-horizontal' | 'chart-bar-stack' | 'chart-area' | 'chart-radar' | 'chart-funnel' | 'chart-gauge' | 'alarm-banner' | 'alarm-stats' | 'alarm-list' | 'table' | 'history-table' | 'oee-kpi' | 'switch' | 'slider' | 'spinner' | 'input' | 'polyline' | 'equipment-panel' | 'reset' | 'jog' | 'pulse' | 'silence' | 'selector' | 'knob' | 'recipe'
export interface ScreenPoint { x: number; y: number }
export interface ScreenState { value: number; label: string; color: string }
// 仪表色带：在量程弧线上按数值区间着色，最多 6 条。
export interface ScreenGaugeBand { from: number; to: number; color: string }
// 图表目标线：用于折线/面积/柱状图显示设定值或阈值。
export interface ScreenTargetLine { value: number; color: string; label?: string | null }
export interface ScreenChartOptions {
  legend?: boolean | null
  grid?: boolean | null
  dataLabels?: boolean | null
  yUnit?: string | null
  targetLines?: ScreenTargetLine[] | null
}
export interface ScreenPort { nodeId: string; port: 'left' | 'right' | 'top' | 'bottom' }
export interface ScreenPanelCommand { key: string; label: string; tagId: number | null; bind?: string | null; value: number }
export interface ScreenAction { type: 'none' | 'navigate' | 'popup' | 'command' | 'sequence' | 'recipe' | 'script'; screenId: number | null; tagId: number | null; value: number; sequenceId?: number | null; recipeId?: number | null; jobId?: number | null; bind?: string | null }
export interface ScreenNode {
  id: string; type: NodeType; x: number; y: number; width: number; height: number; rotation: number
  text: string; color: string; activeColor: string; unit: string; decimals: number; min: number; max: number
  activeThreshold: number; tagId: number | null; action: ScreenAction
  // 可选扩展（为空不保存）：图库符号、报警状态测点、模板实例及绑定、模板内占位符、多语言文字、多测点（图表/表格）、统计窗口秒数、OEE 产线。
  symbol?: string | null; alarmTagId?: number | null; templateKey?: string | null; bindings?: ScreenBinding[] | null
  bind?: string | null; alarmBind?: string | null; texts?: Record<string, string> | null
  tags?: number[] | null; windowSec?: number | null; lineId?: number | null; animation?: ScreenAnimation | null
  points?: ScreenPoint[] | null; states?: ScreenState[] | null; source?: ScreenPort | null; target?: ScreenPort | null; commands?: ScreenPanelCommand[] | null
  // 点动/瞬时按钮：释放值、脉冲宽度（瞬时）与最长保持时间（点动）；复位/消音：模式与时长。
  releaseValue?: number | null; pulseMs?: number | null; maxHoldMs?: number | null
  resetMode?: 'alarms' | 'tag' | null; silenceMinutes?: number | null
  // 配方控件：绑定的配方模板编号。
  recipeTemplateId?: number | null
  // 模板内部占位符：多测点（图表/表格顺序绑定）与动画驱动测点。
  tagBinds?: string[] | null; animationBind?: string | null
  // 主文字字号；为空时使用该控件类型的默认字号。
  fontSize?: number | null
  // 仪表色带与设定值标记。
  gaugeBands?: ScreenGaugeBand[] | null
  setpoint?: number | null
  // 表格显示列（table: name/value/unit；history-table: name/last/min/max/avg）。
  tableColumns?: string[] | null
  // 图表显示选项与目标线。
  chartOptions?: ScreenChartOptions | null
  // 文字与形状样式：对齐、换行/自适应、加粗，以及形状透明度、边框与圆角。
  align?: 'left' | 'center' | 'right' | null; wrap?: boolean | null; autoFit?: boolean | null; bold?: boolean | null
  opacity?: number | null; borderWidth?: number | null; radius?: number | null
  // 成组、锁定与隐藏；为空表示未成组/未锁定/正常显示。
  groupId?: string | null; locked?: boolean | null; hidden?: boolean | null
  // 仅渲染用：展开后的模板内部图元指向其实例。
  ownerId?: string
}
// 各控件类型的主文字默认字号；未设置 fontSize 时保持原有显示。
export const defaultFontSizes: Partial<Record<NodeType, number>> = {
  text: 16, value: 24, button: 16, rectangle: 16, ellipse: 16, marquee: 16, kpi: 28, progress: 14, clock: 26,
  gauge: 15, dial: 15, switch: 11, slider: 12, spinner: 16, input: 13, 'equipment-panel': 13,
  jog: 13, pulse: 13, selector: 12, knob: 12, reset: 14, silence: 14, recipe: 13,
  symbol: 12, pump: 12, valve: 12, motor: 12, fan: 12, tank: 12, 'alarm-banner': 16, 'oee-kpi': 22
}
export const fontSizeTypes = Object.keys(defaultFontSizes) as NodeType[]
export function nodeFontSize(node: ScreenNode, fallback: number): number {
  const value = node.fontSize
  return typeof value === 'number' && Number.isFinite(value) && value >= 8 && value <= 96 ? value : fallback
}
// 矢量动画（A 方案）：由测点值或运行态驱动，经 CSS transform/clip-path 呈现。
export type AnimationType = 'rotate' | 'level' | 'flow'
export type AnimationSource = 'value' | 'state'
export interface ScreenAnimation {
  type: AnimationType; source: AnimationSource; tagId: number | null
  from: number; to: number; outFrom: number; outTo: number
}
export interface ScreenBinding { key: string; tagId: number | null }
export interface LibraryItem {
  id: number; key: string; name: string; category: string; kind: 'symbol' | 'template'; svg: string | null; width: number; height: number
  revision: number; updatedTs: number; updatedBy: string; document: ScreenDocument | null; placeholders: string[]; usedBy?: string[]
}
export const SEPARATOR = '--'
// 图表类图元（在画面内以 ECharts SVG 输出）。
export const chartTypes: NodeType[] = ['chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal', 'chart-bar-stack', 'chart-area', 'chart-radar', 'chart-funnel', 'chart-gauge', 'alarm-stats']
// 写交互控件（通过控制闭环下发运行期取值）。
export const controlTypes: NodeType[] = ['switch', 'slider', 'spinner', 'input', 'jog', 'pulse', 'selector', 'knob']
// 需要绑定多个测点（tags）的图元类型。
const tagTypes: NodeType[] = ['chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal', 'chart-bar-stack', 'chart-area', 'chart-radar', 'chart-funnel', 'table', 'history-table']
// 需要统计窗口（windowSec）的图元类型。
const windowTypes: NodeType[] = ['chart-line', 'chart-area', 'history-table', 'oee-kpi']
// 与服务端 ScreenExpander 相同：模板实例展开为内部图元（编号“实例--内部”），按实例绑定解析占位符；实例自身保留为一个透明框（编辑选取用）。
export function expandNodes(nodes: ScreenNode[], library: LibraryItem[]): ScreenNode[] {
  const out: ScreenNode[] = []
  for (const node of nodes) {
    if (node.type !== 'template') { out.push(node); continue }
    const template = library.find(x => x.kind === 'template' && x.key === node.templateKey)
    if (template?.document) {
      const sx = node.width / template.width, sy = node.height / template.height
      const bound = new Map((node.bindings ?? []).map(b => [b.key, b.tagId]))
      const resolve = (key?: string | null) => (key ? bound.get(key) ?? null : null)
      for (const inner of template.document.nodes) out.push({
        ...inner, id: `${node.id}${SEPARATOR}${inner.id}`, ownerId: node.id, x: node.x + inner.x * sx, y: node.y + inner.y * sy, width: inner.width * sx, height: inner.height * sy,
        tagId: inner.bind ? resolve(inner.bind) : null, alarmTagId: inner.alarmBind ? resolve(inner.alarmBind) : null, bind: null, alarmBind: null,
        tags: inner.tagBinds?.length ? inner.tagBinds.map(resolve).filter((id): id is number => id != null) : inner.tags,
        tagBinds: null, animationBind: null,
        animation: inner.animation ? { ...inner.animation, tagId: inner.animationBind ? resolve(inner.animationBind) : inner.animation.tagId } : null,
        source: inner.source ? { ...inner.source, nodeId: `${node.id}${SEPARATOR}${inner.source.nodeId}` } : null,
        target: inner.target ? { ...inner.target, nodeId: `${node.id}${SEPARATOR}${inner.target.nodeId}` } : null,
        commands: inner.commands?.map(command => ({ ...command, tagId: resolve(command.bind), bind: null })),
        action: { ...inner.action, tagId: inner.action.bind ? resolve(inner.action.bind) : null, bind: null }
      })
    }
    out.push(node)
  }
  return out
}
// 画面保存前去掉空的扩展字段，保持与旧格式一致。
export function cleanNode(node: ScreenNode): ScreenNode {
  const copy: ScreenNode = { ...node, action: { ...node.action } }
  for (const k of ['symbol', 'alarmTagId', 'templateKey', 'bindings', 'bind', 'alarmBind', 'texts', 'tags', 'tagBinds', 'animationBind', 'windowSec', 'lineId', 'animation', 'ownerId', 'points', 'states', 'source', 'target', 'commands', 'releaseValue', 'pulseMs', 'maxHoldMs', 'resetMode', 'silenceMinutes', 'recipeTemplateId', 'fontSize', 'align', 'opacity', 'borderWidth', 'radius', 'groupId', 'gaugeBands', 'setpoint', 'tableColumns', 'chartOptions'] as const) if (copy[k] == null || (k === 'texts' && !Object.keys(copy.texts ?? {}).length) || ((k === 'tags' || k === 'tagBinds' || k === 'gaugeBands' || k === 'tableColumns') && !(copy[k] as unknown[] | null)?.length) || (k === 'chartOptions' && !Object.keys(copy.chartOptions ?? {}).length)) delete copy[k]
  for (const k of ['wrap', 'autoFit', 'bold', 'locked', 'hidden'] as const) if (!copy[k]) delete copy[k]
  if (copy.action.bind == null) delete copy.action.bind
  if (copy.action.sequenceId == null) delete copy.action.sequenceId
  if (copy.action.recipeId == null) delete copy.action.recipeId
  if (copy.action.jobId == null) delete copy.action.jobId
  return copy
}
export interface ScreenDocument { schemaVersion: number; width: number; height: number; background: string; nodes: ScreenNode[]; symbolAssets?: Record<string, string> | null }
export interface ScreenRect { x: number; y: number; width: number; height: number }
export function nodesInRect(nodes: ScreenNode[], box: ScreenRect): ScreenNode[] {
  return nodes.filter(node => {
    const radians = node.rotation * Math.PI / 180
    const width = Math.abs(node.width * Math.cos(radians)) + Math.abs(node.height * Math.sin(radians))
    const height = Math.abs(node.width * Math.sin(radians)) + Math.abs(node.height * Math.cos(radians))
    const left = node.x + (node.width - width) / 2, top = node.y + (node.height - height) / 2
    return left < box.x + box.width && left + width > box.x && top < box.y + box.height && top + height > box.y
  })
}
export interface ScreenSummary { id: number; name: string; revision: number; publishedRevision: number | null; updatedTs: number; updatedBy: string; hasDraftChanges: boolean; layoutCount?: number; layoutNames?: string[] }
export interface ScreenDetail extends ScreenSummary { document: ScreenDocument; requiresRepublish?: boolean; message?: string | null }
export interface ScreenIssue { nodeId: string; message: string }
export const palette: { type: NodeType; label: string; icon: string }[] = [
  { type: 'polyline', label: tr('hmi.polyline'), icon: 'L' }, { type: 'equipment-panel', label: tr('hmi.panel'), icon: 'PLC' }, { type: 'input', label: tr('hmi.input'), icon: '123' },
  { type: 'text', label: tr('model.text'), icon: 'T' }, { type: 'value', label: tr('model.numericValue'), icon: '123' },
  { type: 'pump', label: tr('model.waterPump'), icon: '◉' }, { type: 'valve', label: tr('model.valves'), icon: '⋈' },
  { type: 'tank', label: tr('model.tanks'), icon: '▤' }, { type: 'pipe', label: tr('model.pipes'), icon: '━' },
  { type: 'motor', label: tr('model.motor'), icon: 'M' }, { type: 'fan', label: tr('model.fans'), icon: '✣' },
  { type: 'gauge', label: tr('model.instruments'), icon: '◴' }, { type: 'dial', label: tr('model.dialGauge'), icon: '◔' },
  { type: 'button', label: tr('model.button'), icon: '▣' },
  { type: 'switch', label: tr('model.switch'), icon: '⏻' }, { type: 'slider', label: tr('model.slider'), icon: '⇔' }, { type: 'spinner', label: tr('model.stepper'), icon: '±' },
  { type: 'jog', label: tr('hmi.jog'), icon: '↓' }, { type: 'pulse', label: tr('hmi.pulse'), icon: '↯' },
  { type: 'selector', label: tr('hmi.selector'), icon: '⇶' }, { type: 'knob', label: tr('hmi.knob'), icon: '◑' },
  { type: 'reset', label: tr('hmi.reset'), icon: '⟲' }, { type: 'silence', label: tr('hmi.silence'), icon: '♪' },
  { type: 'recipe', label: tr('hmi.recipe'), icon: '⇩' },
  { type: 'led', label: tr('model.statusLamp'), icon: '●' }, { type: 'progress', label: tr('model.progressBar'), icon: '▰' },
  { type: 'kpi', label: tr('model.kPICard'), icon: '▢' }, { type: 'clock', label: tr('model.clock'), icon: '◷' },
  { type: 'image', label: tr('model.image'), icon: '▨' }, { type: 'marquee', label: tr('model.marquee'), icon: '⇄' },
  { type: 'chart-bar', label: tr('model.barChart'), icon: '▮' }, { type: 'chart-pie', label: tr('model.pieChart'), icon: '◕' },
  { type: 'chart-line', label: tr('model.lineChart'), icon: '⌇' },
  { type: 'chart-bar-horizontal', label: tr('model.horizontalBarChart'), icon: '▬' }, { type: 'chart-bar-stack', label: tr('model.stackedBarChart'), icon: '▥' },
  { type: 'chart-area', label: tr('model.areaChart'), icon: '◭' }, { type: 'chart-radar', label: tr('model.radarChart'), icon: '✳' },
  { type: 'chart-funnel', label: tr('model.funnelChart'), icon: '▽' }, { type: 'chart-gauge', label: tr('model.ringGauge'), icon: '◍' },
  { type: 'alarm-banner', label: tr('model.alarmBanner'), icon: '⚠' }, { type: 'alarm-stats', label: tr('model.alarmStatistics'), icon: '▦' },
  { type: 'alarm-list', label: tr('model.alarmList'), icon: '☰' }, { type: 'table', label: tr('model.dataTable'), icon: '▤' }, { type: 'history-table', label: tr('model.historyStats'), icon: '▦' },
  { type: 'oee-kpi', label: tr('model.oEEDashboard'), icon: '◎' },
  { type: 'rectangle', label: tr('model.rectangle'), icon: '▭' }, { type: 'ellipse', label: tr('model.circle'), icon: '◯' }
]
export function emptyDocument(): ScreenDocument { return { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [] } }

// 服务端存的 document 是一段可演进的 JSON。缺 nodes 时若原样塞进画布，
// 模板里第一处 document.nodes.length 就会抛错，设计器整页变成空白且没有任何提示。
// 这里先判可读性：读不了就交给调用方报错，绝不返回一个「空画布」——
// 否则用户会以为画面本来没有元素，一保存就把原内容覆盖掉。
export function readableDocument(value: unknown): ScreenDocument | null {
  if (!value || typeof value !== 'object') return null
  const candidate = value as Partial<ScreenDocument>
  if (!Array.isArray(candidate.nodes)) return null
  const base = emptyDocument()
  return {
    ...base,
    ...candidate,
    schemaVersion: candidate.schemaVersion ?? base.schemaVersion,
    width: candidate.width ?? base.width,
    height: candidate.height ?? base.height,
    background: candidate.background ?? base.background,
    nodes: candidate.nodes
  }
}
const nodeSize: Partial<Record<NodeType, [number, number]>> = { led: [72, 72], progress: [220, 40], kpi: [200, 112], clock: [240, 96], image: [200, 150], marquee: [320, 48], 'chart-bar': [280, 180], 'chart-pie': [240, 240], 'chart-line': [420, 200], 'chart-bar-horizontal': [280, 200], 'chart-bar-stack': [280, 180], 'chart-area': [420, 200], 'chart-radar': [240, 240], 'chart-funnel': [240, 240], 'chart-gauge': [220, 180], 'alarm-banner': [420, 48], 'alarm-stats': [280, 180], 'alarm-list': [380, 200], table: [320, 180], 'history-table': [440, 180], 'oee-kpi': [300, 200], switch: [120, 56], slider: [240, 56], spinner: [160, 56], jog: [120, 56], pulse: [120, 56], selector: [220, 72], knob: [140, 140], reset: [120, 56], silence: [120, 56], recipe: [300, 150] }
export function createNode(type: NodeType, x = 80, y = 80): ScreenNode {
  if (type === 'symbol') return createSymbolNode('isa:pump', 100, 100, tr('model.centrifugalPump'), x, y)
  const box = type === 'polyline' ? [240, 120] : type === 'equipment-panel' ? [320, 300] : type === 'input' ? [220, 84] : nodeSize[type]
  const defaultText = palette.find(p => p.type === type)?.label ?? type
  return { id: crypto.randomUUID(), type, x, y, width: box ? box[0] : type === 'pipe' ? 200 : 140,
    height: box ? box[1] : ['text', 'value', 'button', 'pipe'].includes(type) ? 48 : 112,
    rotation: 0, text: type === 'image' ? '' : defaultText, color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId: null,
    tags: tagTypes.includes(type) ? [] : null,
    points: type === 'polyline' ? [{ x: 0, y: 0 }, { x: 50, y: 0 }, { x: 50, y: 100 }, { x: 100, y: 100 }] : null,
    states: type === 'selector' || type === 'knob' ? [{ value: 0, label: '0', color: '#6b7580' }, { value: 1, label: '1', color: '#34d399' }] : null,
    commands: type === 'equipment-panel' ? [] : null,
    releaseValue: type === 'jog' || type === 'pulse' ? 0 : null,
    pulseMs: type === 'pulse' ? 500 : null,
    maxHoldMs: type === 'jog' ? 5000 : null,
    resetMode: type === 'reset' ? 'alarms' : null,
    silenceMinutes: type === 'silence' ? 5 : null,
    recipeTemplateId: null,
    windowSec: windowTypes.includes(type) ? 300 : null,
    lineId: null, animation: null,
    action: { type: controlTypes.includes(type) ? 'command' : 'none', screenId: null, tagId: null, value: 1 } }
}
export function createSymbolNode(symbol: string, width: number, height: number, text: string, x = 80, y = 80): ScreenNode {
  return { ...createNode('rectangle', x, y), type: 'symbol', symbol, width, height, text, color: '#6b7580', activeColor: '#e9edf1' }
}
export function createTemplateNode(item: LibraryItem, x = 80, y = 80): ScreenNode {
  return { ...createNode('rectangle', x, y), type: 'template', templateKey: item.key, width: item.width, height: item.height, text: item.name,
    bindings: item.placeholders.map(key => ({ key, tagId: null })) }
}
export function pipeGeometry(node: ScreenNode, nodes: ScreenNode[]): ScreenNode {
  if (node.type !== 'polyline') return node
  if (!node.source && !node.target) return node.points ? node : { ...node, points: [{ x: 0, y: 0 }, { x: 100, y: 100 }] }
  let points = (node.points ?? [{ x: 0, y: 0 }, { x: 100, y: 100 }]).map(point => ({ x: node.x + point.x * node.width / 100, y: node.y + point.y * node.height / 100 }))
  const source = portPoint(node.source, nodes), target = portPoint(node.target, nodes)
  if (source && target) {
    const middle = (source.x + target.x) / 2
    points = [source, { x: middle, y: source.y }, { x: middle, y: target.y }, target]
  } else { if (source) points[0] = source; if (target) points[points.length - 1] = target }
  const x = Math.min(...points.map(point => point.x)), y = Math.min(...points.map(point => point.y))
  const width = Math.max(16, Math.max(...points.map(point => point.x)) - x), height = Math.max(16, Math.max(...points.map(point => point.y)) - y)
  return { ...node, x, y, width, height, rotation: 0, points: points.map(point => ({ x: (point.x - x) / width * 100, y: (point.y - y) / height * 100 })) }
}
export function portPoint(connection: ScreenPort | null | undefined, nodes: ScreenNode[]): ScreenPoint | undefined {
  const target = nodes.find(candidate => candidate.id === connection?.nodeId)
  if (!target || !connection) return
  const offsetX = connection.port === 'left' ? -target.width / 2 : connection.port === 'right' ? target.width / 2 : 0
  const offsetY = connection.port === 'top' ? -target.height / 2 : connection.port === 'bottom' ? target.height / 2 : 0
  const angle = target.rotation * Math.PI / 180
  return { x: target.x + target.width / 2 + offsetX * Math.cos(angle) - offsetY * Math.sin(angle), y: target.y + target.height / 2 + offsetX * Math.sin(angle) + offsetY * Math.cos(angle) }
}
export function clampNode(node: ScreenNode, doc: ScreenDocument): ScreenNode {
  const width = Math.min(doc.width, Math.max(16, node.width)), height = Math.min(doc.height, Math.max(16, node.height))
  return { ...node, width, height, x: Math.max(0, Math.min(doc.width - width, node.x)), y: Math.max(0, Math.min(doc.height - height, node.y)) }
}
export function translateNodes(nodes: ScreenNode[], doc: ScreenDocument, dx: number, dy: number): ScreenNode[] {
  if (!nodes.length) return []
  const offsetX = Math.max(-Math.min(...nodes.map(node => node.x)), Math.min(doc.width - Math.max(...nodes.map(node => node.x + node.width)), dx))
  const offsetY = Math.max(-Math.min(...nodes.map(node => node.y)), Math.min(doc.height - Math.max(...nodes.map(node => node.y + node.height)), dy))
  return nodes.map(node => ({ ...node, x: node.x + offsetX, y: node.y + offsetY }))
}
export type ArrangeOperation = 'left' | 'center-x' | 'right' | 'top' | 'center-y' | 'bottom' | 'space-x' | 'space-y' | 'width' | 'height' | 'size'
export function arrangeNodes(nodes: ScreenNode[], doc: ScreenDocument, operation: ArrangeOperation): ScreenNode[] | null {
  if (nodes.length < 2) return nodes
  const anchor = nodes[0]
  if (operation === 'width' || operation === 'height' || operation === 'size')
    return nodes.map(node => clampNode({ ...node, width: operation === 'height' ? node.width : anchor.width,
      height: operation === 'width' ? node.height : anchor.height }, doc))
  const horizontal = ['left', 'center-x', 'right', 'space-x'].includes(operation)
  const position = horizontal ? 'x' : 'y', size = horizontal ? 'width' : 'height'
  if (operation === 'space-x' || operation === 'space-y') {
    if (nodes.length < 3) return nodes
    const ordered = [...nodes].sort((first, second) => first[position] - second[position])
    const start = ordered[0][position], end = Math.max(...nodes.map(node => node[position] + node[size]))
    const gap = (end - start - nodes.reduce((sum, node) => sum + node[size], 0)) / (nodes.length - 1)
    if (gap < 0) return null
    let cursor = start
    const positions = new Map(ordered.map(node => { const at = cursor; cursor += node[size] + gap; return [node.id, at] }))
    return nodes.map(node => ({ ...node, [position]: positions.get(node.id)! }))
  }
  const fraction = operation.startsWith('center') ? .5 : operation === 'right' || operation === 'bottom' ? 1 : 0
  const largest = Math.max(...nodes.map(node => node[size]))
  const target = Math.max(largest * fraction, Math.min(doc[size] - largest * (1 - fraction), anchor[position] + anchor[size] * fraction))
  return nodes.map(node => ({ ...node, [position]: target - node[size] * fraction }))
}
// Snapshots include the title: undo/redo must restore all editable state, not just node positions.
export class EditHistory {
  private past: string[] = []; private future: string[] = []
  private lastKey = ''; private lastAt = 0
  get canUndo(): boolean { return this.past.length > 0 }
  get canRedo(): boolean { return this.future.length > 0 }
  // key 相同的连续编辑在短时间内合并为一步，避免颜色/步进等字段产生大量撤销项。
  push(snapshot: string, key = ''): void {
    const now = Date.now()
    if (key && key === this.lastKey && now - this.lastAt < 500) { this.lastAt = now; this.future = []; return }
    if (this.past.at(-1) !== snapshot) this.past.push(snapshot)
    if (this.past.length > 100) this.past.shift()
    this.future = []; this.lastKey = key; this.lastAt = now
  }
  undo(current: string): string | undefined {
    this.lastKey = ''; this.lastAt = 0
    const previous = this.past.pop(); if (previous !== undefined) this.future.push(current); return previous
  }
  redo(current: string): string | undefined {
    this.lastKey = ''; this.lastAt = 0
    const next = this.future.pop(); if (next !== undefined) this.past.push(current); return next
  }
  clear(): void { this.past = []; this.future = []; this.lastKey = ''; this.lastAt = 0 }
}