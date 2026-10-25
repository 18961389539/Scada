import { onBeforeUnmount, onMounted } from 'vue'

export interface PollingOptions {
  /** 页面不可见时跳过本轮，回到前台且数据已过时则补一次 */
  pauseWhenHidden?: boolean
  /** 挂载后立即执行一次；调用方通常已有首个加载，默认不重复执行 */
  immediate?: boolean
  /** 返回 false 时跳过本轮（自动刷新开关关闭、编辑器打开、弹窗未显示等），跳过不计入“上次完成” */
  enabled?: () => boolean
}

// 统一轮询节拍：隐藏页暂停、上一轮未完成时跳过、可见时按需补刷。
export function usePolling(
  task: () => unknown | Promise<unknown>,
  intervalMs: number,
  options: PollingOptions = {}
): { run: () => Promise<void> } {
  let timer: number | undefined
  let running = false
  let disposed = false
  let lastFinished = 0
  const pauseWhenHidden = options.pauseWhenHidden ?? true

  async function run(): Promise<void> {
    if (disposed || running) return
    if (pauseWhenHidden && document.hidden) return
    if (options.enabled && !options.enabled()) return
    running = true
    try {
      await task()
    } catch {
      // 各页面自行提示失败；轮询只负责节拍与去重
    } finally {
      running = false
      lastFinished = Date.now()
    }
  }

  function onVisibilityChange(): void {
    if (disposed || document.hidden) return
    if (Date.now() - lastFinished >= intervalMs) void run()
  }

  onMounted(() => {
    if (options.immediate) void run()
    timer = window.setInterval(() => { void run() }, intervalMs)
    document.addEventListener('visibilitychange', onVisibilityChange)
  })

  onBeforeUnmount(() => {
    disposed = true
    if (timer) window.clearInterval(timer)
    document.removeEventListener('visibilitychange', onVisibilityChange)
  })

  return { run }
}
