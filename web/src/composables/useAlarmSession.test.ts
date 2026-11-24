import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ElNotification } from 'element-plus'
import { alarmSessionActive, alarmToastSurface, onCriticalAlarmRaise, resetAlarmSessionForTests, shouldRunAlarmSession, useAlarmSession } from './useAlarmSession'
import { resetAlarmSoundForTests } from './useAlarmSound'

const { onHubEvent, onHubClosed, onHubReconnected, resetHubMock } = vi.hoisted(() => ({
  // 显式声明可变参数：onHubEvent 的真实签名是 (type, handler)，无参 mock 会让 calls[0][1] 过不了类型检查。
  onHubEvent: vi.fn((..._args: unknown[]) => vi.fn()),
  onHubClosed: vi.fn((..._args: unknown[]) => vi.fn()),
  onHubReconnected: vi.fn((..._args: unknown[]) => vi.fn()),
  resetHubMock: vi.fn()
}))

vi.mock('../realtime/hub', () => ({
  onHubEvent,
  onHubClosed,
  onHubReconnected,
  resetHub: resetHubMock
}))

vi.mock('../stores/auth', () => ({
  useAuthStore: () => ({ confirmSession: vi.fn(async () => undefined) })
}))

const { statusMock, startAlarmLoop, stopAlarmLoop } = vi.hoisted(() => ({
  statusMock: vi.fn(async () => []),
  startAlarmLoop: vi.fn(),
  stopAlarmLoop: vi.fn()
}))

vi.mock('../api/annunciators', () => ({
  annunciatorApi: { status: statusMock }
}))

vi.mock('../alarmAudio', () => ({
  handleAlarmPush: vi.fn(),
  primeAlarmAudio: vi.fn(),
  setAlarmMute: vi.fn(),
  startAlarmLoop,
  stopAlarmLoop
}))

vi.mock('element-plus', () => ({
  ElNotification: Object.assign(vi.fn(() => ({ close: vi.fn() })), { closeAll: vi.fn() })
}))

describe('shouldRunAlarmSession', () => {
  it('stays active on console, kiosk and layout preview as long as the user is signed in', () => {
    expect(shouldRunAlarmSession({ token: 't', mustChangePassword: false, isPublic: false })).toBe(true)
  })

  it('does not start on the login page, password-change gate, or after logout', () => {
    expect(shouldRunAlarmSession({ token: '', isPublic: true })).toBe(false)
    expect(shouldRunAlarmSession({ token: 't', mustChangePassword: true, isPublic: false })).toBe(false)
    expect(shouldRunAlarmSession({ isPublic: false })).toBe(false)
  })

  it('stays off on layout preview even when the user is signed in', () => {
    expect(shouldRunAlarmSession({ token: 't', mustChangePassword: false, isPublic: false, suppressAlarmSession: true })).toBe(false)
  })
})

describe('alarmToastSurface', () => {
  it('reserves navigable toasts for ordinary pages and keeps runtime shells on a local surface', () => {
    expect(alarmToastSurface('/alarms')).toBe('toast')
    expect(alarmToastSurface('/console')).toBe('console')
    expect(alarmToastSurface('/console/12')).toBe('console')
    expect(alarmToastSurface('/kiosk/screens/3')).toBe('kiosk')
    expect(alarmToastSurface('/config/layout-preview')).toBe('none')
  })
})

describe('useAlarmSession lifecycle', () => {
  beforeEach(() => {
    onHubEvent.mockClear()
    onHubClosed.mockClear()
    onHubReconnected.mockClear()
    resetHubMock.mockClear()
    localStorage.setItem('scada_sound', 'on')
  })

  afterEach(() => {
    resetAlarmSessionForTests()
    resetAlarmSoundForTests()
  })

  it('starts once and keeps running until an explicit logout stop', () => {
    const session = useAlarmSession()
    session.start()
    session.start()
    expect(alarmSessionActive()).toBe(true)
    expect(onHubEvent).toHaveBeenCalledTimes(1)
    expect(resetHubMock).not.toHaveBeenCalled()

    session.stop()
    expect(alarmSessionActive()).toBe(false)
    expect(resetHubMock).toHaveBeenCalledTimes(1)
  })

  it('does not treat a second start after stop as a no-op: logout then login starts a new session', () => {
    const session = useAlarmSession()
    session.start()
    session.stop()
    resetHubMock.mockClear()
    session.start()
    expect(alarmSessionActive()).toBe(true)
    expect(onHubEvent).toHaveBeenCalledTimes(2)
    expect(resetHubMock).not.toHaveBeenCalled()
    session.stop()
  })
})

function criticalAlarm() {
  return {
    id: 1,
    tagId: 9,
    tagName: 'P1',
    level: 'Critical',
    message: 'trip',
    startTs: 1,
    ackTs: null,
    endTs: null
  }
}

describe('critical raise presentation', () => {
  beforeEach(() => {
    onHubEvent.mockClear()
    onHubClosed.mockClear()
    onHubReconnected.mockClear()
    resetHubMock.mockClear()
    vi.mocked(ElNotification).mockClear()
    localStorage.setItem('scada_sound', 'on')
    window.history.pushState({}, '', '/overview')
  })

  afterEach(() => {
    resetAlarmSessionForTests()
    resetAlarmSoundForTests()
    window.history.pushState({}, '', '/')
  })

  function raiseCritical(): void {
    const session = useAlarmSession()
    session.start()
    const handler = onHubEvent.mock.calls[0]?.[1] as ((payload: { action: string; alarm: ReturnType<typeof criticalAlarm> }) => void) | undefined
    expect(handler).toBeTypeOf('function')
    handler?.({ action: 'raise', alarm: criticalAlarm() })
  }

  it('opens a clickable toast on ordinary pages', () => {
    raiseCritical()
    expect(ElNotification).toHaveBeenCalledTimes(1)
    const options = vi.mocked(ElNotification).mock.calls[0]?.[0] as { onClick?: () => void }
    expect(options.onClick).toBeTypeOf('function')
  })

  it('does not open a navigable critical toast on the console, kiosk, or layout preview', () => {
    for (const path of ['/console', '/kiosk/screens/1', '/config/layout-preview']) {
      vi.mocked(ElNotification).mockClear()
      window.history.pushState({}, '', path)
      raiseCritical()
      expect(ElNotification, path).not.toHaveBeenCalled()
      resetAlarmSessionForTests()
      resetAlarmSoundForTests()
    }
  })

  it('notifies local surfaces so the console or kiosk can focus an in-page banner', () => {
    const seen: string[] = []
    const stop = onCriticalAlarmRaise(alarm => { seen.push(alarm.level) })
    window.history.pushState({}, '', '/console')
    raiseCritical()
    expect(seen).toEqual(['Critical'])
    stop()
  })
})

describe('hub reconnect sound refresh', () => {
  beforeEach(() => {
    onHubEvent.mockClear()
    onHubClosed.mockClear()
    onHubReconnected.mockClear()
    statusMock.mockReset()
    startAlarmLoop.mockReset()
    stopAlarmLoop.mockReset()
    localStorage.setItem('scada_sound', 'on')
  })

  afterEach(() => {
    resetAlarmSessionForTests()
    resetAlarmSoundForTests()
  })

  it('recomputes annunciator sound from current status after a hub reconnect', async () => {
    statusMock.mockResolvedValueOnce([{ enabled: true, soundKey: 'critical', silenced: false }])
    const session = useAlarmSession()
    session.start()
    const onReconnect = onHubReconnected.mock.calls[0]?.[0] as (() => void) | undefined
    expect(onReconnect).toBeTypeOf('function')
    onReconnect?.()
    await vi.waitFor(() => expect(startAlarmLoop).toHaveBeenCalledWith('critical'))

    statusMock.mockResolvedValueOnce([])
    startAlarmLoop.mockClear()
    onReconnect?.()
    await vi.waitFor(() => expect(stopAlarmLoop).toHaveBeenCalled())
  })
})
