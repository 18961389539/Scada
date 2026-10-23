<template>
  <el-container class="layout">
    <el-aside width="218px" class="aside">
      <div class="brand">
        <el-icon :size="24" color="#63e6be"><Monitor /></el-icon>
        <div>
          <div class="brand-title">SCADA 监控</div>
          <div class="brand-sub">数据采集与监视</div>
        </div>
      </div>
      <el-menu :default-active="route.path" router background-color="#101b2d" text-color="#9db0cb" active-text-color="#63e6be">
        <el-menu-item index="/overview"><el-icon><Odometer /></el-icon><span>总览</span></el-menu-item>
        <el-menu-item index="/realtime"><el-icon><DataLine /></el-icon><span>实时监视</span></el-menu-item>
        <el-menu-item index="/screens"><el-icon><Monitor /></el-icon><span>工艺画面</span></el-menu-item>
        <el-menu-item index="/trends"><el-icon><TrendCharts /></el-icon><span>趋势曲线</span></el-menu-item>
        <el-menu-item index="/alarms"><el-icon><Bell /></el-icon><span>报警</span></el-menu-item>
        <el-menu-item index="/reports"><el-icon><Document /></el-icon><span>报表</span></el-menu-item>
        <el-sub-menu v-if="auth.user?.role === 'Admin'" index="config">
          <template #title><el-icon><Setting /></el-icon><span>组态配置</span></template>
          <el-menu-item index="/config/devices">设备配置</el-menu-item>
          <el-menu-item index="/config/object-templates">设备对象与模板</el-menu-item>
          <el-menu-item index="/config/tags">测点配置</el-menu-item>
          <el-menu-item index="/config/rules">报警规则</el-menu-item>
        </el-sub-menu>
        <el-menu-item v-if="auth.user?.role === 'Admin'" index="/system">
          <el-icon><Tools /></el-icon><span>系统管理</span>
        </el-menu-item>
      </el-menu>
      <div class="as-footer">v1.0 · .NET 10 / Vue 3</div>
    </el-aside>

    <el-container>
      <el-header class="header">
        <div class="header-left">
          <span class="page-title">{{ route.meta.title ?? 'SCADA' }}</span>
          <el-tag v-if="health" :type="health.status === 'ok' ? 'success' : 'warning'" effect="light" size="small">
            {{ health.status === 'ok' ? '系统正常' : '部分设备离线' }}
          </el-tag>
        </div>
        <div class="header-right">
          <el-switch v-model="soundEnabled" inline-prompt active-text="声音开" inactive-text="声音关" size="small" />
          <el-badge :value="activeAlarmCount" :hidden="activeAlarmCount === 0" :max="99">
            <el-button circle @click="router.push('/alarms')"><el-icon><Bell /></el-icon></el-button>
          </el-badge>
          <el-dropdown @command="onCommand">
            <span class="user-chip">
              <el-icon><UserFilled /></el-icon>
              {{ auth.user?.displayName ?? auth.user?.userName }}
              <el-tag size="small" effect="plain">{{ roleLabels[auth.user?.role ?? 'Viewer'] }}</el-tag>
              <el-icon><ArrowDown /></el-icon>
            </span>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="logout">退出登录</el-dropdown-item>
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
import { alarmApi, healthApi } from '../api'
import { TOKEN_KEY } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { AlarmEvent, Health } from '../types'
import { levelLabel, notificationType, playAlarmSound, roleLabels } from '../utils'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const health = ref<Health | null>(null)
const activeAlarmCount = ref(0)
const soundEnabled = ref(localStorage.getItem('scada_sound') !== 'off')
let connection: signalR.HubConnection | null = null
let healthTimer: number | undefined

watch(soundEnabled, (value) => localStorage.setItem('scada_sound', value ? 'on' : 'off'))

async function refreshBadges(): Promise<void> {
  try {
    const [h, alarms] = await Promise.all([
      healthApi.get(),
      alarmApi.query({ activeOnly: true, limit: 500 })
    ])
    health.value = h
    activeAlarmCount.value = alarms.filter((x) => !x.ackTs).length
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
        title: `${levelLabel(payload.alarm.level)}报警`,
        message: payload.alarm.message,
        type: notificationType(payload.alarm.level),
        duration: payload.alarm.level === 'Critical' ? 0 : 6000
      })
      if (soundEnabled.value) playAlarmSound(payload.alarm.level)
    }
    refreshBadges()
  })

  try {
    await connection.start()
  } catch {
    // the rest of the UI still works without live pushes
  }
}

function onCommand(command: string): void {
  if (command === 'logout') {
    auth.logout()
    router.push('/login')
  }
}

onMounted(async () => {
  await refreshBadges()
  await startHub()
  healthTimer = window.setInterval(refreshBadges, 30000)
})

onBeforeUnmount(() => {
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

.main {
  padding: 0;
  overflow: auto;
  background: var(--scada-bg);
}
</style>
