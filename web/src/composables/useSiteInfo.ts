import { onMounted, readonly, ref } from 'vue'
import { authApi, type SiteInfo } from '../api'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'

const siteInfo = ref<SiteInfo>()
const loading = ref(false), error = ref('')
let pending: Promise<void> | undefined

function reload(): Promise<void> {
  if (pending) return pending
  loading.value = true
  error.value = ''
  pending = authApi.siteInfo().then(value => {
    siteInfo.value = value
  }).catch(reason => {
    siteInfo.value = undefined
    error.value = errorMessage(reason, tr('ux.login.siteLoadFailed'))
  }).finally(() => {
    loading.value = false
    pending = undefined
  })
  return pending
}

export function useSiteInfo() {
  onMounted(() => { if (!siteInfo.value) void reload() })
  return { siteInfo: readonly(siteInfo), loading: readonly(loading), error: readonly(error), reload }
}
