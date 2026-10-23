<template><div class="page"><div class="toolbar"><el-button @click="router.push('/screens')">返回画面列表</el-button></div><ScreenRuntime :key="id" :screen-id="id" /></div></template>
<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ScreenRuntime from '../components/ScreenRuntime.vue'
const route = useRoute(), router = useRouter(), id = computed(() => Number(route.params.id))
</script>
