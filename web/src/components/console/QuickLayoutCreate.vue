<template>
  <el-dialog
    v-model="visible"
    :title="$t('workbench.quickTitle')"
    class="quick-layout-create-dialog"
    :before-close="close"
    :close-on-click-modal="!saving"
    :close-on-press-escape="!saving"
    :show-close="!saving"
    destroy-on-close
    data-test="quick-layout-dialog"
  >
    <div v-loading="loading" class="quick-layout-body" :aria-busy="loading || saving">
      <el-steps :active="step === 'select' ? 0 : 1" finish-status="success" class="quick-layout-steps">
        <el-step :title="$t('workbench.screenStep')" />
        <el-step :title="$t('workbench.previewStep')" />
        <el-step :title="$t('workbench.publishStep')" />
      </el-steps>
      <p class="quick-layout-help">{{ $t('workbench.quickHelp') }}</p>
      <p class="quick-layout-help">{{ $t('workbench.publishHelp') }}</p>

      <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="quick-layout-error" />
      <el-button
        v-if="canConfigure && (screenLoadError || screenUnavailable)"
        class="quick-layout-retry"
        :loading="loading"
        :disabled="loading || saving"
        data-test="quick-layout-retry"
        @click="loadScreens()"
      >{{ $t('workbench.retry') }}</el-button>

      <template v-if="step === 'select'">
        <div v-if="screensLoaded && !screenLoadError && !screens.length" class="quick-layout-empty">
          <el-empty :description="$t('workbench.noPublishedScreens')" :image-size="64" />
          <RouterLink to="/screens" @click="close()">{{ $t('workbench.openScreens') }}</RouterLink>
        </div>
        <el-form label-position="top" class="quick-layout-form" @submit.prevent="next()">
          <el-form-item :label="$t('workbench.chooseScreen')" :for="`${formId}-screen`" required>
            <WorkbenchTooltip :content="$t('workbenchTips.screenBinding')"><el-select
              :id="`${formId}-screen`"
              :model-value="selectedScreen?.id"
              :aria-label="$t('workbench.chooseScreen')"
              :placeholder="$t('workbench.chooseScreen')"
              :disabled="!canConfigure || loading || saving"
              :loading="loading"
              filterable
              data-test="quick-layout-screen"
              @update:model-value="selectScreen"
            >
              <el-option v-for="screen in screens" :key="screen.id" :value="screen.id" :label="screen.name" />
            </el-select></WorkbenchTooltip>
          </el-form-item>
          <el-form-item :label="$t('workbench.workbenchName')" :for="`${formId}-name`" required>
            <WorkbenchTooltip :content="$t('workbenchTips.layoutName')"><el-input
              :id="`${formId}-name`"
              v-model="name"
              :aria-label="$t('workbench.workbenchName')"
              :disabled="!canConfigure || saving"
              maxlength="100"
              show-word-limit
              data-test="quick-layout-name"
              @input="editName"
            /></WorkbenchTooltip>
          </el-form-item>
          <el-form-item :label="$t('workbench.chooseTemplate')" :for="`${formId}-template`" required>
            <WorkbenchTooltip :content="$t('workbench.quickHelp')"><el-select
              :id="`${formId}-template`"
              v-model="templateKey"
              :aria-label="$t('workbench.chooseTemplate')"
              :disabled="!canConfigure || saving"
              data-test="quick-layout-template"
            >
              <el-option v-for="key in quickTemplates" :key="key" :value="key" :label="$t(`layoutCatalog.template_${key}`)" />
            </el-select></WorkbenchTooltip>
          </el-form-item>
        </el-form>
      </template>
      <section v-else-if="visible && document" :aria-label="$t('workbench.previewStep')" data-test="quick-layout-preview">
        <h3 class="quick-layout-preview-name">{{ name.trim() }}</h3>
        <LayoutRuntimePreview :document="document" :name="name.trim()" />
      </section>
    </div>
    <template #footer>
      <div class="quick-layout-footer">
        <el-button :disabled="saving" @click="close()">{{ $t('layoutsView.k4d0b46') }}</el-button>
        <el-button v-if="step === 'preview'" :disabled="saving" data-test="quick-layout-back" @click="back()">{{ $t('workbench.back') }}</el-button>
        <WorkbenchTooltip v-if="step === 'select'" :content="$t('layoutPreview.readOnlyHelp')" :disabled-trigger="(!canProceed)"><el-button type="primary" :disabled="!canProceed" data-test="quick-layout-next" @click="next()">{{ $t('workbench.nextPreview') }}</el-button></WorkbenchTooltip>
        <WorkbenchTooltip v-else :content="$t('workbenchTips.saveReview')" :disabled-trigger="(!canSave) || (saving)"><el-button type="primary" :loading="saving" :disabled="!canSave" data-test="quick-layout-save" @click="save()">{{ $t('workbench.saveAndReview') }}</el-button></WorkbenchTooltip>
      </div>
    </template>
  </el-dialog>
  <LayoutVersionHistory ref="history" compact @changed="published" />
</template>

<script setup lang="ts">
import WorkbenchTooltip from './WorkbenchTooltip.vue'
import { computed, nextTick, onBeforeUnmount, ref, useId } from 'vue'
import { RouterLink } from 'vue-router'
import { ElMessage } from 'element-plus'
import { canGlobal } from '../../access/policies'
import { errorMessage } from '../../api/http'
import { hmiLayoutApi, type HmiLayout, type HmiLayoutDocument } from '../../api/hmiLayouts'
import { screenApi } from '../../api/screens'
import { tr } from '../../i18n'
import type { ScreenSummary } from '../../screens/model'
import { useAuthStore } from '../../stores/auth'
import { createLayoutTemplate } from './layoutTemplates'
import LayoutRuntimePreview from './LayoutRuntimePreview.vue'
import LayoutVersionHistory from './LayoutVersionHistory.vue'

const emit = defineEmits<{ saved: [layout: HmiLayout]; published: [id: number] }>()
const auth = useAuthStore()
const formId = useId()
const quickTemplates = ['single', 'navigation', 'alarms'] as const
const visible = ref(false), step = ref<'select' | 'preview'>('select')
const loading = ref(false), saving = ref(false), screensLoaded = ref(false)
const screens = ref<ScreenSummary[]>([]), selectedScreenId = ref<number>()
const name = ref(''), nameEdited = ref(false), templateKey = ref<(typeof quickTemplates)[number]>('single')
const document = ref<HmiLayoutDocument | null>(null)
const screenLoadError = ref(''), saveError = ref(''), savedLayoutId = ref<number | null>(null)
const history = ref<InstanceType<typeof LayoutVersionHistory>>()
const canConfigure = computed(() => canGlobal(auth.user, 'Configure'))
const selectedScreen = computed(() => screens.value.find(screen => screen.id === selectedScreenId.value))
const screenUnavailable = computed(() => screensLoaded.value && selectedScreenId.value != null && !selectedScreen.value)
const error = computed(() => !canConfigure.value ? tr('workbench.configureDenied') : screenLoadError.value || saveError.value || (screenUnavailable.value ? tr('workbench.screenUnavailable') : ''))
const canProceed = computed(() => visible.value && canConfigure.value && !loading.value && !saving.value && !screenLoadError.value && !!selectedScreen.value && !!name.value.trim() && name.value.length <= 100)
const canSave = computed(() => canProceed.value && step.value === 'preview' && !!document.value)
let generation = 0, disposed = false

function current(request: number): boolean { return !disposed && request === generation && visible.value }
function defaultName(): void {
  if (selectedScreen.value && !nameEdited.value) name.value = tr('workbench.quickName', [selectedScreen.value.name]).slice(0, 100)
}
function selectScreen(id: number): void {
  selectedScreenId.value = id
  saveError.value = ''
  defaultName()
}
function editName(): void { nameEdited.value = true; saveError.value = '' }

async function open(screenId?: number): Promise<void> {
  if (disposed || saving.value) return
  if (!canConfigure.value) {
    ElMessage.error(tr('workbench.configureDenied'))
    return
  }
  generation++
  loading.value = false; screensLoaded.value = false; screens.value = []
  selectedScreenId.value = screenId; name.value = ''; nameEdited.value = false; templateKey.value = 'single'
  step.value = 'select'; document.value = null; screenLoadError.value = ''; saveError.value = ''; visible.value = true
  await loadScreens()
}

async function loadScreens(): Promise<void> {
  if (disposed || !visible.value || !canConfigure.value || loading.value || saving.value) return
  const request = ++generation
  loading.value = true; screenLoadError.value = ''
  try {
    const result = await screenApi.list()
    if (!current(request)) return
    screens.value = result.filter(screen => screen.publishedRevision != null)
    screensLoaded.value = true
    if (selectedScreenId.value == null) selectedScreenId.value = screens.value[0]?.id
    defaultName()
  } catch (reason) {
    if (current(request)) screenLoadError.value = errorMessage(reason, tr('workbench.screenLoadFailed'))
  } finally {
    if (!disposed && request === generation) loading.value = false
  }
}

function next(): void {
  if (!canProceed.value || !selectedScreen.value) return
  document.value = createLayoutTemplate(templateKey.value, selectedScreen.value.id)
  saveError.value = ''; step.value = 'preview'
}
function back(): void {
  if (saving.value) return
  step.value = 'select'; document.value = null
}
function close(done?: () => void): void {
  if (saving.value) return
  generation++; loading.value = false
  if (done) done(); else visible.value = false
}

async function save(): Promise<void> {
  if (!canSave.value || !document.value) return
  const request = generation
  const payload = { name: name.value.trim(), document: document.value }
  saving.value = true; saveError.value = ''
  try {
    let layout: HmiLayout
    try {
      layout = await hmiLayoutApi.save(payload)
    } catch (reason) {
      if (current(request)) saveError.value = errorMessage(reason, tr('workbench.saveFailed'))
      return
    }
    if (!current(request)) return
    savedLayoutId.value = layout.id
    visible.value = false
    ElMessage.success(tr('workbench.draftSaved'))
    emit('saved', layout)
    await nextTick()
    if (disposed || request !== generation) return
    await history.value!.openPublish(layout.id)
  } finally {
    if (!disposed && request === generation) saving.value = false
  }
}

function published(): void {
  if (disposed || savedLayoutId.value == null) return
  const id = savedLayoutId.value
  savedLayoutId.value = null
  emit('published', id)
}

onBeforeUnmount(() => { disposed = true; generation++ })
defineExpose({ open })
</script>

<style scoped>
:global(.quick-layout-create-dialog) { width: min(1100px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); margin: 16px auto; display: flex; flex-direction: column; }
:global(.quick-layout-create-dialog .el-dialog__body) { min-height: 0; overflow-y: auto; }
.quick-layout-body { min-width: 0; min-height: 80px; }
.quick-layout-steps { margin-bottom: 20px; }
.quick-layout-help { color: var(--scada-text-muted); font-size: 13px; line-height: 1.7; overflow-wrap: anywhere; }
.quick-layout-retry { margin-top: 10px; }
.quick-layout-empty { text-align: center; margin-bottom: 20px; }
.quick-layout-empty a { color: var(--scada-primary); overflow-wrap: anywhere; }
.quick-layout-empty a:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: 3px; }
.quick-layout-form { margin-top: 20px; }
.quick-layout-form .el-select { width: 100%; }
.quick-layout-form :deep(.el-form-item__label) { height: auto; line-height: 1.6; overflow-wrap: anywhere; }
.quick-layout-preview-name { font-size: 16px; overflow-wrap: anywhere; }
.quick-layout-footer { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 8px; }
.quick-layout-footer .el-button { margin: 0; white-space: normal; height: auto; min-height: 32px; }
@media (max-width: 600px) {
  .quick-layout-steps :deep(.el-step__title) { font-size: 12px; line-height: 1.5; overflow-wrap: anywhere; }
  .quick-layout-footer .el-button { max-width: 100%; }
}
</style>
