<template>
  <div class="license-page">
    <el-card v-loading="loading">
      <template #header>
        <div class="card-head">
          <span>{{ $t('license.title') }}</span>
          <el-tag v-if="snapshot" :type="statusType">{{ statusLabel }}</el-tag>
        </div>
      </template>

      <el-alert
        v-if="snapshot"
        :type="snapshot.blocked ? 'error' : snapshot.status === 'ExpiringSoon' ? 'warning' : 'success'"
        :closable="false"
        :title="snapshot.message"
        show-icon
      />

      <el-descriptions v-if="snapshot" :column="2" border class="details">
        <el-descriptions-item :label="$t('license.machineId')" :span="2">
          <code class="machine">{{ snapshot.machineId }}</code>
          <el-button text type="primary" size="small" @click="copyMachine">{{ $t('license.copy') }}</el-button>
        </el-descriptions-item>
        <el-descriptions-item :label="$t('license.licenseId')">{{ snapshot.licenseId || '-' }}</el-descriptions-item>
        <el-descriptions-item :label="$t('license.customer')">{{ snapshot.customer || '-' }}</el-descriptions-item>
        <el-descriptions-item :label="$t('license.expiresAt')">
          {{ snapshot.expiresAt ? formatTime(Date.parse(snapshot.expiresAt)) : '-' }}
        </el-descriptions-item>
        <el-descriptions-item :label="$t('license.daysRemaining')">{{ snapshot.daysRemaining }}</el-descriptions-item>
        <el-descriptions-item :label="$t('license.deviceLimit')">{{ limitText(snapshot.maxDevices) }}</el-descriptions-item>
        <el-descriptions-item :label="$t('license.tagLimit')">{{ limitText(snapshot.maxTags) }}</el-descriptions-item>
        <el-descriptions-item :label="$t('license.features')" :span="2">
          {{ snapshot.features.length ? snapshot.features.join(' / ') : $t('license.baseFeatures') }}
        </el-descriptions-item>
        <el-descriptions-item v-if="snapshot.status === 'Trial'" :label="$t('license.trialEndsAt')" :span="2">
          {{ formatTime(Date.parse(snapshot.trialEndsAt!)) }}
        </el-descriptions-item>
      </el-descriptions>

      <div v-if="canActivate" class="activate">
        <h3>{{ $t('license.activateTitle') }}</h3>
        <el-input
          v-model="code"
          type="textarea"
          :rows="4"
          :placeholder="$t('license.activatePlaceholder')"
          data-test="license-code"
        />
        <div class="actions">
          <el-button type="primary" :loading="activating" :disabled="!code.trim()" @click="activate">
            {{ $t('license.activate') }}
          </el-button>
        </div>
      </div>
      <el-alert v-else type="info" :closable="false" :title="$t('license.needAdmin')" />
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { licenseApi, type LicenseSnapshot, type LicenseStatus } from '../api/license'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import { canGlobal } from '../access/policies'
import { formatTime } from '../utils'
import { tr } from '../i18n'

const auth = useAuthStore()
const snapshot = ref<LicenseSnapshot | null>(null)
const loading = ref(false)
const activating = ref(false)
const code = ref('')
const canActivate = computed(() => canGlobal(auth.user, 'ManageUsers'))

const statusType = computed(() => {
  const status = snapshot.value?.status
  if (status === 'Active') return 'success'
  if (status === 'ExpiringSoon') return 'warning'
  if (status === 'Trial') return 'info'
  return 'danger'
})
const statusLabel = computed(() => {
  const status = snapshot.value?.status
  return status ? tr(`license.status.${status}`) : ''
})

function limitText(value?: number): string {
  return !value ? tr('license.unlimited') : String(value)
}

async function load(): Promise<void> {
  loading.value = true
  try {
    snapshot.value = await licenseApi.get()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('license.loadFailed')))
  } finally {
    loading.value = false
  }
}

async function activate(): Promise<void> {
  activating.value = true
  try {
    snapshot.value = await licenseApi.activate(code.value.trim())
    code.value = ''
    localStorage.removeItem('scada_license_warned_on')
    ElMessage.success(tr('license.activateSuccess'))
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('license.activateFailed')))
  } finally {
    activating.value = false
  }
}

async function copyMachine(): Promise<void> {
  if (!snapshot.value) return
  try {
    await navigator.clipboard.writeText(snapshot.value.machineId)
    ElMessage.success(tr('license.copied'))
  } catch {
    ElMessage.error(tr('license.copyFailed'))
  }
}

onMounted(load)
</script>

<style scoped>
.license-page {
  padding: 16px;
}
.card-head {
  display: flex;
  align-items: center;
  gap: 12px;
  font-weight: 600;
}
.details {
  margin-top: 16px;
}
.machine {
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  letter-spacing: 0.5px;
  margin-right: 8px;
}
.activate {
  margin-top: 24px;
}
.activate h3 {
  margin: 0 0 12px;
  font-size: 15px;
}
.actions {
  margin-top: 12px;
}
</style>