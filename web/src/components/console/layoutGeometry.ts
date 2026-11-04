import type { HmiMonitor, HmiRegion } from '../../api/hmiLayouts'

function onRow(regions: HmiRegion[], row: number): HmiRegion[] {
  return regions.filter(region => region.row <= row && region.row + region.rowSpan > row)
}

/** Rows whose every occupant is a legacy alarm banner. Those rows are not part of the runtime grid. */
export function bannerOnlyRows(monitor: HmiMonitor): number[] {
  const rows: number[] = []
  for (let row = 1; row <= monitor.rows; row++) {
    const occupied = onRow(monitor.regions, row)
    if (occupied.length && occupied.every(region => region.kind === 'alarmBanner')) rows.push(row)
  }
  return rows
}

/** A banner is allowed only while it sits in a leading run of banner-only rows. */
export function misplacedAlarmBanners(monitor: HmiMonitor): boolean {
  let leading = 0
  while (leading < monitor.rows) {
    const occupied = onRow(monitor.regions, leading + 1)
    if (!occupied.length || occupied.some(region => region.kind !== 'alarmBanner')) break
    leading++
  }
  return monitor.regions.some(region => region.kind === 'alarmBanner' && region.row + region.rowSpan - 1 > leading)
}

// Legacy banners render in the protected header. Drop every banner-only row, including
// ones left in the middle, so the runtime grid does not keep an empty band.
export function contentMonitor(monitor: HmiMonitor): HmiMonitor {
  const removed = new Set(bannerOnlyRows(monitor))
  const shift = (row: number) => [...removed].filter(item => item < row).length
  return {
    ...monitor,
    rows: Math.max(1, monitor.rows - removed.size),
    regions: monitor.regions.filter(region => region.kind !== 'alarmBanner').map(region => {
      let span = 0
      for (let row = region.row; row < region.row + region.rowSpan; row++) if (!removed.has(row)) span++
      return { ...region, row: region.row - shift(region.row), rowSpan: Math.max(1, span) }
    })
  }
}

export function previewSize(monitor: HmiMonitor): { width: number; height: number } {
  return { width: monitor.previewWidth ?? 1920, height: monitor.previewHeight ?? 1080 }
}
