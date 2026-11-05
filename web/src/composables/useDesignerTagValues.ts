import { onBeforeUnmount, ref, watch, type Ref } from 'vue'
import { realtimeApi } from '../api'
import { subscribeTags, type TagSubscription } from '../realtime/hub'
import type { TagValue } from '../types'

/**
 * 设计器里的实时值预览。
 *
 * 两条路径并存是有原因的：SignalR 负责“选中的图元立刻跳数”，30 秒一次的 HTTP 快照兜住
 * 断线、重连缺口和首屏（此刻订阅还没建立）。快照只按当前图元的测点取，不再拉全站实时表——
 * 后者在大站点上是一次几千行的响应，而设计器只用到几个点。
 */
export function useDesignerTagValues(options: {
  /** 当前选中图元涉及到的测点 id（去重后） */
  tagIds: Ref<number[]>
  /** 嵌入模式（模板编辑等）不订阅、不轮询 */
  disabled: () => boolean
}) {
  const values = ref<Record<number, TagValue>>({})
  const failed = ref(false)

  let subscription: TagSubscription | undefined
  let generation = 0
  let timer: number | undefined
  let stopped = false

  function stopSubscription(): void {
    generation++
    subscription?.dispose()
    subscription = undefined
  }

  watch(options.tagIds, ids => {
    stopSubscription()
    if (options.disabled() || !ids.length) return
    // 每一轮订阅带自己的 generation：换选中图元后，旧订阅晚到的推送必须丢弃。
    const current = generation
    void subscribeTags(ids, value => {
      if (current !== generation) return
      const previous = values.value[value.tagId]
      if (!previous || value.ts >= previous.ts) values.value = { ...values.value, [value.tagId]: value }
    }).then(created => {
      if (current !== generation || stopped) created.dispose()
      else subscription = created
    }).catch(() => { /* 实时值不可用不影响组态编辑 */ })
  }, { immediate: true })

  async function refresh(): Promise<void> {
    if (options.disabled()) return
    const ids = options.tagIds.value
    if (!ids.length) return
    try {
      const snapshot = await realtimeApi.get(ids)
      const next = { ...values.value }
      for (const value of snapshot) {
        const previous = next[value.tagId]
        if (!previous || value.ts >= previous.ts) next[value.tagId] = value
      }
      values.value = next
      failed.value = false
    } catch {
      failed.value = true
    }
  }

  /** 由视图在挂载成功后调用：首屏补一次快照，并按 30 秒节拍刷新（页面隐藏时跳过）。 */
  function start(): void {
    if (options.disabled()) return
    stopped = false
    void refresh()
    timer = window.setInterval(() => {
      if (window.document.visibilityState === 'visible') void refresh()
    }, 30000)
  }

  function stop(): void {
    stopped = true
    stopSubscription()
    if (timer !== undefined) {
      window.clearInterval(timer)
      timer = undefined
    }
  }

  onBeforeUnmount(stop)

  return { values, failed, refresh, start, stop }
}
