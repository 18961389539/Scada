import { defineStore } from 'pinia'
import { ref } from 'vue'
import { hmiLibraryApi } from '../api/hmiLibrary'
import type { LibraryItem } from '../screens/model'
// 图库缓存：运行画面与编辑器共用；修改后调用 reload。
export const useLibraryStore = defineStore('hmiLibrary', () => {
  const items = ref<LibraryItem[]>([]); let loading: Promise<void> | null = null; const loaded = ref(false)
  async function reload(): Promise<void> { items.value = await hmiLibraryApi.list(); loaded.value = true }
  function ensure(): Promise<void> { if (loaded.value) return Promise.resolve(); loading ??= reload().catch(() => { loading = null }); return loading }
  return { items, loaded, reload, ensure }
})
