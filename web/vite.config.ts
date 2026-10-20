import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 开发期：Vite dev server(5173) 代理到 ASP.NET Core(8899)，前后端同源，Cookie 正常工作。
// 构建期：产物直接产出到 Server 的 wwwroot，由服务端静态托管（生产同源，无需 CORS）。
export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: '../src/ControlServer.Server/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:8899', changeOrigin: true },
      '/hubs': { target: 'http://localhost:8899', changeOrigin: true, ws: true },
      '/healthz': { target: 'http://localhost:8899', changeOrigin: true },
      '/readyz': { target: 'http://localhost:8899', changeOrigin: true },
    },
  },
})
