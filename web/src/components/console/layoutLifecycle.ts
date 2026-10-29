import type { HmiLayoutDocument, HmiMonitor, HmiRegion } from '../../api/hmiLayouts'
import { tr } from '../../i18n'

export interface LayoutContent { name: string; document: HmiLayoutDocument }
export interface LayoutDifference { path: string; before: string; after: string }
export interface LayoutComparison { differences: LayoutDifference[]; summary: string[] }
const empty = () => tr('layoutLifecycle.absent')
const value = (input: unknown): string => input == null || input === '' ? empty() : String(input)

/** Compare domain fields rather than serialized JSON, retaining useful locations for every change. */
export function layoutDifferences(before: LayoutContent | null, after: LayoutContent, screens: ReadonlyMap<number, string> = new Map()): LayoutDifference[] {
  return layoutComparison(before, after, screens).differences
}

interface Pair<T> { before?: T; after?: T; oldIndex?: number; newIndex?: number }
function matchItems<T>(previous: T[], current: T[], keys: ((item: T) => string)[], positional = false): Pair<T>[] {
  const usedOld = new Set<number>(), matched = new Map<number, number>()
  for (const key of keys) {
    const oldKeys = new Map<string, number[]>()
    previous.forEach((item, index) => {
      const identity = key(item)
      if (!usedOld.has(index) && identity) oldKeys.set(identity, [...(oldKeys.get(identity) ?? []), index])
    })
    current.forEach((item, index) => {
      if (matched.has(index)) return
      const oldIndex = oldKeys.get(key(item))?.shift()
      if (oldIndex != null) { matched.set(index, oldIndex); usedOld.add(oldIndex) }
    })
  }
  if (positional) current.forEach((_, index) => { if (!matched.has(index) && previous[index] && !usedOld.has(index)) { matched.set(index, index); usedOld.add(index) } })
  return [
    ...current.map((item, newIndex) => { const oldIndex = matched.get(newIndex); return { before: oldIndex == null ? undefined : previous[oldIndex], after: item, oldIndex, newIndex } }),
    ...previous.flatMap((item, oldIndex) => usedOld.has(oldIndex) ? [] : [{ before: item, oldIndex }])
  ]
}

export function layoutComparison(before: LayoutContent | null, after: LayoutContent, screens: ReadonlyMap<number, string> = new Map()): LayoutComparison {
  const changes: LayoutDifference[] = []
  const counts = new Map<string, number>()
  const count = (kind: string) => counts.set(kind, (counts.get(kind) ?? 0) + 1)
  const add = (path: string, previous: unknown, current: unknown, format = value): boolean => {
    if ((previous ?? '') === (current ?? '')) return false
    changes.push({ path, before: format(previous), after: format(current) }); return true
  }
  const kindValue = (input: unknown) => input == null ? empty() : tr(({ screen: 'layoutsView.k8a1840', alarmBanner: 'layoutsView.k77911d', alarmList: 'layoutsView.k459887', navigation: 'layoutsView.k6be0ed' } as Record<string, string>)[String(input)] ?? String(input))
  const screenValue = (input: unknown) => input == null ? empty() : screens.has(Number(input)) ? `${screens.get(Number(input))} (#${input})` : `#${input}`
  if (add(tr('layoutLifecycle.fieldName'), before?.name, after.name)) count('layoutName')
  const previousMonitors = before?.document.monitors ?? []
  const monitorKeys = [
    (monitor: HmiMonitor) => previousMonitors.filter(item => item.name === monitor.name).length === 1 && after.document.monitors.filter(item => item.name === monitor.name).length === 1 ? monitor.name : '',
    (monitor: HmiMonitor) => JSON.stringify([monitor.columns, monitor.rows, monitor.previewWidth, monitor.previewHeight, monitor.regions])
  ]
  for (const pair of matchItems(previousMonitors, after.document.monitors, monitorKeys, true)) {
    const previous = pair.before, current = pair.after
    const index = pair.newIndex ?? pair.oldIndex ?? 0
    const location = `${tr('layoutLifecycle.monitorLocation', [index + 1])}${(current?.name || previous?.name) ? ` · ${current?.name || previous?.name}` : ''}`
    if (!previous) count('monitorAdded')
    else if (!current) count('monitorRemoved')
    else if (pair.oldIndex !== pair.newIndex) {
      add(`${location} · ${tr('layoutCatalog.monitorOrder')}`, pair.oldIndex! + 1, pair.newIndex! + 1); count('monitorOrder')
    }
    let monitorChanged = false
    for (const key of ['name', 'columns', 'rows', 'previewWidth', 'previewHeight'] as const) monitorChanged = add(`${location} · ${tr(`layoutLifecycle.field_${key}`)}`, previous?.[key], current?.[key]) || monitorChanged
    if (previous && current && monitorChanged) count('monitorChanged')
    const signature = (region: HmiRegion) => JSON.stringify([region.kind, region.title ?? '', region.screenId ?? null, region.row, region.col, region.rowSpan, region.colSpan])
    const regionPairs = matchItems(previous?.regions ?? [], current?.regions ?? [], [region => JSON.stringify([region.id]), signature])
    for (const regionPair of regionPairs) {
      const oldRegion = regionPair.before, newRegion = regionPair.after
      const id = newRegion?.id ?? oldRegion?.id ?? ''
      const source = newRegion ? current!.regions : previous!.regions
      const ordinal = (newRegion ? regionPair.newIndex : regionPair.oldIndex) ?? 0
      const occurrence = source.slice(0, ordinal).filter(region => region.id === id).length
      const regionLocation = `${location} · ${tr('layoutLifecycle.regionLocation', [`${id || '?'}${occurrence ? ` (${occurrence + 1})` : ''}`])}`
      if (!oldRegion) count('regionAdded')
      else if (!newRegion) count('regionRemoved')
      else {
        if (oldRegion.id !== newRegion.id) count('regionRenamed')
        if (oldRegion.screenId !== newRegion.screenId && (oldRegion.screenId ?? null) !== (newRegion.screenId ?? null)) count('binding')
        if (oldRegion.kind !== newRegion.kind) count('regionType')
        if ((oldRegion.title ?? '') !== (newRegion.title ?? '')) count('regionTitle')
        if (['row', 'col', 'rowSpan', 'colSpan'].some(key => oldRegion[key as keyof HmiRegion] !== newRegion[key as keyof HmiRegion])) count('geometry')
      }
      for (const key of ['id', 'kind', 'title', 'screenId', 'row', 'col', 'rowSpan', 'colSpan'] as const) {
        add(`${regionLocation} · ${tr(`layoutLifecycle.field_${key}`)}`, oldRegion?.[key], newRegion?.[key], key === 'kind' ? kindValue : key === 'screenId' ? screenValue : value)
      }
    }
  }
  return { differences: changes, summary: [...counts].map(([kind, total]) => tr(`layoutCatalog.change_${kind}`, [total])) }
}

const kinds = new Set(['screen', 'alarmBanner', 'alarmList', 'navigation'])
const positive = (n: number) => Number.isInteger(n) && n > 0
const overlaps = (a: HmiRegion, b: HmiRegion) => a.row < b.row + b.rowSpan && b.row < a.row + a.rowSpan && a.col < b.col + b.colSpan && b.col < a.col + a.colSpan

/** Publish checks are repeated against the saved snapshot, never the editor's unsaved state. */
export function layoutPublishIssues(layout: LayoutContent, publishedScreens: ReadonlyMap<number, string>): string[] {
  const issues: string[] = []
  if (!layout.name.trim() || layout.name.length > 100) issues.push(tr('layoutLifecycle.nameRule'))
  if (!layout.document.monitors.length || layout.document.monitors.length > 8) issues.push(tr('layoutImprovements.monitorLimit'))
  layout.document.monitors.forEach((monitor, monitorIndex) => {
    const add = (message: string) => issues.push(tr('layoutsView.monitorIssue', [monitor.name || monitorIndex + 1, message]))
    if ((monitor.name?.length ?? 0) > 40) add(tr('layoutImprovements.monitorNameLimit'))
    if (![monitor.columns, monitor.rows].every(n => Number.isInteger(n) && n >= 1 && n <= 24)) add(tr('layoutImprovements.gridLimit'))
    if ((monitor.previewWidth != null || monitor.previewHeight != null) && (!Number.isInteger(monitor.previewWidth) || !Number.isInteger(monitor.previewHeight) || monitor.previewWidth! < 320 || monitor.previewWidth! > 7680 || monitor.previewHeight! < 240 || monitor.previewHeight! > 4320)) add(tr('layoutPreview.sizeRule'))
    if (!monitor.regions.length || monitor.regions.length > 16) add(tr('layoutImprovements.regionLimit'))
    const ids = new Set<string>()
    monitor.regions.forEach((region, index) => {
      if (!region.id?.trim() || region.id.length > 40 || ids.has(region.id)) add(tr('layoutImprovements.regionIdRule', [region.id || '?']))
      ids.add(region.id)
      if (!kinds.has(region.kind)) add(tr('layoutImprovements.invalidKind', [region.id]))
      if (![region.row, region.col, region.rowSpan, region.colSpan].every(positive) || region.row + region.rowSpan - 1 > monitor.rows || region.col + region.colSpan - 1 > monitor.columns) add(tr('layoutsView.regionOut', [region.id, monitor.columns, monitor.rows]))
      if (region.kind === 'screen' && !publishedScreens.has(region.screenId ?? -1)) add(tr('layoutsView.regionScreen', [region.id]))
      if ((region.title?.length ?? 0) > 60) add(tr('layoutImprovements.titleLimit', [region.id]))
      const previous = monitor.regions.findIndex((other, otherIndex) => otherIndex < index && overlaps(other, region))
      if (previous >= 0) add(tr('layoutsView.regionOverlap', [region.id, monitor.regions[previous].id]))
    })
  })
  return issues
}

export function layoutScreenReferences(layout: LayoutContent, screens: ReadonlyMap<number, string>): { location: string; screen: string; valid: boolean }[] {
  return layout.document.monitors.flatMap((monitor, index) => monitor.regions.filter(region => region.kind === 'screen').map(region => ({
    location: `${monitor.name || tr('layoutLifecycle.monitorLocation', [index + 1])} · ${region.id || '?'}`,
    screen: screens.get(region.screenId ?? -1) || tr('layoutImprovements.unavailableScreen', [region.screenId ?? '—']),
    valid: screens.has(region.screenId ?? -1)
  })))
}
