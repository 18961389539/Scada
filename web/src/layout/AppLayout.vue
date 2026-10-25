<template>
  <el-container class="layout">
    <el-aside v-if="!isMobile" width="218px" class="aside">
      <div class="brand">
        <el-icon :size="24" color="#63e6be"><Monitor /></el-icon>
        <div>
          <div class="brand-title">{{ $t('appLayout.k80aeee') }}</div>
          <div class="brand-sub">{{ $t('appLayout.k9c1fc3') }}</div>
        </div>
      </div>
      <NavMenu />
      <div class="as-footer">v1.0 · .NET 10 / Vue 3</div>
    </el-aside>

    <el-drawer v-if="isMobile" v-model="drawer" direction="ltr" size="240px" :with-header="false" class="mobile-drawer" data-test="mobile-drawer">
      <div class="aside mobile-aside"><div class="brand"><el-icon :size="22" color="#63e6be"><Monitor /></el-icon><div class="brand-title">{{ $t('appLayout.k80aeee') }}</div></div><NavMenu /></div>
    </el-drawer>

    <el-container>
      <el-header class="header" :class="{ mobile: isMobile }">
        <div class="header-left">
          <el-button v-if="isMobile" text :aria-label="$t('appLayout.k708245')" data-test="mobile-menu" @click="drawer = true"><el-icon :size="20"><Menu /></el-icon></el-button>
          <span class="page-title">{{ route.meta.title ?? 'SCADA' }}</span>
          <el-tag v-if="health && !isMobile" :type="health.status === 'ok' ? 'success' : 'warning'" effect="light" size="small">
            {{ health.status === 'ok' ? $t('appLayout.kb77781') : $t('appLayout.kc6fdea') }}
          </el-tag>
          <el-tag v-if="health?.ha?.role === 'Standby'" type="danger" effect="dark" size="small" data-test="standby-banner">
            {{ $t('appLayout.k41b031') }}<template v-if="health.ha.primaryUrl">{{ $t('appLayout.k3d889a') }} <a :href="health.ha.primaryUrl" style="color: inherit">{{ health.ha.primaryUrl }}</a></template>
          </el-tag>
        </div>
        <div class="header-right">
          <el-switch v-if="!isMobile" v-model="soundEnabled" inline-prompt :active-text="$t('appLayout.kbf5ec8')" :inactive-text="$t('appLayout.kb085ec')" size="small" />
          <el-badge :value="activeAlarmCount" :hidden="activeAlarmCount === 0" :max="99">
            <el-button circle @click="router.push('/alarms')"><el-icon><Bell /></el-icon></el-button>
          </el-badge>
          <el-dropdown @command="onCommand">
            <span class="user-chip">
              <el-icon><UserFilled /></el-icon>
              <template v-if="!isMobile">{{ auth.user?.displayName ?? auth.user?.userName }}</template>
              <el-tag v-if="!isMobile" size="small" effect="plain">{{ roleLabels[auth.user?.role ?? 'Viewer'] }}</el-tag>
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
  </el-container>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElNotification } from 'element-plus'
import * as signalR from '@microsoft/signalr'
import { alarmApi, authApi, healthApi } from '../api'
import { TOKEN_KEY } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { AlarmEvent, Health } from '../types'
import NavMenu from './NavMenu.vue'
import { applyLocale, currentLocale, locales, type AppLocale } from '../i18n'
import { isMobile, loadClientPolicy } from '../mobile'
import { levelLabel, notificationType, roleLabels } from '../utils'
import { playAlarmOnce, setAlarmMute, startAlarmLoop, stopAlarmLoop } from '../alarmAudio'
import { annunciatorApi } from '../api/annunciators'
import { tr } from '../i18n'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const health = ref<Health | null>(null), drawer = ref(false), locale = currentLocale()
// 语言偏好保存在服务端（按用户），本机同步后重载。
async function switchLocale(value: AppLocale): Promise<void> {
  try { await auth.setLocale(value) } catch { /* 离线时仍允许本机切换 */ }
  applyLocale(value)
}
watch(() => route.fullPath, () => { drawer.value = false })
const activeAlarmCount = ref(0)
const soundEnabled = ref(localStorage.getItem('scada_sound') !== 'off')
let connection: signalR.HubConnection | null = null
let healthTimer: number | undefined

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

async function startHub(): Promise<void> {
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/realtime', { accessTokenFactory: () => localStorage.getItem(TOKEN_KEY) ?? '' })
    .withAutomaticReconnect()
    .build()

  connection.on('alarm', (payload: { action: string; alarm: AlarmEvent }) => {
    if (payload.action === 'raise') {
      ElNotification({
        title: tr('appLayout.k8e59ff', [levelLabel(payload.alarm.level)]),
        message: payload.alarm.message,
        type: notificationType(payload.alarm.level),
        duration: payload.alarm.level === 'Critical' ? 0 : 6000
      })
      if (soundEnabled.value) playAlarmOnce(payload.alarm.level)
      void refreshAnnunciatorSound()
    }
    refreshBadges()
  })

  try {
    await connection.start()
  } catch {
    // the rest of the UI still works without live pushes
  }

  // 授权被撤销时服务端会断开长连接；重连失败后再确认一次登录态，401 由 http 拦截器统一登出。
  connection.onclose(() => { void authApi.me().catch(() => {}) })
}

function onCommand(command: string): void {
  if (command.startsWith('locale:')) { void switchLocale(command.slice(7) as AppLocale); return }
  if (command === 'logout') {
    auth.logout()
    router.push('/login')
  }
}

onMounted(async () => {
  setAlarmMute(!soundEnabled.value)
  void loadClientPolicy()
  await refreshBadges()
  await startHub()
  healthTimer = window.setInterval(refreshBadges, 30000)
})

onBeforeUnmount(() => {
  stopAlarmLoop()
  if (healthTimer) window.clearInterval(healthTimer)
  connection?.stop()
})
</script>

<style scoped>
.layout {
  height: 100%;
}

.aside {
  background: #101b2d;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 18px 16px;
  color: #e7f0ff;
  border-bottom: 1px solid #1c2c44;
}

.brand-title {
  font-weight: 600;
  font-size: 16px;
  letter-spacing: 1px;
}

.brand-sub {
  color: #6d82a2;
  font-size: 11px;
}

.aside :deep(.el-menu) {
  border-right: none;
  flex: 1;
}

.as-footer {
  padding: 12px 16px;
  color: #52658a;
  font-size: 11px;
  border-top: 1px solid #1c2c44;
}

.header {
  height: 56px;
  background: #fff;
  border-bottom: 1px solid var(--scada-border);
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 18px;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 12px;
}

.page-title {
  font-size: 17px;
  font-weight: 600;
}

.header-right {
  display: flex;
  align-items: center;
  gap: 16px;
}

.user-chip {
  display: flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
  color: #33415c;
  outline: none;
}

.header.mobile { height: 48px; padding: 0 6px; }
.header.mobile .page-title { font-size: 15px; }
.header.mobile .header-right { gap: 8px; }
.mobile-aside { height: 100%; }
:global(.mobile-drawer .el-drawer__body) { padding: 0; background: #101b2d; }
.main {
  padding: 0;
  overflow: auto;
  background: var(--scada-bg);
}
</style>
