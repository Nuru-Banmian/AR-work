import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: process.env.ARWORK_API_URL || 'http://127.0.0.1:18088', changeOrigin: true,
        configure(proxy) {
          proxy.on('proxyRes', (upstream, request, response) => {
            if (request.url?.split('?')[0] !== '/api/events') return
            // pipe() does not end its destination on an aborted source. Close
            // this stream so EventSource can reconnect after a receiver crash.
            upstream.once('close', () => { if (!upstream.complete) response.destroy() })
          })
        },
      },
      '/health': { target: process.env.ARWORK_API_URL || 'http://127.0.0.1:18088', changeOrigin: true },
    },
  },
})
