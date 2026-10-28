<template>
  <el-container class="layout">
    <el-aside v-if="!isMobile" :width="isMini ? '64px' : '220px'" class="aside">
      <NavMenu id="primary-navigation" :alarm-count="activeAlarmCount" :alarm-flood="alarmFlood" :collapsed="isMini" />
      <div v-if="!isMini" class="as-footer">v1.0 · .NET 10 / Vue 3</div>
    </el-aside>

    <el-drawer v-if="isMobile" v-model="drawer" direction="ltr" size="240px" :title="$t('nav.primary')" :with-header="false" class="mobile-drawer" data-test="mobile-drawer">
      <div class="aside mobile-aside">
        <div class="drawer-heading">
          <h2>{{ $t('nav.primary') }}</h2>
          <el-button text :aria-label="$t('nav.close')" data-test="mobile-menu-close" @click="drawer = false"><el-icon :size="20"><Close /></el-icon></el-button>
        </div>
        <NavMenu id="primary-navigation" :alarm-count="activeAlarmCount" :alarm-flood="alarmFlood" @navigate="drawer = false" />
      </div>
    </el-drawer>

    <el-container>
      <el-header ref="headerRef" class="header" :class="{ mobile: isMobile }">
        <div class="header-left">
          <el-button v-if="isMobile" text class="nav-toggle" :aria-label="$t('appLayout.k708245')" :aria-expanded="drawer" aria-controls="primary-navigation" data-test="mobile-menu" @click="drawer = true"><el-icon :size="20"><Menu /></el-icon></el-button>
          <el-button v-else text class="nav-toggle" :title="$t(isMini ? 'nav.expand' : 'nav.collapse')" :aria-label="$t(isMini ? 'nav.expand' : 'nav.collapse')" :aria-expanded="!isMini" aria-controls="primary-navigation" data-test="sidebar-toggle" @click="isMini = !isMini"><el-icon :size="20"><Expand v-if="isMini" /><Fold v-else /></el-icon></el-button>
          <div class="page-heading">
            <div class="site-context">
              <span v-if="siteInfo" class="site-name" :title="siteInfo.siteName" data-test="header-site-name">{{ siteInfo.siteName }}</span>
              <el-button v-else-if="siteError" link type="warning" :title="siteError" :aria-label="siteError" :loading="siteLoading" data-test="header-site-error" @click="reloadSite">{{ $t('ux.login.siteUnknown') }}</el-button>
              <span v-else>{{ $t('ux.login.siteLoading') }}</span>
              <el-tag v-if="siteInfo?.demoMode" type="warning" size="small" data-test="header-demo-mode">{{ $t('ux.login.demoBadge') }}</el-tag>
            </div>
            <h1 class="page-title" :title="typeof route.meta.title === 'string' ? route.meta.title : 'SCADA'">{{ route.meta.title ?? 'SCADA' }}</h1>
          </div>
        </div>
        <div v-if="showHeaderStatus" class="header-status" role="status" data-test="header-status">
          <el-tooltip v-if="(health || healthError) && !isMobile" :content="healthError || healthHint" placement="bottom">
            <el-tag :type="healthError ? 'info' : health?.status === 'ok' ? 'success' : 'warning'" effect="light" size="small" data-test="health-tag">
              {{ $t(healthError ? 'overviewStatus.serviceUnknown' : health?.status === 'ok' ? 'overviewStatus.serviceOk' : 'overviewStatus.serviceIssue') }}
            </el-tag>
          </el-tooltip>
          <el-tag v-if="alarmFlood && !isMobile" type="danger" size="small" data-test="header-alarm-flood">{{ $t('overviewStatus.flood') }}</el-tag>
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
            :aria-label="$t('nav.search')"
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
            <el-switch
              v-model="soundEnabled"
              class="sound-switch"
              :inline-prompt="!isMobile"
              :active-text="isMobile ? '' : $t('appLayout.kbf5ec8')"
              :inactive-text="isMobile ? '' : $t('appLayout.kb085ec')"
              size="small"
              data-test="sound-switch"
              :aria-label="$t('appLayout.soundHint')"
            />
          </el-tooltip>
          <el-tooltip :content="alarmCountTooltip" placement="bottom">
            <el-badge
              :class="{ 'has-count': activeAlarmCount > 0 }"
              :value="activeAlarmCount"
              :is-dot="!!alarmCountError && activeAlarmCount === 0"
              :hidden="activeAlarmCount === 0 && !alarmCountError"
              :max="99"
              :data-test="alarmCountError ? 'alarm-count-error' : undefined"
            >
              <el-button circle :type="alarmFlood ? 'danger' : undefined" data-test="alarm-bell" :aria-label="alarmCountTooltip" @click="router.push('/alarms')"><el-icon><Bell /></el-icon></el-button>
            </el-badge>
          </el-tooltip>
          <el-dropdown :trigger="isMobile ? 'click' : 'hover'" @command="onCommand">
            <span class="user-chip" :aria-label="$t('appLayout.accountMenu', [auth.user?.displayName ?? auth.user?.userName ?? ''])" data-test="account-menu">
              <el-icon><UserFilled /></el-icon>
              <template v-if="!isMobile">
                <span class="user-name">{{ auth.user?.displayName ?? auth.user?.userName }}</span>
                <span v-if="auth.user?.userName && auth.user?.userName !== auth.user?.displayName" class="user-id">@{{ auth.user.userName }}</span>
              </template>
              <el-icon v-if="!isMobile"><ArrowDown /></el-icon>
            </span>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item v-for="l in locales" :key="l.value" :command="`locale:${l.value}`" :disabled="l.value === locale" :data-test="`locale-${l.value}`">{{ l.label }}</el-dropdown-item>
                <el-dropdown-item divided command="theme:toggle" data-test="theme-toggle">{{ theme === 'dark' ? $t('appLayout.themeLight') : $t('appLayout.themeDark') }}</el-dropdown-item>
                <el-dropdown-item command="density:toggle" data-test="density-toggle">{{ density === 'compact' ? $t('appLayout.densityComfortable') : $t('appLayout.densityCompact') }}</el-dropdown-item>
                <el-dropdown-item divided command="logout">{{ $t('appLayout.k094774') }}</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </el-header>
      <el-main class="main" :style="mainStyle">
        <router-view :key="route.path" />
      </el-main>
    </el-container>
    <CommandPalette v-model="paletteOpen" />
    <LicenseGuard />
  </el-container>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { ComponentPublicInstance } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElNotification } from 'element-plus'
import { ArrowDown, Bell, Close, Expand, Fold, Menu, Search, UserFilled } from '@element-plus/icons-vue'
import { alarmApi, authApi, healthApi } from '../api'
import { useAuthStore } from '../stores/auth'
import type { AlarmEvent, Health } from '../types'
import NavMenu from './NavMenu.vue'
import CommandPalette from '../components/nav/CommandPalette.vue'
import LicenseGuard from '../components/LicenseGuard.vue'
import { applyLocale, currentLocale, locales, type AppLocale } from '../i18n'
import { isMobile, loadClientPolicy } from '../mobile'
import { applyDensity, applyTheme, currentDensity, currentTheme, type DensityMode, type ThemeMode } from '../theme'
import { levelLabel, notificationType } from '../utils'
import { handleAlarmPush, primeAlarmAudio, setAlarmMute, startAlarmLoop, stopAlarmLoop } from '../alarmAudio'
import { hubState as realtimeHubState, onHubClosed, onHubEvent, resetHub } from '../realtime/hub'
import { errorMessage } from '../api/http'
import { annunciatorApi } from '../api/annunciators'
import { usePolling } from '../composables/usePolling'
import { tr } from '../i18n'
import { pushRecent } from '../nav/recents'
import { useSiteInfo } from '../composables/useSiteInfo'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const { siteInfo, loading: siteLoading, error: siteError, reload: reloadSite } = useSiteInfo()
const headerRef = ref<ComponentPublicInstance | null>(null)
const headerHeight = ref(0)
const mainStyle = computed(() => headerHeight.value ? { '--scada-header-height': `${headerHeight.value}px` } : undefined)
let headerObserver: ResizeObserver | undefined

const health = ref<Health | null>(null), drawer = ref(false), locale = currentLocale()
const healthError = ref('')
const alarmFlood = ref(false)
const paletteOpen = ref(false)
watch(paletteOpen, (open) => { if (open) drawer.value = false })
const modKey = /Mac|iPhone|iPod|iPad/i.test(navigator.platform) ? '⌘' : 'Ctrl+' 
// 1100px 以下的笔记本仍用侧栏，但只留图标，避免正文被 220px 侧栏挤窄；768px 以下走抽屉。
const miniQuery = window.matchMedia('(min-width: 769px) and (max-width: 1120px)')
const isMini = ref(miniQuery.matches)
function onMiniChange(event: MediaQueryListEvent): void { isMini.value = event.matches }

const hubState = realtimeHubState()
// 只在异常状态下给文案；connected / connecting 不占位，避免正常时打扰
const hubIssue = computed(() => {
  if (hubState.value === 'reconnecting') return tr('appLayout.hubReconnecting')
  if (hubState.value === 'disconnected') return tr('appLayout.hubDisconnected')
  return ''
})
const showHeaderStatus = computed(() =>
  (!isMobile.value && (!!health.value || !!healthError.value || alarmFlood.value)) ||
  !!hubIssue.value || health.value?.ha?.role === 'Standby')

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
const alarmCountError = ref('')
const alarmCountTooltip = computed(() => {
  if (!alarmCountError.value) return [alarmFlood.value ? tr('overviewStatus.flood') : '', tr('appLayout.alarmHint', [activeAlarmCount.value])].filter(Boolean).join(' · ')
  if (activeAlarmCount.value > 0) return tr('appLayout.alarmCountStale', [activeAlarmCount.value])
  return alarmCountError.value
})
const soundEnabled = ref(localStorage.getItem('scada_sound') !== 'off')
const theme = ref<ThemeMode>(currentTheme())
const density = ref<DensityMode>(currentDensity())
let disposeAlarmHandler: (() => void) | undefined
let disposeHubClosed: (() => void) | undefined

watch(soundEnabled, (value) => {
  localStorage.setItem('scada_sound', value ? 'on' : 'off')
  setAlarmMute(!value)
  if (!value) stopAlarmLoop()
  else void refreshAnnunciatorSound()
})

let annunciatorTimer: number | undefined
let annunciatorBusy = false

async function refreshAnnunciatorSound(): Promise<void> {
  if (!soundEnabled.value) { stopAlarmLoop(); return }
  if (annunciatorBusy) return
  annunciatorBusy = true
  try {
    const statuses = await annunciatorApi.status()
    const active = statuses.filter((s) => s.enabled && s.soundKey && !s.silenced)
    const rank = (k: string | null) => k === 'critical' ? 3 : k === 'warning' ? 2 : k === 'info' ? 1 : 0
    const best = active.sort((a, b) => rank(b.soundKey) - rank(a.soundKey))[0]
    if (best?.soundKey) startAlarmLoop(best.soundKey)
    else stopAlarmLoop()
  } catch {
    // best-effort
  } finally {
    annunciatorBusy = false
  }
}

// 报警洪峰时每条推送都要求重算播音状态：300ms 合批，听不出延迟也不会把请求翻倍。
function scheduleAnnunciatorSound(): void {
  if (annunciatorTimer !== undefined) window.clearTimeout(annunciatorTimer)
  annunciatorTimer = window.setTimeout(() => {
    annunciatorTimer = undefined
    void refreshAnnunciatorSound()
  }, 300)
}

let badgeTimer: number | undefined
let badgeBusy = false
let badgeQueued = false

async function refreshBadges(): Promise<void> {
  if (badgeBusy) {
    badgeQueued = true
    return
  }
  badgeBusy = true
  try {
    const [, summary] = await Promise.all([
      healthApi.get().then(value => {
        health.value = value
        healthError.value = ''
      }, error => { healthError.value = errorMessage(error) }),
      alarmApi.summary().then(
        (value) => ({ ok: true as const, value }),
        (error: unknown) => ({ ok: false as const, error })
      )
    ])
    // The badge uses the plant-wide summary. Keep the last number when that request fails.
    // A first failure has no number: show a dot instead of an empty bell that looks like zero alarms.
    if (summary.ok) {
      activeAlarmCount.value = summary.value.unack
      alarmFlood.value = !!(summary.value.flood?.inFlood || summary.value.floodGuard?.anyInFlood)
      alarmCountError.value = ''
    } else alarmCountError.value = errorMessage(summary.error, tr('appLayout.alarmCountFailed'))
    scheduleAnnunciatorSound()
  } catch {
    // header badges are best-effort
  } finally {
    badgeBusy = false
  }
  if (badgeQueued) {
    badgeQueued = false
    await refreshBadges()
  }
}

// 顶栏徽标不需要毫秒级实时：推送合批 600ms，一波事件只查一次。
function scheduleBadges(): void {
  if (badgeTimer !== undefined) window.clearTimeout(badgeTimer)
  badgeTimer = window.setTimeout(() => {
    badgeTimer = undefined
    void refreshBadges()
  }, 600)
}

// Critical 弹窗不自动关闭，洪峰时几十条会铺满屏幕并挡住确认操作：只保留最新 3 条。
type NotificationHandle = ReturnType<typeof ElNotification>
const criticalToasts = new Set<NotificationHandle>()
const CRITICAL_TOAST_LIMIT = 3

function notifyRaise(alarm: AlarmEvent): void {
  const critical = alarm.level === 'Critical'
  const handle: NotificationHandle = ElNotification({
    title: tr('appLayout.k8e59ff', [levelLabel(alarm.level)]),
    message: alarm.message,
    type: notificationType(alarm.level),
    duration: critical ? 0 : 6000,
    ...(critical ? { onClose: () => { criticalToasts.delete(handle) } } : {})
  })
  if (!critical) return
  criticalToasts.add(handle)
  while (criticalToasts.size > CRITICAL_TOAST_LIMIT) {
    const oldest = criticalToasts.values().next().value
    if (!oldest) break
    criticalToasts.delete(oldest)
    oldest.close()
  }
}

function registerAlarmHandler(): void {
  disposeAlarmHandler = onHubEvent<{ action: string; alarm: AlarmEvent }>('alarm', (payload) => {
    if (payload.action === 'raise') notifyRaise(payload.alarm)
    // 全应用唯一发声点：静音、暂挂/抑制/抖动过滤、循环音去重都在这里处理，
    // 页面不再各自播放声音，避免共享连接后重复发声或绕过静音开关。
    handleAlarmPush(payload)
    scheduleAnnunciatorSound()
    scheduleBadges()
  })

  // 授权被撤销时服务端会断开长连接；关闭后再确认一次登录态，401 由 http 拦截器统一登出。
  disposeHubClosed = onHubClosed(() => {
    void authApi.me().catch(() => undefined)
  })
}

function onCommand(command: string): void {
  if (command.startsWith('locale:')) { void switchLocale(command.slice(7) as AppLocale); return }
  if (command === 'theme:toggle') {
    theme.value = theme.value === 'dark' ? 'light' : 'dark'
    applyTheme(theme.value)
    // ECharts 画在 canvas 上、不跟随 CSS 变量，重载让所有图表按新色板重建
    window.location.reload()
    return
  }
  if (command === 'density:toggle') {
    density.value = density.value === 'compact' ? 'comfortable' : 'compact'
    applyDensity(density.value)
    return
  }
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

// 30s 兜底轮询：连接断开期间漏掉的徽标状态靠它纠正；隐藏页暂停，回到前台补一次。
usePolling(refreshBadges, 30000)

onMounted(async () => {
  setAlarmMute(!soundEnabled.value)
  const header: unknown = headerRef.value?.$el
  if (header instanceof HTMLElement) {
    const measure = (): void => { headerHeight.value = header.getBoundingClientRect().height }
    measure()
    headerObserver = new ResizeObserver(measure)
    headerObserver.observe(header)
  }
  miniQuery.addEventListener('change', onMiniChange)
  window.addEventListener('pointerdown', unlockAudio, true)
  window.addEventListener('keydown', unlockAudio, true)
  // 尽早注册：注册即触发共享连接建立，避免 refreshBadges 期间漏掉启动瞬间的报警
  registerAlarmHandler()
  void loadClientPolicy()
  pushRecent(auth.user?.userName, route.fullPath)
  window.addEventListener('keydown', onGlobalKey)
  window.addEventListener('scada-open-palette', onOpenPalette)
  await refreshBadges()
})

onBeforeUnmount(() => {
  headerObserver?.disconnect()
  miniQuery.removeEventListener('change', onMiniChange)
  window.removeEventListener('pointerdown', unlockAudio, true)
  window.removeEventListener('keydown', unlockAudio, true)
  window.removeEventListener('keydown', onGlobalKey)
  window.removeEventListener('scada-open-palette', onOpenPalette)
  disposeAlarmHandler?.()
  disposeHubClosed?.()
  stopAlarmLoop()
  if (badgeTimer !== undefined) window.clearTimeout(badgeTimer)
  if (annunciatorTimer !== undefined) window.clearTimeout(annunciatorTimer)
  for (const toast of criticalToasts) toast.close()
  criticalToasts.clear()
  // 布局卸载即离开应用（登出）：断开共享连接，各页面的订阅已随组件卸载 dispose。
  resetHub()
})
</script>

<style scoped>
.layout {
  height: 100%;
}

/* 内层 el-container 必须是可收缩的 flex 列，否则 main 被内容撑高，表格页只能靠 calc(100vh - Npx) 硬算高度。 */
.layout > .el-container {
  min-height: 0;
  min-width: 0;
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

/* 折叠为图标栏时收紧内边距，保证 64px 宽度下图标居中且弹出层仍有文字。 */
.aside :deep(.el-menu--collapse .el-menu-item),
.aside :deep(.el-menu--collapse .el-sub-menu__title) {
  margin: 2px 6px;
  padding: 0 14px !important;
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
  height: auto;
  min-height: var(--scada-header-height);
  background: var(--scada-panel);
  border-bottom: 1px solid var(--scada-border);
  box-shadow: var(--scada-shadow-sm);
  display: grid;
  grid-template-columns: minmax(0, auto) minmax(0, 1fr) auto;
  gap: 6px 12px;
  align-items: center;
  padding: 0 var(--scada-space-5);
}

.header-left {
  grid-column: 1;
  grid-row: 1;
  display: flex;
  align-items: center;
  gap: var(--scada-space-3);
  min-width: 0;
  max-width: 264px;
}
.nav-toggle { flex-shrink: 0; width: 32px; height: 32px; padding: 0; }
.header-status {
  grid-column: 2;
  grid-row: 1;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 8px;
  min-width: 0;
}
.header-status :deep(.el-tag) { max-width: 100%; height: auto; min-height: 20px; white-space: normal; }
.header-status :deep(.el-tag__content) { min-width: 0; overflow-wrap: anywhere; white-space: normal; }

.page-title {
  margin: 0;
  font-size: var(--scada-fs-page);
  line-height: 1.2;
  font-weight: 600;
  color: var(--scada-text);
  letter-spacing: 0.01em;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.page-heading { min-width: 0; max-width: 220px; }
.site-context { display: flex; align-items: center; gap: 6px; min-width: 0; min-height: 16px; font-size: 11px; line-height: 16px; color: var(--scada-text-muted); }
.site-name { min-width: 0; max-width: 180px; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.site-context :deep(.el-tag) { height: 16px; line-height: 14px; flex-shrink: 0; }
.site-context :deep(.el-button) { padding: 0; font-size: 11px; }

.header-right {
  grid-column: 3;
  grid-row: 1;
  display: flex;
  align-items: center;
  gap: var(--scada-space-4);
}
.header-right :deep(.el-badge.has-count) { margin-right: var(--el-badge-size); }

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
.user-chip:focus-visible { outline: 2px solid var(--scada-accent); outline-offset: 2px; }

.header.mobile { grid-template-columns: minmax(0, 1fr) auto; min-height: 48px; padding: 0 6px; column-gap: 8px; }
.header.mobile .header-left { gap: 8px; max-width: none; }
.header.mobile .page-heading { flex: 1; }
.header.mobile .page-title { font-size: 15px; }
.header.mobile .header-right { grid-column: 2; gap: 6px; }
.header.mobile .header-status { grid-column: 1 / -1; grid-row: 2; padding-bottom: 6px; }
.header.mobile .header-right :deep(.el-button.is-circle) { width: 28px; height: 28px; padding: 0; }
.header.mobile .header-right :deep(.el-badge) { margin-right: 6px; }
.header.mobile .header-right :deep(.el-badge.has-count) { margin-right: calc(var(--el-badge-size) + 10px); }
.header.mobile .header-right :deep(.el-badge__content.is-fixed) { top: 10px; }
.header.mobile .user-chip { justify-content: center; width: 28px; height: 28px; padding: 0; }
.mobile-aside { height: 100%; background: var(--scada-dark); }
.drawer-heading {
  --el-fill-color-light: rgba(99, 230, 190, 0.08);
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-shrink: 0;
  min-height: 44px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--scada-dark-border);
  color: var(--scada-dark-text);
}
.drawer-heading h2 { margin: 0; font-size: 14px; font-weight: 600; }
.drawer-heading :deep(.el-button) { color: inherit; }
:global(.mobile-drawer .el-drawer__body) {
  padding: 0;
  background: var(--scada-dark);
}
.main {
  padding: 0;
  overflow: auto;
  min-height: 0;
  min-width: 0;
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
@media (max-width: 1280px) {
  .header:not(.mobile) { padding-inline: 12px; }
  .header-right { gap: 12px; }
  .nav-search-btn { width: 32px; padding: 0; }
  .nav-search-label, .nav-search-kbd, .user-id { display: none; }
  .user-name { max-width: 100px; }
}
</style>
