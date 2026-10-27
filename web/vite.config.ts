import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': { target: process.env.ARWORK_API_URL || 'http://127.0.0.1:18088', changeOrigin: true },
      '/health': { target: process.env.ARWORK_API_URL || 'http://127.0.0.1:18088', changeOrigin: true },
    },
  },
})
