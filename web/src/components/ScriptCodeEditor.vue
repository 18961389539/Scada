<template>
  <div class="script-code-frame" :data-test="file === 'library.cs' ? 'script-library-source' : 'calc-job-csharp-editor'">
    <div ref="container" class="script-code-editor" />
  </div>
</template>
<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import * as monaco from 'monaco-editor/editor/editor.api.js'
import 'monaco-editor/languages/definitions/csharp/register.js'
import EditorWorker from 'monaco-editor/editor/editor.worker.js?worker'
import type { ScriptDiagnosticSpan } from '../api/calcJobs'
import { tr } from '../i18n'
import { scriptSnippets } from './scriptSnippets'

const props = withDefaults(defineProps<{
  modelValue: string
  diagnostics?: ScriptDiagnosticSpan[]
  readTags?: string[]
  writeTags?: string[]
  functions?: { name: string; detail: string }[]
  known?: (token: string, list: 'read' | 'write') => boolean
  acceptTag?: (token: string, list: 'read' | 'write') => void
  file?: 'script.cs' | 'library.cs'
  disabled?: boolean
  maxLength?: number
}>(), {
  diagnostics: () => [], readTags: () => [], writeTags: () => [], functions: () => [],
  known: () => true, file: 'script.cs'
})
const emit = defineEmits<{ 'update:modelValue': [value: string]; save: []; preview: [] }>()
const container = ref<HTMLDivElement>()
let editor: monaco.editor.IStandaloneCodeEditor | undefined
let model: monaco.editor.ITextModel | undefined
let subscription: monaco.IDisposable | undefined
let themeObserver: MutationObserver | undefined

interface EditorHints {
  readTags: string[]
  writeTags: string[]
  functions: { name: string; detail: string }[]
  known: (token: string, list: 'read' | 'write') => boolean
  accept?: (token: string, list: 'read' | 'write') => void
}
const globalState = globalThis as typeof globalThis & {
  MonacoEnvironment?: monaco.Environment
  __scadaCSharpHints?: Map<monaco.editor.ITextModel, EditorHints>
  __scadaCSharpCompletion?: boolean
}
globalState.MonacoEnvironment = { getWorker: () => new EditorWorker() }
globalState.__scadaCSharpHints ??= new Map()
const hints = globalState.__scadaCSharpHints

function memberItems(range: monaco.IRange): monaco.languages.CompletionItem[] {
  const methods = [
    ['Read', 'designReview2.scriptMemberRead', 'Read("${1:tag}")'],
    ['ReadSample', 'designReview2.scriptMemberReadSample', 'ReadSample("${1:tag}")'],
    ['Write', 'designReview2.scriptMemberWrite', 'Write("${1:tag}", ${2:0})'],
    ['Output', 'designReview2.scriptMemberOutput', 'Output("${1:name}", ${2:0})'],
    ['History', 'designReview2.scriptMemberHistory', 'History("${1:tag}")'],
    ['Alarms', 'designReview2.scriptMemberAlarms', 'Alarms()'],
    ['Log', 'designReview2.scriptMemberLog', 'Log("${1}")']
  ] as const
  const properties = [
    ['Now', 'designReview2.scriptMemberNow'],
    ['DeltaSeconds', 'designReview2.scriptMemberDelta'],
    ['HasArgument', 'designReview2.scriptMemberHasArgument'],
    ['Argument', 'designReview2.scriptMemberArgument']
  ] as const
  return [
    ...methods.map(([label, key, insertText]) => item(label, tr(key), insertText, monaco.languages.CompletionItemKind.Method, range, true)),
    ...properties.map(([label, key]) => item(label, tr(key), label, monaco.languages.CompletionItemKind.Property, range, false))
  ]
}

function sampleItems(range: monaco.IRange): monaco.languages.CompletionItem[] {
  return [
    ['Value', 'designReview2.scriptMemberValue'],
    ['Quality', 'designReview2.scriptMemberQuality'],
    ['Timestamp', 'designReview2.scriptMemberTimestamp'],
    ['Good', 'designReview2.scriptMemberGood']
  ].map(([label, key]) => item(label, tr(key), label, monaco.languages.CompletionItemKind.Property, range, false))
}

function alarmItems(range: monaco.IRange): monaco.languages.CompletionItem[] {
  return [
    ['Id', 'designReview2.scriptMemberAlarmId'],
    ['Level', 'designReview2.scriptMemberLevel'],
    ['Message', 'designReview2.scriptMemberMessage'],
    ['Acknowledged', 'designReview2.scriptMemberAcknowledged'],
    ['StartedAt', 'designReview2.scriptMemberStarted']
  ].map(([label, key]) => item(label, tr(key), label, monaco.languages.CompletionItemKind.Property, range, false))
}

function item(label: string, detail: string, insertText: string, kind: monaco.languages.CompletionItemKind, range: monaco.IRange, snippet: boolean): monaco.languages.CompletionItem {
  return {
    label, detail, insertText, kind, range, sortText: label, filterText: label,
    insertTextRules: snippet ? monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet : undefined
  }
}

function textBefore(textModel: monaco.editor.ITextModel, position: monaco.Position): string {
  return textModel.getValueInRange({ startLineNumber: 1, startColumn: 1, endLineNumber: position.lineNumber, endColumn: position.column })
}

function quoteOpen(text: string): boolean {
  let open = false
  for (let index = 0; index < text.length; index++) {
    if (text[index] === '\\') { index++; continue }
    if (text[index] === '"') open = !open
  }
  return open
}

function openTag(text: string): { method: string; partial: string; start: number } | null {
  let quote = -1
  let open = false
  for (let index = 0; index < text.length; index++) {
    if (text[index] === '\\') { index++; continue }
    if (text[index] === '"') { open = !open; if (open) quote = index }
  }
  if (!open || quote < 0) return null
  const call = /context\s*\.\s*(ReadSample|History|Read|Write)\s*\(\s*$/.exec(text.slice(0, quote))
  if (!call) return null
  return { method: call[1], partial: text.slice(quote + 1), start: quote + 1 }
}

function declaredKind(source: string, name: string): 'sample' | 'alarm' | null {
  const ident = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  if (new RegExp(`(?:\\b${ident}\\s*=\\s*context\\s*\\.\\s*ReadSample\\b|foreach\\s*\\(\\s*(?:var\\s+)?${ident}\\s+in\\s+context\\s*\\.\\s*History\\b)`).test(source)) return 'sample'
  if (new RegExp(`(?:\\b${ident}\\s*=\\s*context\\s*\\.\\s*Alarms\\b|foreach\\s*\\(\\s*(?:var\\s+)?${ident}\\s+in\\s+context\\s*\\.\\s*Alarms\\b)`).test(source)) return 'alarm'
  return null
}

function provideCompletion(textModel: monaco.editor.ITextModel, position: monaco.Position): monaco.languages.CompletionList {
  const context = hints.get(textModel)
  if (!context) return { suggestions: [] }
  const before = textBefore(textModel, position)
  const line = textModel.getValueInRange({ startLineNumber: position.lineNumber, startColumn: 1, endLineNumber: position.lineNumber, endColumn: position.column })
  const tag = openTag(before)
  if (!tag && (line.includes('//') || quoteOpen(before))) return { suggestions: [] }
  const word = textModel.getWordUntilPosition(position)
  const wordRange = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn: word.startColumn, endColumn: word.endColumn }
  const dotted = /([A-Za-z_]\w*)\s*\.\s*(\w*)$/.exec(before)
  if (dotted) {
    const startColumn = Math.max(1, position.column - dotted[2].length)
    const range = { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber, startColumn, endColumn: position.column }
    if (dotted[1] === 'context') return { suggestions: memberItems(range) }
    const chain = /context\s*\.\s*(?:ReadSample\s*\([^)]*\)|History\s*\([^)]*\)\s*\[[^\]]*\])\s*\.\s*\w*$/.test(before) ? 'sample'
      : /context\s*\.\s*Alarms\s*\(\s*\)\s*\[[^\]]*\]\s*\.\s*\w*$/.test(before) ? 'alarm'
        : declaredKind(textModel.getValue(), dotted[1])
    if (chain === 'sample') return { suggestions: sampleItems(range) }
    if (chain === 'alarm') return { suggestions: alarmItems(range) }
  }
  if (tag) {
    const start = textModel.getPositionAt(tag.start)
    const range = { startLineNumber: start.lineNumber, startColumn: start.column, endLineNumber: position.lineNumber, endColumn: position.column }
    const labels = tag.method === 'Write' ? context.writeTags : context.readTags
    return { suggestions: labels.map(label => item(label, label, label, monaco.languages.CompletionItemKind.Value, range, false)) }
  }
  return {
    suggestions: [
      ...scriptSnippets().map(snippet => ({ ...item(snippet.label, snippet.label, snippet.insert, monaco.languages.CompletionItemKind.Snippet, wordRange, true), sortText: `0${snippet.label}`, filterText: snippet.label })),
      ...context.functions.map(fn => item(fn.name, fn.detail, `${fn.name}($0)`, monaco.languages.CompletionItemKind.Function, wordRange, true))
    ]
  }
}

function provideActions(textModel: monaco.editor.ITextModel, _range: monaco.Range, context: monaco.languages.CodeActionContext): monaco.languages.CodeActionList {
  const hint = hints.get(textModel)
  const actions: monaco.languages.CodeAction[] = []
  if (!hint?.accept) return { actions, dispose() {} }
  for (const marker of context.markers) {
    const code = typeof marker.code === 'string' ? marker.code : marker.code?.value
    if (code !== 'scada-stale' || !marker.source) continue
    const preferred = marker.source.startsWith('write:') ? 'write' : 'read'
    const token = marker.source.slice(preferred.length + 1)
    for (const list of [preferred, preferred === 'read' ? 'write' : 'read'] as const) {
      actions.push({
        title: tr(list === 'read' ? 'designReview2.scriptAddRead' : 'designReview2.scriptAddWrite'),
        diagnostics: [marker],
        kind: 'quickfix',
        isPreferred: list === preferred,
        command: { id: 'scada.addScriptTag', title: tr('designReview2.scriptAddRead'), arguments: [textModel.uri.toString(), token, list] }
      })
    }
  }
  return { actions, dispose() {} }
}

if (!globalState.__scadaCSharpCompletion) {
  globalState.__scadaCSharpCompletion = true
  monaco.languages.registerCompletionItemProvider('csharp', { triggerCharacters: ['.', '"', '\n'], provideCompletionItems: provideCompletion })
  monaco.languages.registerCodeActionProvider('csharp', { provideCodeActions: provideActions })
  monaco.editor.registerCommand('scada.addScriptTag', (_accessor, uri: string, token: string, list: string) => {
    const textModel = monaco.editor.getModel(monaco.Uri.parse(uri))
    const hint = textModel ? hints.get(textModel) : undefined
    if (hint?.accept && (list === 'read' || list === 'write')) hint.accept(token, list)
  })
}

function syncHints(): void {
  if (model) hints.set(model, { readTags: props.readTags, writeTags: props.writeTags, functions: props.functions, known: props.known, accept: props.acceptTag })
}

function staleMarkers(): monaco.editor.IMarkerData[] {
  if (!model) return []
  const hint = hints.get(model)
  if (!hint) return []
  const text = model.getValue().replace(/\/\*[\s\S]*?\*\//g, match => ' '.repeat(match.length)).replace(/\/\/.*$/gm, match => ' '.repeat(match.length))
  const markers: monaco.editor.IMarkerData[] = []
  for (const match of text.matchAll(/context\s*\.\s*(ReadSample|History|Read|Write)\s*\(\s*"([^"\\]*)"/g)) {
    const token = match[2]
    if (!token || match.index == null) continue
    const list = match[1] === 'Write' ? 'write' : 'read'
    if (hint.known(token, list)) continue
    const tokenAt = match.index + match[0].lastIndexOf(`"${token}`) + 1
    const start = model.getPositionAt(tokenAt)
    const end = model.getPositionAt(tokenAt + token.length)
    markers.push({
      severity: monaco.MarkerSeverity.Error, message: tr('designReview2.scriptTagMissing'),
      startLineNumber: start.lineNumber, startColumn: start.column, endLineNumber: end.lineNumber, endColumn: end.column,
      source: `${list}:${token}`, code: 'scada-stale'
    })
  }
  return markers
}

function updateDiagnostics(): void {
  if (!model) return
  const server = props.diagnostics.filter(span => span.file === props.file).map(span => {
    const startLineNumber = Math.min(Math.max(1, span.startLine), model!.getLineCount())
    const endLineNumber = Math.min(Math.max(startLineNumber, span.endLine), model!.getLineCount())
    const startColumn = Math.min(Math.max(1, span.startColumn), model!.getLineMaxColumn(startLineNumber))
    let endColumn = Math.min(Math.max(1, span.endColumn), model!.getLineMaxColumn(endLineNumber))
    if (endLineNumber === startLineNumber && endColumn <= startColumn) endColumn = Math.min(model!.getLineMaxColumn(endLineNumber), startColumn + 1)
    return { severity: monaco.MarkerSeverity.Error, message: span.message, startLineNumber, startColumn, endLineNumber, endColumn }
  })
  monaco.editor.setModelMarkers(model, 'scada', [...server, ...staleMarkers()])
}

function reveal(line: number, column: number): void {
  if (!editor || !model) return
  const lineNumber = Math.min(Math.max(1, line), model.getLineCount())
  const columnNumber = Math.min(Math.max(1, column), model.getLineMaxColumn(lineNumber))
  editor.revealLineInCenter(lineNumber)
  editor.setPosition({ lineNumber, column: columnNumber })
  editor.focus()
}

function insertSnippet(snippet: string): void {
  editor?.focus()
  editor?.trigger('script', 'editor.action.insertSnippet', { snippet })
}

function syncTheme(): void {
  monaco.editor.setTheme(document.documentElement.classList.contains('dark') ? 'vs-dark' : 'vs')
}

onMounted(() => {
  model = monaco.editor.createModel(props.modelValue, 'csharp')
  syncHints()
  syncTheme()
  themeObserver = new MutationObserver(syncTheme)
  themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })
  editor = monaco.editor.create(container.value!, {
    model, automaticLayout: true, minimap: { enabled: false }, fontSize: 14, lineHeight: 21,
    fontFamily: 'Consolas, monospace', wordWrap: 'on', scrollBeyondLastLine: false,
    readOnly: props.disabled, tabSize: 4, padding: { top: 10, bottom: 10 },
    ariaLabel: 'C# script', fixedOverflowWidgets: true, stickyScroll: { enabled: false },
    wordBasedSuggestions: 'off', snippetSuggestions: 'top',
    quickSuggestions: { other: true, comments: false, strings: true }
  })
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => emit('preview'))
  editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => emit('save'))
  subscription = editor.onDidChangeModelContent(() => {
    const value = editor!.getValue()
    if (props.maxLength && value.length > props.maxLength) {
      const position = editor!.getPosition()
      editor!.setValue(value.slice(0, props.maxLength))
      const next = editor!.getModel()
      if (position && next) {
        const lineNumber = Math.min(position.lineNumber, next.getLineCount())
        editor!.setPosition({ lineNumber, column: Math.min(position.column, next.getLineMaxColumn(lineNumber)) })
      }
      return
    }
    emit('update:modelValue', value)
    updateDiagnostics()
  })
  updateDiagnostics()
})
watch(() => props.modelValue, value => { if (model && model.getValue() !== value) { model.setValue(value); updateDiagnostics() } })
watch(() => props.disabled, value => editor?.updateOptions({ readOnly: value }))
watch(() => props.diagnostics, updateDiagnostics)
watch(() => [props.readTags, props.writeTags, props.functions], () => { syncHints(); updateDiagnostics() })
onBeforeUnmount(() => { if (model) hints.delete(model); themeObserver?.disconnect(); subscription?.dispose(); editor?.dispose(); model?.dispose() })
defineExpose({ reveal, insertSnippet })
</script>
<style scoped>
.script-code-frame { width: 100%; min-width: 0; height: 420px; min-height: 220px; max-height: 70vh; resize: vertical; overflow: hidden; border: 1px solid var(--el-border-color); border-radius: 4px; }
.script-code-editor { width: 100%; height: 100%; }
</style>
