import { ref, type Ref } from 'vue'
import { errorMessage } from '../api/http'

/** 统一的“执行写操作并落到页面错误位”的流程，避免各 CRUD 页面各写一份 guard。 */
export function useActionError() {
  const actionError = ref('')

  async function guard(
    action: () => Promise<unknown>,
    fallback: string,
    target: Ref<string> = actionError
  ): Promise<boolean> {
    target.value = ''
    try {
      await action()
      target.value = ''
      return true
    } catch (error) {
      target.value = errorMessage(error, fallback)
      return false
    }
  }

  return { actionError, guard }
}