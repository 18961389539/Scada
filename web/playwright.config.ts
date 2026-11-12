import { defineConfig } from '@playwright/test'
export default defineConfig({
  testDir: './tests', fullyParallel: false,
  // 并行粒度是 spec 文件；每个用例仍启动独立服务与临时数据目录，端口由 tests/ports.ts 按 worker 分配。
  workers: Number(process.env.PW_WORKERS ?? (process.env.CI ? 4 : 1)),
  // CI 上偶发一次重试，避免网络/进程启动抖动把主干打红；确定性失败仍会被第二次尝试暴露。
  retries: process.env.CI ? 1 : 0, timeout: 30000,
  forbidOnly: !!process.env.CI,
  // PW_CHANNEL=chromium 时用 Playwright 自带浏览器（无 Edge 的环境）；默认 msedge。
  use: {
    baseURL: process.env.PW_BASE_URL ?? 'http://127.0.0.1:18581',
    ...(process.env.PW_CHANNEL === 'chromium' ? {} : { channel: 'msedge' as const }),
    viewport: { width: 1600, height: 1000 },
    serviceWorkers: 'block',
    trace: 'retain-on-failure'
  },
  reporter: [['list']]
})
