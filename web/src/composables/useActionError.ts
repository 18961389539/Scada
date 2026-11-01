import { ref, type Ref } from 'vue'
import { commandErrorMessage, errorMessage } from '../api/http'

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

  /**
   * 现场动作专用（启停顺控、挂牌/摘牌、就地远程切换、中止）。
   * 与配置保存的区别在于：超时/断网时无法判断动作是否已经生效，
   * 此时提示「请稍后重试」会诱导操作员重复动作一次。见 commandErrorMessage。
   */
  async function guardCommand(
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
      target.value = commandErrorMessage(error, fallback)
      return false
    }
  }

  return { actionError, guard, guardCommand }
}