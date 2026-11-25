import { onBeforeUnmount, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { tr } from '../i18n'

export function readReplayAt(value: unknown, now = Date.now()): number | null {
  const at = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(at) && at > 0 && at <= now ? at : null
}

export function useScreenReplay(options?: {
  initialAt?: number | null
  onChange?: (at: number | null, commit?: boolean) => void
}) {
  const replayAt = ref<number | null>(readReplayAt(options?.initialAt ?? null))
  const replayDialog = ref(false)
  const replayDate = ref<Date | null>(new Date(Date.now() - 3600_000))
  const replayPlaying = ref(false)
  const replaySpeed = ref(1)
  let replayTimer: number | undefined

  function notify(commit = false): void {
    options?.onChange?.(replayAt.value, commit)
  }

  function setReplayAt(at: number | null, silent = false): void {
    const next = at == null ? null : readReplayAt(at)
    if (next === replayAt.value) return
    if (next == null) {
      replayPlaying.value = false
      stopReplayTimer()
    }
    replayAt.value = next
    if (!silent) notify()
  }

  function openReplay(): void {
    replayDate.value = new Date((replayAt.value ?? Date.now()) - 3600_000)
    replayDialog.value = true
  }

  function startReplay(): void {
    const at = replayDate.value?.getTime()
    if (!at || !Number.isFinite(at) || at > Date.now()) {
      ElMessage.warning(tr('screenReplay.future'))
      return
    }
    replayAt.value = at
    replayDialog.value = false
    replayPlaying.value = false
    stopReplayTimer()
    notify(true)
  }

  function toggleReplayPlay(): void {
    const current = replayAt.value
    if (current == null) return
    if (replayPlaying.value) {
      replayPlaying.value = false
      stopReplayTimer()
      notify(true)
      return
    }
    replayPlaying.value = true
    stopReplayTimer()
    replayTimer = window.setInterval(() => {
      const at = replayAt.value
      if (at == null) { stopReplayTimer(); return }
      const now = Date.now()
      const next = Math.min(now, at + 1000 * replaySpeed.value)
      replayAt.value = next
      notify(next >= now)
      if (next >= now) {
        replayPlaying.value = false
        stopReplayTimer()
      }
    }, 1000)
  }

  function stopReplayTimer(): void {
    if (replayTimer !== undefined) window.clearInterval(replayTimer)
    replayTimer = undefined
  }

  function stepReplay(seconds: number): void {
    const current = replayAt.value
    if (current == null) return
    const next = Math.max(1, Math.min(Date.now(), current + seconds * 1000))
    replayAt.value = next
    notify(true)
  }

  function exitReplay(): void {
    replayPlaying.value = false
    stopReplayTimer()
    replayAt.value = null
    notify(true)
  }

  onBeforeUnmount(stopReplayTimer)
  return { replayAt, replayDialog, replayDate, replayPlaying, replaySpeed, openReplay, startReplay, toggleReplayPlay, stepReplay, exitReplay, setReplayAt }
}
