import { computed, ref, type ComputedRef } from 'vue'
import { deviceApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'
import { useAuthStore } from '../stores/auth'
import type { Device, Tag } from '../types'
import { can, canGlobal } from '../access/policies'
import { canActOnDevice } from '../utils'
import type { ScreenNode } from './model'

// 运行态测点/设备目录：控制权限判断依赖设备所属区域，故目录加载与权限判定放在一起。
export function useScreenCatalogs(ctx: { isDisposed: () => boolean; monitorMode: () => boolean; runtimeNodes: ComputedRef<ScreenNode[]> }) {
  const auth = useAuthStore()
  const devices = ref<Device[]>([])
  const deviceError = ref('')
  const tagsById = ref(new Map<number, Tag>())
  const tagCatalog = ref<'pending' | 'ready' | 'failed'>('pending')
  const tagError = ref('')

  function canControlBoundTag(tagId?: number | null): boolean {
    if (ctx.monitorMode()) return false
    if (!can(auth.user, 'Control')) return false
    if (tagId == null) return false
    const tag = tagsById.value.get(tagId)
    if (!tag) return tagCatalog.value === 'failed' ? false : tagCatalog.value === 'pending' || canGlobal(auth.user, 'Control')
    if (tag.areaId != null) return can(auth.user, 'Control', tag.areaId)
    if (tag.deviceId != null) return canActOnDevice(auth.user, 'Control', devices.value, tag.deviceId)
    return canGlobal(auth.user, 'Control')
  }

  const needsDeviceCatalog = computed(() => ctx.runtimeNodes.value.some(node => [node.action.type === 'command' ? node.action.tagId : null, ...(node.commands ?? []).map(command => command.tagId)].some(tagId => {
    const tag = tagId == null ? undefined : tagsById.value.get(tagId)
    return !!tag && tag.areaId == null && tag.deviceId != null
  })))
  // 画面内表格/告警列表需要测点名称与单位。
  const tagMeta = computed(() => {
    const meta: Record<number, { name: string; unit: string }> = {}
    for (const tag of tagsById.value.values()) meta[tag.id] = { name: tag.name, unit: tag.unit ?? '' }
    return meta
  })
  const sampleMaxAge = computed(() => {
    const intervals = new Map(devices.value.map(device => [device.id, device.pollIntervalMs]))
    const ages: Record<number, number> = {}
    for (const tag of tagsById.value.values()) ages[tag.id] = Math.max(15000, (tag.kind === 'Calculated' ? tag.calculationIntervalMs ?? 0 : intervals.get(tag.deviceId ?? 0) ?? 0) * 3)
    return ages
  })

  async function loadTags(): Promise<void> {
    try {
      const tags = await tagApi.list()
      if (ctx.isDisposed()) return
      tagsById.value = new Map(tags.map(tag => [tag.id, tag]))
      tagCatalog.value = 'ready'; tagError.value = ''
    } catch (e) {
      if (ctx.isDisposed()) return
      tagsById.value = new Map()
      tagCatalog.value = 'failed'; tagError.value = errorMessage(e, tr('screenMonitor.tagsFailed'))
    }
  }

  async function loadDevices(): Promise<void> {
    try { devices.value = await deviceApi.list(); deviceError.value = '' }
    catch (e) { deviceError.value = errorMessage(e, tr('screenRuntime.devicesLoadFailed')) }
  }

  function deviceCatalogBlocks(tag: Tag): boolean {
    if (!deviceError.value || tag.areaId != null || tag.deviceId == null || canGlobal(auth.user, 'Control')) return false
    return devices.value.find(d => d.id === tag.deviceId)?.areaId == null
  }

  return { auth, devices, deviceError, tagsById, tagCatalog, tagError, canControlBoundTag, needsDeviceCatalog, tagMeta, sampleMaxAge, loadTags, loadDevices, deviceCatalogBlocks }
}
