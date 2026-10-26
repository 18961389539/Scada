import { defineStore } from 'pinia'
import { ref } from 'vue'
import { hmiLibraryApi } from '../api/hmiLibrary'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'
import type { LibraryItem } from '../screens/model'
// 图库缓存：运行画面与编辑器共用；修改后调用 reload。失败保留已有条目，并记下原因供页面展示。
export const useLibraryStore = defineStore('hmiLibrary', () => {
  const items = ref<LibraryItem[]>([])
  const loaded = ref(false)
  const error = ref('')
  let loading: Promise<void> | null = null
  async function reload(): Promise<void> {
    try {
      items.value = await hmiLibraryApi.list()
      loaded.value = true
      error.value = ''
    } catch (e) {
      error.value = errorMessage(e, tr('hmiLibraryView.listFailed'))
      throw e
    }
  }
  function ensure(): Promise<void> {
    if (loaded.value && !error.value) return Promise.resolve()
    loading ??= reload().catch(() => undefined).finally(() => { loading = null })
    return loading
  }
  return { items, loaded, error, reload, ensure }
})
