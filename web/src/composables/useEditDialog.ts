import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch, type Ref } from 'vue'
import { onBeforeRouteLeave, onBeforeRouteUpdate } from 'vue-router'
import { ElMessageBox, type FormInstance } from 'element-plus'
import { tr } from '../i18n'

interface EditDialogOptions {
  visible: Ref<boolean>
  saving: Ref<boolean>
  form: Ref<FormInstance | undefined>
  snapshot: () => string
  initialField: string
  save: () => Promise<void>
}

function fieldInput(field: HTMLElement | undefined): HTMLElement | undefined {
  return field?.querySelector<HTMLElement>('input:not([type="hidden"]):not(:disabled), textarea:not(:disabled), [role="combobox"]:not([aria-disabled="true"]), button:not(:disabled)') ?? undefined
}

/** Focus errors after the saving state has released the form controls. */
export async function focusFormError(form: FormInstance | undefined): Promise<void> {
  await nextTick()
  const root = form?.$el as HTMLElement | undefined
  if (!root?.isConnected) return
  const field = root.querySelector<HTMLElement>('.el-form-item.is-error')
  const target = fieldInput(field ?? undefined) ?? root.closest('.el-dialog')?.querySelector<HTMLElement>('.el-alert--error[tabindex]')
  if (!target) return
  target.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  target.focus({ preventScroll: true })
}

export function useEditDialog(options: EditDialogOptions) {
  const baseline = ref('')
  const dirty = computed(() => options.visible.value && baseline.value !== options.snapshot())
  const modKey = /Mac|iPhone|iPod|iPad/i.test(navigator.platform) ? '⌘' : 'Ctrl'
  let confirming: Promise<boolean> | undefined

  watch(options.visible, visible => {
    baseline.value = visible ? options.snapshot() : ''
  }, { flush: 'sync' })

  async function canLeave(): Promise<boolean> {
    if (!options.visible.value) return true
    if (options.saving.value) return false
    if (!dirty.value) return true
    if (confirming) return confirming
    confirming = ElMessageBox.confirm(tr('editDialog.unsavedMessage'), tr('editDialog.unsavedTitle'), {
      type: 'warning',
      confirmButtonText: tr('editDialog.discard'),
      cancelButtonText: tr('editDialog.keepEditing'),
      closeOnClickModal: false
    }).then(() => true, () => false).finally(() => { confirming = undefined })
    return confirming
  }

  async function close(): Promise<void> {
    if (await canLeave()) options.visible.value = false
  }

  function beforeClose(done: () => void): void {
    void canLeave().then(allowed => { if (allowed) done() })
  }

  async function focusInitial(): Promise<void> {
    await nextTick()
    const field = options.form.value?.getField(options.initialField)?.$el
    fieldInput(field)?.focus({ preventScroll: true })
  }

  function onKey(event: KeyboardEvent): void {
    if (event.isComposing || event.keyCode === 229) return
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.altKey) {
      event.preventDefault()
      void options.save()
    }
  }

  function beforeUnload(event: BeforeUnloadEvent): void {
    if (!options.visible.value || (!dirty.value && !options.saving.value)) return
    event.preventDefault()
    event.returnValue = ''
  }

  onBeforeRouteLeave(canLeave)
  onBeforeRouteUpdate(canLeave)
  onMounted(() => window.addEventListener('beforeunload', beforeUnload))
  onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload))
  return { beforeClose, close, focusInitial, onKey, modKey }
}
