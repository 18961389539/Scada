import type { DeviceAcquisitionInfo } from '../types'

/** 路径刚切换后测点会标成不确定的提示窗口。 */
export const ACQUISITION_PATH_SWITCH_HINT_MS = 30_000

export function isAcquisitionPathSwitching(info: DeviceAcquisitionInfo | undefined | null, now: number): boolean {
  if (!info || info.changedTs <= 0) return false
  const age = now - info.changedTs
  return age >= 0 && age < ACQUISITION_PATH_SWITCH_HINT_MS
}
