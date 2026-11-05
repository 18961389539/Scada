<template>
  <el-dialog v-model="visible" :title="$t('hmi.recipeDownloadTitle')" width="680px" append-to-body :close-on-click-modal="!submitting" :close-on-press-escape="!submitting" :show-close="!submitting" data-test="recipe-download-dialog" @closed="reset">
    <p v-if="recipe"><strong>{{ recipe.name }}</strong> <span class="muted">v{{ recipe.version }} · {{ recipe.templateName }}</span></p>
    <el-alert v-if="error" type="error" :title="error" :closable="false" show-icon data-test="recipe-dialog-error" />
    <el-alert v-if="check?.dualApproval" type="warning" :title="$t('hmi.recipeDualHint')" :closable="false" show-icon data-test="recipe-dialog-dual" />
    <div v-if="checking" v-loading="checking" class="recipe-checking" data-test="recipe-checking" />
    <el-button v-else-if="error && !check && !result" class="recipe-recheck" :disabled="submitting" data-test="recipe-dialog-recheck" @click="runCheck()">{{ $t('recipesView.retryCheck') }}</el-button>
    <el-table v-else-if="check" :data="check.parameters" size="small" data-test="recipe-check">
      <el-table-column prop="name" :label="$t('hmi.recipeParam')" width="120" show-overflow-tooltip />
      <el-table-column prop="tagName" :label="$t('hmi.recipeTag')" min-width="150" show-overflow-tooltip />
      <el-table-column :label="$t('hmi.recipeChange')" width="180" show-overflow-tooltip><template #default="{ row }">{{ fmt(row.current) }} → <strong>{{ row.target }}</strong> <span class="muted">{{ row.unit }}</span></template></el-table-column>
      <el-table-column :label="$t('hmi.recipeCheck')" min-width="180" show-overflow-tooltip><template #default="{ row }"><span v-if="row.refusals.length" class="tiny danger">{{ row.refusals.join('；') }}</span><el-tag v-else class="tag-quiet" size="small">{{ $t('hmi.recipeReady') }}</el-tag></template></el-table-column>
    </el-table>
    <div v-if="check && !check.ok" class="recipe-refusals" data-test="recipe-refusals">
      <p v-for="reason in check.refusals" :key="reason" class="danger tiny">{{ reason }}</p>
    </div>
    <el-alert v-if="result" :type="resultType" :title="resultMessage" :closable="false" show-icon data-test="recipe-result" />
    <template #footer>
      <el-button :disabled="submitting" @click="visible = false">{{ $t('hmi.cancel') }}</el-button>
      <el-button v-if="!result" type="primary" :loading="submitting" :disabled="!canConfirm" data-test="recipe-confirm" @click="confirm()">{{ $t('hmi.recipeDownload') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { recipeApi, type Recipe, type RecipeCheck, type RecipeDownload } from '../api/recipes'
import { commandErrorMessage, errorMessage } from '../api/http'
import { tr } from '../i18n'

const props = defineProps<{ modelValue: boolean; recipe: Recipe | null }>()
const emit = defineEmits<{ 'update:modelValue': [boolean]; completed: [RecipeDownload] }>()
const visible = computed({ get: () => props.modelValue, set: value => emit('update:modelValue', value) })
const check = ref<RecipeCheck | null>(null), checking = ref(false), submitting = ref(false), error = ref(''), result = ref<RecipeDownload | null>(null)
const canConfirm = computed(() => !!check.value?.ok && !check.value.refusals.length && !checking.value && !submitting.value)
const resultType = computed<'success' | 'warning' | 'error' | 'info'>(() => !result.value ? 'info'
  : result.value.status === 'Completed' ? 'success'
    : ['Pending', 'Running'].includes(result.value.status) ? 'warning'
      : ['Failed', 'PartiallyApplied', 'Rejected', 'Aborted'].includes(result.value.status) ? 'error' : 'info')
const resultMessage = computed(() => result.value ? result.value.message || result.value.statusLabel : '')
function fmt(value: number | null): string { return value == null ? '--' : value.toFixed(2) }
async function runCheck(): Promise<void> {
  const recipe = props.recipe
  if (!recipe) return
  checking.value = true; error.value = ''; result.value = null; check.value = null
  try { check.value = await recipeApi.check(recipe.id, recipe.version) }
  catch (e) { error.value = errorMessage(e, tr('hmi.recipeCheckFailed')) }
  finally { checking.value = false }
}
async function confirm(): Promise<void> {
  const recipe = props.recipe
  if (!recipe || !canConfirm.value) return
  submitting.value = true; error.value = ''
  try {
    const download = await recipeApi.download(recipe.id, recipe.version)
    result.value = download
    emit('completed', download)
    if (download.status === 'Pending') ElMessage.warning(tr('hmi.recipePending'))
    else if (download.status === 'Completed') ElMessage.success(tr('hmi.recipeCompleted'))
    else if (['Failed', 'PartiallyApplied', 'Rejected'].includes(download.status)) ElMessage.error(download.message || tr('hmi.recipeFailed'))
  } catch (e) {
    error.value = commandErrorMessage(e, tr('hmi.recipeFailed'))
    // 没有回执时参数可能已经写入：作废这次预检查，重发前必须重新检查，看到当前值再决定。
    if (!(e as { response?: unknown }).response) check.value = null
  }
  finally { submitting.value = false }
}
function reset(): void { check.value = null; checking.value = false; submitting.value = false; error.value = ''; result.value = null }
watch(() => [props.modelValue, props.recipe?.id, props.recipe?.version] as const, () => { if (props.modelValue) void runCheck() })
</script>

<style scoped>
.recipe-checking { min-height: 120px; }
.recipe-refusals { margin-top: 8px; }
.recipe-recheck { margin-top: 8px; }
</style>
