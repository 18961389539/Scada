import { defineConfig } from '@playwright/test'
export default defineConfig({
  testDir: './tests', fullyParallel: false, workers: 1, timeout: 30000,
  // PW_CHANNEL=chromium 时用 Playwright 自带浏览器（无 Edge 的环境）；默认 msedge。
  use: {
    baseURL: process.env.PW_BASE_URL ?? 'http://127.0.0.1:18581',
    ...(process.env.PW_CHANNEL === 'chromium' ? {} : { channel: 'msedge' as const }),
    viewport: { width: 1600, height: 1000 },
    trace: 'retain-on-failure'
  },
  webServer: process.env.PW_BASE_URL ? undefined : { command: 'node tests/start-server.mjs', url: 'http://127.0.0.1:18581/health', timeout: 60000, reuseExistingServer: false },
  reporter: [['list']]
})
