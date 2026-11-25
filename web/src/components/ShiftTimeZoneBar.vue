<template>
  <div class="shift-zone" data-test="shift-timezone">
    <span class="shift-zone-label">{{ $t('shiftZone.label') }}</span>
    <span v-if="loading" class="muted">{{ $t('pageState.loading') }}</span>
    <template v-else>
      <span class="shift-zone-current" data-test="shift-timezone-current">{{ currentText }}</span>
      <template v-if="!readonly">
        <el-select
          v-if="canEdit"
          v-model="selected"
          filterable
          clearable
          :placeholder="$t('shiftZone.placeholder')"
          style="width: 280px"
          :disabled="saving"
          data-test="shift-timezone-select"
          @change="onSelect"
        >
          <el-option :label="$t('shiftZone.serverLocalOption')" value="" />
          <el-option v-for="option in options" :key="option.id" :label="`${option.id} — ${option.display}`" :value="option.id" />
        </el-select>
        <span v-else class="muted">{{ $t('shiftZone.needGlobal') }}</span>
      </template>
    </template>
    <p v-if="!readonly && canEdit" class="muted shift-zone-hint">{{ $t('shiftZone.impactHint') }}</p>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="shift-timezone-error" />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { oeeApi, type ShiftTimeZoneOption, type ShiftTimeZoneView } from '../api/oee'
import { errorMessage } from '../api/http'
import { canGlobal } from '../access/policies'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'

withDefaults(defineProps<{ readonly?: boolean }>(), { readonly: false })

const auth = useAuthStore()
const canEdit = computed(() => canGlobal(auth.user, 'Configure'))
const loading = ref(false)
const saving = ref(false)
const error = ref('')
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

function revertSelection(): void {
  selected.value = view.value?.configured ?? ''
}

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    view.value = await oeeApi.shiftTimezone()
    revertSelection()
  } catch (err) {
    error.value = errorMessage(err, tr('shiftZone.loadFailed'))
  } finally {
    loading.value = false
  }
}

async function onSelect(value: string | null): Promise<void> {
  if (!canEdit.value || saving.value) {
    revertSelection()
    return
  }
  const next = value ? value : null
  const current = view.value?.configured ?? null
  if ((next ?? '') === (current ?? '')) return
  try {
    await ElMessageBox.confirm(tr('shiftZone.confirmBody'), tr('shiftZone.confirmTitle'), { type: 'warning' })
  } catch {
    revertSelection()
    return
  }
  saving.value = true
  error.value = ''
  try {
    view.value = await oeeApi.saveShiftTimezone(next)
    revertSelection()
    ElMessage.success(tr('shiftZone.saved'))
  } catch (err) {
    error.value = errorMessage(err, tr('shiftZone.saveFailed'))
    revertSelection()
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
.shift-zone-hint { width: 100%; margin: 0; font-size: 12px; line-height: 1.5; }
.shift-zone :deep(.el-alert) { width: 100%; }
</style>
