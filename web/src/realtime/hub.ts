// 全局唯一的实时长连接。
// 背景：此前 AppLayout、报警页、实时页、画面运行各自 new HubConnection，
// 导致同一用户维持多条连接、alarm 事件（服务端 Clients.All）被重复消费而重复发声，
// 且页面进出必断连重连。这里收敛为单例：消费者只注册处理器，不管理连接。
// 测点订阅按 tagId 引用计数，归零后才向服务端取消，避免多个消费者互相取消订阅。
import * as signalR from '@microsoft/signalr'
import { ref, type Ref } from 'vue'
import { TOKEN_KEY } from '../api/http'
import type { TagValue } from '../types'

export type HubState = 'disconnected' | 'connecting' | 'connected' | 'reconnecting'

export interface TagSubscription {
  dispose: () => void
}

const state = ref<HubState>('disconnected')
let connection: signalR.HubConnection | null = null
let starting: Promise<void> | null = null
let retry: ReturnType<typeof setTimeout> | undefined

// tagId -> 订阅该测点的处理器集合；集合清空后引用计数归零才真正取消订阅
const tagListeners = new Map<number, Set<(value: TagValue) => void>>()
const tagRefs = new Map<number, number>()

const eventHandlers = new Map<string, Set<(payload: unknown) => void>>()
const reconnectedHandlers = new Set<() => void>()
const closedHandlers = new Set<() => void>()

export function hubState(): Ref<HubState> {
  return state
}

/** 当前仍有消费者的测点，重连后需要整体重新订阅。 */
function activeTagIds(): number[] {
  return [...tagRefs.entries()].filter(([, count]) => count > 0).map(([id]) => id)
}

async function resubscribe(): Promise<void> {
  if (!connection || connection.state !== signalR.HubConnectionState.Connected) return
  const ids = activeTagIds()
  if (!ids.length) return
  try {
    await connection.invoke('Subscribe', ids)
  } catch {
    // 重连竞态下由下一次重连或新订阅补偿
  }
}

function dispatch(event: string, payload: unknown): void {
  const handlers = eventHandlers.get(event)
  if (!handlers?.size) return
  for (const handler of [...handlers]) {
    try {
      handler(payload)
    } catch {
      // 单个消费者异常不应影响其他消费者
    }
  }
}

function build(): signalR.HubConnection {
  const hub = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/realtime', { accessTokenFactory: () => localStorage.getItem(TOKEN_KEY) ?? '' })
    .withAutomaticReconnect()
    .build()

  // 服务端按连接推送「该连接订阅的并集」，这里再按 tagId 只分发给订阅了该测点的消费者，
  // 避免实时页与画面互相收到对方的测点。
  hub.on('values', (incoming: TagValue[]) => {
    for (const value of incoming) {
      const listeners = tagListeners.get(value.tagId)
      if (!listeners?.size) continue
      for (const listener of [...listeners]) {
        try {
          listener(value)
        } catch {
          // 单个订阅者抛异常只隔离它自己：不重试，同一批里其余订阅者照常收到该值。
        }
      }
    }
  })

  hub.on('alarm', (payload: unknown) => dispatch('alarm', payload))

  hub.onreconnecting(() => {
    state.value = 'reconnecting'
  })
  hub.onreconnected(async () => {
    state.value = 'connected'
    await resubscribe()
    for (const handler of [...reconnectedHandlers]) {
      try {
        handler()
      } catch {
        // 单个消费者的回调异常就地丢弃：不重试，也不影响其余消费者的重连通知。
      }
    }
  })
  hub.onclose(() => {
    state.value = 'disconnected'
    for (const handler of [...closedHandlers]) {
      try {
        handler()
      } catch {
        // 同上：断线通知里某个消费者失败不会中断其余消费者的清理。
      }
    }
  })

  return hub
}

function scheduleRetry(): void {
  if (retry !== undefined) return
  // 未登录时不重试，避免登录前反复 401
  if (!localStorage.getItem(TOKEN_KEY)) return
  retry = setTimeout(() => {
    retry = undefined
    void ensureConnection()
  }, 5000)
}

async function ensureConnection(): Promise<void> {
  if (connection && connection.state !== signalR.HubConnectionState.Disconnected) return
  if (starting) return starting
  if (retry !== undefined) {
    clearTimeout(retry)
    retry = undefined
  }
  state.value = 'connecting'
  const hub = build()
  connection = hub
  starting = hub
    .start()
    .then(async () => {
      state.value = 'connected'
      // 首次连上也可能已有消费者登记了测点（订阅早于连接建立）
      await resubscribe()
    })
    .catch(() => {
      if (connection === hub) {
        connection = null
        state.value = 'disconnected'
      }
      scheduleRetry()
    })
    .finally(() => {
      starting = null
    })
  return starting
}

/**
 * 注册实时事件处理器（如 alarm），返回取消注册的函数。
 * 首次注册即触发连接建立，消费者无需关心连接生命周期。
 */
export function onHubEvent<T>(event: string, handler: (payload: T) => void): () => void {
  let handlers = eventHandlers.get(event)
  if (!handlers) {
    handlers = new Set()
    eventHandlers.set(event, handlers)
  }
  const wrapped = (payload: unknown): void => handler(payload as T)
  handlers.add(wrapped)
  void ensureConnection()
  return () => {
    handlers?.delete(wrapped)
    if (handlers && !handlers.size) eventHandlers.delete(event)
  }
}

/** 重连成功回调：消费者据此补拉快照，避免断线期间的数据缺口。 */
export function onHubReconnected(handler: () => void): () => void {
  reconnectedHandlers.add(handler)
  return () => {
    reconnectedHandlers.delete(handler)
  }
}

/** 连接关闭回调：用于恢复性检查（例如授权被撤销后重新确认登录态）。 */
export function onHubClosed(handler: () => void): () => void {
  closedHandlers.add(handler)
  return () => {
    closedHandlers.delete(handler)
  }
}

/**
 * 订阅测点实时值。同一 tagId 可被多个消费者订阅，引用计数归零才取消，
 * 因此互不影响。返回的 dispose 必须由调用方在卸载/换订阅时执行。
 */
export async function subscribeTags(
  ids: readonly number[],
  listener: (value: TagValue) => void
): Promise<TagSubscription> {
  const unique = [...new Set(ids)]
  const wrapped = (value: TagValue): void => listener(value)
  const added: number[] = []
  for (const id of unique) {
    const count = tagRefs.get(id) ?? 0
    tagRefs.set(id, count + 1)
    if (count === 0) added.push(id)
    let listeners = tagListeners.get(id)
    if (!listeners) {
      listeners = new Set()
      tagListeners.set(id, listeners)
    }
    listeners.add(wrapped)
  }
  await ensureConnection()
  if (added.length && connection?.state === signalR.HubConnectionState.Connected) {
    try {
      await connection.invoke('Subscribe', added)
    } catch {
      // 由重连后的 resubscribe 补偿
    }
  }
  let disposed = false
  return {
    dispose: () => {
      if (disposed) return
      disposed = true
      releaseTags(unique, wrapped)
    }
  }
}

function releaseTags(ids: readonly number[], listener: (value: TagValue) => void): void {
  const removed: number[] = []
  for (const id of ids) {
    tagListeners.get(id)?.delete(listener)
    const count = (tagRefs.get(id) ?? 0) - 1
    if (count > 0) {
      tagRefs.set(id, count)
      continue
    }
    tagRefs.delete(id)
    if (!tagListeners.get(id)?.size) tagListeners.delete(id)
    removed.push(id)
  }
  if (removed.length && connection?.state === signalR.HubConnectionState.Connected) {
    void connection.invoke('Unsubscribe', removed).catch(() => {
      // 断线时无需补偿：重连后按引用计数整体重订阅
    })
  }
}

/** 登出或布局卸载时断开共享连接；订阅注册表由各消费者自行 dispose。 */
export function resetHub(): void {
  if (retry !== undefined) {
    clearTimeout(retry)
    retry = undefined
  }
  const hub = connection
  connection = null
  starting = null
  state.value = 'disconnected'
  if (hub) void hub.stop().catch(() => undefined)
}
