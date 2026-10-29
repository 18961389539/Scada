import type { HmiLayoutDocument, HmiMonitor, HmiRegion } from '../../api/hmiLayouts'
import { tr } from '../../i18n'

export const layoutTemplateKeys = ['dual', 'single', 'navigation', 'alarms', 'split'] as const
export type LayoutTemplateKey = typeof layoutTemplateKeys[number]
export function createLayoutTemplate(key: LayoutTemplateKey, screenId: number | null): HmiLayoutDocument {
  const screen = (id: string, row: number, col: number, rowSpan: number, colSpan: number): HmiRegion => ({ id, kind: 'screen', screenId, row, col, rowSpan, colSpan })
  const monitor = (name: string, regions: HmiRegion[], rows = 12): HmiMonitor => ({ name, columns: 12, rows, regions })
  const navigation = (rows: number): HmiRegion => ({ id: 'nav', kind: 'navigation', row: 1, col: 1, rowSpan: rows, colSpan: 2 })
  const alarms = (row: number, rowSpan: number): HmiRegion => ({ id: 'alarms', kind: 'alarmList', row, col: 1, rowSpan, colSpan: 12 })
  const overview = tr('layoutsView.kc122e1')
  switch (key) {
    case 'single': return { monitors: [monitor(overview, [screen('main', 1, 1, 12, 12)])] }
    case 'navigation': return { monitors: [monitor(overview, [navigation(12), screen('main', 1, 3, 12, 10)])] }
    case 'alarms': return { monitors: [monitor(overview, [screen('main', 1, 1, 8, 12), alarms(9, 4)])] }
    case 'split': return { monitors: [monitor(overview, [screen('left', 1, 1, 12, 6), screen('right', 1, 7, 12, 6)])] }
    default: return { monitors: [monitor(overview, [navigation(11), screen('main', 1, 3, 11, 10)], 11), monitor(tr('layoutsView.k4f55ee'), [screen('detail', 1, 1, 8, 12), alarms(9, 4)])] }
  }
}
