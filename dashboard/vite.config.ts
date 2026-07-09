import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';

// Vendored into the PLCGateway solution. `npm run build` emits the production dashboard
// directly into the ASP.NET Core app's wwwroot, so publishing PLCGateway ships the dashboard.
// API calls are same-origin (relative /api/...). `npm run dev` (optional, for frontend HMR)
// proxies /api to the backend running on :5200.
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  build: {
    outDir: '../PLCGateway/wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': 'http://localhost:5200',
    },
  },
});
