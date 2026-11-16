import { ref, type Ref } from 'vue'
import { useRouter } from 'vue-router'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { collectScreenTags } from '../screens/screenTags'
import type { AlarmEvent } from '../types'
import { tr } from '../i18n'

/**
 * 报警 → 画面下钻：按测点索引已发布画面，单个命中直接跳转，多个命中交给选择器。
 * 索引只建一次；单个坏画面只跳过自己，不能让整批「打开画面」失效。
 * 测点收集复用 screens/screenTags，避免第三份实现。
 */
export function useAlarmScreenLocator(actionError: Ref<string>) {
  const router = useRouter()
  const screenPicker = ref(false)
  const screenMatches = ref<{ id: number; name: string }[]>([])
  let screenIndex: Map<number, { id: number; name: string }[]> | null = null
  let screenIndexPromise: Promise<void> | null = null

  async function ensureScreenIndex(): Promise<void> {
    if (screenIndex) return
    if (!screenIndexPromise) screenIndexPromise = (async () => {
      const published = (await screenApi.list()).filter(screen => screen.publishedRevision != null)
      const loaded = await Promise.all(published.map(async screen => {
        try { return { screen, detail: await screenApi.get(screen.id) } }
        catch { return null }
      }))
      if (published.length && loaded.every(item => item == null)) throw new Error('screen-index')
      const index = new Map<number, { id: number; name: string }[]>()
      for (const item of loaded) {
        if (!item) continue
        try {
          const tags = new Set<number>()
          for (const node of item.detail.document.nodes ?? []) collectScreenTags(node, tags)
          for (const tagId of tags) {
            const rows = index.get(tagId) ?? []
            rows.push({ id: item.screen.id, name: item.screen.name })
            index.set(tagId, rows)
          }
        } catch { /* skip malformed screen */ }
      }
      screenIndex = index
    })().catch(error => { screenIndexPromise = null; throw error })
    await screenIndexPromise
  }

  async function openAlarmScreen(alarm: AlarmEvent): Promise<void> {
    actionError.value = ''
    if (!alarm.tagId) { actionError.value = tr('alarmUx.noScreen'); return }
    try { await ensureScreenIndex() }
    catch (error) { actionError.value = errorMessage(error, tr('alarmUx.screenLookupFailed')); return }
    const matches = screenIndex?.get(alarm.tagId) ?? []
    if (!matches.length) { actionError.value = tr('alarmUx.noScreen'); return }
    if (matches.length === 1) { await router.push(`/screens/${matches[0].id}`); return }
    screenMatches.value = matches
    screenPicker.value = true
  }

  function goScreen(id: number): void {
    screenPicker.value = false
    void router.push(`/screens/${id}`)
  }

  return { screenPicker, screenMatches, ensureScreenIndex, openAlarmScreen, goScreen }
}