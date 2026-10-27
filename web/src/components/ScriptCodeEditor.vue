<template><div ref="container" class="script-code-editor" data-test="calc-job-csharp-editor" /></template>
<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import * as monaco from 'monaco-editor/editor/editor.api.js'
import 'monaco-editor/languages/definitions/csharp/register.js'
import EditorWorker from 'monaco-editor/editor/editor.worker.js?worker'

const props = defineProps<{ modelValue: string; diagnostics: string[]; disabled?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const container = ref<HTMLDivElement>()
let editor: monaco.editor.IStandaloneCodeEditor | undefined
let model: monaco.editor.ITextModel | undefined
let subscription: monaco.IDisposable | undefined
const environment = globalThis as typeof globalThis & { MonacoEnvironment?: monaco.Environment }
environment.MonacoEnvironment = { getWorker: () => new EditorWorker() }

function updateDiagnostics(): void {
  if (!model) return
  monaco.editor.setModelMarkers(model, 'scada', props.diagnostics.map(message => {
    const position = /script\.cs\((\d+),(\d+)\)/.exec(message)
    const line = Math.min(model!.getLineCount(), Number(position?.[1] ?? 1))
    const column = Math.min(model!.getLineMaxColumn(line), Number(position?.[2] ?? 1))
    return { severity: monaco.MarkerSeverity.Error, message, startLineNumber: line, startColumn: column, endLineNumber: line, endColumn: column + 1 }
  }))
}
onMounted(() => {
  model = monaco.editor.createModel(props.modelValue, 'csharp')
  editor = monaco.editor.create(container.value!, {
    model, automaticLayout: true, minimap: { enabled: false }, fontSize: 14, lineHeight: 21,
    fontFamily: 'Consolas, monospace', wordWrap: 'on', scrollBeyondLastLine: false,
    readOnly: props.disabled, tabSize: 4, padding: { top: 10, bottom: 10 },
    ariaLabel: 'C# script', fixedOverflowWidgets: true, stickyScroll: { enabled: false }
  })
  subscription = editor.onDidChangeModelContent(() => emit('update:modelValue', editor!.getValue()))
  updateDiagnostics()
})
watch(() => props.modelValue, value => { if (model && model.getValue() !== value) model.setValue(value) })
watch(() => props.disabled, value => editor?.updateOptions({ readOnly: value }))
watch(() => props.diagnostics, updateDiagnostics)
onBeforeUnmount(() => { subscription?.dispose(); editor?.dispose(); model?.dispose() })
</script>
<style scoped>
.script-code-editor { width: 100%; min-width: 0; height: 300px; border: 1px solid var(--el-border-color); border-radius: 4px; overflow: hidden; }
</style>