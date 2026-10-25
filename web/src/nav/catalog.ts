import type { Component } from 'vue'
import {
  Aim,
  Bell,
  BellFilled,
  Connection,
  CopyDocument,
  Cpu,
  DataAnalysis,
  DataLine,
  Document,
  Files,
  FolderOpened,
  Grid,
  Link,
  Message,
  Monitor,
  Odometer,
  Operation,
  Picture,
  PieChart,
  Setting,
  Tickets,
  Timer,
  Tools,
  TrendCharts,
  Warning
} from '@element-plus/icons-vue'
import type { Permission, UserAccess } from '../types'
import { hasAny, hasGlobal } from '../utils'

/** 侧栏 / 命令面板 / 总览快捷入口共用的导航目录。 */
export type NavRequire =
  | { mode: 'any'; permission: Permission }
  | { mode: 'global'; permission: Permission }
  | { mode: 'anyGlobal'; permissions: Permission[] }

export interface NavItemDef {
  id: string
  path: string
  /** i18n key，如 navMenu.kc122e1 */
  titleKey: string
  /** i18n key：空格分隔的中英别名，供模糊搜索 */
  keywordsKey: string
  icon: Component
  /** ops = 顶层菜单；config = 「组态配置」子项 */
  group: 'ops' | 'config'
  require?: NavRequire
  /** 总览页快捷入口 */
  quick?: boolean
}

type AccessHolder = { role?: string; access?: UserAccess } | null | undefined

export const CONFIG_GROUP_KEY = 'navMenu.k208160'

export const NAV_ITEMS: NavItemDef[] = [
  { id: 'overview', path: '/overview', titleKey: 'navMenu.kc122e1', keywordsKey: 'nav.kw.overview', icon: Odometer, group: 'ops', quick: true },
  { id: 'realtime', path: '/realtime', titleKey: 'navMenu.k445294', keywordsKey: 'nav.kw.realtime', icon: DataLine, group: 'ops', quick: true },
  { id: 'screens', path: '/screens', titleKey: 'navMenu.k8a1840', keywordsKey: 'nav.kw.screens', icon: Monitor, group: 'ops', quick: true },
  { id: 'console', path: '/console', titleKey: 'navMenu.k0dd591', keywordsKey: 'nav.kw.console', icon: Grid, group: 'ops' },
  { id: 'control', path: '/control', titleKey: 'navMenu.k4eb87f', keywordsKey: 'nav.kw.control', icon: Operation, group: 'ops', require: { mode: 'any', permission: 'Control' }, quick: true },
  { id: 'recipes', path: '/recipes', titleKey: 'navMenu.k4b435b', keywordsKey: 'nav.kw.recipes', icon: Tickets, group: 'ops', require: { mode: 'any', permission: 'View' } },
  { id: 'trends', path: '/trends', titleKey: 'navMenu.k402be4', keywordsKey: 'nav.kw.trends', icon: TrendCharts, group: 'ops' },
  { id: 'alarms', path: '/alarms', titleKey: 'navMenu.k9f45b3', keywordsKey: 'nav.kw.alarms', icon: Bell, group: 'ops', quick: true },
  { id: 'alarm-analytics', path: '/alarm-analytics', titleKey: 'navMenu.kfce701', keywordsKey: 'nav.kw.alarmAnalytics', icon: PieChart, group: 'ops' },
  { id: 'oee', path: '/oee', titleKey: 'navMenu.oee', keywordsKey: 'nav.kw.oee', icon: DataAnalysis, group: 'ops' },
  { id: 'reports', path: '/reports', titleKey: 'navMenu.k2bdc15', keywordsKey: 'nav.kw.reports', icon: Document, group: 'ops' },
  { id: 'report-center', path: '/report-center', titleKey: 'navMenu.kb41505', keywordsKey: 'nav.kw.reportCenter', icon: Files, group: 'ops' },
  { id: 'calc-jobs', path: '/calc-jobs', titleKey: 'navMenu.k8efdcf', keywordsKey: 'nav.kw.calcJobs', icon: Timer, group: 'ops' },

  { id: 'devices', path: '/config/devices', titleKey: 'navMenu.k845e2b', keywordsKey: 'nav.kw.devices', icon: Setting, group: 'config', require: { mode: 'any', permission: 'Configure' }, quick: true },
  { id: 'gateways', path: '/config/gateways', titleKey: 'navMenu.k626545', keywordsKey: 'nav.kw.gateways', icon: Connection, group: 'config', require: { mode: 'global', permission: 'Configure' } },
  { id: 'hmi-library', path: '/config/hmi-library', titleKey: 'navMenu.k5e9dd6', keywordsKey: 'nav.kw.hmiLibrary', icon: Picture, group: 'config', require: { mode: 'global', permission: 'Configure' } },
  { id: 'layouts', path: '/config/layouts', titleKey: 'navMenu.kbdee6f', keywordsKey: 'nav.kw.layouts', icon: Grid, group: 'config', require: { mode: 'global', permission: 'Configure' } },
  { id: 'object-templates', path: '/config/object-templates', titleKey: 'navMenu.k5c3e7d', keywordsKey: 'nav.kw.objectTemplates', icon: CopyDocument, group: 'config', require: { mode: 'global', permission: 'Configure' } },
  { id: 'tags', path: '/config/tags', titleKey: 'navMenu.kb5c4a3', keywordsKey: 'nav.kw.tags', icon: Aim, group: 'config', require: { mode: 'any', permission: 'Configure' }, quick: true },
  { id: 'project', path: '/config/project', titleKey: 'navMenu.k4d7ee6', keywordsKey: 'nav.kw.project', icon: FolderOpened, group: 'config', require: { mode: 'any', permission: 'Configure' } },
  { id: 'calculations', path: '/config/calculations', titleKey: 'navMenu.k4e0387', keywordsKey: 'nav.kw.calculations', icon: Cpu, group: 'config', require: { mode: 'any', permission: 'Configure' } },
  { id: 'rules', path: '/config/rules', titleKey: 'navMenu.k1c7490', keywordsKey: 'nav.kw.rules', icon: Warning, group: 'config', require: { mode: 'any', permission: 'Configure' } },
  { id: 'notifications', path: '/config/notifications', titleKey: 'navMenu.k0ca38c', keywordsKey: 'nav.kw.notifications', icon: Message, group: 'config', require: { mode: 'any', permission: 'Configure' } },
  { id: 'annunciators', path: '/config/annunciators', titleKey: 'navMenu.annunciator', keywordsKey: 'nav.kw.annunciators', icon: BellFilled, group: 'config', require: { mode: 'any', permission: 'Configure' } },
  { id: 'external-api', path: '/config/external-api', titleKey: 'navMenu.externalApi', keywordsKey: 'nav.kw.externalApi', icon: Link, group: 'config', require: { mode: 'global', permission: 'System' } },

  { id: 'ops', path: '/ops', titleKey: 'navMenu.k74fa83', keywordsKey: 'nav.kw.ops', icon: Connection, group: 'ops', require: { mode: 'global', permission: 'System' } },
  {
    id: 'system',
    path: '/system',
    titleKey: 'navMenu.k04ca1c',
    keywordsKey: 'nav.kw.system',
    icon: Tools,
    group: 'ops',
    require: { mode: 'anyGlobal', permissions: ['ManageUsers', 'System', 'Configure'] }
  }
]

export function isNavVisible(item: NavItemDef, user: AccessHolder): boolean {
  const req = item.require
  if (!req) return true
  if (req.mode === 'any') return hasAny(user, req.permission)
  if (req.mode === 'global') return hasGlobal(user, req.permission)
  return req.permissions.some((p) => hasGlobal(user, p))
}

export function visibleNavItems(user: AccessHolder): NavItemDef[] {
  return NAV_ITEMS.filter((item) => isNavVisible(item, user))
}

/** 简易模糊：连续子序列或空格分词全包含（大小写不敏感）。 */
export function fuzzyMatch(haystack: string, needle: string): boolean {
  const h = haystack.toLowerCase()
  const n = needle.trim().toLowerCase()
  if (!n) return true
  if (h.includes(n)) return true
  const parts = n.split(/\s+/).filter(Boolean)
  if (parts.length > 1 && parts.every((p) => h.includes(p))) return true
  // 子序列：alarm → a...l...a...r...m
  let i = 0
  for (const ch of h) {
    if (ch === n[i]) i++
    if (i >= n.length) return true
  }
  return false
}

export function navSearchText(item: NavItemDef, translate: (key: string) => string): string {
  const title = translate(item.titleKey)
  const keywords = translate(item.keywordsKey)
  const parent = item.group === 'config' ? translate(CONFIG_GROUP_KEY) : ''
  return `${title} ${keywords} ${parent} ${item.path}`
}

export function findNavByPath(path: string): NavItemDef | undefined {
  const bare = path.split('?')[0].replace(/\/$/, '') || '/'
  return NAV_ITEMS.find((item) => item.path === bare)
}
