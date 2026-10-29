import type { HmiMonitor } from '../../api/hmiLayouts'

// Legacy layouts reserve grid rows for a banner now rendered in the protected header.
// Keep this projection identical in runtime, editing and historical previews.
export function contentMonitor(monitor: HmiMonitor): HmiMonitor {
  let leadingRows = 0
  while (leadingRows < monitor.rows - 1) {
    const row = leadingRows + 1
    const occupied = monitor.regions.filter(region => region.row <= row && region.row + region.rowSpan > row)
    if (!occupied.length || occupied.some(region => region.kind !== 'alarmBanner')) break
    leadingRows++
  }
  return {
    ...monitor,
    rows: monitor.rows - leadingRows,
    regions: monitor.regions.filter(region => region.kind !== 'alarmBanner').map(region => ({ ...region, row: region.row - leadingRows }))
  }
}

export function previewSize(monitor: HmiMonitor): { width: number; height: number } {
  return { width: monitor.previewWidth ?? 1920, height: monitor.previewHeight ?? 1080 }
}
