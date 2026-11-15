import { ref } from 'vue'
import { tagApi } from '../api'
import type { Tag } from '../types'

const PAGE_SIZE = 50

export interface AlarmRuleTagPickerOptions {
  resolveTag: (id?: number | null) => Promise<Tag | undefined>
  ensureDevices: () => Promise<void>
  /** 搜索到的新测点同步给页面缓存，供表格名称显示复用。 */
  onLoaded?: (tags: Tag[]) => void
  accept?: () => ((tag: Tag) => boolean) | undefined
  pinnedId?: () => number | undefined
  forcePin?: () => boolean
}

// 报警规则的多处测点选择器（筛选、规则、抑制、四限）共用同一套分页、版本与置顶逻辑。
export function useAlarmRuleTagPicker(options: AlarmRuleTagPickerOptions) {
  const items = ref<Tag[]>([])
  const loading = ref(false)
  let version = 0

  async function search(query: string): Promise<void> {
    const current = ++version
    loading.value = true
    try {
      await options.ensureDevices().catch(() => undefined)
      const page = await tagApi.page({ search: query.trim() || undefined, skip: 0, take: PAGE_SIZE })
      if (current !== version) return
      options.onLoaded?.(page.items)
      const pinned = await options.resolveTag(options.pinnedId?.())
      if (current !== version) return
      items.value = withPinnedTags(page.items, pinned, options.accept?.(), options.forcePin?.() ?? false)
    } catch {
      if (current !== version) return
      const pinned = await options.resolveTag(options.pinnedId?.())
      if (current !== version) return
      if (pinned) items.value = withPinnedTags(items.value, pinned, options.accept?.(), options.forcePin?.() ?? false)
    } finally {
      if (current === version) loading.value = false
    }
  }

  function open(visible: boolean): void {
    if (visible) void search('')
  }

  function reset(tags: Tag[] = []): void {
    version++
    items.value = tags
    loading.value = false
  }

  return { items, loading, search, open, reset }
}

export function withPinnedTags(items: Tag[], pinned: Tag | undefined, allow?: (tag: Tag) => boolean, force = false): Tag[] {
  const list = allow ? items.filter(allow) : items.slice()
  if (pinned && (force || !allow || allow(pinned)) && !list.some(tag => tag.id === pinned.id)) list.unshift(pinned)
  return list
}
