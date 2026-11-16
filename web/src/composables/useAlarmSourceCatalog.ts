import { computed, ref } from 'vue'
import { deviceApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import type { AlarmEvent, Device, Tag } from '../types'
import { can } from '../access/policies'
import { canAckAlarmEvent, formatValue } from '../utils'
import { tr } from '../i18n'
import type { useAuthStore } from '../stores/auth'

type AuthStore = ReturnType<typeof useAuthStore>

/**
 * 报警列表依赖的设备/测点目录：来源名称、单位、区域权限判断都来自这里。
 * 从 useAlarmsView 抽出，加载失败与重试只在这一处处理。
 */
export function useAlarmSourceCatalog(auth: AuthStore, isDisposed: () => boolean) {
  const canAck = computed(() => can(auth.user, 'AckAlarm'))
  const deviceAreas = ref<Map<number, number>>(new Map())
  const sourceDevices = ref(new Map<number, Device>())
  const sourceTags = ref(new Map<number, Tag>())
  const sourceCatalogLoading = ref(false)
  const sourceCatalogError = ref('')

  function alarmSource(alarm: AlarmEvent): string {
    const tag = alarm.tagId ? sourceTags.value.get(alarm.tagId) : undefined
    const deviceId = alarm.deviceId ?? tag?.deviceId
    const device = deviceId ? sourceDevices.value.get(deviceId) : undefined
    return [device?.name ?? (deviceId ? tr('alarmListReview.deviceId', [deviceId]) : ''),
      tag?.name ?? (alarm.tagId ? tr('alarmListReview.tagId', [alarm.tagId]) : '')]
      .filter(Boolean).join(' / ') || tr('overviewStatus.systemEvent')
  }

  // 报警值必须带单位：一屏里压力、温度、流量混排，只写「500.00」无法判断量级。
  // 数字量报警显示 0/1 而不套两位小数——「1.00」会让开关量看着像模拟量。
  function alarmValueText(alarm: AlarmEvent): string {
    const tag = alarm.tagId ? sourceTags.value.get(alarm.tagId) : undefined
    if (tag?.dataType === 'Bool') return alarm.value ? '1' : '0'
    const text = formatValue(alarm.value)
    return tag?.unit ? `${text} ${tag.unit}` : text
  }

  async function loadSourceCatalog(): Promise<void> {
    if (isDisposed() || sourceCatalogLoading.value) return
    sourceCatalogLoading.value = true
    try {
      const [devices, tags] = await Promise.allSettled([deviceApi.list(), tagApi.list()])
      if (isDisposed()) return
      if (devices.status === 'fulfilled') {
        sourceDevices.value = new Map(devices.value.map(device => [device.id, device]))
        deviceAreas.value = new Map(devices.value.filter(device => device.areaId != null).map(device => [device.id, device.areaId!]))
      }
      if (tags.status === 'fulfilled') sourceTags.value = new Map(tags.value.map(tag => [tag.id, tag]))
      sourceCatalogError.value = [devices, tags].flatMap(result => result.status === 'rejected' ? [errorMessage(result.reason)] : []).join(' · ')
    } finally {
      sourceCatalogLoading.value = false
    }
  }

  function canAckAlarm(row: AlarmEvent): boolean {
    return canAckAlarmEvent(auth.user, row, deviceAreas.value)
  }

  function canSilencePanel(areaId: number | null): boolean {
    if (!canAck.value) return false
    return areaId == null || can(auth.user, 'AckAlarm', areaId)
  }

  return {
    canAck,
    deviceAreas,
    sourceDevices,
    sourceTags,
    sourceCatalogLoading,
    sourceCatalogError,
    alarmSource,
    alarmValueText,
    loadSourceCatalog,
    canAckAlarm,
    canSilencePanel
  }
}
