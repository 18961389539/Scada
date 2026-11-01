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

interface SharedConnection {
  hub: signalR.HubConnection
  revision: number
  starting: Promise<void> | null
  syncing: Promise<void> | null
  subscribed: Set<number>
}

const state = ref<HubState>('disconnected')
let connection: SharedConnection | null = null
let retry: ReturnType<typeof setTimeout> | undefined
let enabled = true
let connectedBefore = false
let lifecycle = 0

// tagId -> 订阅该测点的处理器集合；集合清空后引用计数归零才真正取消订阅
const tagListeners = new Map<number, Set<(value: TagValue) => void>>()
const tagRefs = new Map<number, number>()

const eventHandlers = new Map<string, Set<(payload: unknown) => void>>()
const reconnectedHandlers = new Set<() => void>()
const closedHandlers = new Set<() => void>()

export function hubState(): Ref<HubState> {
  return state
}

function hasConsumers(): boolean {
  return !!(tagRefs.size || eventHandlers.size || reconnectedHandlers.size || closedHandlers.size)
}

function canConnect(): boolean {
  return enabled && !!localStorage.getItem(TOKEN_KEY) && hasConsumers()
}

function clearRetry(): void {
  if (retry === undefined) return
  clearTimeout(retry)
  retry = undefined
}

function stopHub(hub: signalR.HubConnection): void {
  void hub.stop().catch(error => { console.warn('Failed to stop realtime connection', error) })
}

function retire(current: SharedConnection): void {
  if (connection !== current) return
  connection = null
  current.revision++
  state.value = 'disconnected'
  stopHub(current.hub)
}

function isCurrent(current: SharedConnection, revision: number): boolean {
  return connection === current && current.revision === revision
}

function usable(current: SharedConnection, revision: number): boolean {
  if (!isCurrent(current, revision)) return false
  if (canConnect()) return true
  resetHub()
  return false
}

function failed(current: SharedConnection, revision: number): void {
  if (!isCurrent(current, revision)) return
  retire(current)
  scheduleRetry()
}

/** 同一连接串行同步订阅差集，启动和并发消费者不会重复 Subscribe。 */
function syncSubscriptions(current: SharedConnection, revision: number): Promise<void> {
  if (!usable(current, revision) || current.hub.state !== signalR.HubConnectionState.Connected) return Promise.resolve()
  if (current.syncing) return current.syncing.then(() => syncSubscriptions(current, revision))
  const ids = new Set(tagRefs.keys())
  const removed = [...current.subscribed].filter(id => !ids.has(id))
  const added = [...ids].filter(id => !current.subscribed.has(id))
  if (!removed.length && !added.length) return Promise.resolve()
  const method = removed.length ? 'Unsubscribe' : 'Subscribe'
  const changed = removed.length ? removed : added
  const syncing = Promise.resolve().then(async () => {
    if (!usable(current, revision)) return
    await current.hub.invoke(method, changed)
    if (!usable(current, revision)) return
    for (const id of changed) {
      if (method === 'Subscribe') current.subscribed.add(id)
      else current.subscribed.delete(id)
    }
  }).finally(() => {
    if (current.syncing === syncing) current.syncing = null
  })
  current.syncing = syncing
  // 包括 await/清理期间发生的登记和释放，通知恢复前必须收敛到最新并集。
  return syncing.then(() => syncSubscriptions(current, revision))
}

async function restore(current: SharedConnection, revision: number): Promise<void> {
  await syncSubscriptions(current, revision)
  if (!usable(current, revision)) return
  if (current.hub.state !== signalR.HubConnectionState.Connected) throw new Error('Realtime connection closed during recovery')
  state.value = 'connected'
  const recovered = connectedBefore
  connectedBefore = true
  if (!recovered) return
  for (const handler of [...reconnectedHandlers]) {
    if (!usable(current, revision)) break
    try {
      handler()
    } catch {
      // 消费者失败只隔离它自己，不影响其余消费者补拉快照。
    }
  }
}

function syncCurrentTags(): Promise<void> {
  const current = connection
  if (!current || current.hub.state !== signalR.HubConnectionState.Connected) return Promise.resolve()
  const revision = current.revision
  return syncSubscriptions(current, revision).catch(() => { failed(current, revision) })
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

function build(): SharedConnection {
  const hub = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/realtime', { accessTokenFactory: () => localStorage.getItem(TOKEN_KEY) ?? '' })
    .withAutomaticReconnect()
    .build()
  const current: SharedConnection = { hub, revision: 0, starting: null, syncing: null, subscribed: new Set() }

  // 服务端推送订阅并集，这里按 tagId 分发；退役连接的迟到推送也必须丢弃。
  hub.on('values', (incoming: TagValue[]) => {
    if (!usable(current, current.revision)) return
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

  hub.on('alarm', (payload: unknown) => {
    if (usable(current, current.revision)) dispatch('alarm', payload)
  })

  hub.onreconnecting(() => {
    if (!usable(current, current.revision)) return
    current.revision++
    current.subscribed.clear()
    current.syncing = null
    state.value = 'reconnecting'
  })
  hub.onreconnected(() => {
    if (!usable(current, current.revision)) return
    const revision = ++current.revision
    current.subscribed.clear()
    current.syncing = null
    void restore(current, revision).catch(() => { failed(current, revision) })
  })
  hub.onclose(() => {
    if (connection !== current) return
    connection = null
    current.revision++
    state.value = 'disconnected'
    const closedLifecycle = lifecycle
    for (const handler of [...closedHandlers]) {
      if (lifecycle !== closedLifecycle) break
      try {
        handler()
      } catch {
        // 某个消费者失败不会中断其余消费者的清理。
      }
    }
    scheduleRetry()
  })

  return current
}

function scheduleRetry(): void {
  // SignalR 自动重连耗尽后仍需重试，但登出、显式断开、无消费者时不能复活连接。
  if (!localStorage.getItem(TOKEN_KEY)) { resetHub(); return }
  if (retry !== undefined || connection || !canConnect()) return
  retry = setTimeout(() => {
    retry = undefined
    void ensureConnection()
  }, 5000)
}

async function ensureConnection(): Promise<void> {
  if (!localStorage.getItem(TOKEN_KEY)) { resetHub(); return }
  if (!canConnect()) return
  if (connection?.starting) return connection.starting
  if (connection && connection.hub.state !== signalR.HubConnectionState.Disconnected) return
  if (connection) retire(connection)
  clearRetry()
  state.value = 'connecting'
  let current: SharedConnection
  try {
    current = build()
  } catch {
    state.value = 'disconnected'
    scheduleRetry()
    return
  }
  connection = current
  const revision = current.revision
  const starting = Promise.resolve().then(async () => {
    if (!usable(current, revision)) return
    await current.hub.start()
    if (connection !== current) {
      stopHub(current.hub)
      return
    }
    if (!isCurrent(current, revision)) return
    await restore(current, revision)
  }).catch(() => {
    failed(current, revision)
  }).finally(() => {
    // Promise 只清理自己所属连接，不能清掉登出后新连接的启动任务。
    if (current.starting === starting) current.starting = null
  })
  current.starting = starting
  return starting
}

function activateConsumer(): Promise<void> {
  // 只有新的注册能解除 resetHub 的显式停止，旧异步任务和定时器不能解除。
  if (localStorage.getItem(TOKEN_KEY)) enabled = true
  return ensureConnection()
}

function stopIfIdle(): void {
  if (!hasConsumers()) resetHub()
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
  void activateConsumer()
  return () => {
    handlers?.delete(wrapped)
    if (handlers && !handlers.size && eventHandlers.get(event) === handlers) eventHandlers.delete(event)
    stopIfIdle()
  }
}

/** 首次启动不通知；自动重连或终止断线重启成功并恢复订阅后，通知消费者补拉快照。 */
export function onHubReconnected(handler: () => void): () => void {
  const wrapped = (): void => handler()
  reconnectedHandlers.add(wrapped)
  void activateConsumer()
  return () => {
    reconnectedHandlers.delete(wrapped)
    stopIfIdle()
  }
}

/** 连接关闭回调：用于恢复性检查（例如授权被撤销后重新确认登录态）。 */
export function onHubClosed(handler: () => void): () => void {
  const wrapped = (): void => handler()
  closedHandlers.add(wrapped)
  void activateConsumer()
  return () => {
    closedHandlers.delete(wrapped)
    stopIfIdle()
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
  const registeredLifecycle = lifecycle
  for (const id of unique) {
    const count = tagRefs.get(id) ?? 0
    tagRefs.set(id, count + 1)
    let listeners = tagListeners.get(id)
    if (!listeners) {
      listeners = new Set()
      tagListeners.set(id, listeners)
    }
    listeners.add(wrapped)
  }
  await activateConsumer()
  if (registeredLifecycle === lifecycle) await syncCurrentTags()
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
  for (const id of ids) {
    tagListeners.get(id)?.delete(listener)
    const count = (tagRefs.get(id) ?? 0) - 1
    if (count > 0) {
      tagRefs.set(id, count)
      continue
    }
    tagRefs.delete(id)
    if (!tagListeners.get(id)?.size) tagListeners.delete(id)
  }
  stopIfIdle()
  void syncCurrentTags()
}

/** 登出或布局卸载时断开共享连接；订阅注册表由各消费者自行 dispose。 */
export function resetHub(): void {
  enabled = false
  connectedBefore = false
  lifecycle++
  clearRetry()
  if (connection) retire(connection)
  state.value = 'disconnected'
}
