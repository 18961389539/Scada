import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { ElLoading } from 'element-plus'
import 'element-plus/theme-chalk/dark/css-vars.css'
import './style.css'
import App from './App.vue'
import router from './router'
import { i18n } from './i18n'
import { initAppearance } from './theme'

initAppearance()

// 发版后旧 hash 分块已被删除：懒加载路由/组件会 404，界面白屏。
// 同一会话 30 秒内只强刷一次，避免网络异常时陷入刷新循环。
function recoverFromStaleChunk(): void {
  const key = 'scada_chunk_reload_at'
  const last = Number(sessionStorage.getItem(key) ?? 0)
  if (Date.now() - last < 30_000) return
  sessionStorage.setItem(key, String(Date.now()))
  location.reload()
}
window.addEventListener('vite:preloadError', (event) => {
  event.preventDefault()
  recoverFromStaleChunk()
})
router.onError((error) => {
  if (/Failed to fetch dynamically imported module|Importing a module script failed|error loading dynamically imported module/i.test(String(error))) {
    recoverFromStaleChunk()
  }
})

const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(i18n)
app.use(ElLoading)
app.mount('#app')
