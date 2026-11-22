/** 测点分页后，批量操作只作用于本页勾选；导出接口目前不能带筛选。 */

export type TagCatalogQuery = {
  deviceId?: number
  search?: string
  quality?: string
  deviceIds?: number[]
}

export function tagQueryHasFilters(query: TagCatalogQuery): boolean {
  if (query.deviceId) return true
  if (query.search?.trim()) return true
  if (query.quality) return true
  if (query.deviceIds?.length) return true
  return false
}

/** 导出接口会拉全部测点时，只要当前有筛选就不能默默导出。无筛选时也要先说明范围。 */
export function tagExportNeedsDisclosure(query: TagCatalogQuery): boolean {
  return true
}

export function tagExportIsUnfiltered(query: TagCatalogQuery): boolean {
  return !tagQueryHasFilters(query)
}
