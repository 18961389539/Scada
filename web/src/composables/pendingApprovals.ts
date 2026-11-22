import { computed, getCurrentInstance, onBeforeUnmount, ref, toValue, watch, type MaybeRefOrGetter } from 'vue'
import { controlApi, type ControlRequest } from '../api/control'
import { errorMessage } from '../api/http'
import type { RecipeDownload } from '../api/recipes'
import { tr } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { allows } from '../access/policies'

export type ControlRequestRow = {
  request: ControlRequest
  sequenceName: string | null
  recipe?: RecipeDownload | null
  mine: boolean
}

const PENDING_POLL_MS = 5000

/** 待复核且仍在有效期内；过期的申请按已超时处理，不再计入角标。 */
export function isPendingControlRequest(request: ControlRequest, now = Date.now()): boolean {
  return request.status === 'Pending' && request.expiresTs > now
}

export function pendingControlRequests(rows: ControlRequestRow[], now = Date.now()): ControlRequestRow[] {
  return rows.filter(row => isPendingControlRequest(row.request, now))
}

export function pendingApprovalCount(rows: ControlRequestRow[], now = Date.now()): number {
  return pendingControlRequests(rows, now).length
}

export const pendingApprovalsPath = '/control'

const rows = ref<ControlRequestRow[]>([])
const loadError = ref('')
const loaded = ref(false)
let inflight: Promise<void> | null = null
let subscribers = 0
let timer: ReturnType<typeof setInterval> | undefined
let pollGeneration = 0

export function pendingApprovalRows(): ControlRequestRow[] {
  return rows.value
}

export function setPendingApprovalRows(next: ControlRequestRow[]): void {
  rows.value = next
  loaded.value = true
  loadError.value = ''
}

export async function refreshPendingApprovals(): Promise<void> {
  if (inflight) return inflight
  const run = (async () => {
    try {
      rows.value = await controlApi.requests()
      loadError.value = ''
      loaded.value = true
    } catch (error) {
      loadError.value = errorMessage(error, tr('controlView.requestsLoadFailed'))
      throw error
    } finally {
      inflight = null
    }
  })()
  inflight = run
  return run
}

function startPolling(): void {
  if (timer !== undefined) return
  const generation = ++pollGeneration
  void refreshPendingApprovals().catch(() => undefined)
  timer = setInterval(() => {
    if (generation !== pollGeneration) return
    void refreshPendingApprovals().catch(() => undefined)
  }, PENDING_POLL_MS)
}

function stopPolling(): void {
  if (timer === undefined) return
  clearInterval(timer)
  timer = undefined
  pollGeneration++
}

function subscribe(): void {
  subscribers++
  if (subscribers === 1) startPolling()
}

function unsubscribe(): void {
  subscribers = Math.max(0, subscribers - 1)
  if (subscribers === 0) stopPolling()
}

/** 测试或登出时释放模块级轮询，避免跨用例泄漏定时器。 */
export function resetPendingApprovalsForTests(): void {
  subscribers = 0
  stopPolling()
  rows.value = []
  loadError.value = ''
  loaded.value = false
  inflight = null
}

/**
 * 控制室顶栏、侧栏「操作许可」和许可页共用同一份待复核列表，避免两套轮询口径不一致。
 * 默认仅在当前用户具备控制权限时订阅；预览里也可以只读订阅以显示数量。
 */
export function usePendingApprovals(options?: { enabled?: MaybeRefOrGetter<boolean> }) {
  const auth = useAuthStore()
  const enabled = computed(() => {
    if (options?.enabled !== undefined) return !!toValue(options.enabled)
    return allows(auth.user, 'any-control')
  })
  const pending = computed(() => pendingControlRequests(rows.value))
  const pendingCount = computed(() => pending.value.length)

  let subscribed = false
  const sync = (on: boolean) => {
    if (on && !subscribed) { subscribe(); subscribed = true }
    else if (!on && subscribed) { unsubscribe(); subscribed = false }
  }
  watch(enabled, sync, { immediate: true })
  if (getCurrentInstance()) onBeforeUnmount(() => sync(false))

  return {
    rows,
    pending,
    pendingCount,
    error: loadError,
    loaded,
    path: pendingApprovalsPath,
    refresh: refreshPendingApprovals
  }
}
