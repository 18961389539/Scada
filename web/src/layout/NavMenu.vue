<template>
<el-menu :default-active="route.path" router background-color="#101b2d" text-color="#9db0cb" active-text-color="#63e6be">
  <el-menu-item index="/overview"><el-icon><Odometer /></el-icon><span>{{ $t('navMenu.kc122e1') }}</span></el-menu-item>
  <el-menu-item index="/realtime"><el-icon><DataLine /></el-icon><span>{{ $t('navMenu.k445294') }}</span></el-menu-item>
  <el-menu-item index="/screens"><el-icon><Monitor /></el-icon><span>{{ $t('navMenu.k8a1840') }}</span></el-menu-item>
  <el-menu-item index="/console"><el-icon><Grid /></el-icon><span>{{ $t('navMenu.k0dd591') }}</span></el-menu-item>
  <el-menu-item v-if="hasAny(auth.user, 'Control')" index="/control"><el-icon><Operation /></el-icon><span>{{ $t('navMenu.k4eb87f') }}</span></el-menu-item>
  <el-menu-item v-if="hasAny(auth.user, 'View')" index="/recipes"><el-icon><Tickets /></el-icon><span>{{ $t('navMenu.k4b435b') }}</span></el-menu-item>
  <el-menu-item index="/trends"><el-icon><TrendCharts /></el-icon><span>{{ $t('navMenu.k402be4') }}</span></el-menu-item>
  <el-menu-item index="/alarms"><el-icon><Bell /></el-icon><span>{{ $t('navMenu.k9f45b3') }}</span></el-menu-item>
  <el-menu-item index="/alarm-analytics"><el-icon><PieChart /></el-icon><span>{{ $t('navMenu.kfce701') }}</span></el-menu-item>
  <el-menu-item index="/oee"><el-icon><DataAnalysis /></el-icon><span>{{ $t('navMenu.oee') }}</span></el-menu-item>
  <el-menu-item index="/reports"><el-icon><Document /></el-icon><span>{{ $t('navMenu.k2bdc15') }}</span></el-menu-item>
  <el-menu-item index="/report-center"><el-icon><Files /></el-icon><span>{{ $t('navMenu.kb41505') }}</span></el-menu-item>
  <el-menu-item index="/calc-jobs"><el-icon><Timer /></el-icon><span>{{ $t('navMenu.k8efdcf') }}</span></el-menu-item>
  <el-sub-menu v-if="hasAny(auth.user, 'Configure')" index="config">
    <template #title><el-icon><Setting /></el-icon><span>{{ $t('navMenu.k208160') }}</span></template>
    <el-menu-item index="/config/devices">{{ $t('navMenu.k845e2b') }}</el-menu-item>
    <el-menu-item v-if="hasGlobal(auth.user, 'Configure')" index="/config/gateways">{{ $t('navMenu.k626545') }}</el-menu-item>
    <el-menu-item v-if="hasGlobal(auth.user, 'Configure')" index="/config/hmi-library">{{ $t('navMenu.k5e9dd6') }}</el-menu-item>
    <el-menu-item v-if="hasGlobal(auth.user, 'Configure')" index="/config/layouts">{{ $t('navMenu.kbdee6f') }}</el-menu-item>
    <el-menu-item v-if="hasGlobal(auth.user, 'Configure')" index="/config/object-templates">{{ $t('navMenu.k5c3e7d') }}</el-menu-item>
    <el-menu-item index="/config/tags">{{ $t('navMenu.kb5c4a3') }}</el-menu-item>
    <el-menu-item index="/config/project">{{ $t('navMenu.k4d7ee6') }}</el-menu-item>
    <el-menu-item index="/config/calculations">{{ $t('navMenu.k4e0387') }}</el-menu-item>
    <el-menu-item index="/config/rules">{{ $t('navMenu.k1c7490') }}</el-menu-item>
    <el-menu-item index="/config/notifications">{{ $t('navMenu.k0ca38c') }}</el-menu-item>
  </el-sub-menu>
  <el-menu-item v-if="hasGlobal(auth.user, 'System')" index="/ops"><el-icon><Connection /></el-icon><span>{{ $t('navMenu.k74fa83') }}</span></el-menu-item>
  <el-menu-item v-if="hasGlobal(auth.user, 'ManageUsers') || hasGlobal(auth.user, 'System') || hasGlobal(auth.user, 'Configure')" index="/system">
    <el-icon><Tools /></el-icon><span>{{ $t('navMenu.k04ca1c') }}</span>
  </el-menu-item>
</el-menu>
</template>

<script setup lang="ts">
import { useRoute } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { hasAny, hasGlobal } from '../utils'
const route = useRoute(), auth = useAuthStore()
</script>
