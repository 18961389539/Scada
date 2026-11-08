/** 将当前路由映射到“本页说明”内容键；只返回确定有文案的页面。 */
const EXACT: Record<string, string> = {
  '/overview': 'overview',
  '/realtime': 'realtime',
  '/trends': 'trends',
  '/alarms': 'alarms',
  '/alarm-analytics': 'alarmAnalytics',
  '/oee': 'oee',
  '/production/events': 'productionEvents',
  '/assets': 'assets',
  '/spc': 'spc',
  '/quality': 'quality',
  '/reports': 'measurementReports',
  '/report-center': 'reportCenter',
  '/calc-jobs': 'calcJobs',
  '/recipes': 'recipes',
  '/traceability': 'traceability',
  '/control': 'control',
  '/screens': 'screens',
  '/console': 'console',
  '/ops': 'ops',
  '/license': 'license',
  '/system': 'system',
  '/config/devices': 'devices',
  '/config/gateways': 'gateways',
  '/config/hmi-library': 'hmiLibrary',
  '/config/layouts': 'layouts',
  '/config/layout-preview': 'console',
  '/config/object-templates': 'objectTemplates',
  '/config/tags': 'tags',
  '/config/project': 'project',
  '/config/calculations': 'calculations',
  '/config/oee': 'oeeConfig',
  '/config/rules': 'alarmRules',
  '/config/notifications': 'notifications',
  '/config/annunciators': 'annunciators',
  '/config/external-api': 'externalApi'
}

const PATTERNS: [RegExp, string][] = [
  [/^\/console\/[^/]+$/, 'console'],
  [/^\/config\/object-templates\/[^/]+$/, 'objectTemplateEditor'],
  [/^\/config\/screens\/[^/]+$/, 'screenDesigner'],
  [/^\/screens\/[^/]+$/, 'screenRun']
]

export function helpKeyForPath(path: string): string | null {
  const normalized = path.replace(/\/+$/, '') || '/'
  if (normalized in EXACT) return EXACT[normalized]
  for (const [pattern, key] of PATTERNS) if (pattern.test(normalized)) return key
  return null
}

/**
 * 反向映射：帮助内容键 → 页面路径，供跨页帮助搜索跳转。
 * 设计器、画面运行页、模板编辑器只有带 id 的路径，没有固定落点，返回 null 不参与跳转。
 */
export function helpPathForKey(key: string): string | null {
  for (const [path, value] of Object.entries(EXACT)) if (value === key) return path
  return null
}
