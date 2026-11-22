import { h } from 'vue'
import { ElMessage } from 'element-plus'
import type { Router } from 'vue-router'
import { tr } from '../i18n'
import { pendingApprovalsPath } from './pendingApprovals'

/** 提交进入双人复核后的提示：保留原文，并给出打开待复核的动作。 */
export function notifySubmittedForApproval(message: string, router?: Pick<Router, 'push'>): void {
  ElMessage({
    type: 'warning',
    duration: 8000,
    showClose: true,
    message: h('span', { class: 'pending-approval-toast', 'data-test': 'pending-approval-toast' }, [
      h('span', message),
      h('button', {
        type: 'button',
        class: 'pending-approval-toast__action',
        'data-test': 'open-pending-approvals',
        onClick: () => { void (router ?? fallbackRouter()).push(pendingApprovalsPath) }
      }, tr('pendingApprovals.openList'))
    ])
  })
}

function fallbackRouter(): Pick<import('vue-router').Router, 'push'> {
  return {
    push: (location) => import('../router').then(({ default: router }) => router.push(location))
  }
}
