import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    // 画面模型会经 i18n 读取 localStorage/document，用 jsdom 保持与浏览器一致的初始化环境。
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
    restoreMocks: true
  }
})
