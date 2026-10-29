import { test as base } from '@playwright/test'
import { execFileSync, fork } from 'node:child_process'
import { once } from 'node:events'
import { resolve } from 'node:path'

export * from '@playwright/test'

// Each test owns a server and database, including on retries and repeat-each runs.
export const test = base.extend<{ isolatedServer: void }>({
  isolatedServer: [async ({}, use, testInfo) => {
    if (process.env.PW_BASE_URL) {
      throw new Error('Isolated tests cannot use PW_BASE_URL. Run package smoke with its dedicated runner instead.')
    }
    const child = fork(resolve('tests/start-server.mjs'), [], { silent: true })
    let output = ''
    const collect = (data: Buffer) => { output = (output + data.toString()).slice(-65536) }
    child.stdout?.on('data', collect)
    child.stderr?.on('data', collect)
    const exited = once(child, 'exit')
    try {
      await new Promise<void>((ready, reject) => {
        const timer = setTimeout(() => reject(new Error(`Test server startup timed out:\n${output}`)), 60000)
        child.once('error', error => { clearTimeout(timer); reject(error) })
        child.once('exit', code => { clearTimeout(timer); reject(new Error(`Test server exited (${code}):\n${output}`)) })
        child.on('message', message => {
          if (message === 'ready') { clearTimeout(timer); ready() }
        })
      })
      await use()
    } finally {
      if (child.connected) child.send('stop')
      let forcedStop = false
      const timer = setTimeout(() => {
        forcedStop = true
        if (process.platform === 'win32' && child.pid) {
          execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true })
        } else child.kill('SIGKILL')
      }, 15000)
      let code: number | null
      try { [code] = await exited } finally { clearTimeout(timer) }
      if (testInfo.status !== testInfo.expectedStatus) {
        await testInfo.attach('isolated-server.log', { body: output, contentType: 'text/plain' })
      }
      if (forcedStop || code !== 0) throw new Error(`Test server cleanup failed (${code}):\n${output}`)
    }
  }, { auto: true, timeout: 90000 }]
})
