import { computed, ref, type Ref } from 'vue'
import { healthApi } from '../api'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'
import { hubState as realtimeHubState } from '../realtime/hub'
import type { Health } from '../types'

export type HubIssueState = 'reconnecting' | 'disconnected' | string

/** 与顶栏文案一致：只在重连/断开时占用位置。 */
export function hubIssueText(state: HubIssueState, translate: (key: string) => string = tr): string {
  if (state === 'reconnecting') return translate('appLayout.hubReconnecting')
  if (state === 'disconnected') return translate('appLayout.hubDisconnected')
  return ''
}

export function isHealthAbnormal(health: Health | null, healthError: string): boolean {
  return !!healthError || (!!health && health.status !== 'ok')
}

export function isStandbyRole(health: Health | null): boolean {
  return health?.ha?.role === 'Standby'
}

export function standbyPrimaryUrl(health: Health | null): string {
  return health?.ha?.primaryUrl ?? ''
}

export function healthHintText(health: Health | null, translate: (key: string, args?: unknown[]) => string = tr): string {
  if (!health) return ''
  const free = health.disk?.freeGb
  return translate('appLayout.healthDetail', [health.onlineDevices, health.totalDevices, health.busBacklog, free == null ? '--' : free.toFixed(1)])
}

/**
 * 控制室与主壳层共用同一套服务/主备/连接指示，避免两套判定漂移。
 * 调用方负责轮询节奏；本 composable 只保存最近一次健康快照并派生状态。
 */
export function useServiceStatus() {
  const health = ref<Health | null>(null)
  const healthError = ref('')
  const hubState = realtimeHubState()
  const hubIssue = computed(() => hubIssueText(hubState.value))
  const healthAbnormal = computed(() => isHealthAbnormal(health.value, healthError.value))
  const healthHint = computed(() => healthHintText(health.value))
  const isStandby = computed(() => isStandbyRole(health.value))
  const primaryUrl = computed(() => standbyPrimaryUrl(health.value))
  const showServiceStatus = computed(() => healthAbnormal.value || !!hubIssue.value || isStandby.value)

  async function refreshHealth(): Promise<void> {
    try {
      health.value = await healthApi.get()
      healthError.value = ''
    } catch (error) {
      healthError.value = errorMessage(error, tr('appLayout.healthFailed'))
    }
  }

  return {
    health: health as Ref<Health | null>,
    healthError,
    healthAbnormal,
    healthHint,
    hubIssue,
    isStandby,
    primaryUrl,
    showServiceStatus,
    refreshHealth
  }
}
