import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { VitePWA } from 'vite-plugin-pwa'
import Components from 'unplugin-vue-components/vite'
import { ElementPlusResolver } from 'unplugin-vue-components/resolvers'
import ElementPlus from 'unplugin-element-plus/vite'

export default defineConfig({
  plugins: [
    vue(),
    Components({ resolvers: [ElementPlusResolver()], dts: false }),
    ElementPlus({ useSource: false }),
    // 现场常用 localhost 访问，补 Service Worker 后浏览器才会判定“可安装”。
    // manifest 继续用 public/manifest.webmanifest，避免插件生成的清单覆盖现有站点配置。
    VitePWA({
      registerType: 'autoUpdate',
      injectRegister: 'auto',
      manifest: false,
      workbox: {
        globPatterns: ['**/*.{js,css,html,svg,png,webmanifest,woff2}'],
        navigateFallback: 'index.html',
        // 实时接口、SignalR 与健康检查必须直连服务端，不能被 SW 接管。
        navigateFallbackDenylist: [/^\/api\//, /^\/hubs\//, /^\/health$/, /^\/metrics$/, /^\/openapi\//],
        cleanupOutdatedCaches: true,
        maximumFileSizeToCacheInBytes: 4 * 1024 * 1024
      }
    })
  ],
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
    manifest: true,
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
