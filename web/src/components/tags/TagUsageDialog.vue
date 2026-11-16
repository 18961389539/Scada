<template>
  <el-dialog v-model="visible" :title="$t('tagUsage.title')" width="680px" data-test="tag-usage-dialog" @closed="reset">
    <div class="usage-head">
      <strong>{{ tag?.name }}</strong><span class="tiny muted">#{{ tag?.id }}</span><span class="spacer" />
      <el-button size="small" :loading="loading" data-test="tag-usage-refresh" @click="loadUsage">{{ $t('tagUsage.refresh') }}</el-button>
    </div>
    <p class="tiny muted">{{ $t('tagUsage.hint') }}</p>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="tag-usage-error" />
    <el-alert v-if="usage?.unreadable" :title="$t('tagUsage.unreadable')" type="warning" :closable="false" show-icon />
    <div v-loading="loading" class="usage-body">
      <el-empty v-if="!loading && !usage?.references.length" :description="$t('tagUsage.empty')" :image-size="48" />
      <section v-for="group in groups" :key="group.key" class="usage-group" :data-test="`tag-usage-${group.key}`">
        <h4>{{ group.label }} <small>{{ group.items.length }}</small></h4>
        <ul>
          <li v-for="item in group.items" :key="`${group.key}-${item.id}-${item.name}`">
            <span>{{ item.name }}<small v-if="item.detail"> · {{ item.detail }}</small></span>
            <el-tag v-if="!item.editable" size="small" type="info">{{ $t('tagUsage.noPermission') }}</el-tag>
            <el-tag v-else-if="item.requiresRepublish" size="small" type="warning">{{ $t('tagUsage.republish') }}</el-tag>
          </li>
        </ul>
      </section>
    </div>
    <el-alert v-if="blocked" :title="$t('tagUsage.calculatedBlocked')" type="warning" :closable="false" show-icon />
    <el-alert v-else-if="hasForbidden" :title="$t('tagUsage.notEditable')" type="warning" :closable="false" show-icon />
    <el-alert v-if="notice" :title="notice" :type="published.length ? 'warning' : 'success'" :closable="false" show-icon data-test="tag-usage-result">
      <ul v-if="published.length"><li v-for="item in published" :key="item.id">{{ item.name }}</li></ul>
    </el-alert>
    <section v-if="usage?.references.length" class="usage-replace">
      <strong>{{ $t('tagUsage.target') }}</strong>
      <el-select v-model="targetId" filterable clearable :placeholder="$t('tagUsage.targetPlaceholder')" data-test="tag-usage-target" style="width: 100%">
        <el-option v-for="item in targets" :key="item.id" :label="`#${item.id} · ${item.name}`" :value="item.id" />
      </el-select>
      <p class="tiny muted">{{ $t('tagUsage.publishedHint') }}</p>
      <el-button type="primary" :loading="busy" :disabled="!targetId || blocked || hasForbidden" data-test="tag-usage-replace" @click="rebind">{{ $t('tagUsage.replace') }}</el-button>
    </section>
    <template #footer><el-button @click="visible = false">{{ $t('tagUsage.close') }}</el-button></template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { tagApi, type TagRebindResult, type TagUsage, type TagUsageRef } from '../../api'
import { errorMessage } from '../../api/http'
import type { Tag } from '../../types'
import { tr } from '../../i18n'

const props = defineProps<{ canConfigureTag: (tag: Tag) => boolean; fallbackCandidates: Tag[] }>()
const emit = defineEmits<{ changed: [] }>()

const visible = ref(false)
const tag = ref<Tag | null>(null)
const usage = ref<TagUsage | null>(null)
const loading = ref(false), error = ref('')
const targetId = ref<number | null>(null), busy = ref(false)
const candidates = ref<Tag[]>([])
const notice = ref(''), published = ref<TagUsageRef[]>([])

const categoryLabels: Record<string, string> = {
  screenDraft: tr('tagUsage.category.screenDraft'),
  screenPublished: tr('tagUsage.category.screenPublished'),
  template: tr('tagUsage.category.template'),
  alarmRule: tr('tagUsage.category.alarmRule'),
  sequence: tr('tagUsage.category.sequence'),
  recipe: tr('tagUsage.category.recipe'),
  calculatedTag: tr('tagUsage.category.calculatedTag')
}
const groups = computed(() => {
  const grouped = new Map<string, TagUsageRef[]>()
  for (const reference of usage.value?.references ?? []) {
    const items = grouped.get(reference.category) ?? []
    items.push(reference)
    grouped.set(reference.category, items)
  }
  return [...grouped].map(([key, items]) => ({ key, label: categoryLabels[key] ?? key, items }))
})
const targets = computed(() => candidates.value.filter(item => item.id !== tag.value?.id && item.kind === 'Physical' && props.canConfigureTag(item)))
const blocked = computed(() => (usage.value?.references ?? []).some(reference => reference.category === 'calculatedTag'))
const hasForbidden = computed(() => (usage.value?.references ?? []).some(reference => !reference.editable))

function open(row: Tag): void {
  tag.value = row
  usage.value = null
  candidates.value = props.fallbackCandidates
  error.value = ''
  targetId.value = null
  notice.value = ''
  published.value = []
  visible.value = true
  void loadUsage()
  void loadCandidates()
}

async function loadCandidates(): Promise<void> {
  try { candidates.value = await tagApi.list() } catch { /* 保留当前页测点作为候选 */ }
}

function reset(): void {
  tag.value = null
  usage.value = null
  error.value = ''
  targetId.value = null
  notice.value = ''
  published.value = []
}

async function loadUsage(): Promise<void> {
  if (!tag.value) return
  loading.value = true
  error.value = ''
  try { usage.value = await tagApi.usage(tag.value.id) }
  catch (reason) { error.value = tr('tagUsage.loadFailed', [errorMessage(reason, tr('tagsView.k72250c'))]) }
  finally { loading.value = false }
}

async function rebind(): Promise<void> {
  const row = tag.value, target = targetId.value
  if (!row || !target) return
  const targetTag = candidates.value.find(item => item.id === target)
  const count = usage.value?.references.length ?? 0
  try {
    await ElMessageBox.confirm(
      tr('tagUsage.replaceConfirm', [count, row.name, targetTag?.name ?? `#${target}`]),
      tr('tagUsage.replace'),
      { type: 'warning', confirmButtonText: tr('tagUsage.replace'), cancelButtonText: tr('tagsView.k4d0b46') })
  } catch { return }
  busy.value = true
  error.value = ''
  try {
    const result: TagRebindResult = await tagApi.rebind(row.id, target)
    published.value = result.publishedScreens ?? []
    const replaced = result.screens + result.templates + result.alarmRules + result.sequences + result.recipes
    notice.value = published.value.length
      ? tr('tagUsage.replaceDone', [replaced, published.value.length])
      : tr('tagUsage.replaceDoneNoPublish', [replaced])
    targetId.value = null
    emit('changed')
    await loadUsage()
  } catch (reason) {
    error.value = tr('tagUsage.replaceFailed', [errorMessage(reason, tr('tagsView.k72250c'))])
  } finally { busy.value = false }
}

defineExpose({ open })
</script>

<style scoped>
.usage-head { display: flex; align-items: center; gap: 8px; }
.usage-head .spacer { flex: 1; }
.usage-body { min-height: 80px; max-height: 320px; overflow: auto; margin: 10px 0; }
.usage-group h4 { margin: 12px 0 4px; font-size: 13px; }
.usage-group ul { margin: 0; padding-left: 18px; }
.usage-group li { display: flex; align-items: center; justify-content: space-between; gap: 8px; line-height: 1.8; font-size: 13px; }
.usage-replace { display: grid; gap: 8px; margin-top: 12px; padding-top: 12px; border-top: 1px solid var(--scada-border); }
</style>
