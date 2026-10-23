import { defineConfig } from '@playwright/test'
export default defineConfig({
  testDir: './tests', fullyParallel: false, workers: 1, timeout: 30000,
  use: { baseURL: 'http://127.0.0.1:18581', channel: 'msedge', viewport: { width: 1600, height: 1000 }, trace: 'retain-on-failure' },
  webServer: { command: 'node tests/start-server.mjs', url: 'http://127.0.0.1:18581/health', timeout: 60000, reuseExistingServer: false },
  reporter: [['list']]
})
