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
  - `plc_daily_trends` is the one exception *and it is not history*: it is a derived rollup, fully reconstructible from `plc_historical_data`, so it may be cleared and rebuilt (`--rebuild-aggregation`). Never treat it as a source of truth, and never delete raw rows because the rollup already has the summary.
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
| `calculation_requests`         | Dashboard writes, Backend processes | Section 2 trigger — dashboard inserts here. `selected_parameters TEXT[]` carries the per-parameter toggles (NULL = all) |
| `plc_filtered_parameters`      | Backend writes, Dashboard reads     | Section 2 scalar results per request                                   |
| `plc_filtered_cycle_data`      | Backend writes, Dashboard reads     | Section 2 per-cycle breakdown per request                              |
| `plc_filtered_shots_breakdown` | **Retired — nothing writes it**     | Was the Section 2 shots breakdown. Shots are Section 1 only now; existing rows kept, never dropped |
| `plc_spare_status`             | Backend                             | 140 rows — spare health per impeller per spare                         |
| `plc_filtered_metal_production`| Backend writes, Dashboard reads     | Section 2 production per declared casting item (summed declared weights). Column says metal, UI says item |
| `plc_filtered_amps_data`       | Backend writes, Dashboard reads     | Section 2 per-cycle average impeller current per request (`AVG(value_num)` per cycle×impeller) |
| `plc_aggregation_state`        | Backend                             | Single row — incremental Section 1 watermark + running totals          |
| `plc_daily_trends`            | Backend writes, Dashboard reads      | **Derived** daily rollup (one row per day) powering the all-time graphs. Rebuildable; never a substitute for Tier 2 |
| `gateway_status`               | Backend                             | Single row — PLC connection state (`plc_connected`, `last_scan_at`)    |
| `gateway_license_state`        | Backend                             | Single row — licence check state. `last_success_utc` (true UTC) + `last_success_url` = the grace anchor; `locked` mirrors the lock |
| `gateway_settings`             | Backend (dashboard saves via API)   | Single row — `selected_impellers`: which impellers are shown AND counted in energy / spares. See "Impeller selection" |
| `users`                        | Backend                             | Dashboard login accounts (local; no tenant/subscription)               |


---



## Services


| Service                      | Interval   | Role                                                                                         |
| ---------------------------- | ---------- | -------------------------------------------------------------------------------------------- |
| `GatewayWorker`              | 1s         | Batched region read of DB60 → parse via `TagParser` → in-memory cache → **batched** Tier 1 upsert + Tier 2 insert. Drives `PlcConnectionState` (disconnect ⇒ forced-OFF, stale). |
| `CovDetectionService`        | per tag    | ≥2% relative (analogs), **absolute** deadband (accumulators), state-change (BOOL); 60 s heartbeat for core tags |
| `AggregationService`         | 1 min      | Calls `ComputeLifetimeParametersAsync()` — **incremental/watermarked** → `plc_lifetime_parameters` + `plc_shots_breakdown`; then refreshes `plc_daily_trends` for **yesterday + today only** (bounded work per pass) |
| `CycleTrackingService`       | 2s         | Falling edge on `Blast ON/OFF` → reads Tier 1 for metal/tonnage → writes `plc_cycles` (with computed `production_kg`, `energy_kwh`) |
| `FilteredCalculationService` | 5s poll    | Picks up pending `calculation_requests`, computes **only the parameters the request selected** + per-item production split |
| `SpareMonitoringService`     | 10s        | Reads trigger/runhour/replaced tags for the **selected** impellers (140 with all ten) → upserts `plc_spare_status` (skips while PLC disconnected) |
| `LicenseCheckService`        | 60 min (5 min while locked) | Cloud licence check. 2xx = open; 401/402/403 = **lock at once**; no answer = open until 72 h after the last 2xx; empty `CheckUrl` = check off. Locks only the dashboard and its API — recording continues |
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
| `energy_kwh_total`          | `Σ (avg_amps_per_impeller × cycle_duration_hours)` across the **selected** impellers (`gateway_settings`) × all cycles | `plc_cycles` + `Current_imp_N` from Tier 2 |
| `energy_per_casting_kwh_kg` | `energy_kwh_total ÷ production_qty_kg`                                              | derived                                                |
| `blast_time_sec`            | Total seconds where `Blast ON/OFF = true`                                           | `Blast ON/OFF` Tier 2                                  |
| `cycle_count`               | Rising edges `0→1` on `Blast ON/OFF`                                                | `Blast ON/OFF` Tier 2                                  |
| `avg_shot_refill_time_sec`  | Elapsed time since first refill ÷ refill count                                      | `Refil shots weight` Tier 2                            |
| `last_refill_epoch_sec`     | Unix epoch of latest `Refil shots weight` change                                    | `Refil shots weight` Tier 2                            |
| `effective_shots_usage_kg_per_ton` | `total_refill_weight_kg ÷ (production_qty_kg ÷ 1000)` (both cumulative since commissioning). **kg/T — lower is better.** `—` when production is 0 | `Tonnage`, `Refil shots weight` Tier 2 |




### Section 1 — `plc_shots_breakdown` (table, upserted incrementally)

For each pair of consecutive `Refil shots weight` COV events: count `Blast ON/OFF` rising edges between them.
Output: `(refill_timestamp, blast_count)`.

> **The row is keyed by the refill that CLOSES the interval, not the one that opens it.**
> `FoldRefillAsync` calls `InsertLifetimeShotsBreakdownAsync(ev.Timestamp, blastCount)` where
> `blastCount` counts edges between `PrevRefillChangeTs` and `ev.Timestamp`. So a bar labelled
> "8 Sept" holds the cycles run *since the previous refill on 3 Sept* — 4.7 days' worth. Two
> consequences the dashboard got wrong until now: the label is the END of the window, and the
> interval currently in progress has **no row at all** (no closing refill yet), so the last bar is
> the last COMPLETED interval, never "the current one". Interval lengths are irregular by nature —
> a refill fires when the hopper crosses its low mark — so this chart can never be read as one bar
> per day. N refills produce N−1 rows.

`GET /api/shotsbreakdown` returns `intervalStartTimestamp` alongside — the opening refill. For every
row but the first that is simply the previous row, but the FIRST row's opener has no row of its own,
so the API recovers it from the Tier 2 refill events. Without it the earliest bar was the only one
that could not name its own window.

Maintained by the incremental engine via upsert (no TRUNCATE), so the dashboard never reads an empty/partial table. Shared dataset for parameters #7 and #8.

> **Section 1 is now incremental** (`plc_aggregation_state`): each pass folds only Tier 2 rows newer than the stored watermark into running accumulators, producing identical outputs to the old full-replay engine. Energy is a running sum of the per-cycle `plc_cycles.energy_kwh`. Run with `--rebuild-aggregation` to replay from scratch.

### Section 2 differences


| Parameter           | Section 2 formula                                                                   |
| ------------------- | ----------------------------------------------------------------------------------- |
| production          | **Reported per casting metal, not as a scalar.** `plc_filtered_metal_production` holds `Σ` declared `Casting metal N weight` grouped by metal name over the in-scope cycles. Not derived from `Tonnage` at all. |
| `energy_kwh_total`  | `Σ plc_cycles.energy_kwh` over in-scope cycles                                       |
| `energy_per_casting_kwh_kg` | `energy_kwh_total ÷ (total declared metal weight)` — same denominator as the per-metal table, so "per casting kg" means one thing in Section 2 |
| `machine_status`    | Not included                                                                        |
| `avg_shot_refill_time_sec`, `last_refill_epoch_sec`, `effective_shots_usage_kg_per_ton`, shots breakdown | Not included — Section 1 only, does not respond to any filter. (`production_qty_kg` IS in Section 2, with a different formula — see below) |


> **Section 1 vs Section 2 production is a deliberate split.** Section 1 = the PLC's `Tonnage` accumulator (what the machine measured). Section 2 = declared casting-metal weights (what the plant said it cast, per metal). These can legitimately disagree for the same window; neither is "wrong". A slot only counts when its weight > 0; a weight with a blank name lands in `unspecified`; a cycle declaring nothing contributes nothing.




### Per-cycle breakdown (`plc_filtered_cycle_data`)


| Column          | Formula                                                    |
| --------------- | ---------------------------------------------------------- |
| `production_kg` | `tonnage_kg(this cycle) − tonnage_kg(prev cycle)`, floor 0 |
| `energy_kwh`    | `avg_amps_all_impellers × cycle_duration_hours`            |




### Live display (not stored as parameters — dashboard reads directly)


| What                    | Source               | Query pattern                                                                 |
| ----------------------- | -------------------- | ----------------------------------------------------------------------------- |
| Amps per selected impeller | `plc_current_values` | `WHERE parameter_name = 'Current_imp_N'`, ordered by `substring(parameter_name from '[0-9]+$')::int` — a plain `ORDER BY parameter_name` is a TEXT sort, which puts `Current_imp_10` directly after `Current_imp_1`. Both amps panels lay the ten tiles out five to a row (CSS grid; MUI's 12 columns cannot divide into fifths) |
| Spare health (140 rows) | `plc_spare_status`   | All rows, or `WHERE trigger_active = TRUE AND threshold_hours > 0` for alerts |


---



## Dashboard UI contract


### Card order (both sections)

Cards are sorted by `PARAM_ORDER` in `dashboard/src/utils/unitConverters.ts`, **not** by the API's
`ORDER BY parameter_name`:

`machine_status` → `machine_utility_pct` → `production_qty_kg` → `energy_kwh_total` →
`energy_per_casting_kwh_kg` → `blast_time_sec` → `cycle_count` → `avg_shot_refill_time_sec` →
`last_refill_epoch_sec` → `effective_shots_usage_kg_per_ton`

`machine_status` is rendered by `MachineStatusTile` above the grid (it also carries the PLC link
state), so it is filtered out of the grid itself.


### Graphs


| Card | Section 1 graph | Section 2 graph |
| ---- | --------------- | --------------- |
| `machine_utility_pct` | All-time utility, `/api/trends` (month buckets) | Time filter only — hourly/daily buckets for the window. **Not offered for cycle/item filters**, and its toggle is disabled entirely under an item filter (those set `filter_start`/`filter_end` to `NOW()` placeholders, so there is no meaningful time axis) |
| `production_qty_kg` | All-time production: bars = produced per bucket, line = cumulative `Tonnage` | **Bar chart of declared weight per casting item** (`ItemProductionGraph`, from `plc_filtered_metal_production`). The scalar is the total of those bars — a different formula from Section 1's, by design |
| `energy_kwh_total` | All-time energy per bucket, `/api/trends` | Per-cycle bars (`plc_filtered_cycle_data`) |
| `energy_per_casting_kwh_kg` | **n/a — scalar only** | **n/a — scalar only.** kWh/kg drifts by thousandths across a bucket, so any axis fitted to it turns rounding into an apparent trend |
| `blast_time_sec` | Blast hours per bucket, `/api/trends` (`blast_on_sec`) | Same chart over the filter window (time filter only) |
| `cycle_count` | Completed cycles per bucket, `/api/trends` (`cycle_count`) | Same chart over the filter window (time filter only) |
| Amps tile (×10) | **Every recorded cycle, one point each** — that cycle's average current (`GET /api/amps/by-cycle`); 1 463 cycles in ~130 ms / 109 KB. The x-axis is CYCLE NUMBER, not time, so the trace does not return to zero between points and must not: nothing exists "between" cycle 960 and 961 to plot. The rise across the series is the blade-wear model. A per-sample view on 5/25/100-cycle ranges was built alongside this and **removed as redundant**; restoring it needs a time axis over a bounded window, since per-second detail across all cycles is ~178 000 samples per impeller. The tile headline is **always** the live Tier 1 reading, including 0 A between loads; while the impeller is not turning, the **last completed cycle's average** (`GET /api/amps/last-cycle`) is shown underneath as "ran at N A" | Same tile/dialog layout, driven by the FilterBar (all three modes, incl. custom range): tile shows a duration-weighted average current for the filter's cycles (`plc_filtered_amps_data`), dialog chart is one point per cycle (`AVG(value_num)` in that cycle's blast window) instead of raw per-second samples. **All cycles are plotted** — the old 200-cycle cap silently dropped ~1 250 of 1 448 while the tile beside it averaged every one |
| "Blast Cycles per Refill Interval" | Bar chart of `plc_shots_breakdown` — one bar per refill, height = blast cycles until the next refill. Labels are **date only**; a clock time is added only to bars that share a date with another | **n/a — removed from Section 2.** A refill interval spans whatever cycles fall in it, mixing items, so no filter scopes it meaningfully. Section 1 only, above the filter bar |

`effective_shots_usage_kg_per_ton` has no graph in either section — no `plc_daily_trends` rollup
column backs it, and it is Section 1 only. `energy_per_casting_kwh_kg` has no graph either, for a
different reason: the column exists, the series is just too flat to plot honestly.

**All graph math is server-side.** The dashboard plots `/api/trends` values verbatim — the same
rule the cloud mirror follows. The old browser-side `utilityCompute.ts` was deleted with the
rollup; there is no longer a second implementation to drift.

**Bucket granularity is chosen SERVER-SIDE** (`bucket=auto`, the default): ≤2 days of requested
window ⇒ `hour`; otherwise from the span of history that actually exists — ≤400 days ⇒ `day`, else
`month`. The resolved choice comes back in the `X-Trend-Bucket` response header so the dashboard
can title its axis without re-deriving it. `dashboard/src/utils/trendBuckets.ts` now only formats
labels.

> The browser used to choose, and it read "no bounds" as "all-time, so months" — which turned a
> plant with 29 days of history into **two** bars, one covering 19 days and one covering 6. Only
> the server knows how much history exists, so only the server can make this call.

**Every trend series is gap-filled.** A day the plant did not run has no `plc_daily_trends` row;
the API emits a zero bucket for it anyway. Without that, a chart drew 25 evenly-spaced points for a
29-day span and put a Saturday flush against a Monday — equal spacing that did not mean equal time.
Charts rely on this: they use category axes with strided ticks, which are only honest on a dense
series.

> `machine_utility_pct` scalar vs graph: the **scalar** clamps its denominator to start at the
> first-ever blast (avoiding a meaningless lifetime ratio), while each **graph bucket** is a plain
> `blast ÷ machine` for that bucket. Small divergence is by design, not a bug.


### Removed

- **"Effective Shots Usage" tile (original, pre-2026-08 version)** — deleted. It showed the blast
  count of the most recent refill interval labelled as a "usage" (a cycle count, not a ratio),
  which clashed with Section 2's `shots_usage` column at the time. The "Blast Cycles per Refill
  Interval" chart already shows that number for every interval.
- **Section 2's per-cycle `shots_usage`** (`refill_weight_in_cycle ÷ production_kg`) — also
  removed. Refills don't align to cycle boundaries, so a per-cycle ratio wasn't meaningful.
  Replaced by a new Section 1 scalar of the same display name — a different formula and different
  units. Do not conflate the "Effective Shots Usage" tiles across time; there have now been
  **three** distinct things under that name:

  | Era | Meaning | Unit | Direction |
  | --- | ------- | ---- | --------- |
  | pre-2026-08 | blast count of the most recent refill interval | cycles | — |
  | 2026-08 | `production_qty_kg ÷ total_refill_weight_kg` | kg/kg | higher better |
  | **current** (`effective_shots_usage_kg_per_ton`) | `total_refill_weight_kg ÷ (production_qty_kg ÷ 1000)` | **kg/T** | **lower better** |

- **Section 2's "Parameters" table** — the two-column Parameter/Value table that only restated the
  tiles above it. That duplication was the reason to remove it.

  The two tables that carry data the tiles do **not** are kept: **Production by Casting Item** (the
  per-item split behind the single Production total) and **Cycle Breakdown** (what each individual
  cycle contributed, and which items it declared). The test for whether a Section 2 table stays is
  "does it show something the tiles cannot" — not "is it a table".
- **Section 2's shots breakdown**, in full — computation, `/api/filter/{id}/shots`,
  `fetchFilterShots`, `section2.shotsBreakdown` in the admin API, and the Excel "Shots Breakdown"
  sheet. It is a Section 1 fact only.


### PLC disconnected

`machine_status` is the disconnect indicator: the backend forces the value to `0` and sets
`is_stale` when the link drops, so the tile reads **Stopped** plus a "PLC Disconnected" chip and
the last successful scan time. `AmpsPanel` and `SpareHealthTable` show a warning banner saying
their values are last-known and not advancing. Recording continues regardless.


---



## Key tag names (as they appear in `parameter_name` column)


| Tag                                | Type   | Address      |
| ---------------------------------- | ------ | ------------ |
| `Machine status`                   | BYTE   | DB60.DBB0    |
| `Blast ON/OFF`                     | BOOL   | —            |
| `Tonnage`                          | DINT   | —            |
| `Refil shots weight`               | DINT   | DB60.DBD2    |
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
| `'metal'`          | `filter_metal_name` — **displayed as "Item"**. Scopes every selected parameter to only the matching cycles; `machine_utility_pct` is disabled in this mode |


`filter_start` and `filter_end` are NOT NULL — always required. Pass `NOW()` as placeholder when using cycle or metal filter.

---



## Spare monitoring

10 impellers × 14 spares = 140 rows, monitored and shown only for the selected impellers (see
"Impeller selection"). Tag patterns:

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
| `PLCGateway/ImpellerSelection.cs`          | In-memory impeller selection (loaded from `gateway_settings`), read by every impeller-aware service |
| `PLCGateway/SecretConfig.cs`               | Reads secrets; a `REPLACE_WITH…` placeholder or a too-short key counts as NOT SET. Also `JwtSigningKey` — one signing key shared by `AuthController` and JwtBearer |
| `PLCGateway/appsettings.Production.json`   | **Git-ignored, never published.** This install's secrets (`Jwt:Key`, `Admin:ApiKey`, `License:*`). Loaded when the app runs as Production |
| `tools/Set-DashboardPassword.ps1`          | Changes a dashboard password: asks twice without showing it, stores only the hash |
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
| `PLCGateway/Api/Controllers/*.cs`          | Dashboard API + `AuthController` (JWT) + `AdminController` (cloud pulls: `live`, `trends`, `filter`, `history`) + `TrendsController` (local dashboard's graph series, JWT-protected) + `SettingsController` (`GET/PUT /api/settings/impellers`) |
| `PLCGateway/Api/Services/*.cs`             | API data services (typed reads), `TrendsService` (all graph math, `bucket=auto`, gap-fill), `UserService`, `LicenseState` |
| `dashboard/src/utils/trendBuckets.ts`      | Axis + tooltip label formatting per bucket (selection moved server-side)  |
| `dashboard/src/utils/chartAxis.ts`         | Shared axis construction — strided ticks, rounded y-domains, axis titles  |
| `dashboard/src/utils/useTrendSeries.ts`    | One fetch + labelling path for every trend chart                          |
| `dashboard/src/components/TrendChartFrame.tsx` | Loading/error/empty + the "one point per day" interval caption        |
| `dashboard/src/components/TrendMetricGraph.tsx` | Energy / blast-time / cycle-count bars from the rollup (all one component) |
| `dashboard/src/utils/exportFilteredExcel.ts` | Section 2 → 3-sheet .xlsx export (Parameters / Item Production / Cycles). Reads the FETCHED dataset, never a rendered table — which is why removing the tables left it working. Write-only; never parses a workbook |
| `dashboard/src/components/ItemProductionGraph.tsx` | Section 2 production tile's graph — declared weight per casting item (replaced the per-metal table) |
| `dashboard/src/components/FilterBar.tsx`    | Filter mode tabs + per-parameter toggles (Select All / Clear All); stays usable while a filter is applied |
| `dashboard/src/utils/usePlcConnection.ts`  | Shared PLC-link poll behind the amps + spares staleness banners            |
| `dashboard/src/components/LicenseGate.tsx` | Swaps the dashboard for a lock screen while `GET /api/license` says locked; warns during the grace period |
| `dashboard/src/components/ImpellerSelector.tsx` | Impeller number buttons + confirm dialog in the Live Impeller Current header. `services/settingsService.ts` fires `IMPELLER_SELECTION_CHANGED` after a save so the slow-polling panels reload at once |
| `PLCGateway/Api/Middleware/*.cs`           | `AdminGuardMiddleware` (IP+key), `LicenseLockMiddleware` (402 when locked) |
| `dashboard/`                               | **Dashboard source** (Vite + React + TS), vendored in-repo. `npm run build` emits into `PLCGateway/wwwroot` (same-origin). |
| `PLCGateway/wwwroot/`                       | Built React dashboard (served same-origin) — generated from `dashboard/`   |
| `PLCGateway/web.config`                    | IIS ASP.NET Core Module (InProcess) config                                |
| `DEPLOYMENT-NOTES.md`                       | IIS setup, app pool, 443/cert, admin IP restriction, config placeholders  |
| `README.md`                                | Full architecture, schema, formulas, run instructions                     |


---



## Latest revision — six changes (see README for the full reference)

1. **Layout: the complete Section 1 above the filter, Section 2 below it.** *(Superseded — see
   "Layout, current" below. Kept because the key lists it names are still the ones in the code.)*
   Above the filter bar: the Section 1-ONLY parameters (`machine_status`,
   `avg_shot_refill_time_sec`, `last_refill_epoch_sec`, `effective_shots_usage_kg_per_ton`,
   shots-per-refill chart, spare health). Below the filter bar: the parameters that exist in BOTH
   sections (`machine_utility_pct`, `production_qty_kg`, `energy_kwh_total`,
   `energy_per_casting_kwh_kg`, `blast_time_sec`, `cycle_count`, impeller current), showing their
   Section 1 real-time values until a filter is applied and their Section 2 values after.
   Key lists: `SECTION1_ONLY_PARAM_KEYS` / `SHARED_PARAM_KEYS`; `LifetimeSection` is rendered twice.
   **Nothing is computed until Apply is pressed** — no `calculation_requests` row on page load.
   The old `if (filterApplied) hide Section 1` ternary is deleted, and the sections share no fetch,
   cache key or state slice.
2. **`effective_shots_usage` → `effective_shots_usage_kg_per_ton`.** Inverted and rescaled:
   `total_refill_weight_kg ÷ (production_qty_kg ÷ 1000)`, unit kg/T, **lower is better**, null
   guard moved to production. `migration.sql` deletes the stale row. `production_qty_kg` itself is
   unchanged (still raw `Tonnage`) — the commissioning-baseline idea was investigated and
   deliberately not built; see README.
3. **"Metal" → "Item" in the dashboard UI only.** PLC tag names, `appsettings.json` keys, DB
   columns, C# models, DTOs, TypeScript types and API routes/fields all still say `metal`. Rule:
   if a user reads it, it says *item*; if a machine reads it, it says *metal*.
4. **Per-parameter calculation toggles.** `calculation_requests.selected_parameters TEXT[]`
   (`NULL` = all). Unselected parameters are never computed. Single source of truth for the key
   list: `CalculationService.Section2ParameterKeys`, mirrored in `SECTION2_PARAM_KEYS`.
5. **Item filter genuinely scopes.** `blast_time_sec` and `cycle_count` become cycle-derived under
   an item filter (the window replay leaked other items' cycles); declared-weight sums count only
   the filtered item. `machine_utility_pct` is disabled under an item filter — machine on-time is
   not attributable to one item.
6. **Section 2 = tiles + graphs + the two data tables.** Removed only the "Parameters" table,
   which restated the tiles above it. **Production by Casting Item** and **Cycle Breakdown** stay —
   they carry data the tiles cannot. New Section 2 scalar `production_qty_kg` (Σ declared item
   weight) with a per-item bar graph sits above the item table. Excel export reads the fetched
   dataset, not the tables.

**Removed in this revision:** `GET /api/filter/{id}/shots`, `IFilterService.GetShotsBreakdownAsync`,
`section2.shotsBreakdown` in the admin API, `CalculationService.ComputeShotsBreakdownAsync`,
`fetchFilterShots`, the Excel "Shots Breakdown" sheet. The shots breakdown is Section 1 only — it
does not respond to a filter. `plc_filtered_shots_breakdown` is left in place, unwritten.

**Two cloud-contract breaks** (documented in `CONTRACT-admin-api.md`): the renamed lifetime key and
the removed `section2.shotsBreakdown`.

---

## Layout, current

Three blocks, top to bottom:

| Block | Contents |
| ----- | -------- |
| **Above the filter** | The COMPLETE Section 1 picture: `MachineStatusTile`, then **every** Section 1 parameter (`ALL_SECTION1_PARAM_KEYS` = `SECTION1_ONLY_PARAM_KEYS` + `SHARED_PARAM_KEYS`), the shots-per-refill chart, live `AmpsPanel`, and `SpareHealthTable`. All-time, never responds to the filter. |
| **The filter bar** | Unchanged. |
| **Below the filter** | The shared parameters again — Section 1 values until Apply is pressed, Section 2 values after. |

The shared parameters therefore appear **twice** while no filter is applied. That repetition is
deliberate and was requested: the upper block is a fixed all-time reference that never moves, so a
filtered figure below can be compared against its all-time counterpart without clearing the filter.
Revision #1 above removed exactly this duplication; it is back by request. Do not "fix" it.

---

## Graph rules (apply to every chart)

These exist because the charts were reviewed and found unreadable. Breaking any one of them
reintroduces a specific defect that was reported:

| Rule | Why |
| ---- | --- |
| **Equal spacing must mean equal interval.** Category axes are permitted only on a gap-filled series. | 4 missing Sundays were drawn as 25 evenly-spaced points across a 29-day span. |
| **Ticks are strided** (`tickInterval()`), never `interval="preserveStartEnd"`. | preserveStartEnd thins by whatever fits, so the gap between printed ticks varies along the axis. |
| **Numeric axes use `type="number"` + `numericTicks()`**, never `interval` striding. | `interval` strides by row INDEX, so a cycle axis running 1…1463 printed 1, 106, 211, 316. Positional ticks land on 100, 200, 300 — numbers a reader can actually look up. |
| **Label EVERY point when the labels fit.** Tilted date axes use `categoryXAxis(count)` (target 31 — a full month of days); horizontal numeric axes pass `tickInterval(n, 14)`, since unrotated text needs its full width. | A chart captioned "one point per day" that labels every third day makes the reader count gridlines to find a date. Striding only begins past what physically fits. |
| **Both axes carry a title** naming the unit or the interval (`xAxisTitle` / `yAxisTitle`). | Most charts had no axis titles at all. |
| **Y domains start at 0 and use rounded steps** (`niceScale()`). | Auto domains put gridlines on numbers like 4 731.6 and magnified flat series into apparent trends. |
| **Plot every point in scope — never truncate.** | `.slice(-200)` dropped ~1 250 of 1 448 cycles while the tile beside it totalled all of them. |
| **No reference line without a legend entry.** | A hard-coded dashed 80 % target on the utility chart read as an unexplained second data series above the real one. |
| **Charts open at `maxWidth="lg"` and `CHART_HEIGHT`.** | A dense series in a 600 px dialog forced labels to be thinned to nothing. |

Shared helpers live in `dashboard/src/utils/chartAxis.ts`; every trend chart fetches through
`useTrendSeries` and renders inside `TrendChartFrame`, so granularity, labelling and empty-state
wording cannot drift between charts.

---

## Impeller currents are heartbeat tags

`appsettings.json` lists `Current_imp_1…10` in `HeartbeatTags`, so a live gateway writes a row per
impeller every 60 s **even at 0 A between loads**. Keep them there: without the heartbeat a 3–4
minute changeover carries only two rows — the zero at blast end and the zero at the next blast
start — so the amps trace crosses the whole gap on one straight segment and a tooltip anywhere
along it reports the same timestamp.

Blast periods are unaffected, since the blast-time samples are denser than the heartbeat, and
per-cycle energy is unchanged because the idle rows fall outside every `[blast_start, blast_end]`
window.

---

## Impeller selection (`gateway_settings.selected_impellers`)

Which impellers the site includes, picked with the number buttons in the **Live Impeller Current**
header (`ImpellerSelector`). It replaced the expo-era `Impellers:Count` config key. The choice is
**machine-wide** — saved on the gateway for every viewer and the cloud — and it is **not only a
display filter: hidden impellers are left out of the calculations too.** Any signed-in user may
change it (site decision, 2026-09-15).

| Reader | Effect |
| ------ | ------ |
| `DatabaseService.InsertCycleAsync` | per-cycle `energy_kwh` sums only the selected impellers (`EnergyKwhExpr`, `unnest(@impellers)`) |
| `DatabaseService.InsertFilteredAmpsDataAsync` | the Section 2 per-impeller current split covers only the selected impellers |
| `AmpsService` | live and last-cycle amps return only the selected impellers |
| `SpareMonitoringService` | maintains spares for the selected impellers only |
| `SpareStatusService` | `WHERE impeller_num = ANY(@imps)` — **filters, never deletes**; a deselected impeller's rows come back unchanged when it is selected again |
| `AdminController` `/live` | adds `impellers.selected`; `amps` / `spareGrid` / `spareAlerts` hold only those impellers |
| Dashboard | derives everything from what the API returns; `SpareHealthTable` builds its columns from the rows themselves |
| `GatewayWorker` | **unaffected** — raw `Current_imp_1…10` are recorded for every impeller, always |

**Saving recalculates all recorded energy.** `PUT /api/settings/impellers` →
`CalculationService.ApplyImpellerSelectionAsync` → `DatabaseService.SaveImpellerSelectionAsync`,
one transaction: `gateway_settings`, `plc_cycles.energy_kwh` for every cycle (re-read from Tier 2),
`plc_daily_trends.energy_kwh` per day, and `plc_aggregation_state.energy_total`. The lifetime
parameters are then re-emitted, so `energy_kwh_total`, `energy_per_casting_kwh_kg` and the energy
graphs move together. Tier 2 is only read and no row is deleted. Selecting all ten again reproduces
the original stored figures exactly (verified cycle by cycle on a copy of the real database).

- **The Section 1 lock.** `CalculationService._section1Lock` serialises the aggregation pass, the
  yesterday+today rollup refresh (`RefreshRecentDailyTrendsAsync`) and a selection save. Without it
  a pass that loaded `plc_aggregation_state` before a save committed would write the OLD energy
  total straight back. Anything new that writes Section 1 energy must take it.
- **Not routed through `RetryAsync`,** which logs and swallows a final failure — the dashboard must
  be told when a save did not happen. The in-memory selection is put back if the save fails.
- **Section 2 results are snapshots.** A filter computed before a change keeps the old impeller set
  until it is applied again; the confirm dialog says so.

Both amps panels and the spare table are **width-capped and CENTRED**. The cap is what makes
centring work at all: `1fr` tracks always fill their container, so two impellers would stretch to
half the screen each and `justifyContent` would have nothing left to centre.

Alignment went centred → left → centred across review rounds. Left-aligning was tried because the
section headings are flush left and a centred block can read as detached from its own title; the
table also looked cramped at the time, because it was `fit-content` and collapsed to ~110 px
columns. Widening the columns (`SPARE_COL_WIDTH` + `IMPELLER_COL_WIDTH` per impeller) fixed the
cramping, and centred is the chosen look. **Centred is the current decision — do not "correct" it
back to left.**

---

## Running vs Loading vs Stopped

`MachineStatusTile` shows three states, not two:

- **Running** — blast on.
- **Loading** — powered and reachable, not blasting: the plant is loading the next batch. A normal
  state, and most of any shift. (Labelled "Idle" until 2026-09; only the wording changed.)
- **Stopped** — the gateway cannot reach the PLC, so the zero is inferred rather than measured
  (the backend forces the value to 0 and flags the row stale).

Collapsing Loading into Stopped made a healthy machine between loads read identically to a dead link.

---

## Secrets, logins and the licence lock

Set up 2026-09-18 so the gateway can face the internet (the cloud admin API, and testing through a
Cloudflare quick tunnel — `DEPLOYMENT-NOTES.md` section 11).

- **Secrets live in `appsettings.Production.json`** next to the app — git-ignored and excluded from
  publish, so a republish never overwrites it. `appsettings.json` keeps only `REPLACE_WITH…`
  placeholders. IIS runs as Production and loads it; locally use `dotnet run --no-launch-profile`.
- **Placeholders are never keys** (`SecretConfig`). Before this, the public placeholder strings were
  accepted as real keys. Now `Admin:ApiKey` missing, placeholder or under 32 characters ⇒ every
  `/api/admin/*` request is 403; `Jwt:Key` the same ⇒ a random key per run (logins work but do not
  survive a restart). The hard-coded fallback JWT key is gone. Both cases log a startup warning.
- **Logins**: `tools/Set-DashboardPassword.ps1` changes a password. Hashes are still plain unsalted
  SHA-256, and there is **no login-attempt limit** — offered and declined 2026-09-18. Revisit both
  before the gateway gets a permanent public address.
- **Licence rules (settled 2026-09-18)**: 2xx = valid; 401/402/403 = lock at once; anything else =
  "unreachable", open until 72 h after the last 2xx; empty `CheckUrl` = check off, dashboard open,
  and it never moves the grace clock. The grace anchor is stored with its URL (`last_success_url`),
  so a success from another URL — or from the empty-URL era — never carries over. The lock is
  HTTP-only: `LicenseLockMiddleware` answers 402 on the dashboard API (`/api/admin`, `/api/auth`,
  `/api/license`, `/api/health` exempt) and `LicenseGate` shows the lock screen, while every hosted
  service keeps recording (verified: a blast cycle was recorded while locked).
- **Time-zone bug fixed**: licence times were written as UTC-marked values into `TIMESTAMP` columns,
  so PostgreSQL stored them as local time, and they were read back as UTC — silently adding 5 h 30 min
  of grace. They are now stored as true UTC wall-clock values.
- **`/api/admin/history` takes and returns local time without `Z`** — the one exception to the
  contract's UTC rule. Documented rather than changed, so existing callers keep working.

---

## Resolved client decisions (do not reopen without the client)

- **Energy formula — settled.** The client confirmed the PLC delivers energy as a correct value, so **no conversion formula is applied**. `energy_kwh_total` and per-cycle `energy_kwh` stay as `Σ(avg_amps × duration_hours)`. The unused `EnergyCalculation` config block (SupplyVoltageV / PowerFactor / ActiveImpellerCount) was **removed** — it was referenced by no code.
- **`Refil shots weight` — settled as `DINT`.** `appsettings.json` is authoritative; docs corrected to match.
- **Empty casting metal slot — settled.** `CycleTrackingService` trims names and normalises blanks to `NULL` at recording time, so an empty slot is always `null`, never `""`.
- **Section 2 production — settled.** Section 2 reports production **per declared casting metal** (sum of `Casting metal N weight` grouped by name), *not* from `Tonnage`. Section 1 keeps `Tonnage`. The two intentionally answer different questions and need not reconcile.
- **`shots_usage` — settled as Section 1 only, cumulative.** Section 2's per-cycle `shots_usage` (`refill_weight_in_cycle ÷ production_kg`) was removed because refills don't align to cycle boundaries. A new Section 1 scalar, `effective_shots_usage` (`production_qty_kg ÷ total_refill_weight_kg`, both cumulative since commissioning), replaces it with a genuinely meaningful ratio. Do not reintroduce a per-cycle version.
- **Admin API auth — settled as key-only, no IP allowlist.** The cloud caller runs on Firebase Cloud Functions, which has no fixed egress IP on the current plan, so an IP gate can never pass. `AdminGuardMiddleware` checks `X-Api-Key` only.

## Pending items

- ~~Cloud `/api/admin/*` contract needs a broader pass~~ — **done.** `AdminController` now exposes three endpoints: `GET /api/admin/live` (extended: `section2.metals[]` added), `GET /api/admin/trends` (new — whole-history graph series, same rollup logic as the local `/api/trends`, behind `AdminGuardMiddleware` instead of JWT), `POST /api/admin/filter` (new — synchronous cloud-triggered Section 2 calculation, no `calculation_requests` polling wait). `CONTRACT-admin-api.md` and `sample-response.json` are current as of this pass. The metal-filter latency number in that doc is a structural argument, not a measured one — this repo's dev DB only has 5 cycles, too small to stress-test; re-benchmark before the cloud finalizes a hard timeout.
- **Commissioning `Tonnage` baseline for `effective_shots_usage_kg_per_ton`** — investigated, **deliberately not built**. `production_qty_kg` is the PLC's own lifetime accumulator while `total_refill_weight_kg` only accumulates from gateway commissioning, so on a gateway retrofitted to a running machine the ratio under-reports shot consumption (converging as history grows). A fix means storing `baseline_tonnage_kg` + `baseline_tonnage_set` in `plc_aggregation_state`, seeding from the earliest Tier 2 `Tonnage` row, and clearing both on `--rebuild-aggregation`. **The client decided `production_qty_kg` stays as raw `Tonnage`.** Applying a baseline to only the usage denominator would leave the two tiles mutually inconsistent — settle that with the client before building it. Full write-up in README, "Effective Shots Usage".
- **`plc_historical_data` partitioning** — recommended within a couple of years (monthly declarative partitioning, retains every row). See README "Scaling".
- **1 Hz `BLAST_ON` current logging** — `GatewayWorker` writes 10 impeller-current rows *per second* while blast is ON, which is 60–80% of all Tier 2 rows. Coarsening it to 5–10 s would cut storage 5–10× with negligible effect on cycle-average energy, but would coarsen the per-cycle `AmpsGraph` trace. Client decision, unchanged.
- **Recording gaps inflate the Section 1 lifetime scalars.** `FoldBlast`/`FoldMachine` in `CalculationService` accumulate the full span between consecutive events with no cap, so a multi-week gateway outage is counted as runtime. The `plc_daily_trends` rollup **does** guard against this (segments over 5 min are treated as gaps), which is why a graph bucket and the lifetime scalar can disagree after an outage. The scalar was left alone deliberately — changing it would silently move a long-standing headline number. Decide with the client before touching it.
- **`plc_historical_data` carries more redundant indexes.** Beyond the `idx_historical_name_time` dropped in this pass, `idx_historical_parameter` (`parameter_name`) is a prefix of `idx_historical_name_num`, and `idx_historical_address` is a prefix of `idx_historical_address_timestamp` — 7 indexes total on the hottest write path. Dropping the two prefixes is a further easy write-cost win; not done here because it was outside the agreed scope.

---



## How to run migration

```powershell
psql -U postgres -d sreesakthi_gateway -f PLCGateway/migration.sql
```

Safe to re-run on existing databases — all statements are idempotent (typed-column backfill and per-cycle production/energy backfill are guarded and run once).

## How to run / deploy

- **Local:** `dotnet run --project PLCGateway` (serves API + dashboard on the Kestrel port). `--rebuild-aggregation` resets Section 1 incremental state and replays history.
- **Production:** publish and host under IIS — see `DEPLOYMENT-NOTES.md`. App pool must be AlwaysRunning / Idle 0 / Preload so the PLC scan loop is never idled.