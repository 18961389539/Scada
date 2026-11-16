import { defineConfig } from 'vitest/config'

export default defineConfig({
  test: {
    // 画面模型会经 i18n 读取 localStorage/document，用 jsdom 保持与浏览器一致的初始化环境。
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
    restoreMocks: true,
    coverage: {
      provider: 'v8',
      reporter: ['text-summary'],
      // 只对已经有单测的共享模块设门槛：未覆盖的巨型视图不参与分母，
      // 否则分母会被页面组件淹没，门槛既不会上涨也挡不住回归。
      include: [
        'src/composables/batchRunner.ts',
        'src/composables/formErrors.ts',
        'src/composables/lastChoice.ts',
        'src/composables/routeQuery.ts',
        'src/composables/useActionError.ts',
        'src/composables/useAlarmSourceCatalog.ts',
        'src/composables/useAlarmsView.ts',
        'src/composables/useNowTicker.ts',
        'src/composables/usePolling.ts',
        'src/screens/model.ts',
        'src/screens/screenTags.ts',
        'src/screens/templateBindings.ts',
        'src/utils.ts'
      ],
      // 以本轮实测值为基线（行 65.67 / 分支 68.37 / 函数 58.70），向下留约 1 个点，
      // 只允许往上调；低于门槛直接失败。
      thresholds: {
        lines: 64,
        functions: 57,
        branches: 67,
        statements: 64
      }
    }
  }
})
