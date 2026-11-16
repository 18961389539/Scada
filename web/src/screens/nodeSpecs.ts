// 画面图元注册表：类型全集、调色板顺序、分区特性与 cleanNode 字段分组的唯一来源。
// 该文件不依赖 model.ts，避免与画布模型形成运行时循环引用；labelKey 由调用方翻译。

export type NodeFeature =
  | 'labelText' | 'fontSize' | 'textStyle' | 'shapeStyle' | 'typeSettings' | 'dataBinding'
  | 'control' | 'controlRange' | 'multiTag' | 'window' | 'chart' | 'chartOptions' | 'chartTarget'
  | 'states' | 'positional' | 'animation' | 'points' | 'panel' | 'jog' | 'reset' | 'silence'
  | 'recipe' | 'gauge' | 'table' | 'oee' | 'template' | 'placeholder' | 'symbol'

export interface NodeSpec {
  labelKey: string
  features: readonly NodeFeature[]
  defaultFontSize?: number
  defaultSize?: readonly [number, number]
}

const SPECS = {
  text: { labelKey: 'model.text', features: ['labelText', 'textStyle', 'fontSize'], defaultFontSize: 16 },
  value: { labelKey: 'model.numericValue', features: ['dataBinding', 'textStyle', 'fontSize'], defaultFontSize: 24 },
  pump: { labelKey: 'model.waterPump', features: ['labelText', 'dataBinding', 'typeSettings', 'states', 'animation', 'fontSize'], defaultFontSize: 12 },
  valve: { labelKey: 'model.valves', features: ['labelText', 'dataBinding', 'typeSettings', 'states', 'animation', 'fontSize'], defaultFontSize: 12 },
  tank: { labelKey: 'model.tanks', features: ['labelText', 'dataBinding', 'typeSettings', 'states', 'animation', 'fontSize'], defaultFontSize: 12 },
  pipe: { labelKey: 'model.pipes', features: ['dataBinding', 'typeSettings', 'animation'] },
  rectangle: { labelKey: 'model.rectangle', features: ['labelText', 'textStyle', 'shapeStyle', 'fontSize'], defaultFontSize: 16 },
  ellipse: { labelKey: 'model.circle', features: ['labelText', 'textStyle', 'shapeStyle', 'fontSize'], defaultFontSize: 16 },
  button: { labelKey: 'model.button', features: ['labelText', 'textStyle', 'shapeStyle', 'fontSize'], defaultFontSize: 16 },
  gauge: { labelKey: 'model.instruments', features: ['dataBinding', 'typeSettings', 'gauge', 'fontSize'], defaultFontSize: 15 },
  motor: { labelKey: 'model.motor', features: ['labelText', 'dataBinding', 'typeSettings', 'states', 'fontSize'], defaultFontSize: 12 },
  fan: { labelKey: 'model.fans', features: ['labelText', 'dataBinding', 'typeSettings', 'states', 'fontSize'], defaultFontSize: 12 },
  symbol: { labelKey: 'screenDesignerView.symbol', features: ['labelText', 'dataBinding', 'typeSettings', 'states', 'animation', 'symbol', 'fontSize'], defaultFontSize: 12 },
  template: { labelKey: 'screenDesignerView.templateFaceplate', features: ['template', 'placeholder'] },
  led: { labelKey: 'model.statusLamp', features: ['dataBinding'], defaultSize: [72, 72] },
  progress: { labelKey: 'model.progressBar', features: ['dataBinding', 'fontSize'], defaultFontSize: 14, defaultSize: [220, 40] },
  kpi: { labelKey: 'model.kPICard', features: ['labelText', 'dataBinding', 'fontSize'], defaultFontSize: 28, defaultSize: [200, 112] },
  clock: { labelKey: 'model.clock', features: ['fontSize'], defaultFontSize: 26, defaultSize: [240, 96] },
  image: { labelKey: 'model.image', features: ['typeSettings'], defaultSize: [200, 150] },
  marquee: { labelKey: 'model.marquee', features: ['labelText', 'fontSize'], defaultFontSize: 16, defaultSize: [320, 48] },
  dial: { labelKey: 'model.dialGauge', features: ['dataBinding', 'typeSettings', 'gauge', 'fontSize'], defaultFontSize: 15 },
  'chart-bar': { labelKey: 'model.barChart', features: ['typeSettings', 'multiTag', 'chart', 'chartOptions', 'chartTarget'], defaultSize: [280, 180] },
  'chart-pie': { labelKey: 'model.pieChart', features: ['typeSettings', 'multiTag', 'chart', 'chartOptions'], defaultSize: [240, 240] },
  'chart-line': { labelKey: 'model.lineChart', features: ['typeSettings', 'multiTag', 'window', 'chart', 'chartOptions', 'chartTarget'], defaultSize: [420, 200] },
  'chart-bar-horizontal': { labelKey: 'model.horizontalBarChart', features: ['typeSettings', 'multiTag', 'chart', 'chartOptions', 'chartTarget'], defaultSize: [280, 200] },
  'chart-bar-stack': { labelKey: 'model.stackedBarChart', features: ['typeSettings', 'multiTag', 'chart', 'chartOptions', 'chartTarget'], defaultSize: [280, 180] },
  'chart-area': { labelKey: 'model.areaChart', features: ['typeSettings', 'multiTag', 'window', 'chart', 'chartOptions', 'chartTarget'], defaultSize: [420, 200] },
  'chart-radar': { labelKey: 'model.radarChart', features: ['typeSettings', 'multiTag', 'chart', 'chartOptions'], defaultSize: [240, 240] },
  'chart-funnel': { labelKey: 'model.funnelChart', features: ['typeSettings', 'multiTag', 'chart', 'chartOptions'], defaultSize: [240, 240] },
  'chart-gauge': { labelKey: 'model.ringGauge', features: ['typeSettings', 'chart', 'chartOptions'], defaultSize: [220, 180] },
  'alarm-banner': { labelKey: 'model.alarmBanner', features: ['fontSize'], defaultFontSize: 16, defaultSize: [420, 48] },
  'alarm-stats': { labelKey: 'model.alarmStatistics', features: ['chart'], defaultSize: [280, 180] },
  'alarm-list': { labelKey: 'model.alarmList', features: [], defaultSize: [380, 200] },
  table: { labelKey: 'model.dataTable', features: ['typeSettings', 'multiTag', 'table'], defaultSize: [320, 180] },
  'history-table': { labelKey: 'model.historyStats', features: ['typeSettings', 'multiTag', 'window', 'table'], defaultSize: [440, 180] },
  'oee-kpi': { labelKey: 'model.oEEDashboard', features: ['typeSettings', 'window', 'oee', 'fontSize'], defaultFontSize: 22, defaultSize: [300, 200] },
  switch: { labelKey: 'model.switch', features: ['control', 'fontSize'], defaultFontSize: 11, defaultSize: [120, 56] },
  slider: { labelKey: 'model.slider', features: ['control', 'controlRange', 'fontSize'], defaultFontSize: 12, defaultSize: [240, 56] },
  spinner: { labelKey: 'model.stepper', features: ['control', 'controlRange', 'fontSize'], defaultFontSize: 16, defaultSize: [160, 56] },
  input: { labelKey: 'hmi.input', features: ['labelText', 'control', 'controlRange', 'fontSize'], defaultFontSize: 13, defaultSize: [220, 84] },
  polyline: { labelKey: 'hmi.polyline', features: ['dataBinding', 'typeSettings', 'points', 'animation'], defaultSize: [240, 120] },
  'equipment-panel': { labelKey: 'hmi.panel', features: ['labelText', 'dataBinding', 'typeSettings', 'panel', 'states', 'fontSize'], defaultFontSize: 13, defaultSize: [320, 300] },
  reset: { labelKey: 'hmi.reset', features: ['typeSettings', 'reset', 'fontSize'], defaultFontSize: 14, defaultSize: [120, 56] },
  jog: { labelKey: 'hmi.jog', features: ['control', 'jog', 'fontSize'], defaultFontSize: 13, defaultSize: [120, 56] },
  pulse: { labelKey: 'hmi.pulse', features: ['control', 'jog', 'fontSize'], defaultFontSize: 13, defaultSize: [120, 56] },
  silence: { labelKey: 'hmi.silence', features: ['typeSettings', 'silence', 'fontSize'], defaultFontSize: 14, defaultSize: [120, 56] },
  selector: { labelKey: 'hmi.selector', features: ['control', 'typeSettings', 'states', 'positional', 'fontSize'], defaultFontSize: 12, defaultSize: [220, 72] },
  knob: { labelKey: 'hmi.knob', features: ['control', 'typeSettings', 'states', 'positional', 'fontSize'], defaultFontSize: 12, defaultSize: [140, 140] },
  recipe: { labelKey: 'hmi.recipe', features: ['typeSettings', 'recipe', 'fontSize'], defaultFontSize: 13, defaultSize: [300, 150] }
} as const satisfies Record<string, NodeSpec>

export type NodeType = keyof typeof SPECS
export const NODE_SPECS: { readonly [K in NodeType]: NodeSpec } = SPECS
export const NODE_TYPES = Object.keys(SPECS) as NodeType[]

// 调色板顺序与分组保持旧界面不变；symbol/template 由图库入口创建，不在调色板中。
export const PALETTE_ORDER: readonly NodeType[] = [
  'polyline', 'equipment-panel', 'input', 'text', 'value',
  'pump', 'valve', 'tank', 'pipe', 'motor', 'fan',
  'gauge', 'dial', 'button',
  'switch', 'slider', 'spinner', 'jog', 'pulse', 'selector', 'knob', 'reset', 'silence', 'recipe',
  'led', 'progress', 'kpi', 'clock', 'image', 'marquee',
  'chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal', 'chart-bar-stack', 'chart-area',
  'chart-radar', 'chart-funnel', 'chart-gauge', 'alarm-banner', 'alarm-stats', 'alarm-list',
  'table', 'history-table', 'oee-kpi', 'rectangle', 'ellipse'
]

// 默认字号键顺序即旧 defaultFontSizes 的键顺序，保证派生对象逐字不变。
export const FONT_SIZE_ORDER: readonly NodeType[] = [
  'text', 'value', 'button', 'rectangle', 'ellipse', 'marquee', 'kpi', 'progress', 'clock',
  'gauge', 'dial', 'switch', 'slider', 'spinner', 'input', 'equipment-panel',
  'jog', 'pulse', 'selector', 'knob', 'reset', 'silence', 'recipe',
  'symbol', 'pump', 'valve', 'motor', 'fan', 'tank', 'alarm-banner', 'oee-kpi'
]

export function hasFeature(type: NodeType | null | undefined, feature: NodeFeature): boolean {
  // 画面文档可能来自旧版本或被外部改写；未知类型按“无此能力”处理，不能让属性面板整块报错。
  const spec = type == null ? undefined : NODE_SPECS[type] as NodeSpec | undefined
  return spec?.features.includes(feature) ?? false
}

export function typesWith(feature: NodeFeature): NodeType[] {
  return NODE_TYPES.filter(type => hasFeature(type, feature))
}

// cleanNode 字段分组：保存前删除空扩展字段，保持与旧格式逐字一致。
export type NullableNodeField =
  | 'symbol' | 'alarmTagId' | 'templateKey' | 'bindings' | 'bind' | 'alarmBind' | 'texts' | 'tags'
  | 'tagBinds' | 'animationBind' | 'windowSec' | 'lineId' | 'animation' | 'ownerId' | 'points' | 'states'
  | 'source' | 'target' | 'commands' | 'releaseValue' | 'pulseMs' | 'maxHoldMs' | 'resetMode'
  | 'silenceMinutes' | 'recipeTemplateId' | 'fontSize' | 'align' | 'opacity' | 'borderWidth' | 'radius'
  | 'groupId' | 'gaugeBands' | 'setpoint' | 'tableColumns' | 'chartOptions'
export type EmptyCollectionField = 'bindings' | 'tags' | 'tagBinds' | 'gaugeBands' | 'tableColumns'
export type EmptyObjectField = 'texts' | 'chartOptions'
export type FalseyNodeFlag = 'wrap' | 'autoFit' | 'bold' | 'locked' | 'hidden'

export const NULLABLE_NODE_FIELDS: readonly NullableNodeField[] = [
  'symbol', 'alarmTagId', 'templateKey', 'bindings', 'bind', 'alarmBind', 'texts', 'tags', 'tagBinds',
  'animationBind', 'windowSec', 'lineId', 'animation', 'ownerId', 'points', 'states', 'source', 'target',
  'commands', 'releaseValue', 'pulseMs', 'maxHoldMs', 'resetMode', 'silenceMinutes', 'recipeTemplateId',
  'fontSize', 'align', 'opacity', 'borderWidth', 'radius', 'groupId', 'gaugeBands', 'setpoint',
  'tableColumns', 'chartOptions'
]
export const EMPTY_COLLECTION_FIELDS: readonly EmptyCollectionField[] = ['bindings', 'tags', 'tagBinds', 'gaugeBands', 'tableColumns']
export const EMPTY_OBJECT_FIELDS: readonly EmptyObjectField[] = ['texts', 'chartOptions']
export const FALSEY_NODE_FLAGS: readonly FalseyNodeFlag[] = ['wrap', 'autoFit', 'bold', 'locked', 'hidden']
