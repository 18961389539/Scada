import { onBeforeUnmount, onMounted, readonly, ref } from 'vue'

// 全应用共享的“现在”时钟：所有订阅者共用一个 1 秒定时器。
// 一个控制台页面会同时挂载主画面 + N 个区域画面，若每个运行实例各自 setInterval，
// 后台标签页会每秒唤醒 N 次并触发整棵 SVG 重渲染；共享后固定为 1 次，页面隐藏时完全停止。
const TICK_MS = 1000
const now = ref(Date.now())
let timer: number | undefined
let subscribers = 0

function tick(): void {
  now.value = Date.now()
}

function stop(): void {
  if (timer !== undefined) {
    window.clearInterval(timer)
    timer = undefined
  }
}

function start(): void {
  stop()
  timer = window.setInterval(tick, TICK_MS)
}

// 后台标签页不常驻定时器；回到前台立即校正一次，避免“x 秒前”显示陈旧。
function onVisibilityChange(): void {
  if (document.hidden) {
    stop()
    return
  }
  tick()
  start()
}

export function useNowTicker() {
  onMounted(() => {
    subscribers++
    if (subscribers > 1) return
    tick()
    if (!document.hidden) start()
    document.addEventListener('visibilitychange', onVisibilityChange)
  })

  onBeforeUnmount(() => {
    subscribers = Math.max(0, subscribers - 1)
    if (subscribers > 0) return
    stop()
    document.removeEventListener('visibilitychange', onVisibilityChange)
  })

  return { now: readonly(now) }
}