import { ref } from 'vue'
import { alarmApi } from '../api'
import type { AlarmEvent } from '../types'

// 全应用共享的活动报警缓存。
// 控制台页面里有 1 个总报警条 + N 个区域画面，每个区域原本各自每 10s 拉 2000 条，
// 同一份数据被拉 N+1 次；这里用单例缓存 + 在途去重，并发调用共用一次请求。
export const ACTIVE_ALARMS_LIMIT = 2000

const alarms = ref<AlarmEvent[]>([])
let inflight: Promise<AlarmEvent[]> | undefined
let lastFinished = 0

async function request(): Promise<AlarmEvent[]> {
  const list = await alarmApi.query({ activeOnly: true, live: true, priorityFirst: true, limit: ACTIVE_ALARMS_LIMIT })
  alarms.value = list
  return list
}

// maxAgeMs：允许复用最近一次结果的时间窗。多个区域同时刷新时只会有一次网络请求。
export async function refreshActiveAlarms(maxAgeMs = 0): Promise<AlarmEvent[]> {
  if (!inflight && maxAgeMs > 0 && Date.now() - lastFinished < maxAgeMs) return alarms.value
  if (!inflight) {
    inflight = request().finally(() => {
      inflight = undefined
      lastFinished = Date.now()
    })
  }
  return inflight
}