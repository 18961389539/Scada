import { describe, expect, it } from 'vitest'
import { applyFieldErrors, clearFieldErrors, mapServerFieldError, serverFormErrors } from './formErrors'

describe('mapServerFieldError', () => {
  it('maps Chinese and English hints to form fields', () => {
    expect(mapServerFieldError('测点名称不能为空')).toEqual({ name: '测点名称不能为空' }) // i18n-ignore
    expect(mapServerFieldError('device not found')).toEqual({ deviceId: 'device not found' })
    expect(mapServerFieldError('端口无效')).toEqual({ port: '端口无效' }) // i18n-ignore
  })

  it('prefers an explicitly requested field before the global hint order', () => {
    expect(mapServerFieldError('必须指定测点', ['tagId'])).toEqual({ tagId: '必须指定测点' }) // i18n-ignore
    expect(mapServerFieldError('设备地址无效', ['deviceId'])).toEqual({ address: '设备地址无效' }) // i18n-ignore
  })

  it('falls back to the form-level error when no hint matches', () => {
    expect(mapServerFieldError('something else')).toEqual({ _form: 'something else' })
    expect(mapServerFieldError('  ')).toEqual({})
  })
})

describe('serverFormErrors', () => {
  it('keeps the message and maps it to a field', () => {
    const result = serverFormErrors(new Error('测点不存在'), '保存失败') // i18n-ignore

    expect(result.message).toBe('测点不存在') // i18n-ignore
    expect(result.fields).toEqual({ tagId: '测点不存在' }) // i18n-ignore
  })
})

describe('field error helpers', () => {
  it('replaces all keys on apply and can clear them', () => {
    const target: Record<string, string> = { old: 'value', name: 'old' }

    applyFieldErrors(target, { name: 'new' })
    expect(target).toEqual({ name: 'new' })

    clearFieldErrors(target)
    expect(target).toEqual({})
  })
})
