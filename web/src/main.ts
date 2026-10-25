import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import en from 'element-plus/es/locale/lang/en'
import 'element-plus/dist/index.css'
import './style.css'
import App from './App.vue'
import router from './router'
import { currentLocale, i18n } from './i18n'

const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(i18n)
// Element Plus 组件内置文案（分页、日期选择、空状态等）随界面语言切换；切换语言时整页重载。
app.use(ElementPlus, { locale: currentLocale() === 'en-US' ? en : zhCn })
app.mount('#app')
