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

const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(i18n)
app.use(ElLoading)
app.mount('#app')
