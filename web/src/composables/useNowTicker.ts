import { onBeforeUnmount, onMounted, ref } from 'vue'

// 只在页面可见时推进的“现在”时钟：用于“x 秒前”和数据新鲜度判断。
// 后台标签页不常驻 1 秒定时器，避免无意义的 CPU/电量消耗；回到前台立即校正一次。
export function useNowTicker(intervalMs = 1000) {
  const now = ref(performance.now())
  let timer: number | undefined

  function stop(): void {
    if (timer !== undefined) {
      window.clearInterval(timer)
      timer = undefined
    }
  }

  function start(): void {
    stop()
    timer = window.setInterval(() => { now.value = performance.now() }, intervalMs)
  }

  function onVisibilityChange(): void {
    if (document.hidden) {
      stop()
      return
    }
    now.value = performance.now()
    start()
  }

  onMounted(() => {
    if (!document.hidden) start()
    document.addEventListener('visibilitychange', onVisibilityChange)
  })

  onBeforeUnmount(() => {
    stop()
    document.removeEventListener('visibilitychange', onVisibilityChange)
  })

  return { now }
}