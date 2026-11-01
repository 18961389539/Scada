<template>
  <div class="page not-found">
    <section class="panel not-found-panel">
      <el-icon class="not-found-icon" :size="40"><QuestionFilled /></el-icon>
      <h2>{{ $t('uxShell.notFoundTitle') }}</h2>
      <p class="not-found-body">{{ $t('uxShell.notFoundBody') }}</p>
      <p v-if="attempted" class="not-found-path tiny muted"><code data-test="not-found-path">{{ attempted }}</code></p>
      <div class="not-found-actions">
        <el-button type="primary" data-test="not-found-home" @click="goHome">{{ $t('uxShell.notFoundBack') }}</el-button>
        <el-button data-test="not-found-search" @click="openPalette">{{ $t('uxShell.notFoundSearch') }}</el-button>
      </div>
    </section>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { QuestionFilled } from '@element-plus/icons-vue'
import { useAuthStore } from '../stores/auth'
import { defaultLandingPath } from '../access/policies'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
// 保留用户实际输入的地址，避免“打错字却不知道错在哪”
const attempted = computed(() => route.fullPath)

function goHome(): void {
  void router.replace(defaultLandingPath(auth.user))
}

// 与 AppLayout 的全局快捷键复用同一个人口，不重复实现面板状态
function openPalette(): void {
  window.dispatchEvent(new Event('scada-open-palette'))
}
</script>

<style scoped>
.not-found {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100%;
  padding: var(--scada-space-6);
}
.not-found-panel {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--scada-space-3);
  max-width: 560px;
  padding: var(--scada-space-6);
}
.not-found-icon { color: var(--scada-text-muted); }
.not-found-panel h2 {
  margin: 0;
  font-size: var(--scada-fs-page);
  line-height: 1.3;
  color: var(--scada-text);
}
.not-found-body {
  margin: 0;
  font-size: 14px;
  line-height: 1.7;
  color: var(--scada-text-secondary);
}
.not-found-path { margin: 0; }
.not-found-path code {
  padding: 2px 6px;
  border-radius: 4px;
  background: var(--scada-panel-muted);
  border: 1px solid var(--scada-border);
  font-family: var(--scada-font-mono);
  overflow-wrap: anywhere;
}
.not-found-actions { display: flex; flex-wrap: wrap; gap: var(--scada-space-3); margin-top: var(--scada-space-2); }
</style>
