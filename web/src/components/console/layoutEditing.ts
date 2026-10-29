import type { HmiMonitor, HmiRegion } from '../../api/hmiLayouts'

export type RegionGeometry = Pick<HmiRegion, 'row' | 'col' | 'rowSpan' | 'colSpan'>
export type RegionAlignment = 'left' | 'center' | 'right' | 'top' | 'middle' | 'bottom'
const clamp = (value: number, min: number, max: number) => Math.max(min, Math.min(max, value))
const validGrid = (columns: number, rows: number) => [columns, rows].every(value => Number.isInteger(value) && value >= 1 && value <= 24)
const overlaps = (a: RegionGeometry, b: RegionGeometry) => a.col < b.col + b.colSpan && b.col < a.col + a.colSpan && a.row < b.row + b.rowSpan && b.row < a.row + a.rowSpan

export function geometryChanged(regions: RegionGeometry[], next: RegionGeometry[]): boolean {
  return regions.length === next.length && regions.some((region, index) => (['row', 'col', 'rowSpan', 'colSpan'] as const).some(key => region[key] !== next[index][key]))
}

export function alignRegion(monitor: HmiMonitor, index: number, alignment: RegionAlignment): RegionGeometry | null {
  const region = monitor.regions[index]
  if (!region || !validGrid(monitor.columns, monitor.rows) || ![region.colSpan, region.rowSpan].every(value => Number.isInteger(value) && value > 0)
    || region.colSpan > monitor.columns || region.rowSpan > monitor.rows) return null
  const result: RegionGeometry = { row: region.row, col: region.col, rowSpan: region.rowSpan, colSpan: region.colSpan }
  if (alignment === 'left') result.col = 1
  if (alignment === 'center') result.col = Math.floor((monitor.columns - region.colSpan) / 2) + 1
  if (alignment === 'right') result.col = monitor.columns - region.colSpan + 1
  if (alignment === 'top') result.row = 1
  if (alignment === 'middle') result.row = Math.floor((monitor.rows - region.rowSpan) / 2) + 1
  if (alignment === 'bottom') result.row = monitor.rows - region.rowSpan + 1
  if (![result.row, result.col].every(value => Number.isInteger(value) && value > 0) || result.row + result.rowSpan - 1 > monitor.rows || result.col + result.colSpan - 1 > monitor.columns
    || monitor.regions.some((other, otherIndex) => otherIndex !== index && overlaps(result, other))) return null
  return result
}

// Partition all regions in list order. Remainder cells go to the first regions.
export function divideRegions(monitor: HmiMonitor, axis: 'columns' | 'rows'): RegionGeometry[] | null {
  const count = monitor.regions.length, cells = monitor[axis]
  if (!validGrid(monitor.columns, monitor.rows) || !count || count > cells) return null
  let start = 1
  return monitor.regions.map((_, index) => {
    const span = Math.floor(cells / count) + (index < cells % count ? 1 : 0)
    const geometry = axis === 'columns' ? { row: 1, col: start, rowSpan: monitor.rows, colSpan: span } : { row: start, col: 1, rowSpan: span, colSpan: monitor.columns }
    start += span
    return geometry
  })
}

// Scale grid boundaries together, then relocate collisions into free rectangles.
// Return no result if preserving every region's adapted size is impossible.
export function fitRegionsToGrid(monitor: HmiMonitor, columns = monitor.columns, rows = monitor.rows): RegionGeometry[] | null {
  if (!validGrid(columns, rows) || monitor.regions.length > columns * rows
    || monitor.regions.some(region => ![region.row, region.col, region.rowSpan, region.colSpan].every(Number.isInteger))) return null
  const sourceColumns = Math.max(1, monitor.columns, ...monitor.regions.map(region => Math.max(1, region.col) - 1 + Math.max(1, region.colSpan)))
  const sourceRows = Math.max(1, monitor.rows, ...monitor.regions.map(region => Math.max(1, region.row) - 1 + Math.max(1, region.rowSpan)))
  const result = monitor.regions.map(region => {
    const left = clamp(Math.floor((Math.max(1, region.col) - 1) * columns / sourceColumns), 0, columns - 1)
    const top = clamp(Math.floor((Math.max(1, region.row) - 1) * rows / sourceRows), 0, rows - 1)
    const right = clamp(Math.floor((Math.max(1, region.col) - 1 + Math.max(1, region.colSpan)) * columns / sourceColumns), left + 1, columns)
    const bottom = clamp(Math.floor((Math.max(1, region.row) - 1 + Math.max(1, region.rowSpan)) * rows / sourceRows), top + 1, rows)
    return { row: top + 1, col: left + 1, rowSpan: bottom - top, colSpan: right - left }
  })
  const placed: HmiRegion[] = []
  // Place larger regions first; keep the original list order in the result.
  const order = result.map((geometry, index) => ({ index, area: geometry.rowSpan * geometry.colSpan })).sort((a, b) => b.area - a.area || a.index - b.index)
  for (const { index } of order) {
    const geometry = result[index]
    if (placed.some(other => overlaps(geometry, other))) {
      const space = findRegionSpace({ ...monitor, columns, rows, regions: placed }, geometry)
      if (!space) return null
      Object.assign(geometry, space)
    }
    placed.push({ ...monitor.regions[index], ...geometry })
  }
  return result
}

// Copies keep their size and use the first unoccupied rectangle in the target grid.
export function findRegionSpace(monitor: HmiMonitor, size: Pick<HmiRegion, 'rowSpan' | 'colSpan'>): { row: number; col: number } | null {
  if (![monitor.rows, monitor.columns, size.rowSpan, size.colSpan].every(value => Number.isInteger(value) && value > 0)
    || monitor.rows > 24 || monitor.columns > 24) return null
  for (let row = 1; row <= monitor.rows - size.rowSpan + 1; row++) {
    for (let col = 1; col <= monitor.columns - size.colSpan + 1; col++) {
      if (!monitor.regions.some(region => row < region.row + region.rowSpan && region.row < row + size.rowSpan
        && col < region.col + region.colSpan && region.col < col + size.colSpan)) return { row, col }
    }
  }
  return null
}

export function copiedRegionId(source: string, monitor: HmiMonitor): string {
  for (let index = 1; ; index++) {
    const suffix = `_copy${index === 1 ? '' : index}`
    const id = (source.trim() || 'region').slice(0, 40 - suffix.length) + suffix
    if (!monitor.regions.some(region => region.id === id)) return id
  }
}
