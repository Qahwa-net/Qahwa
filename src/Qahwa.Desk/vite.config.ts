import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// In development the Desk runs on Vite and proxies /api to the .NET server, so the session cookie is same-origin.
// A production build is written straight into the server's wwwroot, which the server serves.
export default defineConfig({
  plugins: [react()],
  server: { proxy: { '/api': 'http://localhost:5101' } },
  build: { outDir: '../Qahwa.Server/wwwroot', emptyOutDir: true },
  test: { environment: 'node', include: ['src/**/*.test.{ts,tsx}'] },
})
