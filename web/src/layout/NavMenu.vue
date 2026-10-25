<template>
<el-menu :default-active="route.path" router background-color="#101b2d" text-color="#9db0cb" active-text-color="#63e6be">
  <el-menu-item index="/overview"><el-icon><Odometer /></el-icon><span>总览</span></el-menu-item>
  <el-menu-item index="/realtime"><el-icon><DataLine /></el-icon><span>实时监视</span></el-menu-item>
  <el-menu-item index="/screens"><el-icon><Monitor /></el-icon><span>工艺画面</span></el-menu-item>
  <el-menu-item index="/console"><el-icon><Grid /></el-icon><span>运行布局</span></el-menu-item>
  <el-menu-item v-if="hasAny(auth.user, 'Control')" index="/control"><el-icon><Operation /></el-icon><span>现场操作</span></el-menu-item>
  <el-menu-item v-if="hasAny(auth.user, 'View')" index="/recipes"><el-icon><Tickets /></el-icon><span>配方管理</span></el-menu-item>
  <el-menu-item index="/trends"><el-icon><TrendCharts /></el-icon><span>趋势曲线</span></el-menu-item>
  <el-menu-item index="/alarms"><el-icon><Bell /></el-icon><span>报警</span></el-menu-item>
  <el-menu-item index="/alarm-analytics"><el-icon><PieChart /></el-icon><span>报警分析</span></el-menu-item>
  <el-menu-item index="/reports"><el-icon><Document /></el-icon><span>即席报表</span></el-menu-item>
  <el-menu-item index="/report-center"><el-icon><Files /></el-icon><span>报表中心</span></el-menu-item>
  <el-menu-item index="/calc-jobs"><el-icon><Timer /></el-icon><span>计算任务</span></el-menu-item>
  <el-sub-menu v-if="hasAny(auth.user, 'Configure')" index="config">
    <template #title><el-icon><Setting /></el-icon><span>组态配置</span></template>
    <el-menu-item index="/config/devices">设备配置</el-menu-item>
    <el-menu-item v-if="hasGlobal(auth.user, 'Configure')" index="/config/gateways">采集网关</el-menu-item>
    <el-menu-item v-if="hasGlobal(auth.user, 'Configure')" index="/config/hmi-library">图库与模板</el-menu-item>
    <el-menu-item v-if="hasGlobal(auth.user, 'Configure')" index="/config/layouts">运行布局与工作站</el-menu-item>
    <el-menu-item v-if="hasGlobal(auth.user, 'Configure')" index="/config/object-templates">设备对象与模板</el-menu-item>
    <el-menu-item index="/config/tags">测点配置</el-menu-item>
    <el-menu-item index="/config/project">工程管理</el-menu-item>
    <el-menu-item index="/config/calculations">计算测点</el-menu-item>
    <el-menu-item index="/config/rules">报警规则</el-menu-item>
    <el-menu-item index="/config/notifications">报警通知</el-menu-item>
  </el-sub-menu>
  <el-menu-item v-if="hasGlobal(auth.user, 'System')" index="/ops"><el-icon><Connection /></el-icon><span>运维与高可用</span></el-menu-item>
  <el-menu-item v-if="hasGlobal(auth.user, 'ManageUsers') || hasGlobal(auth.user, 'System') || hasGlobal(auth.user, 'Configure')" index="/system">
    <el-icon><Tools /></el-icon><span>系统管理</span>
  </el-menu-item>
</el-menu>
</template>

<script setup lang="ts">
import { useRoute } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { hasAny, hasGlobal } from '../utils'
const route = useRoute(), auth = useAuthStore()
</script>
