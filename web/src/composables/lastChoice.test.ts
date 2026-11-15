import { beforeEach, describe, expect, it } from 'vitest'
import { readLastNumber, readLastText, rememberNumber, rememberText } from './lastChoice'

beforeEach(() => localStorage.clear())

describe('lastChoice', () => {
  it('scopes stored values by the current user', () => {
    localStorage.setItem('scada_user', JSON.stringify({ userName: 'Alice' }))
    rememberNumber('pageSize', 50)
    rememberText('filter', 'line-A')

    expect(localStorage.getItem('scada_last_alice:pageSize')).toBe('50')
    expect(readLastNumber('pageSize')).toBe(50)
    expect(readLastText('filter')).toBe('line-A')

    localStorage.setItem('scada_user', JSON.stringify({ userName: 'Bob' }))
    expect(readLastNumber('pageSize')).toBeNull()
    expect(readLastText('filter')).toBe('')
  })

  it('survives missing users and corrupt stored values', () => {
    expect(readLastNumber('missing')).toBeNull()
    expect(readLastText('missing')).toBe('')

    localStorage.setItem('scada_last_:missing', 'not-a-number')
    expect(readLastNumber('missing')).toBeNull()

    localStorage.setItem('scada_user', '{bad json')
    rememberText('key', 'value')
    expect(localStorage.getItem('scada_last_:key')).toBe('value')
  })

  it('removes entries when the value is reset', () => {
    rememberNumber('pageSize', 20)
    rememberNumber('pageSize', null)
    rememberText('filter', 'x')
    rememberText('filter', '')

    expect(readLastNumber('pageSize')).toBeNull()
    expect(readLastText('filter')).toBe('')
  })
})
