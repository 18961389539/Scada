<template>
  <div class="shift-zone" data-test="shift-timezone">
    <span class="shift-zone-label">{{ $t('shiftZone.label') }}</span>
    <span v-if="loading" class="muted">{{ $t('pageState.loading') }}</span>
    <template v-else>
      <span class="shift-zone-current" data-test="shift-timezone-current">{{ currentText }}</span>
      <el-select
        v-if="canEdit"
        v-model="selected"
        filterable
        clearable
        :placeholder="$t('shiftZone.placeholder')"
        style="width: 280px"
        :disabled="saving"
        data-test="shift-timezone-select"
        @change="save"
      >
        <el-option :label="$t('shiftZone.serverLocalOption')" value="" />
        <el-option v-for="option in options" :key="option.id" :label="`${option.id} — ${option.display}`" :value="option.id" />
      </el-select>
      <span v-else class="muted">{{ $t('shiftZone.needGlobal') }}</span>
    </template>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="shift-timezone-error" />
    <el-alert v-if="notPersisted" :title="$t('shiftZone.notPersisted')" type="warning" :closable="false" show-icon data-test="shift-timezone-unpersisted" />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { oeeApi, type ShiftTimeZoneOption, type ShiftTimeZoneView } from '../api/oee'
import { errorMessage } from '../api/http'
import { canGlobal } from '../access/policies'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'

const auth = useAuthStore()
const canEdit = computed(() => canGlobal(auth.user, 'Configure'))
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const notPersisted = ref(false)
const view = ref<ShiftTimeZoneView | null>(null)
const selected = ref('')
const options = computed(() => view.value?.options ?? [] as ShiftTimeZoneOption[])
const currentText = computed(() => {
  const current = view.value
  if (!current) return ''
  return current.usesServerLocal
    ? tr('shiftZone.serverLocal', [current.effectiveId])
    : tr('shiftZone.configured', [current.configured ?? current.effectiveId])
})

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    view.value = await oeeApi.shiftTimezone()
    selected.value = view.value.configured ?? ''
    notPersisted.value = false
  } catch (err) {
    error.value = errorMessage(err, tr('shiftZone.loadFailed'))
  } finally {
    loading.value = false
  }
}

async function save(value: string | null): Promise<void> {
  if (!canEdit.value || saving.value) return
  saving.value = true
  error.value = ''
  try {
    view.value = await oeeApi.saveShiftTimezone(value ? value : null)
    selected.value = view.value.configured ?? ''
    notPersisted.value = !view.value.persisted
    ElMessage.success(tr('shiftZone.saved'))
  } catch (err) {
    error.value = errorMessage(err, tr('shiftZone.saveFailed'))
    selected.value = view.value?.configured ?? ''
  } finally {
    saving.value = false
  }
}

onMounted(() => { void load() })
</script>

<style scoped>
.shift-zone { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; margin: 8px 0 12px; }
.shift-zone-label { font-weight: 600; }
.shift-zone-current { color: var(--el-text-color-regular); }
.muted { color: var(--el-text-color-secondary); }
.shift-zone :deep(.el-alert) { width: 100%; }
</style>
