import { computed, reactive, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'

export function useCatalogQuery(defaults: Record<string, string>) {
  const route = useRoute(), router = useRouter()
  const applied = computed(() => {
    const values: Record<string, string> = {}
    for (const [key, fallback] of Object.entries(defaults)) {
      const value = route.query[key]
      values[key] = typeof value === 'string' ? value : fallback
    }
    return values
  })
  const draft = reactive({ ...applied.value })
  watch(() => JSON.stringify(applied.value), () => Object.assign(draft, applied.value))
  const pending = computed(() => Object.keys(defaults).some(key => draft[key]?.trim() !== applied.value[key]))
  async function apply(reset = false): Promise<boolean> {
    const query = { ...route.query }
    for (const [key, fallback] of Object.entries(defaults)) {
      const value = reset ? fallback : (draft[key] ?? fallback).trim()
      if (value === fallback) delete query[key]
      else query[key] = value
    }
    delete query.page
    const changed = Object.keys(defaults).some(key => query[key] !== route.query[key])
    await router.replace({ query })
    Object.assign(draft, applied.value)
    return changed
  }
  const page = computed({
    get: () => {
      const value = Number(route.query.page)
      return Number.isSafeInteger(value) && value > 0 ? value : 1
    },
    set: value => { void router.replace({ query: { ...route.query, page: value > 1 ? String(value) : undefined } }) }
  })
  return { applied, draft, pending, apply, page }
}
