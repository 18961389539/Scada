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
        onlyExplicitManualChunks: true,
        manualChunks(id) {
          if (!id.includes('node_modules')) return
          if (id.includes('monaco-editor') || id.includes('dompurify') || id.includes('/marked/')) return 'vendor-monaco'
          if (id.includes('echarts') || id.includes('zrender')) return 'vendor-echarts'
        }
      }
    }
  }
})
