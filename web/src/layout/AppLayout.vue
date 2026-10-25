<template>
  <el-container class="layout">
    <el-aside v-if="!isMobile" width="220px" class="aside">
      <NavMenu />
      <div class="as-footer">v1.0 · .NET 10 / Vue 3</div>
    </el-aside>

    <el-drawer v-if="isMobile" v-model="drawer" direction="ltr" size="240px" :with-header="false" class="mobile-drawer" data-test="mobile-drawer">
      <div class="aside mobile-aside"><NavMenu /></div>
    </el-drawer>

    <el-container>
      <el-header class="header" :class="{ mobile: isMobile }">
        <div class="header-left">
          <el-button v-if="isMobile" text :aria-label="$t('appLayout.k708245')" data-test="mobile-menu" @click="drawer = true"><el-icon :size="20"><Menu /></el-icon></el-button>
          <h1 class="page-title">{{ route.meta.title ?? 'SCADA' }}</h1>
          <el-tooltip v-if="health && !isMobile" :content="healthHint" placement="bottom">
            <el-tag :type="health.status === 'ok' ? 'success' : 'warning'" effect="light" size="small" data-test="health-tag">
              {{ health.status === 'ok' ? $t('appLayout.kb77781') : $t('appLayout.kc6fdea') }}
            </el-tag>
          </el-tooltip>
          <!-- 实时连接断开时必须让用户看见：否则页面看着正常、数据其实已经不再更新 -->
          <el-tooltip v-if="hubIssue" :content="$t('appLayout.hubHint')" placement="bottom">
            <el-tag type="danger" effect="dark" size="small" data-test="hub-state">{{ hubIssue }}</el-tag>
          </el-tooltip>
          <el-tag v-if="health?.ha?.role === 'Standby'" type="danger" effect="dark" size="small" data-test="standby-banner">
            {{ $t('appLayout.k41b031') }}<template v-if="health.ha.primaryUrl">{{ $t('appLayout.k3d889a') }} <a :href="health.ha.primaryUrl" style="color: inherit">{{ health.ha.primaryUrl }}</a></template>
          </el-tag>
        </div>
        <div class="header-right">
          <el-button
            v-if="!isMobile"
            class="nav-search-btn"
            data-test="nav-search"
            @click="paletteOpen = true"
          >
            <el-icon><Search /></el-icon>
            <span class="nav-search-label">{{ $t('nav.search') }}</span>
            <kbd class="nav-search-kbd">{{ modKey }}K</kbd>
          </el-button>
          <el-button v-else circle data-test="nav-search" :aria-label="$t('nav.search')" @click="paletteOpen = true">
            <el-icon><Search /></el-icon>
          </el-button>
          <!-- 移动端同样需要静音（手机浏览器也会播报警音），只是不显示开/关文字 -->
          <el-tooltip :content="$t('appLayout.soundHint')" placement="bottom">
            <el-switch v-model="soundEnabled" class="sound-switch" :inline-prompt="!isMobile" :active-text="$t('appLayout.kbf5ec8')" :inactive-text="$t('appLayout.kb085ec')" size="small" data-test="sound-switch" />
          </el-tooltip>
          <el-tooltip :content="$t('appLayout.alarmHint', [activeAlarmCount])" placement="bottom">
            <el-badge :value="activeAlarmCount" :hidden="activeAlarmCount === 0" :max="99">
              <el-button circle data-test="alarm-bell" @click="router.push('/alarms')"><el-icon><Bell /></el-icon></el-button>
            </el-badge>
          </el-tooltip>
          <el-dropdown @command="onCommand">
            <span class="user-chip">
              <el-icon><UserFilled /></el-icon>
              <template v-if="!isMobile">
                <span class="user-name">{{ auth.user?.displayName ?? auth.user?.userName }}</span>
                <span v-if="auth.user?.userName && auth.user?.userName !== auth.user?.displayName" class="user-id">@{{ auth.user.userName }}</span>
              </template>
              <el-icon><ArrowDown /></el-icon>
            </span>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item v-for="l in locales" :key="l.value" :command="`locale:${l.value}`" :disabled="l.value === locale" :data-test="`locale-${l.value}`">{{ l.label }}</el-dropdown-item>
                <el-dropdown-item divided command="logout">{{ $t('appLayout.k094774') }}</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </el-header>
      <el-main class="main">
        <router-view :key="route.path" />
      </el-main>
    </el-container>
    <CommandPalette v-model="paletteOpen" />
  </el-container>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElNotification } from 'element-plus'
import { ArrowDown, Bell, Menu, Search, UserFilled } from '@element-plus/icons-vue'
import { alarmApi, authApi, healthApi } from '../api'
import { useAuthStore } from '../stores/auth'
import type { AlarmEvent, Health } from '../types'
import NavMenu from './NavMenu.vue'
import CommandPalette from '../components/nav/CommandPalette.vue'
import { applyLocale, currentLocale, locales, type AppLocale } from '../i18n'
import { isMobile, loadClientPolicy } from '../mobile'
import { levelLabel, notificationType } from '../utils'
import { handleAlarmPush, primeAlarmAudio, setAlarmMute, startAlarmLoop, stopAlarmLoop } from '../alarmAudio'
import { hubState as realtimeHubState, onHubClosed, onHubEvent, resetHub } from '../realtime/hub'
import { annunciatorApi } from '../api/annunciators'
import { tr } from '../i18n'
import { pushRecent } from '../nav/recents'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const health = ref<Health | null>(null), drawer = ref(false), locale = currentLocale()
const paletteOpen = ref(false)
const modKey = /Mac|iPhone|iPod|iPad/i.test(navigator.platform) ? '⌘' : 'Ctrl+' 

const hubState = realtimeHubState()
// 只在异常状态下给文案；connected / connecting 不占位，避免正常时打扰
const hubIssue = computed(() => {
  if (hubState.value === 'reconnecting') return tr('appLayout.hubReconnecting')
  if (hubState.value === 'disconnected') return tr('appLayout.hubDisconnected')
  return ''
})

// 健康标签的补充信息：设备在线数、总线积压、磁盘余量，比只看「系统正常」有用
const healthHint = computed(() => {
  const h = health.value
  if (!h) return ''
  return tr('appLayout.healthDetail', [h.onlineDevices, h.totalDevices, h.busBacklog, h.disk.freeGb.toFixed(1)])
})
// 语言偏好保存在服务端（按用户），本机同步后重载。
async function switchLocale(value: AppLocale): Promise<void> {
  try { await auth.setLocale(value) } catch { /* 离线时仍允许本机切换 */ }
  applyLocale(value)
}
watch(() => route.fullPath, (path) => {
  drawer.value = false
  pushRecent(auth.user?.userName, path)
})
const activeAlarmCount = ref(0)
const soundEnabled = ref(localStorage.getItem('scada_sound') !== 'off')
let healthTimer: number | undefined
let disposeAlarmHandler: (() => void) | undefined
let disposeHubClosed: (() => void) | undefined

watch(soundEnabled, (value) => {
  localStorage.setItem('scada_sound', value ? 'on' : 'off')
  setAlarmMute(!value)
  if (!value) stopAlarmLoop()
  else void refreshAnnunciatorSound()
})

async function refreshAnnunciatorSound(): Promise<void> {
  if (!soundEnabled.value) { stopAlarmLoop(); return }
  try {
    const statuses = await annunciatorApi.status()
    const active = statuses.filter((s) => s.enabled && s.soundKey && !s.silenced)
    const rank = (k: string | null) => k === 'critical' ? 3 : k === 'warning' ? 2 : k === 'info' ? 1 : 0
    const best = active.sort((a, b) => rank(b.soundKey) - rank(a.soundKey))[0]
    if (best?.soundKey) startAlarmLoop(best.soundKey)
    else stopAlarmLoop()
  } catch {
    // best-effort
  }
}

async function refreshBadges(): Promise<void> {
  try {
    const [h, alarms] = await Promise.all([
      healthApi.get(),
      alarmApi.query({ activeOnly: true, limit: 500 })
    ])
    health.value = h
    activeAlarmCount.value = alarms.filter((x) => !x.ackTs).length
    void refreshAnnunciatorSound()
  } catch {
    // header badges are best-effort
  }
}

function registerAlarmHandler(): void {
  disposeAlarmHandler = onHubEvent<{ action: string; alarm: AlarmEvent }>('alarm', (payload) => {
    if (payload.action === 'raise') {
      ElNotification({
        title: tr('appLayout.k8e59ff', [levelLabel(payload.alarm.level)]),
        message: payload.alarm.message,
        type: notificationType(payload.alarm.level),
        duration: payload.alarm.level === 'Critical' ? 0 : 6000
      })
      void refreshAnnunciatorSound()
    }
    // 全应用唯一发声点：静音、暂挂/抑制/抖动过滤、循环音去重都在这里处理，
    // 页面不再各自播放声音，避免共享连接后重复发声或绕过静音开关。
    handleAlarmPush(payload)
    refreshBadges()
  })

  // 授权被撤销时服务端会断开长连接；关闭后再确认一次登录态，401 由 http 拦截器统一登出。
  disposeHubClosed = onHubClosed(() => {
    void authApi.me().catch(() => undefined)
  })
}

function onCommand(command: string): void {
  if (command.startsWith('locale:')) { void switchLocale(command.slice(7) as AppLocale); return }
  if (command === 'logout') {
    auth.logout()
    router.push('/login')
  }
}

function onGlobalKey(e: KeyboardEvent): void {
  const meta = e.ctrlKey || e.metaKey
  if (meta && (e.key === 'k' || e.key === 'K')) {
    e.preventDefault()
    paletteOpen.value = !paletteOpen.value
  }
}

function onOpenPalette(): void {
  paletteOpen.value = true
}

// 浏览器要求用户手势后才能播放音频：首次点击/按键时解锁 AudioContext，
// 否则页面加载后第一条报警推送没有手势、会被自动播放策略拦截（表现为首次不响）。
function unlockAudio(): void {
  primeAlarmAudio()
  window.removeEventListener('pointerdown', unlockAudio, true)
  window.removeEventListener('keydown', unlockAudio, true)
}

onMounted(async () => {
  setAlarmMute(!soundEnabled.value)
  window.addEventListener('pointerdown', unlockAudio, true)
  window.addEventListener('keydown', unlockAudio, true)
  // 尽早注册：注册即触发共享连接建立，避免 refreshBadges 期间漏掉启动瞬间的报警
  registerAlarmHandler()
  void loadClientPolicy()
  pushRecent(auth.user?.userName, route.fullPath)
  window.addEventListener('keydown', onGlobalKey)
  window.addEventListener('scada-open-palette', onOpenPalette)
  await refreshBadges()
  healthTimer = window.setInterval(refreshBadges, 30000)
})

onBeforeUnmount(() => {
  window.removeEventListener('pointerdown', unlockAudio, true)
  window.removeEventListener('keydown', unlockAudio, true)
  window.removeEventListener('keydown', onGlobalKey)
  window.removeEventListener('scada-open-palette', onOpenPalette)
  disposeAlarmHandler?.()
  disposeHubClosed?.()
  stopAlarmLoop()
  if (healthTimer) window.clearInterval(healthTimer)
  // 布局卸载即离开应用（登出）：断开共享连接，各页面的订阅已随组件卸载 dispose。
  resetHub()
})
</script>

<style scoped>
.layout {
  height: 100%;
}

.aside {
  background: var(--scada-dark);
  display: flex;
  flex-direction: column;
  overflow: hidden;
  border-right: 1px solid var(--scada-dark-border);
}

.aside :deep(.nav-wrap) {
  flex: 1;
  min-height: 0;
}
.aside :deep(.el-menu) {
  border-right: none;
  flex: 1;
  overflow: auto;
  background: transparent !important;
  padding: 8px 0 12px;
}

.aside :deep(.el-menu-item),
.aside :deep(.el-sub-menu__title) {
  margin: 2px 10px;
  border-radius: var(--scada-radius-sm);
  height: 42px;
  line-height: 42px;
}

.aside :deep(.el-menu-item:hover),
.aside :deep(.el-sub-menu__title:hover) {
  background: rgba(99, 230, 190, 0.08) !important;
}

.aside :deep(.el-menu-item.is-active) {
  background: rgba(99, 230, 190, 0.14) !important;
  color: var(--scada-accent) !important;
  font-weight: 600;
}

.aside :deep(.el-sub-menu .el-menu-item) {
  min-width: auto;
  padding-left: 40px !important;
  font-size: 13px;
}

.aside :deep(.el-menu::-webkit-scrollbar) {
  width: 8px;
}
.aside :deep(.el-menu::-webkit-scrollbar-thumb) {
  background: rgba(232, 238, 248, 0.16);
  border-radius: 4px;
}
.aside :deep(.el-menu::-webkit-scrollbar-track) {
  background: transparent;
}

.as-footer {
  padding: 12px 18px;
  color: var(--scada-dark-faint);
  font-size: var(--scada-fs-tiny);
  border-top: 1px solid var(--scada-dark-border);
  letter-spacing: 0.02em;
}

.header {
  height: var(--scada-header-height);
  background: var(--scada-panel);
  border-bottom: 1px solid var(--scada-border);
  box-shadow: var(--scada-shadow-sm);
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 var(--scada-space-5);
}

.header-left {
  display: flex;
  align-items: center;
  gap: var(--scada-space-3);
  min-width: 0;
}

.page-title {
  margin: 0;
  font-size: var(--scada-fs-page);
  font-weight: 600;
  color: var(--scada-text);
  letter-spacing: 0.01em;
  white-space: nowrap;
}

.header-right {
  display: flex;
  align-items: center;
  gap: var(--scada-space-4);
}

.user-chip {
  display: flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
  color: var(--scada-text-secondary);
  outline: none;
  padding: 4px 6px;
  border-radius: var(--scada-radius-sm);
}

.user-name {
  font-size: 13px;
  color: var(--scada-text);
  max-width: 140px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.user-id {
  font-family: var(--scada-font-mono);
  font-size: 11px;
  color: var(--scada-text-muted);
}

.sound-switch {
  --el-switch-on-color: #2f6f5e;
  --el-switch-off-color: #b9c6d8;
}

.user-chip:hover {
  background: var(--scada-panel-muted);
  color: var(--scada-text);
}

.header.mobile { height: 48px; padding: 0 6px; }
.header.mobile .page-title { font-size: 15px; }
.header.mobile .header-right { gap: 8px; }
.mobile-aside { height: 100%; background: var(--scada-dark); }
:global(.mobile-drawer .el-drawer__body) {
  padding: 0;
  background: var(--scada-dark);
}
.main {
  padding: 0;
  overflow: auto;
  background: var(--scada-bg);
}
.nav-search-btn {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  height: 32px;
  padding: 0 10px 0 12px;
  border-radius: 8px;
  background: var(--scada-panel-muted);
  border: 1px solid var(--scada-border);
  color: var(--scada-text-secondary);
}
.nav-search-btn:hover {
  color: var(--scada-text);
  border-color: var(--scada-accent);
}
.nav-search-label { font-size: 13px; }
.nav-search-kbd {
  font: 11px/1.2 ui-monospace, SFMono-Regular, Menlo, monospace;
  padding: 2px 5px;
  border-radius: 4px;
  background: var(--scada-panel);
  border: 1px solid var(--scada-border);
  color: var(--scada-text-muted);
  margin-left: 4px;
}
</style>
