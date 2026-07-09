# PLCGateway — Claude Context

## Project summary

Industry project. A single **unified ASP.NET Core (.NET 10) application** hosted under IIS that (a) reads a Siemens S7-1200 PLC every second via background hosted services and stores data in PostgreSQL, (b) exposes the dashboard's JSON API, and (c) serves the React dashboard's static build from `wwwroot/`. The cloud reaches the app only over HTTPS via secured `/api/admin/*` endpoints — never the database directly.

**Stack:** .NET 10, C#, ASP.NET Core (WebApplication + hosted services), S7.NetPlus (PLC driver), Npgsql (PostgreSQL), JWT bearer auth. Frontend: Vite + React + TypeScript (built into `wwwroot`).

**PLC:** Siemens S7-1200 at 192.168.0.180, Rack 0, Slot 1. Data Block DB60. ~443 tags (read as a batched byte region).

**Database:** `sreesakthi_gateway` on localhost:5432, user=postgres, password=Pass

**Deployment:** see `DEPLOYMENT-NOTES.md` (IIS app pool AlwaysRunning / Idle 0 / Preload; 443 + cert; admin IP restriction; PostgreSQL localhost-only).

---

## Non-negotiable constraints

- **Never delete historical data.** `plc_historical_data` must grow forever. No compression, archiving, or deletion — ever. This is a hard client requirement. The typed-column migration keeps the original TEXT `value` column **frozen** (never dropped).
- **DB is still the message bus for Section 2.** The dashboard triggers filtered calculations by inserting into `calculation_requests`; the backend processes them and writes results. Live dashboard data is served by the in-process JSON API (added in the unified-app redesign) — it reads the same tables, never recomputes heavy work per request.
- **Discuss architecture before coding.** For any non-trivial change, draft the plan as a table or diagram and wait for go-ahead before writing code.
- **Terminology is strict:** `tags` = raw PLC readings stored in `plc_historical_data`; `parameters` = calculated business values stored in `plc_lifetime_parameters` and related tables. Never confuse these in code, comments, or conversation.
- **Reserved PLC write path.** `PlcService.Write` is kept for the spares REPLACED write-back flow; it is not used by the current scan loop. Do not delete it.

---



## Terminology


| Term          | Meaning                                                                                                                |
| ------------- | ---------------------------------------------------------------------------------------------------------------------- |
| **Tag**       | A raw value read from the PLC. Stored in `plc_current_values` (latest) and `plc_historical_data` (COV history).        |
| **Parameter** | A calculated business value. Stored in `plc_lifetime_parameters` (Section 1) or `plc_filtered_parameters` (Section 2). |
| **Section 1** | All-time cumulative parameters, updated every minute. Dashboard reads directly.                                        |
| **Section 2** | On-demand filtered parameters. Dashboard triggers by writing to `calculation_requests`.                                |
| **COV**       | Change of value. Tier 2 storage on ≥2% relative change (analogs), **absolute** deadband (accumulators: tonnage, run-hours), or state change (BOOL). Plus a real 60 s heartbeat for the ~22 core calculation tags. |
| **Tier 1**    | `plc_current_values` — one row per tag, always the latest value. Typed columns `value_num`/`value_bool`/`value_text`; `is_stale` flag set while the PLC is disconnected. |
| **Tier 2**    | `plc_historical_data` — full time-series, never deleted. Source of truth for all calculations. Typed columns `value_num`/`value_bool`/`value_text`; old TEXT `value` kept frozen. |


---



## Database tables

> Redesign additions: `plc_filtered_metal_production`, `plc_aggregation_state`, `gateway_status`, `gateway_license_state`, `users`. Tier 1/2 gained typed columns (`value_num`/`value_bool`/`value_text`) with the old TEXT `value` frozen; `plc_current_values` gained `is_stale`; `plc_cycles` gained `production_kg`/`energy_kwh`.


| Table                          | Owner                               | Purpose                                                                |
| ------------------------------ | ----------------------------------- | ---------------------------------------------------------------------- |
| `plc_current_values`           | Backend                             | Tier 1 — latest tag value per address                                  |
| `plc_historical_data`          | Backend                             | Tier 2 — COV-based history, never deleted                              |
| `plc_lifetime_parameters`      | Backend writes, Dashboard reads     | Section 1 scalar parameters (1 row per param)                          |
| `plc_shots_breakdown`          | Backend writes, Dashboard reads     | Section 1 shots/refill breakdown table (upsert keyed on refill_timestamp) |
| `plc_cycles`                   | Backend                             | One row per completed blast cycle                                      |
| `calculation_requests`         | Dashboard writes, Backend processes | Section 2 trigger — dashboard inserts here                             |
| `plc_filtered_parameters`      | Backend writes, Dashboard reads     | Section 2 scalar results per request                                   |
| `plc_filtered_cycle_data`      | Backend writes, Dashboard reads     | Section 2 per-cycle breakdown per request                              |
| `plc_filtered_shots_breakdown` | Backend writes, Dashboard reads     | Section 2 shots breakdown per request                                  |
| `plc_spare_status`             | Backend                             | 140 rows — spare health per impeller per spare                         |
| `plc_filtered_metal_production`| Backend writes, Dashboard reads     | Section 2 production split per casting metal (proportional by weight)  |
| `plc_aggregation_state`        | Backend                             | Single row — incremental Section 1 watermark + running totals          |
| `gateway_status`               | Backend                             | Single row — PLC connection state (`plc_connected`, `last_scan_at`)    |
| `gateway_license_state`        | Backend                             | Single row — last successful cloud license check                       |
| `users`                        | Backend                             | Dashboard login accounts (local; no tenant/subscription)               |


---



## Services


| Service                      | Interval   | Role                                                                                         |
| ---------------------------- | ---------- | -------------------------------------------------------------------------------------------- |
| `GatewayWorker`              | 1s         | Batched region read of DB60 → parse via `TagParser` → in-memory cache → **batched** Tier 1 upsert + Tier 2 insert. Drives `PlcConnectionState` (disconnect ⇒ forced-OFF, stale). |
| `CovDetectionService`        | per tag    | ≥2% relative (analogs), **absolute** deadband (accumulators), state-change (BOOL); 60 s heartbeat for core tags |
| `AggregationService`         | 1 min      | Calls `ComputeLifetimeParametersAsync()` — **incremental/watermarked** → `plc_lifetime_parameters` + `plc_shots_breakdown` |
| `CycleTrackingService`       | 2s         | Falling edge on `Blast ON/OFF` → reads Tier 1 for metal/tonnage → writes `plc_cycles` (with computed `production_kg`, `energy_kwh`) |
| `FilteredCalculationService` | 5s poll    | Picks up pending `calculation_requests`, runs all Section 2 calculations + per-metal split |
| `SpareMonitoringService`     | 10s        | Reads 140 trigger/runhour/replaced tags → upserts `plc_spare_status` (skips while PLC disconnected) |
| `LicenseCheckService`        | 60 min     | Cloud license check with grace period; on expiry locks the data API/dashboard (recording continues) |
| `CalculationService`         | shared lib | Math: `ComputeLifetimeParametersAsync` (incremental), `ComputeFilteredParametersAsync`      |
| `PlcService`                 | —          | S7.NetPlus wrapper: batched `ReadRegion`; reserved `Write` for spares REPLACED write-back  |
| API controllers + services   | HTTP       | `/api/*` JSON for the dashboard (JWT-protected); `/api/admin/*` cloud pulls (IP allowlist + API key) |


---



## Parameters



### Section 1 — `plc_lifetime_parameters` (scalar, updated every minute)


| `parameter_name`            | Formula                                                                             | Source tags                                            |
| --------------------------- | ----------------------------------------------------------------------------------- | ------------------------------------------------------ |
| `machine_status`            | `value ≠ "0" → 1, else 0`                                                           | `Machine status` BYTE at `DB60.DBB0`, live from Tier 1 |
| `machine_utility_pct`       | `blast_time_sec ÷ machine_on_time_sec × 100`                                        | `Blast ON/OFF`, `Machine status` from Tier 2           |
| `production_qty_kg`         | Latest raw `Tonnage` value                                                          | `Tonnage` from Tier 1 (PLC is running accumulator)     |
| `energy_kwh_total`          | `Σ (avg_amps_per_impeller × cycle_duration_hours)` across 10 impellers × all cycles | `plc_cycles` + `Current_imp_1`…`10` from Tier 2        |
| `energy_per_casting_kwh_kg` | `energy_kwh_total ÷ production_qty_kg`                                              | derived                                                |
| `blast_time_sec`            | Total seconds where `Blast ON/OFF = true`                                           | `Blast ON/OFF` Tier 2                                  |
| `cycle_count`               | Rising edges `0→1` on `Blast ON/OFF`                                                | `Blast ON/OFF` Tier 2                                  |
| `last_refill_epoch_sec`     | Unix epoch of latest `Refil shots weight` change                                    | `Refil shots weight` Tier 2                            |




### Section 1 — `plc_shots_breakdown` (table, upserted incrementally)

For each pair of consecutive `Refil shots weight` COV events: count `Blast ON/OFF` rising edges between them.
Output: `(refill_timestamp, blast_count)`. Maintained by the incremental engine via upsert (no TRUNCATE), so the dashboard never reads an empty/partial table. Shared dataset for parameters #7 and #8.

> **Section 1 is now incremental** (`plc_aggregation_state`): each pass folds only Tier 2 rows newer than the stored watermark into running accumulators, producing identical outputs to the old full-replay engine. Energy is a running sum of the per-cycle `plc_cycles.energy_kwh`. Run with `--rebuild-aggregation` to replay from scratch.

### Section 2 differences


| Parameter           | Section 2 formula                                                                   |
| ------------------- | ----------------------------------------------------------------------------------- |
| production          | `Σ plc_cycles.production_kg` over in-scope cycles (used as energy-per-casting denominator; also split per metal in `plc_filtered_metal_production`) |
| `energy_kwh_total`  | `Σ plc_cycles.energy_kwh` over in-scope cycles                                       |
| per-metal split     | each cycle's `production_kg` split proportionally by declared metal weight; no weights ⇒ `unspecified` |
| `machine_status`    | Not included                                                                        |




### Per-cycle breakdown (`plc_filtered_cycle_data`)


| Column          | Formula                                                    |
| --------------- | ---------------------------------------------------------- |
| `production_kg` | `tonnage_kg(this cycle) − tonnage_kg(prev cycle)`, floor 0 |
| `energy_kwh`    | `avg_amps_all_impellers × cycle_duration_hours`            |
| `shots_usage`   | `refill_weight_in_cycle ÷ production_kg`                   |




### Live display (not stored as parameters — dashboard reads directly)


| What                    | Source               | Query pattern                                                                 |
| ----------------------- | -------------------- | ----------------------------------------------------------------------------- |
| Amps per impeller (×10) | `plc_current_values` | `WHERE parameter_name = 'Current_imp_N'`                                      |
| Spare health (140 rows) | `plc_spare_status`   | All rows, or `WHERE trigger_active = TRUE AND threshold_hours > 0` for alerts |


---



## Key tag names (as they appear in `parameter_name` column)


| Tag                                | Type   | Address      |
| ---------------------------------- | ------ | ------------ |
| `Machine status`                   | BYTE   | DB60.DBB0    |
| `Blast ON/OFF`                     | BOOL   | —            |
| `Tonnage`                          | DINT   | —            |
| `Refil shots weight`               | REAL   | —            |
| `Current_imp_1` … `Current_imp_10` | REAL   | —            |
| `Casting metal 1-4 name`           | STRING | —            |
| `Casting metal 1-4 weight`         | DINT   | —            |
| `Spares trigger imp{N}[{M}]`       | BOOL   | — (140 tags) |
| `Spares_Runhour_imp{N}[{M}]`       | REAL   | — (140 tags) |
| `REPLACED_{N}[{M}]`                | BOOL   | — (140 tags) |


---



## Section 2 filter modes


| `filter_by`        | Columns used                           |
| ------------------ | -------------------------------------- |
| `'time'` (default) | `filter_start`, `filter_end`           |
| `'cycle'`          | `filter_cycle_from`, `filter_cycle_to` |
| `'metal'`          | `filter_metal_name`                    |


`filter_start` and `filter_end` are NOT NULL — always required. Pass `NOW()` as placeholder when using cycle or metal filter.

---



## Spare monitoring

10 impellers × 14 spares = 140 rows. Tag patterns:

- Trigger: `Spares trigger imp{N}[{M}]` (BOOL) — set by PLC when run-hour threshold crossed
- Run hours: `Spares_Runhour_imp{N}[{M}]` (REAL) — accumulated hours, reset by PLC on replacement
- Replaced: `REPLACED_{N}[{M}]` (BOOL) — set by HMI/PLC when spare is replaced

Thresholds (spare_index 0–13, hours): 100, 300, 300, 600, 2000, 2000, 300, 2000, 300, 0 (skip), 2000, 2000, 5000, 600

---



## Files


| File                                       | Purpose                                                                   |
| ------------------------------------------ | ------------------------------------------------------------------------- |
| `PLCGateway/migration.sql`                 | Idempotent DB migration — run once before starting                        |
| `PLCGateway/appsettings.json`              | PLC IP, DB conn, COV rules, heartbeat tags, JWT, Admin, License, Seed      |
| `PLCGateway/Program.cs`                    | **WebApplication** setup — hosted services + API + static SPA + JWT + middleware |
| `PLCGateway/GatewayWorker.cs`              | Batched PLC scan loop + connection-state hooks                            |
| `PLCGateway/PlcConnectionState.cs`         | Shared PLC connect/disconnect state                                       |
| `PLCGateway/TagParser.cs`                  | Parses tags from raw DB byte regions (S7 big-endian)                      |
| `PLCGateway/AggregationService.cs`         | Section 1 incremental computation trigger (1 min)                         |
| `PLCGateway/CalculationService.cs`         | Parameter math — incremental Section 1 + Section 2 (+ metal split)        |
| `PLCGateway/CycleTrackingService.cs`       | Blast cycle detection; stores production_kg + energy_kwh                  |
| `PLCGateway/FilteredCalculationService.cs` | Section 2 request processor                                               |
| `PLCGateway/SpareMonitoringService.cs`     | Spare health monitoring                                                   |
| `PLCGateway/LicenseCheckService.cs`        | Periodic cloud license check with grace period                           |
| `PLCGateway/DatabaseService.cs`            | All PostgreSQL queries (typed columns, batched writes, agg state)         |
| `PLCGateway/PlcService.cs`                 | S7.NetPlus wrapper — `ReadRegion`, reserved `Write`                       |
| `PLCGateway/CovDetectionService.cs`        | COV logic (relative/absolute deadband, state-change)                     |
| `PLCGateway/Models/*.cs`                   | `PlcCycle`, `CalculationRequest`, `AggregationState`, `ScanWrites`, …     |
| `PLCGateway/Api/Controllers/*.cs`          | Dashboard API + `AuthController` (JWT) + `AdminController` (cloud pulls)   |
| `PLCGateway/Api/Services/*.cs`             | API data services (typed reads), `UserService`, `LicenseState`            |
| `PLCGateway/Api/Middleware/*.cs`           | `AdminGuardMiddleware` (IP+key), `LicenseLockMiddleware` (402 when locked) |
| `dashboard/`                               | **Dashboard source** (Vite + React + TS), vendored in-repo. `npm run build` emits into `PLCGateway/wwwroot` (same-origin). |
| `PLCGateway/wwwroot/`                       | Built React dashboard (served same-origin) — generated from `dashboard/`   |
| `PLCGateway/web.config`                    | IIS ASP.NET Core Module (InProcess) config                                |
| `DEPLOYMENT-NOTES.md`                       | IIS setup, app pool, 443/cert, admin IP restriction, config placeholders  |
| `README.md`                                | Full architecture, schema, formulas, run instructions                     |


---



## Pending items (need client confirmation — intentionally NOT changed)

- **Energy formula** — `energy_kwh_total` and per-cycle `energy_kwh` are currently `avg_amps × duration_hours` (i.e. amp-hours, not kWh). `EnergyCalculation:SupplyVoltageV` (415) and `PowerFactor` (0.85) exist in config but are **not applied**. A real 3-phase estimate would be `√3 × V × I × PF ÷ 1000`. Also pending: whether a direct energy-meter address should replace the `Current_imp_N` amp tags. Formula left unchanged until the client decides.
- **`Refil shots weight` type** — typed `DINT` in `appsettings.json` but documented REAL elsewhere. Pending confirmation; unchanged.
- **Empty casting metal slot format** — whether empty slots arrive as blank string or null (affects per-metal attribution and cycle recording).

---



## How to run migration

```powershell
psql -U postgres -d sreesakthi_gateway -f PLCGateway/migration.sql
```

Safe to re-run on existing databases — all statements are idempotent (typed-column backfill and per-cycle production/energy backfill are guarded and run once).

## How to run / deploy

- **Local:** `dotnet run --project PLCGateway` (serves API + dashboard on the Kestrel port). `--rebuild-aggregation` resets Section 1 incremental state and replays history.
- **Production:** publish and host under IIS — see `DEPLOYMENT-NOTES.md`. App pool must be AlwaysRunning / Idle 0 / Preload so the PLC scan loop is never idled.