# PLCGateway

Unified application for a shot-blast machine (foundry). A single ASP.NET Core (.NET 10) app,
hosted under IIS, that reads a Siemens S7-1200 PLC every second (background hosted services),
stores raw tag history in PostgreSQL, computes business parameters, **serves the dashboard's
JSON API**, and **serves the React dashboard build** from `wwwroot/` — all in one process. The
cloud reaches the app only over HTTPS via secured `/api/admin/*` endpoints, never the database.

**Stack:** .NET 10, C#, ASP.NET Core (WebApplication + hosted services), S7.NetPlus,
Npgsql/PostgreSQL, JWT auth; Vite + React dashboard built into `wwwroot`.

---

## Architecture

```
Siemens S7-1200 PLC (192.168.0.180)
         │  S7.NetPlus — 1 second scan
         ▼
   GatewayWorker
         │
         ├──► plc_current_values     (Tier 1 — latest value per tag, always updated)
         │
         └──► plc_historical_data    (Tier 2 — COV-based, never deleted)
                    │
                    ├── AggregationService (every 1 min)
                    │         ├──► plc_lifetime_parameters    (Section 1 — scalar cumulative params)
                    │         └──► plc_shots_breakdown        (Section 1 — shots/refill breakdown table)
                    │
                    ├── CycleTrackingService (every 2 sec)
                    │         └──► plc_cycles                 (one row per blast cycle)
                    │
                    ├── FilteredCalculationService (every 5 sec)
                    │         ◄── calculation_requests        (Section 2 — dashboard trigger)
                    │         ├──► plc_filtered_parameters    (Section 2 — scalar results)
                    │         ├──► plc_filtered_cycle_data    (Section 2 — per-cycle breakdown)
                    │         └──► plc_filtered_shots_breakdown (Section 2 — shots breakdown)
                    │
                    └── SpareMonitoringService (every 10 sec)
                              └──► plc_spare_status           (140 spare health rows)
```

### Services

| Service | Interval | Role |
|---|---|---|
| `GatewayWorker` | 1 second | Reads all PLC tags → Tier 1 always, Tier 2 on COV |
| `CovDetectionService` | per tag | ≥2% change for numeric, state change for BOOL, 60s heartbeat |
| `AggregationService` | 1 minute | Computes all lifetime parameters → `plc_lifetime_parameters` + `plc_shots_breakdown`, then refreshes `plc_daily_trends` for yesterday+today only (bounded work per pass) |
| `CycleTrackingService` | 2 seconds | Detects blast cycle end (falling edge on Blast ON/OFF), writes `plc_cycles` |
| `FilteredCalculationService` | 5 seconds | Polls `calculation_requests`, runs aggregate + per-cycle + shots + per-metal calculations |
| `SpareMonitoringService` | 10 seconds | Reads 140 spare trigger/run-hour/replaced tags, updates `plc_spare_status` |

---

## Database Schema

Run `PLCGateway/migration.sql` once on your PostgreSQL database before starting.
The script is idempotent — safe to run on a fresh database or to upgrade from v1 or v2.

### `plc_current_values` — Tier 1
One row per PLC tag. Always holds the latest value.

| Column | Type | Description |
|---|---|---|
| `address` | VARCHAR PK | PLC address (e.g. `DB60.DBB0`) |
| `parameter_name` | VARCHAR | Tag name |
| `value` | TEXT | Latest raw value |
| `data_type` | VARCHAR | BOOL / BYTE / DINT / REAL / STRING |
| `last_updated` | TIMESTAMP | Last scan write |
| `last_stored_historical` | TIMESTAMP | Last Tier 2 write |
| `last_heartbeat` | TIMESTAMP | Last periodic heartbeat |

### `plc_historical_data` — Tier 2
COV-triggered time-series. **Never deleted.** Source of truth for all calculations.

| Column | Type | Description |
|---|---|---|
| `id` | SERIAL PK | |
| `address` | VARCHAR | PLC address |
| `parameter_name` | VARCHAR | Tag name |
| `value` | TEXT | Value at time of storage |
| `data_type` | VARCHAR | |
| `storage_reason` | VARCHAR | `INITIAL` / `COV` / `STATE_CHANGE` / `PERIODIC` |
| `timestamp` | TIMESTAMP | When stored |
| `previous_value` | TEXT | Value before this change |

### `plc_lifetime_parameters` — Section 1 scalar output
One row per parameter. Updated every minute. Dashboard reads directly.

| Column | Type | Description |
|---|---|---|
| `parameter_name` | VARCHAR PK | Parameter identifier |
| `value` | NUMERIC | Current cumulative value |
| `updated_at` | TIMESTAMP | Last calculation time |

### `plc_shots_breakdown` — Section 1 shots/refill breakdown
Cleared and rewritten every minute. One row per refill interval (from second refill event onward).
Dashboard renders as table + graph for parameters #7 and #8.

| Column | Type | Description |
|---|---|---|
| `id` | SERIAL PK | |
| `refill_timestamp` | TIMESTAMP | When this refill event occurred |
| `blast_count` | INTEGER | Blast cycles between previous and this refill |
| `calculated_at` | TIMESTAMP | |

### `plc_cycles` — Cycle log
One row per completed blast cycle. Written by `CycleTrackingService`.

| Column | Type | Description |
|---|---|---|
| `cycle_number` | SERIAL PK | Global sequential cycle ID, never resets |
| `blast_start` | TIMESTAMP | When Blast ON/OFF went TRUE |
| `blast_end` | TIMESTAMP | When Blast ON/OFF went FALSE |
| `duration_sec` | NUMERIC | Blast duration in seconds |
| `metal_1_name` … `metal_4_name` | TEXT | Casting metal name per slot (NULL if slot unused) |
| `metal_1_weight_kg` … `metal_4_weight_kg` | NUMERIC | Casting metal weight per slot |
| `tonnage_kg` | NUMERIC | Accumulated tonnage at cycle end (read from Tier 1) |
| `recorded_at` | TIMESTAMP | When backend logged this row |

### `calculation_requests` — Section 2 trigger
Dashboard inserts a row here to request a filtered calculation.

| Column | Type | Description |
|---|---|---|
| `id` | SERIAL PK | |
| `filter_start` | TIMESTAMP | Start of requested period (required — pass NOW() if not used) |
| `filter_end` | TIMESTAMP | End of requested period (required — pass NOW() if not used) |
| `period_label` | VARCHAR | `hour` / `shift` / `day` / `week` / `month` / `year` / NULL |
| `filter_by` | VARCHAR | `time` (default) / `cycle` / `metal` |
| `filter_cycle_from` | INTEGER | Cycle number range start (when `filter_by = 'cycle'`) |
| `filter_cycle_to` | INTEGER | Cycle number range end |
| `filter_metal_name` | TEXT | Casting metal name to filter by (when `filter_by = 'metal'`) |
| `status` | VARCHAR | `pending` → `processing` → `done` / `error` |
| `created_at` | TIMESTAMP | When dashboard submitted |
| `processed_at` | TIMESTAMP | When backend completed |

### `plc_filtered_parameters` — Section 2 scalar results
One row per parameter per request.

| Column | Type | Description |
|---|---|---|
| `id` | SERIAL PK | |
| `request_id` | INTEGER FK | References `calculation_requests.id` |
| `parameter_name` | VARCHAR | Parameter identifier |
| `value` | NUMERIC | Calculated value for the window |
| `calculated_at` | TIMESTAMP | |

### `plc_filtered_cycle_data` — Section 2 per-cycle breakdown
One row per cycle per request.

| Column | Type | Description |
|---|---|---|
| `id` | SERIAL PK | |
| `request_id` | INTEGER FK | References `calculation_requests.id` |
| `cycle_number` | INTEGER | Global cycle number |
| `blast_start` | TIMESTAMP | |
| `blast_end` | TIMESTAMP | |
| `metal_1_name` … `metal_4_name` | TEXT | |
| `metal_1_weight_kg` … `metal_4_weight_kg` | NUMERIC | |
| `production_kg` | NUMERIC | Tonnage delta vs previous cycle |
| `energy_kwh` | NUMERIC | avg amps × duration hours across all 10 impellers |
| `shots_usage` | NUMERIC | Refill weight in cycle ÷ production_kg |
| `calculated_at` | TIMESTAMP | |

### `plc_filtered_shots_breakdown` — Section 2 shots breakdown
One row per refill interval per request. Same structure as `plc_shots_breakdown`.

| Column | Type | Description |
|---|---|---|
| `id` | SERIAL PK | |
| `request_id` | INTEGER FK | References `calculation_requests.id` |
| `refill_timestamp` | TIMESTAMP | When this refill event occurred |
| `blast_count` | INTEGER | Blast cycles between previous and this refill |
| `calculated_at` | TIMESTAMP | |

### `plc_spare_status` — Spare health (140 rows)
One row per spare per impeller. Updated every 10 seconds by `SpareMonitoringService`.

| Column | Type | Description |
|---|---|---|
| `impeller_num` | INTEGER | 1–10 |
| `spare_index` | INTEGER | 0–13 |
| `spare_name` | TEXT | From `appsettings.json` |
| `threshold_hours` | NUMERIC | Run-hour limit from OEM spec (0 = not tracked) |
| `current_run_hours` | NUMERIC | Accumulated hours from PLC (`Spares_Runhour_impN[M]`) |
| `trigger_active` | BOOLEAN | TRUE when PLC flag is set (`Spares trigger impN[M]`) |
| `last_replaced_at` | TIMESTAMP | Set when `REPLACED_N[M]` rising edge detected |
| `last_updated_at` | TIMESTAMP | Last poll time |
| PRIMARY KEY | `(impeller_num, spare_index)` | |

Dashboard alert condition: `trigger_active = TRUE AND threshold_hours > 0`

---

## Parameter Calculations

### Section 1 — Lifetime (`plc_lifetime_parameters`)

| `parameter_name` | Formula | Source |
|---|---|---|
| `machine_status` | `Machine status ≠ 0 → 1`, else `0` | `plc_current_values` address `DB60.DBB0`, live |
| `machine_utility_pct` | `blast_time_sec ÷ machine_on_time_sec × 100` | `Blast ON/OFF`, `Machine status` from Tier 2 |
| `production_qty_kg` | Latest raw value of `Tonnage` tag | `plc_current_values` — PLC is a running accumulator |
| `energy_kwh_total` | `Σ (avg_amps_per_impeller × cycle_duration_hours)` across all 10 impellers × all cycles | `plc_cycles` for boundaries; Tier 2 `Current_imp_1`…`10` for COV readings per cycle |
| `energy_per_casting_kwh_kg` | `energy_kwh_total ÷ production_qty_kg` | Derived |
| `blast_time_sec` | Total seconds where `Blast ON/OFF = true` | Tier 2 state transitions |
| `cycle_count` | Rising edges `0→1` on `Blast ON/OFF` | Tier 2 |
| `avg_shot_refill_time_sec` | Elapsed since first refill ÷ refill count | Tier 2 refill events |
| `last_refill_epoch_sec` | Unix timestamp of latest `Refil shots weight` change | Tier 2 MAX timestamp |

> **Energy formula is final.** The client confirmed the PLC supplies energy as a correct value, so
> no voltage/power-factor conversion is applied. The previously unused `EnergyCalculation` config
> block has been removed.

### Section 1 — Shots breakdown (`plc_shots_breakdown`)

For each pair of consecutive `Refil shots weight` COV events, count the number of `Blast ON/OFF` rising edges between them. Output: `(refill_timestamp, blast_count)`. Maintained by idempotent upsert keyed on `refill_timestamp` (never TRUNCATE, so the dashboard never reads a half-empty table).

### `plc_daily_trends` — derived daily rollup (graph source)

One row per calendar day, refreshed by `AggregationService` every minute for **yesterday and today
only**, so the work per pass is bounded by two days of data rather than growing with total history.

It exists because the Section 1 graphs are all-time. Serving those from raw Tier 2 would mean
shipping every row to the browser: with 60 s heartbeats on `Blast ON/OFF` **and** `Machine status`,
that pair alone accumulates roughly **1 M rows/year**. The rollup collapses that to ~365 rows/year,
so a graph open costs the same in year ten as on day one.

| Column | Meaning |
|---|---|
| `day` | Calendar day (PK) |
| `machine_on_sec`, `blast_on_sec` | Seconds each tag was on that day |
| `cycle_count` | Blast rising edges that day |
| `production_kg`, `energy_kwh` | Summed from cycles ending that day |
| `tonnage_end` | Last `Tonnage` reading of the day; NULL if none (read path carries the previous value forward) |

This table is **derived, not history** — clear and rebuild it any time with
`--rebuild-aggregation`. It is never a reason to delete raw rows.

*Segment accounting* — two rules, both driven by real data:

- On-segments are **split at day boundaries**, each day credited only its own slice. Crediting a
  whole segment to its start day allowed one segment to report 28 days of runtime inside a single
  calendar day.
- A segment longer than **5 minutes counts as a recording gap, not runtime** (only its first 5
  minutes count). The 60 s heartbeat guarantees closer spacing whenever the gateway is scanning, so
  a wider gap means the gateway was down. Without this, an outage inflated machine-on time and
  could push `blast_on_sec` above `machine_on_sec` (utility over 100%).

The same two rules are applied by the live hourly path in `TrendsService`, so the hourly and daily
views agree. Note the **Section 1 `machine_utility_pct` scalar** in `CalculationService` does *not*
apply the gap guard — it is the long-standing lifetime figure and was left unchanged; see
CLAUDE.md "Pending items".

### Section 2 — Filtered (`plc_filtered_parameters` + `plc_filtered_cycle_data` + `plc_filtered_shots_breakdown` + `plc_filtered_metal_production`)

Same parameters as Section 1 over the requested window, with these differences:

| Parameter | Section 2 logic |
|---|---|
| production | **Not a scalar.** Reported per casting metal in `plc_filtered_metal_production`: `Σ` declared `Casting metal N weight` grouped by metal name. Not derived from `Tonnage`. |
| `energy_kwh_total` | `Σ plc_cycles.energy_kwh` for cycles within the filter |
| `energy_per_casting_kwh_kg` | `energy_kwh_total ÷ total declared metal weight` (same denominator as the per-metal table) |
| `machine_status`, `production_qty_kg`, `avg_shot_refill_time_sec`, `last_refill_epoch_sec` | Not included |

**Section 1 vs Section 2 production is a deliberate split:** Section 1 reports what the machine
*measured* (the `Tonnage` accumulator); Section 2 reports what the plant *declared* it cast, per
metal. The two can legitimately disagree over the same window and neither is wrong. A metal slot
counts only when its weight > 0; a weight with a blank name lands in `unspecified`; a cycle that
declared nothing contributes nothing.

**Per-cycle breakdown (`plc_filtered_cycle_data`):**

| Column | Description |
|---|---|
| `production_kg` | `tonnage_kg(this cycle) − tonnage_kg(previous cycle)` |
| `energy_kwh` | `avg_amps_all_impellers × cycle_duration_hours` |
| `shots_usage` | `refill_weight_in_cycle ÷ production_kg` |

### Section 2 filter modes

| `filter_by` | Uses | Example |
|---|---|---|
| `'time'` (default) | `filter_start` / `filter_end` | All cycles between two timestamps |
| `'cycle'` | `filter_cycle_from` / `filter_cycle_to` | Cycles 10 through 25 |
| `'metal'` | `filter_metal_name` | All cycles that used "Aluminium" in any metal slot |

### Spare thresholds (from appsettings.json)

| # | Spare Name | Run Hours |
|---|---|---|
| 0 | Blade | 100 |
| 1 | Blade Mounting Piece | 300 |
| 2 | Narrow Plate | 300 |
| 3 | Curved Plate | 600 |
| 4 | Feeding End | 2000 |
| 5 | Bearing End | 2000 |
| 6 | Impeller | 300 |
| 7 | Wall Plate | 2000 |
| 8 | Control Gauge | 300 |
| 9 | Disc Spacer | — (not tracked) |
| 10 | Doom Nut 1/2in | 2000 |
| 11 | Doom Nut 5/8 | 2000 |
| 12 | Disc | 5000 |
| 13 | Guide Plate | 600 |

---

## Configuration (appsettings.json)

```json
"PLC": {
  "IpAddress": "192.168.0.180",
  "Rack": 0,
  "Slot": 1
},
"PostgreSQL": {
  "ConnectionString": "Host=localhost;Port=5432;Database=sreesakthi_gateway;Username=postgres;Password=Pass"
},
"ScanIntervalMs": 1000,
"DataCollection": {
  "CovDeadbandPercent": 2.0,
  "PeriodicHeartbeatSeconds": 60
},
"MaintenanceThresholds": {
  "SpareNames": ["Blade", "Blade Mounting Piece", ...],
  "SpareLifeBlastHours": [100, 300, 300, 600, 2000, 2000, 300, 2000, 300, 0, 2000, 2000, 5000, 600]
}
```

---

## How to Run

### Prerequisites
- .NET 10 SDK
- PostgreSQL running with database `sreesakthi_gateway`
- Siemens S7-1200 PLC reachable at the configured IP

### 1. Set up the database

```bash
psql -U postgres -d sreesakthi_gateway -f PLCGateway/migration.sql
```

Safe to run on existing databases — uses `IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`, and `DO $$ ... $$` blocks throughout.

### 2. Run locally (one process)

The dashboard source is vendored in `dashboard/`; its build output lives in `PLCGateway/wwwroot`
and is served by the same app. Build the dashboard once (only needed if `wwwroot` is missing or
the frontend changed), then run the backend — one process serves the UI and the API.

```bash
# (once, or after frontend changes) build the dashboard into wwwroot
cd dashboard
npm install
npm run build          # emits into ../PLCGateway/wwwroot (same-origin)

# run the unified app (serves dashboard + API + PLC pipeline)
cd ../PLCGateway
dotnet run             # listens on http://localhost:5200 (see Properties/launchSettings.json)
# one-off recovery: dotnet run -- --rebuild-aggregation
#   resets Section 1 incremental state AND the plc_daily_trends rollup, then replays both from
#   full history. Both are derived data — raw Tier 2 history is never touched.
```

Then open **http://localhost:5200** and log in with `admin` / `admin123` (change this password).

Optional — frontend hot-reload while developing the UI: run `dotnet run` (backend on `:5200`)
and, in another terminal, `cd dashboard && npm run dev`. The Vite dev server proxies `/api` to
the backend. This is only for editing the dashboard; the shipped app is the single process above.

### 3. Production (IIS)

Publish and host under IIS with the .NET 10 Hosting Bundle (ASP.NET Core Module, InProcess).
The application pool must be **AlwaysRunning / Idle Time-out 0 / Preload Enabled** so the PLC
scan loop is never idled, and **overlapped recycle disabled** so two pollers never run at once.

```bash
dotnet publish -c Release /p:PublishProfile=FolderProfile
# copy bin/Release/net10.0/publish/ to the IIS site root (includes wwwroot + web.config)
```

Full step-by-step (Windows features, certificate/443 binding, admin IP restriction, router
port-forwarding, config placeholders) is in **`DEPLOYMENT-NOTES.md`**.

---

## Dashboard API (JWT-protected)

| Endpoint | Returns |
|---|---|
| `GET /api/machinestatus` | Machine status byte + `isStale` / `plcConnected` / `lastScanAt` |
| `GET /api/lifetime` | Section 1 scalar parameters |
| `GET /api/shotsbreakdown` | Section 1 shots-per-refill table |
| `GET /api/amps` | Live current for the 10 impellers |
| `GET /api/sparestatus` · `/alerts` | Spare grid (140 rows) · triggered subset |
| `GET /api/trends` | **Graph series.** `bucket=day` (default) / `month` read the `plc_daily_trends` rollup; omit `start`/`end` for all-time. `bucket=hour` is computed live from Tier 2 and requires both bounds. |
| `GET /api/historical?name=&start=&end=` | Raw Tier 2 points for one tag (used by the per-impeller amps trace) |
| `GET /api/cycles/latest` | Most recent completed cycle |
| `POST /api/filter` → `GET /api/filter/{id}/status` | Submit a Section 2 request, then poll |
| `GET /api/filter/{id}/results` · `/cycles` · `/shots` · `/metals` | Section 2 output once `done` |

Graph arithmetic (on-seconds, utility %, efficiency) is performed **server-side** in
`TrendsService`, and the dashboard plots the values verbatim — the same rule the cloud mirror
follows. There is deliberately no second implementation in the browser.

---

## Scaling

Sizing is dominated by one write path. `GatewayWorker` logs all 10 impeller-current tags on
**every 1 s scan while blast is ON** (`storage_reason = 'BLAST_ON'`), which is 60–80% of all Tier 2
rows.

Rough daily rates (derived from config, not measured — plug in real figures once the plant is
running):

| Source | Rows/day | Assumption |
|---|---|---|
| `BLAST_ON` currents | ~288,000 | 10/sec × 8 h blast |
| Heartbeats (22 tags) | ~31,700 | 60 s, always |
| `Spares_Runhour` (140 tags) | ~11,200 | 0.1 h deadband, 8 h run |
| `Tonnage` | ~20,000 | 1 kg deadband, 20 t/day |
| **Total** | **≈350,000/day → ~128 M/year** | ⇒ roughly **30 GB/year** incl. indexes |

**What stays flat as history grows:** graph opens (rollup only), the per-minute rollup refresh
(bounded to 2 days), Section 1 aggregation (watermarked), Tier 1 upserts (fixed 443 rows), and
inserts themselves (append-only with monotonic timestamps = B-tree right-edge appends).

**What does not:** long-window Section 2 filters replay raw events proportional to the window, so a
one-year filter is inherently heavy. Bounded windows (hour/shift/day/week/month) are fine.

**Recommended next steps** (neither deletes anything):

1. **Monthly declarative partitioning of `plc_historical_data`** — every row is retained, but
   inserts touch only the current month's smaller indexes, windowed queries prune to relevant
   months, and old partitions can move to a cheaper tablespace. This is the structural answer to
   multi-year growth. Needs a maintenance window (table rewrite).
2. **Ask the client about the 1 Hz `BLAST_ON` logging** — 5 s or 10 s would cut total storage
   5–10× with negligible effect on cycle-average energy, at the cost of a coarser per-cycle amps
   trace. Biggest single lever available.

Already applied: the duplicate `idx_historical_name_time` index was dropped — it had the same key
columns as `idx_historical_name_num`, so it added a third of the index write cost per insert for no
read benefit.

---

## Troubleshooting

**PLC not connecting**
- Verify IP, rack, slot in `appsettings.json`
- Check PLC is in RUN mode and network is reachable
- Windows firewall must allow port 102 (S7 protocol)

**No cycles appearing in plc_cycles**
- `CycleTrackingService` requires `Blast ON/OFF` state-change records in `plc_historical_data`
- Watermark starts from `MAX(blast_end)` in `plc_cycles` — on first run it starts from year 2000

**Shots breakdown table is empty**
- `plc_shots_breakdown` requires at least 2 `Refil shots weight` COV events in `plc_historical_data`
- It is maintained by upsert (keyed on `refill_timestamp`) as the incremental engine runs — check `AggregationService` is running; run once with `--rebuild-aggregation` if the incremental state looks stale

**Graphs say "No … data recorded yet"**
- They read `plc_daily_trends`. Confirm the table has rows; it is populated by `AggregationService`
  (every minute) and backfilled at startup when empty.
- After importing history behind the app's back, run `--rebuild-aggregation` to rebuild the rollup.

**Spare status not updating**
- All 140 trigger/run-hour/replaced tags must be defined in `appsettings.json`
- `SpareMonitoringService` reads from `plc_current_values` — requires `GatewayWorker` running and PLC connected

**Calculation request stuck in pending**
- `FilteredCalculationService` polls every 5 seconds — check service is running
- If status is `error`, check logs for the specific request ID

**energy_kwh column not found**
- Run `migration.sql` again — it contains the idempotent rename of `energy_amp_sec` → `energy_kwh`

**Database errors**
- Verify PostgreSQL is running and credentials are correct
- Confirm `migration.sql` has been run (all 10 tables must exist)
