import { describe, expect, it } from 'vitest'
import { ACQUISITION_PATH_SWITCH_HINT_MS, isAcquisitionPathSwitching } from './acquisitionPathHint'
import type { DeviceAcquisitionInfo } from '../types'

function info(changedTs: number): DeviceAcquisitionInfo {
  return {
    deviceId: 1, name: 'pump', areaId: 1, activePath: 'Standby', activeGatewayId: null,
    activeChannelId: null, activeHost: '10.0.0.2', activePort: 502, hasStandby: true,
    manualLock: true, reason: '人工切换', changedBy: 'engineer', changedTs
  }
}

describe('isAcquisitionPathSwitching', () => {
  it('is true only inside the limited window after a path change', () => {
    expect(isAcquisitionPathSwitching(undefined, 10_000)).toBe(false)
    expect(isAcquisitionPathSwitching(info(0), 10_000)).toBe(false)
    expect(isAcquisitionPathSwitching(info(10_000), 10_000)).toBe(true)
    expect(isAcquisitionPathSwitching(info(1), 1 + ACQUISITION_PATH_SWITCH_HINT_MS - 1)).toBe(true)
    expect(isAcquisitionPathSwitching(info(1), 1 + ACQUISITION_PATH_SWITCH_HINT_MS)).toBe(false)
  })
})
