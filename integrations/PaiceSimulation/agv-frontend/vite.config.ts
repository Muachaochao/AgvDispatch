import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url))
    }
  },
  server: {
    host: '127.0.0.1',
    strictPort: true,
    port: 5270,
    proxy: {
      '/dispatch-api': {
        target: 'http://127.0.0.1:5199',
        changeOrigin: true,
        rewrite: path => path.replace(/^\/dispatch-api/, ''),
        bypass(req) {
          const path = new URL(req.url ?? '/', 'http://127.0.0.1').pathname.replace(/^\/dispatch-api/, '')
          const read = req.method === 'GET' && (/^\/api\/simulation(?:\/(?:tasks(?:\/[\w-]+)?|resources|snapshot|report))?$/.test(path) || path === '/api/snapshot' || /^\/api\/maps\/[^/]+\/workspace$/.test(path))
          const write = req.method === 'POST' && /^\/api\/simulation\/(?:start|stop|scheme-start|run|abort)$/.test(path)
          if (!read && !write) return false
        }
      },
      '/api': {
        target: 'http://127.0.0.1:5271',
        changeOrigin: true
      },
      '/ws': {
        target: 'ws://127.0.0.1:5271',
        ws: true
      }
    }
  },
  build: {
    chunkSizeWarningLimit: 1200,
    rollupOptions: {
      output: {
        manualChunks: {
          three: ['three'],
          echarts: ['echarts'],
          element: ['element-plus']
        }
      }
    }
  }
})
