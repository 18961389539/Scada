<template>
  <div class="designer" v-loading="loading" :class="{ busy, embedded }">
    <div class="designer-toolbar" :inert="busy">
      <template v-if="!embedded"><el-button @click="router.push('/screens')">{{ $t('screenDesignerView.kc7725d') }}</el-button>
        <el-input v-model="name" :disabled="busy" :aria-label="$t('screenDesignerView.ka02198')" maxlength="100" style="width: 180px" @change="commit" />
        <el-tag :type="dirty ? 'warning' : 'success'" size="small">{{ dirty ? $t('screenDesignerView.k4123f1') : $t('screenDesignerView.kcdfab9') }}</el-tag>
        <span class="revision">{{ $t('screenDesignerView.kdaaa8a', [revision, publishedRevision ? `r${publishedRevision}` : $t('screenDesignerView.k720777')]) }}</span></template>
      <strong v-else>{{ $t('screenDesignerView.kaed0ee') }} <small class="muted">{{ $t('screenDesignerView.k13b084') }}</small></strong>
      <span class="spacer" />
      <el-button :disabled="!history.canUndo" @click="undo">{{ $t('screenDesignerView.k9fcefd') }}</el-button><el-button :disabled="!history.canRedo" @click="redo">{{ $t('screenDesignerView.k1238f0') }}</el-button>
      <template v-if="!embedded"><el-button @click="validate">{{ $t('screenDesignerView.k20feec') }}</el-button><el-button @click="previewVisible = true">{{ $t('screenDesignerView.kde61aa') }}</el-button>
        <el-button :loading="busy" @click="save">{{ $t('screenDesignerView.k4cd30e') }}</el-button><el-button type="primary" :loading="busy" @click="publish">{{ $t('screenDesignerView.k94f172') }}</el-button></template>
    </div>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="designer-action-error" />
    <el-alert v-if="guardError" :title="guardError" type="warning" :closable="false" show-icon data-test="designer-guard-error" />
    <el-alert v-else-if="library.error" :title="library.error" type="error" :closable="false" show-icon data-test="designer-library-error">
      <el-button link type="primary" data-test="designer-library-retry" @click="library.ensure()">{{ $t('screenDesignerView.retryLibrary') }}</el-button>
    </el-alert>
    <div v-if="!loadError" class="designer-body" :inert="busy">
      <aside class="palette">
        <el-input v-model="componentQuery" type="search" clearable :aria-label="$t('screenDesignerView.searchComponents')" :placeholder="$t('screenDesignerView.searchComponents')" />
        <p v-if="componentQuery.trim()" class="search-summary" role="status">{{ $t('screenDesignerView.searchCount', [componentCount, componentTotal]) }}</p>
        <p v-if="!componentCount" class="search-summary">{{ $t('screenDesignerView.noComponents') }}</p>
        <div v-if="filteredPalette.length" class="section-title">{{ $t('screenDesignerView.ka20f23') }} <small>{{ $t('screenDesignerView.k93ad10') }}</small></div>
        <div v-if="filteredPalette.length" class="palette-grid"><button v-for="item in filteredPalette" :key="item.type" draggable="true" :data-palette="item.type"
          @dragstart="startPaletteDrag(item.type, $event)" @click="add(item.type)"><span>{{ item.icon }}</span>{{ item.label }}</button></div>
        <div v-if="filteredSymbols.length" class="section-title">{{ $t('screenDesignerView.k95f68c') }} <small>{{ $t('screenDesignerView.k5f354b') }}</small></div>
        <el-collapse v-if="filteredSymbols.length" v-model="expandedGroups" class="symbol-groups" data-test="symbol-library">
          <el-collapse-item v-for="(group, cat) in symbolGroups" :key="cat" :title="symbolCategoryLabel(cat)" :name="cat">
            <div class="symbol-grid"><button v-for="sym in group" :key="sym.key" draggable="true" :data-symbol="sym.key" :title="symbolName(sym)"
              @dragstart="startSymbolDrag(sym.key, $event)" @click="addSymbol(sym.key)"><span class="sym-preview" v-html="sym.svg" />{{ symbolName(sym) }}</button></div>
          </el-collapse-item>
        </el-collapse>
        <template v-if="filteredUserSymbols.length">
          <div class="section-title">{{ $t('screenDesignerView.k169219') }}</div>
          <div class="symbol-grid"><button v-for="sym in filteredUserSymbols" :key="sym.key" :data-symbol="`user:${sym.key}`" @click="addSymbol(`user:${sym.key}`)"><span class="sym-preview" v-html="sym.svg" />{{ sym.name }}</button></div>
        </template>
        <template v-if="!embedded && (!componentQuery.trim() || filteredTemplates.length)">
          <div class="section-title">{{ $t('screenDesignerView.kc10f0d') }} <small>{{ $t('screenDesignerView.kb4c295') }}</small></div>
          <div class="layers" data-test="template-palette"><button v-for="item in filteredTemplates" :key="item.key" :data-template="item.key" @click="addTemplate(item)">{{ item.name }}<small>{{ $t('screenDesignerView.ke3de2d', [item.placeholders.length]) }}</small></button>
            <p v-if="!templates.length" class="muted tiny">{{ library.error || $t('screenDesignerView.kb47ff3') }}</p></div>
        </template>
        <div class="section-title">{{ $t('screenDesignerView.kec4bca') }} <small role="status">{{ $t('screenDesignerView.searchCount', [filteredLayers.length, document.nodes.length]) }}</small></div>
        <el-input v-model="layerQuery" type="search" clearable :aria-label="$t('screenDesignerView.searchLayers')" :placeholder="$t('screenDesignerView.searchLayers')" />
        <div class="layers" data-test="designer-layers"><button v-for="node in filteredLayers" :key="node.id" :class="{ active: selectedIds.includes(node.id) }" :aria-pressed="selectedIds.includes(node.id)" :data-layer-id="node.id" :title="`${node.text || node.type} (#${node.id})`"
          @click="selectLayer(node.id, $event.shiftKey)"><span>{{ node.text || node.type }}</span><small>#{{ node.id.slice(0, 6) }}</small></button>
          <p v-if="!filteredLayers.length" class="search-summary">{{ $t(document.nodes.length ? 'screenDesignerView.noLayers' : 'screenDesignerView.emptyLayers') }}</p></div>
        <div class="keyboard-help">{{ $t('screenDesignerView.k3d6b6c') }}<br>{{ $t('screenDesignerView.k48d27e') }}<br>{{ $t('screenDesignerView.k69cabc') }}<br>{{ $t('screenDesignerView.selectionKeys') }}</div>
      </aside>
      <main class="workspace">
        <div class="canvas-toolbar"><el-select :model-value="zoom" :aria-label="$t('screenDesignerView.kc48773')" style="width: 100px" @update:model-value="changeZoom"><el-option v-for="z in zoomOptions" :key="z" :label="`${Math.round(z * 100)}%`" :value="z" /></el-select>
          <el-button size="small" @click="fitCanvas">{{ $t('screenDesignerView.fitCanvas') }}</el-button>
          <el-button size="small" :disabled="!selectedIds.length" @click="revealSelection">{{ $t('screenDesignerView.revealSelection') }}</el-button>
          <el-checkbox v-model="snap">{{ $t('screenDesignerView.k04c186') }}</el-checkbox><span class="spacer" />
          <el-button v-if="!embedded" size="small" :disabled="!selectedIds.length" data-test="save-as-template" @click="openTemplateDialog">{{ $t('screenDesignerView.k9ca5e2') }}</el-button>
          <el-button size="small" :disabled="!selectedIds.length" @click="duplicate">{{ $t('screenDesignerView.k4edd1d') }}</el-button><el-button size="small" :disabled="!selectedIds.length" @click="reorder(false)">{{ $t('screenDesignerView.k05c46f') }}</el-button>
          <el-button size="small" :disabled="!selectedIds.length" @click="reorder(true)">{{ $t('screenDesignerView.k8a0c83') }}</el-button><el-button size="small" type="danger" :disabled="!selectedIds.length" @click="remove">{{ $t('screenDesignerView.k3755f5') }}</el-button>
        </div>
        <div ref="stage" class="stage" tabindex="0" role="region" :aria-label="$t('screenDesignerView.k95b210')" data-testid="designer-stage">
          <ScreenCanvas :document="document" editing :zoom="zoom" :library="library.items" :library-failed="!!library.error" :selected-ids="selectedIds" @background="selectedIds = []" @drop="drop"
            @node-pointer-down="beginDrag" @resize-pointer-down="beginResize" />
        </div>
        <div class="status-line"><span>{{ document.width }} × {{ document.height }}</span><span>{{ $t('screenDesignerView.k672212', [selectedIds.length]) }}</span><span v-if="savedAt">{{ $t('screenDesignerView.k8c34f3', [savedAt]) }}</span></div>
      </main>
      <aside class="properties" @change="commit">
        <div class="section-title">{{ selected ? $t('screenDesignerView.k1c72c3') : $t('screenDesignerView.k5a1727') }}</div>
        <el-form label-position="top" size="small">
          <template v-if="selected">
            <el-form-item :label="$t('screenDesignerView.kf4d3da')"><el-input v-model="selected.text" maxlength="500" :aria-label="$t('screenDesignerView.kd27949')" /></el-form-item>
            <div class="field-pair"><el-form-item label="X"><el-input-number v-model="selected.x" :min="0" :max="document.width - selected.width" :controls="false" @change="commit" /></el-form-item>
              <el-form-item label="Y"><el-input-number v-model="selected.y" :min="0" :max="document.height - selected.height" :controls="false" @change="commit" /></el-form-item></div>
            <div class="field-pair"><el-form-item :label="$t('screenDesignerView.k6395f4')"><el-input-number v-model="selected.width" :min="16" :max="document.width - selected.x" :controls="false" @change="commit" /></el-form-item>
              <el-form-item :label="$t('screenDesignerView.kb096b3')"><el-input-number v-model="selected.height" :min="16" :max="document.height - selected.y" :controls="false" @change="commit" /></el-form-item></div>
            <el-form-item :label="$t('screenDesignerView.k053b6f')"><el-input v-model="englishText" maxlength="500" :placeholder="$t('screenDesignerView.kc82bd7')" data-test="node-text-en" @change="commit" /></el-form-item>
            <el-form-item v-if="selected.type === 'symbol'" :label="$t('screenDesignerView.ke2cf6f')"><el-select v-model="selected.symbol" filterable data-test="node-symbol" @change="commit">
              <el-option v-for="sym in builtinSymbols" :key="sym.key" :value="sym.key" :label="symbolName(sym)" />
              <el-option v-for="sym in userSymbols" :key="sym.key" :value="`user:${sym.key}`" :label="$t('screenDesignerView.kf933f2', [sym.name])" /></el-select></el-form-item>
            <el-form-item :label="$t('screenDesignerView.ka69e51')"><el-input-number v-model="selected.rotation" :min="-360" :max="360" @change="commit" /></el-form-item>
            <div class="field-pair"><el-form-item :label="$t('screenDesignerView.kce0326')"><input v-model="selected.color" type="color" :aria-label="$t('screenDesignerView.kce0326')" /></el-form-item>
              <el-form-item :label="$t('screenDesignerView.ka5b26c')"><input v-model="selected.activeColor" type="color" :aria-label="$t('screenDesignerView.ka5b26c')" /></el-form-item></div>
            <template v-if="selected.type === 'template'">
              <el-divider>{{ $t('screenDesignerView.keaf30f') }}</el-divider>
              <p class="muted tiny">{{ $t('screenDesignerView.kcd1e70', [selected.templateKey]) }}</p>
              <el-form-item v-for="b in selected.bindings ?? []" :key="b.key" :label="b.key"><el-select v-model="b.tagId" filterable clearable :data-test="`binding-${b.key}`" @change="commit">
                <el-option v-for="tag in tags" :key="tag.id" :value="tag.id" :label="tagLabel(tag)" /></el-select></el-form-item>
              <el-button size="small" @click="syncBindings(selected)">{{ $t('screenDesignerView.k74c779') }}</el-button>
            </template>
            <template v-else>
            <el-divider>{{ $t('screenDesignerView.kf02d67') }}</el-divider>
            <el-form-item :label="$t('screenDesignerView.k3d738d')"><el-select v-model="selected.alarmTagId" filterable clearable :aria-label="$t('screenDesignerView.k3d738d')" data-test="node-alarm-tag" @change="commit">
              <el-option v-for="tag in tags" :key="tag.id" :value="tag.id" :label="tagLabel(tag)" /></el-select></el-form-item>
            <el-form-item :label="$t('screenDesignerView.k292dd4')"><el-select v-model="selected.tagId" filterable clearable :aria-label="$t('screenDesignerView.k292dd4')" @change="bindingChanged">
              <el-option v-for="tag in tags" :key="tag.id" :value="tag.id" :label="tagLabel(tag)" /></el-select></el-form-item>
            <el-form-item :label="$t('screenDesignerView.k8ec8c8')"><el-input v-model="selected.unit" maxlength="32" /></el-form-item>
            <div class="field-pair"><el-form-item :label="$t('screenDesignerView.k6e3463')"><el-input-number v-model="selected.min" :controls="false" @change="commit" /></el-form-item>
              <el-form-item :label="$t('screenDesignerView.k9a96e0')"><el-input-number v-model="selected.max" :controls="false" @change="commit" /></el-form-item></div>
            <el-form-item :label="$t('screenDesignerView.k75bf5c')"><el-input-number v-model="selected.decimals" :min="0" :max="6" @change="commit" /></el-form-item>
            <el-form-item :label="$t('screenDesignerView.ke755ee')"><el-input-number v-model="selected.activeThreshold" :controls="false" @change="commit" /></el-form-item>
            <el-divider>{{ $t('screenDesignerView.k054db2') }}</el-divider>
            <el-form-item :label="$t('screenDesignerView.kd9d982')"><el-select v-model="selected.action.type" :aria-label="$t('screenDesignerView.k054db2')" @change="commit"><el-option :label="$t('screenDesignerView.kc5cc5b')" value="none" />
              <el-option :label="$t('screenDesignerView.kae5db8')" value="navigate" /><el-option :label="$t('screenDesignerView.kf57bb1')" value="popup" /><el-option :label="$t('screenDesignerView.ke4c845')" value="command" /></el-select></el-form-item>
            <el-form-item v-if="['navigate', 'popup'].includes(selected.action.type)" :label="$t('screenDesignerView.k1c0e40')"><el-select v-model="selected.action.screenId" filterable @change="commit">
              <el-option v-for="screen in screens" :key="screen.id" :value="screen.id" :label="$t('screenDesignerView.k6fe2eb', [screen.name, screen.publishedRevision ? '' : $t('screenDesignerView.k9a12d8')])" /></el-select></el-form-item>
            <template v-if="selected.action.type === 'command'">
              <el-form-item :label="$t('screenDesignerView.k005c1c')"><el-select v-model="selected.action.tagId" filterable @change="commit"><el-option v-for="tag in tags.filter(t => t.writable)" :key="tag.id" :value="tag.id" :label="tagLabel(tag)" /></el-select></el-form-item>
              <el-form-item :label="$t('screenDesignerView.k5ae08a')"><el-input-number v-model="selected.action.value" :controls="false" @change="commit" /></el-form-item>
              <p class="muted tiny">{{ $t('screenDesignerView.k72aa15') }}</p>
            </template>
            </template>
          </template>
          <template v-else>
            <el-form-item :label="$t('screenDesignerView.k98fce6')"><el-input-number v-model="document.width" :min="320" :max="7680" :step="20" @change="resizeCanvas" /></el-form-item>
            <el-form-item :label="$t('screenDesignerView.ke11812')"><el-input-number v-model="document.height" :min="200" :max="4320" :step="20" @change="resizeCanvas" /></el-form-item>
            <el-form-item :label="$t('screenDesignerView.k4a2d8a')"><input v-model="document.background" type="color" :aria-label="$t('screenDesignerView.k4a2d8a')" /></el-form-item>
            <p class="muted">{{ $t('screenDesignerView.k8cd1c3') }}</p>
          </template>
        </el-form>
        <div v-if="selectedIds.length > 1" class="alignment"><el-button size="small" @click="align('left')">{{ $t('screenDesignerView.k413f8d') }}</el-button><el-button size="small" @click="align('top')">{{ $t('screenDesignerView.k8e2a94') }}</el-button></div>
      </aside>
    </div>
    <el-dialog v-model="templateVisible" :title="$t('screenDesignerView.k9ca5e2')" width="560px" data-test="template-dialog">
      <el-alert v-if="templateError" :title="templateError" type="error" :closable="false" show-icon data-test="template-create-error" />
      <el-form label-width="90px" size="small">
        <el-form-item :label="$t('screenDesignerView.k5a8c89')"><el-input v-model="templateForm.key" :placeholder="$t('screenDesignerView.k5b6ae2')" data-test="template-key" /></el-form-item>
        <el-form-item :label="$t('screenDesignerView.k1be7ae')"><el-input v-model="templateForm.name" data-test="template-name" /></el-form-item>
        <el-form-item :label="$t('screenDesignerView.k435c52')"><el-input v-model="templateForm.category" /></el-form-item>
        <el-form-item v-for="p in templateForm.placeholders" :key="p.tagId" :label="`#${p.tagId}`"><el-input v-model="p.key" :data-test="`template-ph-${p.tagId}`"><template #prepend>{{ $t('screenDesignerView.ka6ea64') }}</template></el-input>
          <span class="tiny muted">{{ tagLabel(tags.find(t => t.id === p.tagId) ?? ({ id: p.tagId, name: '?' } as Tag)) }}</span></el-form-item>
        <el-form-item><el-checkbox v-model="templateForm.replace">{{ $t('screenDesignerView.k5f853f') }}</el-checkbox></el-form-item>
      </el-form>
      <template #footer><el-button @click="templateVisible = false">{{ $t('screenDesignerView.k4d0b46') }}</el-button><el-button type="primary" data-test="template-create" @click="createTemplate">{{ $t('screenDesignerView.kbce23c') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="previewVisible" :title="$t('screenDesignerView.ke57c0a', [name])" width="94%" destroy-on-close><ScreenRuntime v-if="previewVisible" :preview-document="document" :preview-name="name" /></el-dialog>
    <el-dialog v-model="issuesVisible" :title="$t('screenDesignerView.kb44e8f')" width="560px" data-test="designer-issues">
      <el-alert v-if="issuesError" :title="issuesError" type="error" :closable="false" show-icon data-test="designer-issues-error" />
      <template v-else><el-result v-if="!issues.length" icon="success" :title="$t('screenDesignerView.k7035f8')" :sub-title="$t('screenDesignerView.k457883')" />
      <ul v-else class="issue-list"><li v-for="(issue, i) in issues" :key="i"><button @click="focusIssue(issue)">{{ issue.nodeId ? document.nodes.find(n => n.id === issue.nodeId)?.text || issue.nodeId : $t('screenDesignerView.k95b210') }}</button>：{{ issue.message }}</li></ul></template></el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate, useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import ScreenCanvas from '../components/ScreenCanvas.vue'
import ScreenRuntime from '../components/ScreenRuntime.vue'
import { screenApi } from '../api/screens'
import { deviceApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import { clampNode, cleanNode, createNode, createSymbolNode, createTemplateNode, EditHistory, emptyDocument, palette } from '../screens/model'
import type { LibraryItem, NodeType, ScreenDocument, ScreenIssue, ScreenNode, ScreenSummary } from '../screens/model'
import { builtinSymbols, symbolCategories, type BuiltinSymbol } from '../screens/symbols'
import { useLibraryStore } from '../stores/library'
import { hmiLibraryApi } from '../api/hmiLibrary'
import { useI18n } from 'vue-i18n'
import type { Device, Tag } from '../types'
import { formatTime } from '../utils'
import { tr } from '../i18n'
const props = withDefaults(defineProps<{ embedded?: boolean; modelValue?: ScreenDocument; bindingTags?: Tag[]; disabled?: boolean }>(), { embedded: false, disabled: false })
const emit = defineEmits<{ 'update:modelValue': [document: ScreenDocument]; save: [] }>()
const route = useRoute(), router = useRouter(), id = Number(route.params.id)
const document = ref<ScreenDocument>(emptyDocument()), name = ref(''), revision = ref(1), publishedRevision = ref<number | null>(null)
const loading = ref(true), loadError = ref(''), actionError = ref(''), guardError = ref(''), busy = ref(false), zoom = ref(.75), snap = ref(true), selectedIds = ref<string[]>([])
const tags = ref<Tag[]>([]), devices = ref<Device[]>([]), screens = ref<ScreenSummary[]>([]), stage = ref<HTMLDivElement>()
const previewVisible = ref(false), issuesVisible = ref(false), issues = ref<ScreenIssue[]>([]), issuesError = ref(''), savedAt = ref('')
const history = reactive(new EditHistory()), savedSnapshot = ref('')
let stableSnapshot = '', clipboard: ScreenNode[] = [], drag: { x: number; y: number; nodes: ScreenNode[]; resize: boolean; moved: boolean } | null = null
let disposed = false
const selected = computed(() => document.value.nodes.find(n => n.id === selectedIds.value[0]))
function snapshot(): string { return JSON.stringify({ name: name.value, document: document.value }) }
const dirty = computed(() => savedSnapshot.value !== '' && snapshot() !== savedSnapshot.value)
function notify(): void { if (props.embedded) emit('update:modelValue', JSON.parse(JSON.stringify(document.value))) }
function commit(): void { const state = snapshot(); if (stableSnapshot && state !== stableSnapshot) { history.push(stableSnapshot); stableSnapshot = state; notify() } }
function restore(state: string | undefined): void { if (!state) return; const parsed = JSON.parse(state); name.value = parsed.name; document.value = parsed.document; stableSnapshot = state; selectedIds.value = []; notify() }
function undo(): void { commit(); restore(history.undo(snapshot())) }
function redo(): void { restore(history.redo(snapshot())) }
function tagLabel(tag: Tag): string { return props.embedded ? tag.name : tr('screenDesignerView.kfc2b1a', [tag.deviceId === null ? tr('screenDesignerView.k4e0387') : devices.value.find(d => d.id === tag.deviceId)?.name ?? tag.deviceId, tag.name, tag.id]) }
function bindingChanged(): void { if (selected.value) { selected.value.tagId ??= null; selected.value.unit = tags.value.find(t => t.id === selected.value?.tagId)?.unit ?? '' }; commit() }
function select(nodeId: string, additive = false): void { commit(); selectedIds.value = additive ? selectedIds.value.includes(nodeId) ? selectedIds.value.filter(i => i !== nodeId) : [...selectedIds.value, nodeId] : [nodeId] }
function block(message: string): void { guardError.value = message }
function add(type: NodeType, x = 60 + document.value.nodes.length % 8 * 20, y = 60 + document.value.nodes.length % 8 * 20): void {
  if (busy.value) return
  if (document.value.nodes.length >= 1000) { block(tr('screenDesignerView.k10f364')); return }
  const node = clampNode(createNode(type, x, y), document.value); document.value.nodes.push(node); selectedIds.value = [node.id]; guardError.value = ''; commit()
}
const library = useLibraryStore()
const { locale } = useI18n()
const userSymbols = computed(() => library.items.filter(x => x.kind === 'symbol'))
const templates = computed(() => library.items.filter(x => x.kind === 'template'))
const symbolName = (s: BuiltinSymbol) => (locale.value === 'en-US' ? s.nameEn : s.name)
const symbolCategoryLabel = (c: string | number) => (locale.value === 'en-US' ? symbolCategories[c]?.en : symbolCategories[c]?.zh) ?? String(c)
const componentQuery = ref(''), layerQuery = ref(''), expandedGroups = ref<string[]>([])
function matches(query: string, ...values: unknown[]): boolean {
  const text = values.filter(v => v != null).join(' ').toLocaleLowerCase()
  return query.trim().toLocaleLowerCase().split(/\s+/).every(word => text.includes(word))
}
const filteredPalette = computed(() => palette.filter(p => matches(componentQuery.value, p.type, p.label)))
const filteredSymbols = computed(() => builtinSymbols.filter(s => matches(componentQuery.value, s.key, s.name, s.nameEn, s.category, symbolCategoryLabel(s.category))))
const filteredUserSymbols = computed(() => userSymbols.value.filter(s => matches(componentQuery.value, s.key, s.name, s.category)))
const filteredTemplates = computed(() => templates.value.filter(t => matches(componentQuery.value, t.key, t.name, t.category, ...t.placeholders)))
const componentTotal = computed(() => palette.length + builtinSymbols.length + userSymbols.value.length + (props.embedded ? 0 : templates.value.length))
const componentCount = computed(() => filteredPalette.value.length + filteredSymbols.value.length + filteredUserSymbols.value.length + (props.embedded ? 0 : filteredTemplates.value.length))
const symbolGroups = computed(() => filteredSymbols.value.reduce<Record<string, BuiltinSymbol[]>>((acc, s) => { (acc[s.category] ??= []).push(s); return acc }, {}))
watch(componentQuery, query => { if (query.trim()) expandedGroups.value = Object.keys(symbolGroups.value) })
const tagNames = computed(() => new Map(tags.value.map(tag => [tag.id, tag.name])))
const filteredLayers = computed(() => document.value.nodes.filter(n => {
  const ids = [n.tagId, n.alarmTagId, n.action.tagId, ...(n.bindings?.map(b => b.tagId) ?? [])].filter((id): id is number => id != null)
  return matches(layerQuery.value, n.id, n.text, ...Object.values(n.texts ?? {}), n.type, palette.find(p => p.type === n.type)?.label,
    n.symbol, n.templateKey, ...ids.flatMap(id => [id, tagNames.value.get(id)]))
}).reverse())
const zoomOptions = computed(() => [...new Set([.1, .25, .35, .5, .75, 1, 1.25, 1.5, zoom.value])].sort((a, b) => a - b))
async function fitCanvas(): Promise<void> {
  const viewport = stage.value; if (!viewport) return
  zoom.value = Math.max(.01, Math.floor(Math.min(1.5, (viewport.clientWidth - 40) / document.value.width, (viewport.clientHeight - 40) / document.value.height) * 100) / 100)
  await nextTick(); if (!disposed) viewport.scrollTo(0, 0)
}
async function changeZoom(value: number): Promise<void> {
  const viewport = stage.value; if (!viewport || !Number.isFinite(value) || value <= 0) return
  const x = (viewport.scrollLeft + viewport.clientWidth / 2 - 20) / zoom.value
  const y = (viewport.scrollTop + viewport.clientHeight / 2 - 20) / zoom.value
  zoom.value = value
  await nextTick(); if (!disposed) viewport.scrollTo(x * value + 20 - viewport.clientWidth / 2, y * value + 20 - viewport.clientHeight / 2)
}
async function revealSelection(): Promise<void> {
  await nextTick()
  const viewport = stage.value; if (!viewport || disposed) return
  const selectedBounds = () => selectedIds.value.flatMap(id => {
    const node = viewport.querySelector(`[data-node-id="${CSS.escape(id)}"]`)
    return node ? [node.getBoundingClientRect()] : []
  })
  let boxes = selectedBounds()
  if (!boxes.length) return
  const width = Math.max(...boxes.map(b => b.right)) - Math.min(...boxes.map(b => b.left))
  const height = Math.max(...boxes.map(b => b.bottom)) - Math.min(...boxes.map(b => b.top))
  const scale = Math.min(1, (viewport.clientWidth - 40) / width, (viewport.clientHeight - 40) / height)
  if (scale < 1) {
    zoom.value = Math.max(.01, Math.floor(zoom.value * scale * 100) / 100)
    await nextTick(); if (disposed) return
    boxes = selectedBounds()
    if (!boxes.length) return
  }
  const bounds = viewport.getBoundingClientRect()
  const x = (Math.min(...boxes.map(b => b.left)) + Math.max(...boxes.map(b => b.right))) / 2
  const y = (Math.min(...boxes.map(b => b.top)) + Math.max(...boxes.map(b => b.bottom))) / 2
  viewport.scrollTo(viewport.scrollLeft + x - bounds.left - viewport.clientWidth / 2, viewport.scrollTop + y - bounds.top - viewport.clientHeight / 2)
}
function selectLayer(nodeId: string, additive: boolean): void { select(nodeId, additive); if (selectedIds.value.includes(nodeId)) void revealSelection() }
const englishText = computed({
  get: () => selected.value?.texts?.['en-US'] ?? '',
  set: (v: string) => { if (!selected.value) return; const texts = { ...(selected.value.texts ?? {}) }; if (v.trim()) texts['en-US'] = v; else delete texts['en-US']; selected.value.texts = Object.keys(texts).length ? texts : null }
})
function pushNode(node: ScreenNode): void {
  if (busy.value) return
  if (document.value.nodes.length >= 1000) { block(tr('screenDesignerView.k10f364')); return }
  const n = clampNode(node, document.value); document.value.nodes.push(n); selectedIds.value = [n.id]; guardError.value = ''; commit()
}
function nextPos(): [number, number] { return [60 + document.value.nodes.length % 8 * 20, 60 + document.value.nodes.length % 8 * 20] }
function addSymbol(key: string, x?: number, y?: number): void {
  const b = builtinSymbols.find(s => s.key === key), u = userSymbols.value.find(s => `user:${s.key}` === key)
  const [px, py] = x === undefined ? nextPos() : [x, y!]
  pushNode(createSymbolNode(key, b?.w ?? u?.width ?? 100, b?.h ?? u?.height ?? 100, b ? b.name : u?.name ?? key, px, py))
}
function addTemplate(item: LibraryItem, x?: number, y?: number): void { const [px, py] = x === undefined ? nextPos() : [x, y!]; pushNode(createTemplateNode(item, px, py)) }
function syncBindings(node: ScreenNode): void {
  const item = templates.value.find(t => t.key === node.templateKey); if (!item) { block(tr('screenDesignerView.kecb0c7')); return }
  guardError.value = ''
  const old = new Map((node.bindings ?? []).map(b => [b.key, b.tagId]))
  node.bindings = item.placeholders.map(key => ({ key, tagId: old.get(key) ?? null })); commit()
}
function startSymbolDrag(key: string, event: DragEvent): void { event.dataTransfer?.setData('application/scada-symbol', key); if (event.dataTransfer) event.dataTransfer.effectAllowed = 'copy' }

// 存为模板：所选图元按包围盒归一化，具体测点替换为占位符（同一测点同一占位符）。
const templateVisible = ref(false), templateError = ref('')
const templateForm = reactive<{ key: string; name: string; category: string; replace: boolean; placeholders: { tagId: number; key: string }[] }>({ key: '', name: '', category: tr('screenDesignerView.k35cad0'), replace: true, placeholders: [] })
function openTemplateDialog(): void {
  const picked = document.value.nodes.filter(n => selectedIds.value.includes(n.id))
  if (picked.some(n => n.type === 'template')) { block(tr('screenDesignerView.k27b75e')); return }
  guardError.value = ''
  const ids = [...new Set(picked.flatMap(n => [n.tagId, n.alarmTagId, n.action.type === 'command' ? n.action.tagId : null]).filter((x): x is number => !!x))]
  Object.assign(templateForm, { key: '', name: '', category: tr('screenDesignerView.k35cad0'), replace: true, placeholders: ids.map((tagId, i) => {
    const raw = (tags.value.find(t => t.id === tagId)?.name ?? '').replace(/[^a-zA-Z0-9_-]/g, '')
    return { tagId, key: /^[a-zA-Z]/.test(raw) ? raw.slice(0, 64) : `p${i + 1}` }
  }) })
  templateError.value = ''; templateVisible.value = true
}
async function createTemplate(): Promise<void> {
  const picked = document.value.nodes.filter(n => selectedIds.value.includes(n.id)); if (!picked.length) return
  const keys = templateForm.placeholders.map(p => p.key.trim())
  templateError.value = ''
  if (new Set(keys).size !== keys.length) { templateError.value = tr('screenDesignerView.k1b2933'); return }
  const keyOf = new Map(templateForm.placeholders.map(p => [p.tagId, p.key.trim()]))
  const minX = Math.min(...picked.map(n => n.x)), minY = Math.min(...picked.map(n => n.y))
  const width = Math.max(16, Math.max(...picked.map(n => n.x + n.width)) - minX), height = Math.max(16, Math.max(...picked.map(n => n.y + n.height)) - minY)
  const nodes = picked.map(n => cleanNode({ ...JSON.parse(JSON.stringify(n)), x: n.x - minX, y: n.y - minY, tagId: null, alarmTagId: null, bind: n.tagId ? keyOf.get(n.tagId) : null, alarmBind: n.alarmTagId ? keyOf.get(n.alarmTagId) : null,
    action: { ...n.action, tagId: null, bind: n.action.type === 'command' && n.action.tagId ? keyOf.get(n.action.tagId) : null } }))
  try {
    const { item } = await hmiLibraryApi.create({ key: templateForm.key.trim(), name: templateForm.name.trim() || templateForm.key.trim(), category: templateForm.category, kind: 'template', width, height, placeholders: keys,
      document: { schemaVersion: 1, width: Math.max(320, width), height: Math.max(200, height), background: document.value.background, nodes } })
    await library.reload()
    if (templateForm.replace) {
      const instance = createTemplateNode(item, minX, minY)
      instance.bindings = item.placeholders.map(key => ({ key, tagId: templateForm.placeholders.find(p => p.key.trim() === key)?.tagId ?? null }))
      document.value.nodes = [...document.value.nodes.filter(n => !selectedIds.value.includes(n.id)), instance]; selectedIds.value = [instance.id]; commit()
    }
    templateVisible.value = false; ElMessage.success(tr('screenDesignerView.k6673b7', [item.name]))
  } catch (e) { templateError.value = errorMessage(e, tr('screenDesignerView.k505390')) }
}
function startPaletteDrag(type: NodeType, event: DragEvent): void { event.dataTransfer?.setData('application/scada-node', type); if (event.dataTransfer) event.dataTransfer.effectAllowed = 'copy' }
function drop(event: DragEvent): void {
  const type = event.dataTransfer?.getData('application/scada-node') as NodeType
  const rect = (event.currentTarget as SVGSVGElement).getBoundingClientRect()
  const symbol = event.dataTransfer?.getData('application/scada-symbol')
  if (symbol) { addSymbol(symbol, Math.round((event.clientX - rect.left) / zoom.value), Math.round((event.clientY - rect.top) / zoom.value)); return }
  if (!palette.some(p => p.type === type)) return
  add(type, Math.round((event.clientX - rect.left) / zoom.value), Math.round((event.clientY - rect.top) / zoom.value))
}
function beginDrag(node: ScreenNode, event: PointerEvent): void {
  if (event.button !== 0 || busy.value) return
  if (event.shiftKey) { select(node.id, true); return }
  commit(); if (!selectedIds.value.includes(node.id)) selectedIds.value = [node.id]
  drag = { x: event.clientX, y: event.clientY, nodes: JSON.parse(JSON.stringify(document.value.nodes.filter(n => selectedIds.value.includes(n.id)))), resize: false, moved: false }
  event.preventDefault()
}
function beginResize(node: ScreenNode, event: PointerEvent): void { if (busy.value) return; select(node.id); drag = { x: event.clientX, y: event.clientY, nodes: [JSON.parse(JSON.stringify(node))], resize: true, moved: false }; event.preventDefault() }
function pointerMove(event: PointerEvent): void {
  if (!drag || busy.value) return
  const grid = snap.value ? 10 : 1
  let dx = Math.round((event.clientX - drag.x) / zoom.value / grid) * grid, dy = Math.round((event.clientY - drag.y) / zoom.value / grid) * grid
  if (!drag.resize) {
    dx = Math.max(-Math.min(...drag.nodes.map(n => n.x)), Math.min(document.value.width - Math.max(...drag.nodes.map(n => n.x + n.width)), dx))
    dy = Math.max(-Math.min(...drag.nodes.map(n => n.y)), Math.min(document.value.height - Math.max(...drag.nodes.map(n => n.y + n.height)), dy))
  }
  if (dx || dy) drag.moved = true
  for (const original of drag.nodes) {
    const index = document.value.nodes.findIndex(n => n.id === original.id)
    document.value.nodes[index] = clampNode(drag.resize ? { ...original, width: original.width + dx, height: original.height + dy } : { ...original, x: original.x + dx, y: original.y + dy }, document.value)
  }
}
function pointerUp(): void { if (drag?.moved) commit(); drag = null }
function remove(): void { document.value.nodes = document.value.nodes.filter(n => !selectedIds.value.includes(n.id)); selectedIds.value = []; commit() }
function copy(): void { clipboard = JSON.parse(JSON.stringify(document.value.nodes.filter(n => selectedIds.value.includes(n.id)))) }
function paste(): void {
  if (!clipboard.length) return
  if (document.value.nodes.length + clipboard.length > 1000) { block(tr('screenDesignerView.k10f364')); return }
  guardError.value = ''
  const clones = clipboard.map(n => clampNode({ ...JSON.parse(JSON.stringify(n)), id: crypto.randomUUID(), x: n.x + 20, y: n.y + 20 }, document.value))
  document.value.nodes.push(...clones); selectedIds.value = clones.map(n => n.id); clipboard = JSON.parse(JSON.stringify(clones)); commit()
}
function duplicate(): void { copy(); paste() }
function reorder(front: boolean): void {
  const picked = document.value.nodes.filter(n => selectedIds.value.includes(n.id)), others = document.value.nodes.filter(n => !selectedIds.value.includes(n.id))
  document.value.nodes = front ? [...others, ...picked] : [...picked, ...others]; commit()
}
function align(axis: 'left' | 'top'): void { if (!selected.value) return; const pos = axis === 'left' ? selected.value.x : selected.value.y; for (const n of document.value.nodes.filter(n => selectedIds.value.includes(n.id))) { if (axis === 'left') n.x = pos; else n.y = pos }; commit() }
function resizeCanvas(): void { document.value.nodes = document.value.nodes.map(n => clampNode(n, document.value)); commit() }
async function save(): Promise<boolean> {
  if (props.embedded) { commit(); emit('save'); return false }
  if (busy.value || loadError.value) return false
  commit(); busy.value = true; actionError.value = ''
  const sent = snapshot()
  try { const data = JSON.parse(sent); const result = await screenApi.save(id, data.name, revision.value, data.document); revision.value = result.revision; savedSnapshot.value = sent; savedAt.value = formatTime(Date.now()); actionError.value = ''; ElMessage.success(tr('screenDesignerView.k9d84cf')); return true }
  catch (e) { actionError.value = errorMessage(e, tr('screenDesignerView.k991a47')); return false }
  finally { busy.value = false }
}
async function validate(): Promise<void> {
  try { issues.value = await screenApi.validate(document.value, id); issuesError.value = ''; issuesVisible.value = true }
  catch (e) { issues.value = []; issuesError.value = errorMessage(e, tr('screenDesignerView.validateFailed')); issuesVisible.value = true }
}
async function publish(): Promise<void> {
  if (busy.value || loadError.value) return
  if (dirty.value && !await save()) return
  busy.value = true; actionError.value = ''
  try {
    const result = await screenApi.publish(id, revision.value); revision.value = result.revision; publishedRevision.value = result.publishedRevision
    actionError.value = ''
    if (result.warning) ElMessage.warning({ message: result.warning, duration: 8000 }); else ElMessage.success(tr('screenDesignerView.k13d6f4'))
  } catch (e) {
    const response = e as { response?: { data?: { issues?: ScreenIssue[] } } }
    const bodyIssues = response.response?.data?.issues
    if (bodyIssues?.length) { issues.value = bodyIssues; issuesError.value = ''; issuesVisible.value = true }
    else actionError.value = errorMessage(e, tr('screenDesignerView.k7e7f5d'))
  }
  finally { busy.value = false }
}
async function focusIssue(issue: ScreenIssue): Promise<void> {
  // Expanded template children belong to the editable instance.
  const owner = document.value.nodes.find(n => n.id === issue.nodeId) ?? document.value.nodes.find(n => n.type === 'template' && issue.nodeId?.startsWith(`${n.id}--`))
  selectedIds.value = owner ? [owner.id] : []; issuesVisible.value = false
  await revealSelection()
}
function keydown(event: KeyboardEvent): void {
  const target = event.target as HTMLElement
  if (event.defaultPrevented || event.isComposing || event.altKey || loading.value || loadError.value || busy.value || previewVisible.value || issuesVisible.value || templateVisible.value) return
  if (target.closest('[role="dialog"]') || (!target.closest('.designer') && target !== window.document.body)) return
  const modifier = event.ctrlKey || event.metaKey
  if (modifier && event.key.toLowerCase() === 's') { event.preventDefault(); void save(); return }
  if (target.closest('input, textarea, select, [contenteditable="true"], .el-select, [role="dialog"]')) return
  if (modifier && event.key.toLowerCase() === 'a') { event.preventDefault(); selectedIds.value = document.value.nodes.map(n => n.id); return }
  if (event.key === 'Escape') { selectedIds.value = []; return }
  if (modifier && event.key.toLowerCase() === 'z') { event.preventDefault(); event.shiftKey ? redo() : undo(); return }
  if (modifier && event.key.toLowerCase() === 'y') { event.preventDefault(); redo(); return }
  if (modifier && event.key.toLowerCase() === 'c') { event.preventDefault(); copy(); return }
  if (modifier && event.key.toLowerCase() === 'v') { event.preventDefault(); paste(); return }
  if (event.key === 'Delete' || event.key === 'Backspace') { event.preventDefault(); remove(); return }
  if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key) && selectedIds.value.length) {
    event.preventDefault(); const step = event.shiftKey ? 10 : 1
    document.value.nodes = document.value.nodes.map(n => selectedIds.value.includes(n.id) ? clampNode({ ...n,
      x: n.x + (event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0), y: n.y + (event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0) }, document.value) : n); commit()
  }
}
function beforeUnload(event: BeforeUnloadEvent): void { if (!props.embedded && dirty.value) { event.preventDefault(); event.returnValue = '' } }
async function confirmLeave(): Promise<boolean> { if (props.embedded || !dirty.value) return true; try { await ElMessageBox.confirm(tr('screenDesignerView.k0f064e'), tr('screenDesignerView.k70ac7e'), { type: 'warning', confirmButtonText: tr('screenDesignerView.k0ba189'), cancelButtonText: tr('screenDesignerView.k3a481b') }); return true } catch { return false } }
onBeforeRouteLeave(confirmLeave)
onBeforeRouteUpdate(confirmLeave)
onMounted(async () => {
  void library.ensure()
  try {
    if (props.embedded) {
      document.value = JSON.parse(JSON.stringify(props.modelValue ?? emptyDocument())); tags.value = props.bindingTags ?? []
      name.value = tr('screenDesignerView.k4fb3e5'); stableSnapshot = snapshot(); savedSnapshot.value = stableSnapshot
      await nextTick(); await fitCanvas()
    } else {
    const [screen, allTags, allDevices, allScreens] = await Promise.all([screenApi.get(id, true), tagApi.list(), deviceApi.list(), screenApi.list(true)])
    document.value = screen.document; name.value = screen.name; revision.value = screen.revision; publishedRevision.value = screen.publishedRevision
    tags.value = allTags; devices.value = allDevices; screens.value = allScreens; stableSnapshot = snapshot(); savedSnapshot.value = stableSnapshot
    await nextTick(); await fitCanvas()
    }
  } catch (e) { loadError.value = errorMessage(e, tr('screenDesignerView.kfb670f')) } finally { loading.value = false }
  if (disposed) return
  window.addEventListener('pointermove', pointerMove); window.addEventListener('pointerup', pointerUp); window.addEventListener('pointercancel', pointerUp)
  window.addEventListener('keydown', keydown); window.addEventListener('beforeunload', beforeUnload)
})
watch(() => props.bindingTags, tagsToUse => { if (props.embedded && tagsToUse) tags.value = tagsToUse })
watch(() => props.disabled, disabled => { if (props.embedded) busy.value = disabled })
onBeforeUnmount(() => { disposed = true; window.removeEventListener('pointermove', pointerMove); window.removeEventListener('pointerup', pointerUp); window.removeEventListener('pointercancel', pointerUp); window.removeEventListener('keydown', keydown); window.removeEventListener('beforeunload', beforeUnload) })
</script>

<style scoped>
.designer { margin: 0; height: calc(100vh - 56px); min-height: 600px; display: flex; flex-direction: column; background: var(--scada-bg); }
.designer.embedded { height: 720px; border: 1px solid var(--scada-border); }
.spacer { flex: 1; }
.designer-toolbar { display: flex; gap: 8px; align-items: center; padding: 12px; background: var(--scada-panel); border-bottom: 1px solid var(--scada-border); flex-wrap: wrap; }
.revision { color: var(--scada-text-muted); font-size: 12px; } .designer-body { display: grid; grid-template-columns: 180px minmax(300px, 1fr) 240px; flex: 1; min-height: 0; }
.palette, .properties { overflow: auto; background: var(--scada-panel); padding: 14px; } .palette { border-right: 1px solid var(--scada-border); } .properties { border-left: 1px solid var(--scada-border); }
.section-title { font-weight: 600; margin-bottom: 14px; color: var(--scada-text); } .section-title small { display: block; color: var(--scada-text-muted); font-weight: 400; font-size: 11px; margin-top: 4px; }
.palette-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; margin-bottom: 24px; } .palette-grid button { border: 1px solid var(--scada-border); border-radius: 6px; padding: 8px 2px; background: var(--scada-panel-muted); color: var(--scada-text-secondary); cursor: grab; font: inherit; font-size: 12px; }
.palette-grid button:hover { border-color: var(--scada-primary); background: var(--scada-primary-soft); }.palette-grid span { display: block; font-size: 24px; height: 30px; color: var(--scada-primary); }
.symbol-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; margin-bottom: 12px; }
.symbol-grid button { border: 1px solid var(--scada-border); border-radius: 6px; padding: 4px 2px; background: #4a5560; color: #f1f3f5; cursor: grab; font: inherit; font-size: 11px; }
.symbol-grid button:hover { border-color: var(--scada-primary); }
.sym-preview { display: block; height: 34px; color: var(--scada-text-muted); } .sym-preview :deep(svg) { height: 34px; width: 100%; }
.symbol-groups { margin-bottom: 16px; } .symbol-groups :deep(.el-collapse-item__header) { height: 30px; font-size: 12px; }
.search-summary { font-size: 11px; color: var(--scada-text-muted); line-height: 1.6; }.palette > .el-input { margin-bottom: 12px; }
.layers { max-height: 270px; overflow: auto; } .layers button { display: flex; align-items: center; gap: 6px; justify-content: space-between; width: 100%; padding: 8px; border: 0; text-align: left; background: transparent; color: var(--scada-text-secondary); cursor: pointer; }
.layers button span { min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }.layers button small { flex-shrink: 0; }.palette button:focus-visible, .stage:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: -2px; }
.layers button.active { background: var(--scada-primary-soft); color: var(--scada-primary); } .layers small { font-size: 9px; color: var(--scada-text-muted); } .keyboard-help { font-size: 11px; line-height: 1.8; color: var(--scada-text-muted); padding-top: 20px; }
.workspace { display: flex; flex-direction: column; min-width: 0; min-height: 0; }.canvas-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 10px; background: var(--scada-panel-muted); }.canvas-toolbar :deep(.el-button + .el-button) { margin-left: 0; }.canvas-toolbar :deep(.el-select) { flex-shrink: 0; }
.stage { flex: 1; overflow: auto; padding: 20px; background: var(--scada-bg-subtle); min-height: 0; }.stage :deep(svg.screen-canvas) { box-shadow: 0 4px 20px #1f334c26; }
.status-line { padding: 7px 14px; display: flex; gap: 24px; font-size: 11px; color: var(--scada-text-muted); background: var(--scada-panel); }
.field-pair { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }.field-pair :deep(.el-input-number) { width: 100%; }.properties :deep(.el-select) { width: 100%; }
.properties input[type=color] { width: 100%; height: 30px; padding: 2px; border: 1px solid var(--scada-border); border-radius: 4px; }.properties p { font-size: 12px; line-height: 1.8; }
.busy .designer-body { pointer-events: none; opacity: .75; }.busy .designer-toolbar { pointer-events: none; }.issue-list { line-height: 2; padding-left: 18px; }.issue-list button { border: 0; background: transparent; color: var(--scada-primary); cursor: pointer; }
@media (max-width: 1250px) { .designer-body { grid-template-columns: 140px minmax(260px, 1fr) 210px; }.palette, .properties { padding: 10px; }.revision { display: none; } }
</style>
