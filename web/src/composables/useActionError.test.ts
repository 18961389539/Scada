import { describe, expect, it } from 'vitest'
import { ref } from 'vue'
import { tr } from '../i18n'
import { useActionError } from './useActionError'

describe('useActionError', () => {
  it('clears the previous error on success and writes the failure message', async () => {
    const { actionError, guard } = useActionError()
    actionError.value = 'old'

    await expect(guard(async () => undefined, 'save failed')).resolves.toBe(true)
    expect(actionError.value).toBe('')

    await expect(guard(async () => { throw new Error('boom') }, 'save failed')).resolves.toBe(false)
    expect(actionError.value).toBe('boom')
  })

  it('can write to a caller-provided target', async () => {
    const { guard } = useActionError()
    const target = ref('old')

    await expect(guard(async () => { throw new Error('boom') }, 'save failed', target)).resolves.toBe(false)
    expect(target.value).toBe('boom')
  })

  it('marks command failures without a server response as an unknown outcome', async () => {
    const { actionError, guardCommand } = useActionError()

    await expect(guardCommand(async () => { throw new Error('offline') }, 'save failed')).resolves.toBe(false)
    expect(actionError.value).toBe(tr('commandUx.outcomeUnknown'))
  })
})
