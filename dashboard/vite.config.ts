import { defineConfig, loadEnv } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';

// Vendored into the PLCGateway solution. `npm run build` emits the production dashboard
// directly into the ASP.NET Core app's wwwroot, so publishing PLCGateway ships the dashboard.
// API calls are same-origin (relative /api/...). `npm run dev` (optional, for frontend HMR)
// proxies /api to the backend running on :5200.
export default defineConfig(({ mode }) => {
  // Sub-path hosting (IIS application alias, e.g. "/shotsense/"). Must start AND end with "/".
  // Unset = hosted at the site root. Set in .env.production only, so `npm run dev` stays at "/"
  // and the /api proxy below keeps working. This single value drives asset URLs, the router
  // basename and the API prefix via import.meta.env.BASE_URL — no second place to keep in sync.
  const env = loadEnv(mode, __dirname, '');
  const base = env.VITE_BASE_PATH || '/';

  return {
    base,
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
  };
});
