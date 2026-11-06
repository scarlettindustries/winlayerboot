import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

// The build output goes into WinLayerBoot.Cli/wwwroot and is served by wlb webui from the embedded resources in the exe; in development the API is proxied to WLB_API (set in .env.local, not in git)
export default defineConfig(({ command, mode }) => {
  const api = loadEnv(mode, import.meta.dirname, 'WLB_').WLB_API
  if (command === 'serve' && !api) throw new Error('WLB_API is not set: add WLB_API=http://<host>:5080 to .env.local')
  return {
    plugins: [react()],
    build: { outDir: '../WinLayerBoot.Cli/wwwroot', emptyOutDir: true },
    server: { host: true, proxy: api ? { '/api': api } : undefined },
  }
})
