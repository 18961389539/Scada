<template>
  <div class="page">
    <div class="toolbar">
      <el-input v-model="search" clearable :placeholder="$t('productionBatches.search')" :aria-label="$t('productionBatches.search')" class="search" data-test="batch-search" @clear="reload" @keyup.enter="reload" />
      <el-button type="primary" :disabled="mobileBlocked || !canStart" data-test="batch-start" @click="openStart()">{{ $t('productionBatches.start') }}</el-button>
      <el-button :loading="loading" data-test="batch-refresh" @click="reload">{{ $t('productionBatches.refresh') }}</el-button>
    </div>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon />
    <el-table :data="rows" size="small" data-test="production-batches" :empty-text="loadError || $t('productionBatches.empty')" @row-click="openDetail">
      <el-table-column prop="batchNumber" :label="$t('productionBatches.batchNumber')" min-width="140" show-overflow-tooltip />
      <el-table-column prop="recipeName" :label="$t('productionBatches.recipe')" min-width="160" show-overflow-tooltip>
        <template #default="{ row }">{{ row.recipeName }} v{{ row.recipeVersion }}</template>
      </el-table-column>
      <el-table-column prop="sequenceName" :label="$t('productionBatches.sequence')" min-width="140" show-overflow-tooltip>
        <template #default="{ row }">{{ row.sequenceName || '—' }}</template>
      </el-table-column>
      <el-table-column :label="$t('productionBatches.status')" width="110">
        <template #default="{ row }"><el-tag size="small" :type="statusType[row.status]">{{ row.statusLabel }}</el-tag></template>
      </el-table-column>
      <el-table-column :label="$t('productionBatches.startedBy')" min-width="140" show-overflow-tooltip>
        <template #default="{ row }"><div>{{ row.startedBy }}</div><div class="muted tiny">{{ formatTime(row.startedTs) }}</div></template>
      </el-table-column>
      <el-table-column prop="message" :label="$t('productionBatches.message')" min-width="220" show-overflow-tooltip />
      <el-table-column width="160">
        <template #default="{ row }">
          <el-button size="small" link data-test="batch-open" @click.stop="openDetail(row)">{{ $t('productionBatches.detail') }}</el-button>
          <el-button v-if="canAbort(row)" size="small" type="danger" link :disabled="mobileBlocked" :data-test="`batch-abort-${row.id}`" @click.stop="abort(row)">{{ $t('productionBatches.abort') }}</el-button>
        </template>
      </el-table-column>
    </el-table>
    <el-button v-if="more" :loading="loadingOlder" data-test="batches-older" @click="loadOlder">{{ $t('designReview2.loadOlder') }}</el-button>

    <el-drawer v-model="detailOpen" :title="detailTitle" size="640px" data-test="batch-detail">
      <template v-if="detail">
        <p><strong>{{ $t('productionBatches.batchNumber') }}</strong> {{ detail.batch.batchNumber }} · {{ detail.batch.statusLabel }}</p>
        <p class="muted">{{ detail.batch.message }}</p>
        <h4>{{ $t('productionBatches.recipeResults') }}</h4>
        <el-table v-if="detail.download" :data="detail.download.results" size="small" data-test="batch-recipe-results">
          <el-table-column prop="name" :label="$t('productionBatches.parameter')" min-width="100" />
          <el-table-column prop="target" :label="$t('productionBatches.target')" width="90" />
          <el-table-column prop="readBack" :label="$t('productionBatches.readBack')" width="90" />
          <el-table-column prop="status" :label="$t('productionBatches.status')" width="90" />
          <el-table-column prop="message" :label="$t('productionBatches.message')" min-width="140" show-overflow-tooltip />
        </el-table>
        <p v-else class="muted">{{ $t('productionBatches.noDownload') }}</p>
        <h4>{{ $t('productionBatches.sequenceResults') }}</h4>
        <el-table v-if="detail.sequenceRun" :data="detail.sequenceRun.steps" size="small" data-test="batch-sequence-steps">
          <el-table-column prop="name" :label="$t('productionBatches.step')" min-width="120" />
          <el-table-column prop="status" :label="$t('productionBatches.status')" width="90" />
          <el-table-column prop="message" :label="$t('productionBatches.message')" min-width="160" show-overflow-tooltip />
        </el-table>
        <p v-else class="muted">{{ $t('productionBatches.noSequence') }}</p>
        <h4>{{ $t('productionBatches.audits') }}</h4>
        <el-table :data="detail.audits" size="small" data-test="batch-audits">
          <el-table-column :label="$t('productionBatches.time')" width="150"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
          <el-table-column prop="userName" :label="$t('productionBatches.operator')" width="110" show-overflow-tooltip />
          <el-table-column prop="action" :label="$t('productionBatches.action')" min-width="160" show-overflow-tooltip />
          <el-table-column prop="result" :label="$t('productionBatches.result')" min-width="180" show-overflow-tooltip />
        </el-table>
      </template>
    </el-drawer>

    <el-dialog v-model="startOpen" :title="$t('productionBatches.start')" width="560px" :close-on-click-modal="!submitting" data-test="batch-start-dialog">
      <el-form label-width="120px">
        <el-form-item :label="$t('productionBatches.batchNumber')" required>
          <el-input v-model="form.batchNumber" maxlength="100" data-test="batch-number" />
        </el-form-item>
        <el-form-item :label="$t('productionBatches.recipe')" required>
          <el-select v-model="form.recipeId" filterable :placeholder="$t('productionBatches.pickRecipe')" data-test="batch-recipe" @change="onRecipeChange">
            <el-option v-for="r in recipes" :key="r.id" :label="`${r.name} v${r.version}`" :value="r.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('productionBatches.sequence')">
          <el-select v-model="form.sequenceId" clearable filterable :placeholder="$t('productionBatches.optionalSequence')" data-test="batch-sequence">
            <el-option v-for="s in sequences" :key="s.id" :label="s.name" :value="s.id" />
          </el-select>
        </el-form-item>
      </el-form>
      <el-alert v-if="startError" :title="startError" type="error" :closable="false" show-icon style="margin-bottom: 8px" />
      <ul v-if="check?.refusals?.length" class="refusals">
        <li v-for="item in check.refusals" :key="item">{{ item }}</li>
      </ul>
      <template #footer>
        <el-button :disabled="submitting" @click="startOpen = false">{{ $t('productionBatches.cancel') }}</el-button>
        <el-button :loading="checking" :disabled="!form.recipeId" data-test="batch-check" @click="prepare">{{ $t('productionBatches.check') }}</el-button>
        <el-button type="primary" :loading="submitting" :disabled="!check?.ok" data-test="batch-confirm" @click="confirm">{{ $t('productionBatches.confirm') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { productionBatchApi, type ProductionBatch, type ProductionBatchCheck, type ProductionBatchDetail } from '../api/productionBatches'
import { recipeApi, type Recipe } from '../api/recipes'
import { controlApi, type ControlSequence } from '../api/control'
import { errorMessage } from '../api/http'
import { formatTime } from '../utils'
import { can } from '../access/policies'
import { useAuthStore } from '../stores/auth'
import { usePolling } from '../composables/usePolling'
import { controlBlockedOnMobile, loadClientPolicy } from '../mobile'
import { tr } from '../i18n'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const mobileBlocked = computed(() => controlBlockedOnMobile())
const canStart = computed(() => can(auth.user, 'Control'))
function canAbort(row: ProductionBatch): boolean {
  return can(auth.user, 'Control', row.areaId) && (row.status === 'Starting' || row.status === 'Running')
}

const statusType: Record<string, 'warning' | 'danger' | 'info' | 'success' | undefined> = {
  Starting: 'warning', Running: 'warning', Completed: 'success', Failed: 'danger', Aborted: 'info', NeedsConfirmation: 'warning'
}

const search = ref('')
const rows = ref<ProductionBatch[]>([])
const loading = ref(false)
const loadError = ref('')
const more = ref(false)
const next = ref<number | null>(null)
const loadingOlder = ref(false)
let disposed = false
let seq = 0
const recipes = ref<Recipe[]>([])
const sequences = ref<ControlSequence[]>([])

async function reload(): Promise<void> {
  const token = ++seq
  loading.value = true
  try {
    const page = await productionBatchApi.list(search.value.trim() || undefined)
    if (disposed || token !== seq) return
    rows.value = page.items
    more.value = page.more
    next.value = page.next
    loadError.value = ''
  } catch (e) {
    if (!disposed && token === seq) loadError.value = errorMessage(e, tr('productionBatches.loadFailed'))
  } finally { if (token === seq) loading.value = false }
}

async function loadOlder(): Promise<void> {
  if (loadingOlder.value || next.value == null) return
  loadingOlder.value = true
  try {
    const page = await productionBatchApi.list(search.value.trim() || undefined, next.value)
    const ids = new Set(rows.value.map(r => r.id))
    rows.value = [...rows.value, ...page.items.filter(r => !ids.has(r.id))]
    more.value = page.more
    next.value = page.next
  } catch (e) {
    loadError.value = errorMessage(e, tr('productionBatches.loadFailed'))
  } finally { loadingOlder.value = false }
}

const active = computed(() => rows.value.some(r => r.status === 'Starting' || r.status === 'Running'))
usePolling(reload, 1000, { enabled: () => !loading.value && active.value })

const detailOpen = ref(false)
const detail = ref<ProductionBatchDetail | null>(null)
const detailTitle = computed(() => detail.value ? `${tr('productionBatches.detail')} ${detail.value.batch.batchNumber}` : tr('productionBatches.detail'))

async function openDetail(row: ProductionBatch): Promise<void> {
  try {
    detail.value = await productionBatchApi.get(row.id)
    detailOpen.value = true
  } catch (e) {
    ElMessage.error(errorMessage(e, tr('productionBatches.loadFailed')))
  }
}

async function abort(row: ProductionBatch): Promise<void> {
  try {
    await ElMessageBox.confirm(tr('productionBatches.confirmAbort', [row.batchNumber]), tr('productionBatches.abort'), { type: 'warning' })
  } catch { return }
  try {
    await productionBatchApi.abort(row.id)
    await reload()
    if (detail.value?.batch.id === row.id) detail.value = await productionBatchApi.get(row.id)
  } catch (e) {
    ElMessage.error(errorMessage(e, tr('productionBatches.abortFailed')))
  }
}

const startOpen = ref(false)
const submitting = ref(false)
const checking = ref(false)
const startError = ref('')
const check = ref<ProductionBatchCheck | null>(null)
const form = reactive({ batchNumber: '', recipeId: null as number | null, sequenceId: null as number | null })

function onRecipeChange(): void { check.value = null }

async function openStart(preset?: { recipeId?: number; version?: number; sequenceId?: number }): Promise<void> {
  startError.value = ''
  check.value = null
  form.batchNumber = form.batchNumber || `B${Date.now().toString().slice(-8)}`
  try {
    await Promise.all([
      recipeApi.list().then(list => { recipes.value = list }).catch(() => { recipes.value = [] }),
      controlApi.sequences().then(list => { sequences.value = list }).catch(() => { sequences.value = [] })
    ])
  } catch { /* 列表失败时仍允许打开对话框 */ }
  if (preset?.recipeId) form.recipeId = preset.recipeId
  if (preset?.sequenceId) form.sequenceId = preset.sequenceId
  startOpen.value = true
}

async function prepare(): Promise<void> {
  if (!form.recipeId) return
  checking.value = true
  startError.value = ''
  try {
    const recipe = recipes.value.find(r => r.id === form.recipeId)
    check.value = await productionBatchApi.check(form.recipeId, recipe?.version ?? 0, form.sequenceId)
    if (!check.value.ok) startError.value = check.value.refusals.join('；') || tr('productionBatches.checkFailed')
  } catch (e) {
    check.value = null
    startError.value = errorMessage(e, tr('productionBatches.checkFailed'))
  } finally { checking.value = false }
}

async function confirm(): Promise<void> {
  if (!form.recipeId || !check.value?.ok || !form.batchNumber.trim()) {
    startError.value = tr('productionBatches.needNumber')
    return
  }
  if (controlBlockedOnMobile()) { startError.value = tr('screenRuntime.mobileControlBlocked'); return }
  submitting.value = true
  startError.value = ''
  try {
    const recipe = recipes.value.find(r => r.id === form.recipeId)
    const created = await productionBatchApi.start(form.batchNumber.trim(), form.recipeId, check.value.version || recipe?.version || 0, form.sequenceId, check.value.confirmationChallenge)
    startOpen.value = false
    await reload()
    await openDetail(created)
    await router.replace({ query: {} })
  } catch (e) {
    startError.value = errorMessage(e, tr('productionBatches.startFailed'))
    check.value = null
  } finally { submitting.value = false }
}

watch(() => route.query, q => {
  const recipeId = Number(q.recipeId)
  const sequenceId = Number(q.sequenceId)
  if (recipeId > 0 || sequenceId > 0) void openStart({ recipeId: recipeId > 0 ? recipeId : undefined, sequenceId: sequenceId > 0 ? sequenceId : undefined })
})

onMounted(async () => {
  await loadClientPolicy()
  await reload()
  const recipeId = Number(route.query.recipeId)
  const sequenceId = Number(route.query.sequenceId)
  if (recipeId > 0 || sequenceId > 0) await openStart({ recipeId: recipeId > 0 ? recipeId : undefined, sequenceId: sequenceId > 0 ? sequenceId : undefined })
})
onBeforeUnmount(() => { disposed = true; seq++ })
</script>

<style scoped>
.page { display: flex; flex-direction: column; gap: 12px; }
.toolbar { display: flex; gap: 8px; align-items: center; }
.search { max-width: 240px; }
.muted { color: var(--el-text-color-secondary); }
.tiny { font-size: 12px; }
.refusals { margin: 0 0 8px; padding-left: 1.2em; color: var(--el-color-danger); }
</style>
