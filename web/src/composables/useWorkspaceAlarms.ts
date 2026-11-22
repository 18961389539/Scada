import { reactive, ref, watch, type Ref } from 'vue'
import { alarmApi } from '../api'
import { screenApi } from '../api/screens'
import type { ScreenDocument } from '../screens/model'
import type { AlarmEvent } from '../types'
import { usePolling } from './usePolling'

/** 只需要这两个字段；完整的 WorkspaceTab 留在视图里，避免 composable 绑死 UI 状态。 */
export interface WorkspaceTabRef {
  key: number
  screenId: number
}

export type WorkspaceAlarmIncomplete = 'capped' | 'fetch-failed' | 'prefetch-failed'

export interface WorkspaceTabAlarmSummary {
  count: number
  incomplete: boolean
  reason: WorkspaceAlarmIncomplete | null
  alarms: AlarmEvent[]
}

const UNACK_LIMIT = 500

/**
 * 多页面监视的报警摘要：把全局未确认报警按每个标签绑定的测点归组，供标签/网格显示红点。
 *
 * 不新增端点、不为每个标签单独订阅：每 15s 拉一次全局未确认报警（≤500 条）再在前端匹配。
 * 非活动标签不挂载 ScreenRuntime、收不到 screen-loaded，所以打开标签时还要预取一次主文档绑定；
 * 激活后运行时用更完整的绑定（含弹窗）覆盖。
 */
export function useWorkspaceAlarms(tabs: Ref<WorkspaceTabRef[]>) {
  const tabTagIds = reactive(new Map<number, number[]>())
  const unackedAlarms = ref<AlarmEvent[]>([])
  const fetchFailed = ref(false)
  const fetchCapped = ref(false)
  const prefetchFailed = reactive(new Set<number>())

  function alarmsForTab(tab: WorkspaceTabRef): AlarmEvent[] {
    const ids = tabTagIds.get(tab.key)
    if (!ids?.length) return []
    const bound = new Set(ids)
    return unackedAlarms.value.filter(alarm => alarm.tagId != null && bound.has(alarm.tagId))
  }

  function tabAlarmSummary(tab: WorkspaceTabRef): WorkspaceTabAlarmSummary {
    const alarms = alarmsForTab(tab)
    const prefetch = prefetchFailed.has(tab.key) && !tabTagIds.has(tab.key)
    const reason: WorkspaceAlarmIncomplete | null = prefetch
      ? 'prefetch-failed'
      : fetchFailed.value
        ? 'fetch-failed'
        : fetchCapped.value
          ? 'capped'
          : null
    return { count: alarms.length, incomplete: !!reason, reason, alarms }
  }

  /** 徽标文案：不完整时不用一个看起来很确定的数字。 */
  function tabAlarmBadgeText(summary: WorkspaceTabAlarmSummary): string {
    if (!summary.incomplete && summary.count <= 0) return ''
    if (summary.incomplete && summary.count <= 0) return '?'
    if (summary.count > 99) return '99+'
    return summary.incomplete ? `${summary.count}+` : String(summary.count)
  }

  function tabAlarmCount(tab: WorkspaceTabRef): number {
    return tabAlarmSummary(tab).count
  }

  async function refreshTabAlarms(): Promise<void> {
    if (!tabs.value.length) {
      unackedAlarms.value = []
      fetchFailed.value = false
      fetchCapped.value = false
      return
    }
    try {
      const page = await alarmApi.queryPage({ live: true, state: 'unack', offset: 0, limit: UNACK_LIMIT })
      unackedAlarms.value = page.items
      fetchFailed.value = false
      fetchCapped.value = !!(page.capped || page.total > UNACK_LIMIT || page.items.length >= UNACK_LIMIT)
    } catch {
      // 标签徽标是辅助提示：拉取失败时保留上一轮结果，但必须标明计数不完整。
      fetchFailed.value = true
    }
  }

  const { run: pollTabAlarms } = usePolling(refreshTabAlarms, 15_000, { enabled: () => tabs.value.length > 0 })

  /** 画面主文档里的绑定测点；与 ScreenRuntime 的 boundIds 同口径（弹窗内的节点由运行时补齐）。 */
  function documentTagIds(document: ScreenDocument | null | undefined): number[] {
    if (!document?.nodes) return []
    return [...new Set(document.nodes.flatMap(node => [
      node.tagId, node.alarmTagId, node.animation?.tagId,
      node.action.type === 'command' ? node.action.tagId : null,
      ...(node.commands ?? []).map(command => command.tagId),
      ...(node.tags ?? [])
    ].filter((value): value is number => !!value)))]
  }

  const prefetchedTags = new Set<string>()
  const tabTagLoading = new Set<number>()
  async function ensureTabTags(tab: WorkspaceTabRef): Promise<void> {
    const stamp = `${tab.key}:${tab.screenId}`
    if (prefetchedTags.has(stamp) || tabTagLoading.has(tab.key)) return
    tabTagLoading.add(tab.key)
    try {
      const detail = await screenApi.get(tab.screenId)
      if (!tabs.value.some(item => item.key === tab.key)) return
      tabTagIds.set(tab.key, documentTagIds(detail.document))
      prefetchFailed.delete(tab.key)
      prefetchedTags.add(stamp)
      void pollTabAlarms()
    } catch {
      // 预取失败不阻止打开；激活后 ScreenRuntime 会用完整绑定（含弹窗）补齐。未补齐前徽标标为不完整。
      prefetchFailed.add(tab.key)
    } finally {
      tabTagLoading.delete(tab.key)
    }
  }

  /** 运行时（含弹窗）上抛的完整绑定；覆盖预取结果。 */
  function setTabTags(key: number, tagIds: number[]): void {
    tabTagIds.set(key, tagIds)
    prefetchFailed.delete(key)
    void pollTabAlarms()
  }

  // 关闭标签时丢弃绑定；新标签/换画面时预取绑定测点。
  watch(() => tabs.value.map(tab => `${tab.key}:${tab.screenId}`), () => {
    const keys = new Set(tabs.value.map(tab => tab.key))
    for (const key of [...tabTagIds.keys()]) if (!keys.has(key)) tabTagIds.delete(key)
    for (const key of [...prefetchFailed]) if (!keys.has(key)) prefetchFailed.delete(key)
    for (const tab of tabs.value) void ensureTabTags(tab)
  }, { immediate: true })

  return { tabAlarmCount, tabAlarmSummary, tabAlarmBadgeText, pollTabAlarms, setTabTags, documentTagIds }
}
