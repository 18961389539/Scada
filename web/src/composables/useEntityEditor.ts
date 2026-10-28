import { onBeforeUnmount, reactive, ref, type Ref } from 'vue'
import { ElMessage, ElMessageBox, type FormInstance } from 'element-plus'
import { applyFieldErrors, clearFieldErrors, serverFormErrors, type FieldErrorMap } from './formErrors'
import { errorMessage } from '../api/http'

export interface EntityEditorMessages {
  validationFailed: string
  saveFailed: string
  saved: string
  conflictHint: (message: string) => string
  removeConfirm: string
  removeTitle: string
  removeText: string
  cancelText: string
  removeFailed: string
}

/**
 * 主数据表单的保存/删除流程：校验、字段级服务端错误、409 冲突、成功提示、删除确认。
 * 页面只保留业务字段与 API 调用，避免每个 CRUD 页面重复一套 saving/fieldErrors/confirm 逻辑。
 */
export function useEntityEditor(actionError: Ref<string>, messages: EntityEditorMessages) {
  const saving = ref(false)
  const fieldErrors = reactive<FieldErrorMap>({})
  const formError = ref('')
  let disposed = false
  onBeforeUnmount(() => { disposed = true })

  function resetErrors(): void {
    clearFieldErrors(fieldErrors)
    formError.value = ''
  }

  function isConflict(error: unknown): boolean {
    return (error as { response?: { status?: number } } | undefined)?.response?.status === 409
  }

  async function saveWith(
    formEl: FormInstance | undefined,
    submit: () => Promise<unknown>,
    after: () => Promise<void> | void = () => undefined
  ): Promise<boolean> {
    if (disposed || saving.value) return false
    saving.value = true
    resetErrors()
    try {
      try {
        await formEl?.validate()
      } catch {
        if (!disposed) formError.value = messages.validationFailed
        return false
      }
      if (disposed) return false
      try {
        await submit()
      } catch (error) {
        if (disposed) return false
        const { message, fields } = serverFormErrors(error, messages.saveFailed)
        applyFieldErrors(fieldErrors, fields)
        formError.value = isConflict(error) ? messages.conflictHint(message) : message
        return false
      }
      if (disposed) return false
      ElMessage.success(messages.saved)
      await after()
      return !disposed
    } finally {
      saving.value = false
    }
  }

  async function remove(
    remove: () => Promise<unknown>,
    after: () => Promise<void> | void = () => undefined
  ): Promise<void> {
    try {
      await ElMessageBox.confirm(messages.removeConfirm, messages.removeTitle, {
        type: 'warning',
        confirmButtonText: messages.removeText,
        cancelButtonText: messages.cancelText
      })
    } catch {
      return
    }
    actionError.value = ''
    try {
      await remove()
      await after()
    } catch (error) {
      actionError.value = errorMessage(error, messages.removeFailed)
    }
  }

  return { saving, fieldErrors, formError, resetErrors, saveWith, remove }
}