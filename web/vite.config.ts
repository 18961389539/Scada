import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5080',
        changeOrigin: true
      },
      '/hubs': {
        target: 'http://localhost:5080',
        changeOrigin: true,
        ws: true
      },
      '/health': {
        target: 'http://localhost:5080',
        changeOrigin: true
      }
    }
  },
  build: {
    outDir: 'dist',
    chunkSizeWarningLimit: 1500,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (!id.includes('node_modules')) return
          // 图表库只在趋势/报警分析/OEE 页加载；其余第三方依赖合并为可长期缓存的 vendor 包。
          if (id.includes('echarts') || id.includes('zrender')) return 'vendor-echarts'
          return 'vendor'
        }
      }
    }
  }
})
