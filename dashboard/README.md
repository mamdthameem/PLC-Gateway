# PLCGateway Dashboard (frontend source)

Vite + React + TypeScript source for the dashboard. It is **vendored into the PLCGateway
solution**: `npm run build` emits the production build directly into `../PLCGateway/wwwroot`,
which the unified ASP.NET Core app serves same-origin. In production there is no separate
frontend server — the backend at `../PLCGateway` serves both the dashboard and the API.

## Build into the app

```bash
npm install        # first time only
npm run build      # outputs to ../PLCGateway/wwwroot (same-origin; API calls are relative /api/...)
```

Then run or publish the backend (`../PLCGateway`); it serves this build plus the JSON API.

## Optional: hot-reload while editing the UI

```bash
npm run dev        # Vite dev server on http://localhost:5173
```

Run the backend separately (`cd ../PLCGateway && dotnet run`, listens on `:5200`); the dev
server proxies `/api` to it (see `vite.config.ts`). This is only for frontend development — the
shipped app is the single backend process.

## Notes

- API base is same-origin by default (`.env` leaves `VITE_API_URL` empty). Don't hardcode a host.
- Login uses the backend JWT endpoint (`POST /api/auth/login`); users live in the `users` table.
- Data comes from the backend API (live tags, KPIs, cycles, spares) — there is no file-upload flow.
