<template>
  <div class="license-page">
    <el-card v-loading="loading">
      <template #header>
        <div class="card-head">
          <span>{{ $t('license.title') }}</span>
          <el-tag v-if="snapshot" :class="{ 'tag-quiet': snapshot.status === 'Active' }" :type="statusType">{{ statusLabel }}</el-tag>
        </div>
      </template>

      <p v-if="snapshot?.status === 'Active' && !snapshot.blocked" class="license-message">{{ snapshot.message }}</p>
      <el-alert
        v-else-if="snapshot"
        :type="licenseAlert"
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
          <el-button type="primary" :loading="activating" :disabled="!code.trim()" data-test="license-activate" @click="activate">
            {{ $t('license.activate') }}
          </el-button>
        </div>
      </div>
      <el-alert v-else type="info" :closable="false" :title="$t('license.needAdmin')" />
    </el-card>

    <el-dialog v-model="previewVisible" :title="$t('designReview2.licensePreviewTitle')" width="640px" :close-on-click-modal="false" data-test="license-preview">
      <el-alert v-for="warning in previewWarnings" :key="warning" :title="warning" type="warning" :closable="false" show-icon class="preview-warning" data-test="license-preview-warning" />
      <el-table v-if="preview" :data="previewRows" size="small" border>
        <el-table-column prop="label" :label="$t('designReview2.licenseItem')" width="140" />
        <el-table-column prop="current" :label="$t('designReview2.licenseCurrent')" min-width="180" />
        <el-table-column prop="next" :label="$t('designReview2.licenseNext')" min-width="180" />
      </el-table>
      <template #footer>
        <el-button :disabled="activating" @click="previewVisible = false">{{ $t('designReview2.licenseCancel') }}</el-button>
        <el-button type="primary" :loading="activating" data-test="license-confirm" @click="confirmActivate">{{ $t('designReview2.licenseConfirm') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { licenseApi, type LicensePreview, type LicenseSnapshot } from '../api/license'
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

const statusType = computed((): 'warning' | 'info' | 'danger' | undefined => {
  const status = snapshot.value?.status
  if (status === 'Active') return undefined
  if (status === 'ExpiringSoon') return 'warning'
  if (status === 'Trial') return 'info'
  return 'danger'
})
const licenseAlert = computed((): 'error' | 'warning' | 'info' => {
  if (snapshot.value?.blocked) return 'error'
  if (snapshot.value?.status === 'ExpiringSoon') return 'warning'
  if (snapshot.value?.status === 'Trial') return 'info'
  return 'error'
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

// 激活会整体替换现有授权：先校验并列出新旧差异，确认后才真正激活
const preview = ref<LicensePreview | null>(null), previewVisible = ref(false)
let previewedCode = ''
const features = (list: string[]) => list.length ? list.join(' / ') : tr('license.baseFeatures')
const expiryText = (iso: string | null | undefined) => iso ? formatTime(Date.parse(iso)) : '-'
const previewRows = computed(() => {
  const now = snapshot.value, next = preview.value
  if (!next) return []
  return [
    { label: tr('license.licenseId'), current: now?.licenseId || '-', next: next.licenseId || '-' },
    { label: tr('license.customer'), current: now?.customer || '-', next: next.customer || '-' },
    { label: tr('license.expiresAt'), current: now?.status === 'Trial' ? `${tr('license.status.Trial')} · ${expiryText(now.trialEndsAt)}` : expiryText(now?.expiresAt), next: expiryText(next.expiresAt) },
    { label: tr('license.deviceLimit'), current: limitText(now?.maxDevices), next: `${limitText(next.maxDevices)}${tr('designReview2.licenseInUse', [next.devices])}` },
    { label: tr('license.tagLimit'), current: limitText(now?.maxTags), next: `${limitText(next.maxTags)}${tr('designReview2.licenseInUse', [next.tags])}` },
    { label: tr('license.features'), current: now ? features(now.features) : '-', next: features(next.features) }
  ]
})
const previewWarnings = computed(() => {
  const now = snapshot.value, next = preview.value
  if (!next) return []
  const warnings: string[] = []
  if (next.maxDevices && next.devices > next.maxDevices) warnings.push(tr('designReview2.licenseOverDevices', [next.devices, next.maxDevices]))
  if (next.maxTags && next.tags > next.maxTags) warnings.push(tr('designReview2.licenseOverTags', [next.tags, next.maxTags]))
  const active = now && (now.status === 'Active' || now.status === 'ExpiringSoon') && now.expiresAt
  if (active && Date.parse(next.expiresAt) < Date.parse(now.expiresAt!)) warnings.push(tr('designReview2.licenseEarlier', [expiryText(now.expiresAt)]))
  const lost = active ? now.features.filter(f => !next.features.includes(f)) : []
  if (lost.length) warnings.push(tr('designReview2.licenseLostFeatures', [lost.join(' / ')]))
  if (active && now.customer && next.customer && now.customer !== next.customer) warnings.push(tr('designReview2.licenseOtherCustomer', [next.customer]))
  return warnings
})
async function activate(): Promise<void> {
  if (activating.value) return
  const value = code.value.trim()
  activating.value = true
  try {
    preview.value = await licenseApi.preview(value)
    previewedCode = value
    previewVisible.value = true
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('license.activateFailed')))
  } finally {
    activating.value = false
  }
}
async function confirmActivate(): Promise<void> {
  if (activating.value || !previewedCode) return
  activating.value = true
  try {
    snapshot.value = await licenseApi.activate(previewedCode)
    if (code.value.trim() === previewedCode) code.value = ''
    previewVisible.value = false; previewedCode = ''
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
.license-message {
  margin: 0;
  color: var(--scada-text-secondary);
  line-height: 1.5;
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
.preview-warning {
  margin-bottom: 8px;
}
</style>
