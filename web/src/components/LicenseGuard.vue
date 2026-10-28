<template>
  <el-dialog
    v-model="visible"
    :title="blocked ? $t('license.blockedTitle') : $t('license.reminderTitle')"
    width="560px"
    :close-on-click-modal="false"
    :close-on-press-escape="false"
    :show-close="false"
    append-to-body
    data-test="license-dialog"
  >
    <el-alert :type="blocked ? 'error' : 'warning'" :closable="false" :title="snapshot?.message ?? ''" show-icon />
    <p class="license-hint">{{ blocked ? $t('license.blockedHint') : $t('license.warningHint') }}</p>
    <el-descriptions v-if="snapshot" :column="1" border size="small">
      <el-descriptions-item :label="$t('license.machineId')">
        <code class="machine">{{ snapshot.machineId }}</code>
      </el-descriptions-item>
      <el-descriptions-item v-if="snapshot.expiresAt" :label="$t('license.expiresAt')">
        {{ formatTime(Date.parse(snapshot.expiresAt)) }}
      </el-descriptions-item>
      <el-descriptions-item :label="$t('license.daysRemaining')">{{ snapshot.daysRemaining }}</el-descriptions-item>
    </el-descriptions>
    <template #footer>
      <el-button v-if="!blocked" @click="dismissToday">{{ $t('license.later') }}</el-button>
      <el-button type="primary" @click="goActivate">{{ $t('license.goActivate') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { licenseApi, type LicenseSnapshot } from '../api/license'
import { usePolling } from '../composables/usePolling'
import { formatTime } from '../utils'

const WARN_KEY = 'scada_license_warned_on'
const auth = useAuthStore()
const router = useRouter()
const snapshot = ref<LicenseSnapshot | null>(null)
const visible = ref(false)
const blocked = computed(() => snapshot.value?.blocked === true)

function todayKey(): string {
  return new Date().toISOString().slice(0, 10)
}

function maybeWarn(): void {
  const current = snapshot.value
  if (!current) return
  if (current.blocked) {
    visible.value = true
    return
  }
  if (current.status === 'ExpiringSoon' || (current.status === 'Trial' && current.daysRemaining <= 15)) {
    if (localStorage.getItem(WARN_KEY) !== todayKey()) visible.value = true
  }
}

async function refresh(): Promise<void> {
  if (!auth.user) return
  try {
    snapshot.value = await licenseApi.get()
    maybeWarn()
  } catch {
    // 服务不可达或未登录：不打扰用户，等下一轮。
  }
}

function dismissToday(): void {
  if (blocked.value) return
  localStorage.setItem(WARN_KEY, todayKey())
  visible.value = false
}

function goActivate(): void {
  if (!blocked.value) localStorage.setItem(WARN_KEY, todayKey())
  visible.value = false
  void router.push('/license')
}

usePolling(refresh, 60_000)
onMounted(refresh)
</script>

<style scoped>
.license-hint {
  margin: 12px 0;
  color: var(--el-text-color-regular);
  line-height: 1.6;
}
.machine {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  letter-spacing: 0.5px;
}
</style>