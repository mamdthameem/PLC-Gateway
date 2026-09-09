# PLCGateway

Unified application for a shot-blast machine (foundry). A single ASP.NET Core (.NET 10) app,
hosted under IIS, that reads a Siemens S7-1200 PLC every second (background hosted services),
stores raw tag history in PostgreSQL, computes business parameters, **serves the dashboard's
JSON API**, and **serves the React dashboard build** from `wwwroot/` — all in one process. The
cloud reaches the app only over HTTPS via secured `/api/admin/*` endpoints, never the database.

**Stack:** .NET 10, C#, ASP.NET Core (WebApplication + hosted services), S7.NetPlus,
Npgsql/PostgreSQL, JWT auth; Vite + React dashboard built into `wwwroot`.

---

## What changed in the latest revision — read this first

Six changes landed together. Each has a full section below; this is the index.

| # | Change | Where it is documented | Breaking? |
|---|---|---|---|
| 1 | **Section 1-only parameters above the filter; shared parameters below it.** The lower tiles show Section 1 real-time values until a filter is applied, then Section 2 values. | [Dashboard layout](#dashboard-layout--what-sits-above-the-filter-and-what-sits-below) | No |
| 2 | **Effective Shots Usage is now kg/T and inverted** — `effective_shots_usage` → `effective_shots_usage_kg_per_ton`. **Lower is better.** | [Effective Shots Usage](#effective-shots-usage--read-this-before-interpreting-the-number) | **Yes** — cloud API key renamed |
| 3 | **"Metal" is displayed as "Item"** — in the dashboard only. PLC tags, DB columns, DTOs and API fields still say metal. | [Casting item vs casting metal](#casting-item-vs-casting-metal--the-naming-rule) | No |
| 4 | **Per-parameter calculation toggles.** Only ticked parameters are computed at all. | [Per-parameter calculation toggles](#per-parameter-calculation-toggles) | No (`NULL` = all) |
| 5 | **Item filter actually scopes the data.** `machine_utility_pct` is disabled under an item filter. | [Item filter scoping](#item-filter-scoping) | No — fixes wrong numbers |
| 6 | **Section 2: tiles + graphs + two data tables.** The tile-duplicating "Parameters" table is gone; the item and cycle tables stay. The shots breakdown moved to Section 1. | [Section 2 reference](#section-2--filtered-plc_filtered_parameters--plc_filtered_cycle_data--plc_filtered_metal_production--plc_filtered_amps_data) | **Yes** — two API removals |

**Two things break for the cloud consumer** (`CONTRACT-admin-api.md` has the details):

1. The lifetime parameter key `effective_shots_usage` no longer exists. It is
   `effective_shots_usage_kg_per_ton`, with the **inverse formula and a different unit** — a
   consumer reading the old key silently gets `undefined`, not an error.
2. `section2.shotsBreakdown` was removed from `/api/admin/live` and `/api/admin/filter`. The
   machine-wide `shotsBreakdown` at the top level of `/api/admin/live` is unaffected.

**Deployment order matters:** run `migration.sql` (adds `selected_parameters`, deletes the stale
`effective_shots_usage` row), then publish. The dashboard bundle in `wwwroot/` must be rebuilt with
`npm run build` — an old bundle calls the removed `/api/filter/{id}/shots`.

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
                    │         ◄── calculation_requests        (Section 2 — dashboard trigger,
                    │         │                                carries selected_parameters)
                    │         ├──► plc_filtered_parameters    (Section 2 — scalar results)
                    │         ├──► plc_filtered_cycle_data    (Section 2 — per-cycle breakdown)
                    │         ├──► plc_filtered_metal_production (Section 2 — per-item production)
                    │         └──► plc_filtered_amps_data     (Section 2 — per-cycle impeller current)
                    │
                    └── SpareMonitoringService (every 10 sec)
                              └──► plc_spare_status           (140 spare health rows)
```

### Dashboard layout — what sits above the filter, and what sits below

Which parameter lives in which block is the whole design. There are three blocks:

```
┌─────────────────────────────────────────────────────────────────────┐
│ SECTION 1 ONLY — no filtered equivalent exists, ever                │
│   • Machine status tile              (5 s poll, + PLC link state)   │
│   • Avg Shot Refill Time                                            │
│   • Last Shot Refill                                                │
│   • Effective Shots Usage (kg/T)                                    │
│   • Blast Cycles per Refill Interval chart                          │
│   • Spare health grid                (10 s poll)                    │
├─────────────────────────────────────────────────────────────────────┤
│ FILTER BAR — mode tabs + vertical parameter toggles + Apply         │
├─────────────────────────────────────────────────────────────────────┤
│ SHARED PARAMETERS — the same tiles in both states                   │
│                                                                     │
│   No filter  →  "Section 1 — Real-time"   (all-time, 60 s poll)     │
│   Filtered   →  "Section 2 — Filtered"    (the chosen scope)        │
│                                                                     │
│   • Machine Utility        • Blast Time                             │
│   • Production             • Blast Cycles                           │
│   • Total Energy           • Impeller Current                       │
│   • Energy per Casting                                              │
└─────────────────────────────────────────────────────────────────────┘
```

**The upper block is what a filter cannot touch.** Each of those parameters is unfilterable by
nature, not by omission: `machine_status` is a live state rather than a window aggregate, and the
two refill figures plus `effective_shots_usage_kg_per_ton` are cumulative-since-commissioning by
definition. Spare run-hours are per-spare lifetime counters from the PLC. None of them is repeated
below the bar.

**The lower block is the same set of tiles in two scopes.** Unfiltered they show their Section 1
all-time values, live; applying a filter replaces them with the Section 2 values for the chosen
scope. Same parameters, same position, different scope — which is exactly what a filter is
expected to do. Clearing the filter returns them to the Section 1 view.

The two key lists are `SECTION1_ONLY_PARAM_KEYS` and `SHARED_PARAM_KEYS` in
`dashboard/src/types/index.ts`; `LifetimeSection` is rendered twice, once with each.

**Nothing is calculated until Apply is pressed.** Section 2 is an on-demand computation that
inserts a `calculation_requests` row and runs the whole Section 2 engine, so no filter request is
fired on page load — the lower block simply reads the Section 1 endpoints until you ask for a
filter.

**The two sections never interact.** This is a hard rule, not an emergent property:

- Section 1 does **not** respond to the filter — not its values, not its visibility, not its
  graphs. It keeps its own polling and its own cumulative all-time calculation while a filter is
  active.
- Section 1 and Section 2 share **no fetch, no cache key and no state slice**. Section 1's blocks
  each poll independently; Section 2 reads only `plc_filtered_*` tables keyed by its `request_id`.
- There is no "hide Section 1 while filtered" branch anywhere. Section 1 previously *unmounted*
  when a filter ran; that behaviour was deleted.
- Applying a filter changes the lower block only. The upper block never moves.

> **Two shared names, two different formulas.** `production_qty_kg` and
> `energy_per_casting_kwh_kg` are computed differently in each section — Section 1 uses the PLC's
> `Tonnage` accumulator, Section 2 uses declared casting-item weight. Switching the lower block
> from unfiltered to filtered therefore changes *what the number means*, not just its scope. Both
> tiles carry a subtitle saying which is which. The other four shared parameters are the same
> formula over a different scope and are directly comparable.

### Services

| Service | Interval | Role |
|---|---|---|
| `GatewayWorker` | 1 second | Reads all PLC tags → Tier 1 always, Tier 2 on COV |
| `CovDetectionService` | per tag | ≥2% change for numeric, state change for BOOL, 60s heartbeat |
| `AggregationService` | 1 minute | Computes all lifetime parameters → `plc_lifetime_parameters` + `plc_shots_breakdown`, then refreshes `plc_daily_trends` for yesterday+today only (bounded work per pass) |
| `CycleTrackingService` | 2 seconds | Detects blast cycle end (falling edge on Blast ON/OFF), writes `plc_cycles` |
| `FilteredCalculationService` | 5 seconds | Polls `calculation_requests`, computes **only the parameters the request selected** (aggregate + per-cycle + per-item production + impeller current) |
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
| `filter_metal_name` | TEXT | Casting item name to filter by (when `filter_by = 'metal'`). Displayed as "Item" |
| `selected_parameters` | TEXT[] | Section 2 parameter keys to compute. **`NULL` = all of them** — which is what every row written before this column existed means |
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
| `calculated_at` | TIMESTAMP | |

### `plc_filtered_shots_breakdown` — **retired, no longer written**

The shots breakdown became Section 1 only: a refill interval spans whatever cycles fall inside it,
mixing casting items, so no filter scopes it meaningfully. Nothing writes to this table any more,
and `GET /api/filter/{id}/shots` was removed.

The table and its existing rows are **left in place** — nothing in this project is ever dropped.
Section 1's `plc_shots_breakdown` is unaffected and still powers the "Blast Cycles per Refill
Interval" chart above the filter bar.

### `plc_filtered_metal_production` — Section 2 production per casting item
One row per declared item name per request. Written only when `production_qty_kg` is selected.

| Column | Type | Description |
|---|---|---|
| `id` | SERIAL PK | |
| `request_id` | INTEGER FK | References `calculation_requests.id` |
| `metal_name` | TEXT | Declared casting item name; `unspecified` when a weight was declared with a blank name. (Column says metal, UI says item — see the naming rule) |
| `production_kg` | NUMERIC | `Σ` declared weight for that item over the in-scope cycles |
| `calculated_at` | TIMESTAMP | |

Under an item filter this holds **only the filtered item** — see "Item filter scoping".

### `plc_filtered_amps_data` — Section 2 impeller current
One row per cycle × impeller per request. Written only when `impeller_current` is selected.

| Column | Type | Description |
|---|---|---|
| `request_id` | INTEGER FK | References `calculation_requests.id` |
| `cycle_number` | INTEGER | Global cycle number |
| `impeller_number` | SMALLINT | 1–10 |
| `avg_amps` | NUMERIC | `AVG(value_num)` in that cycle's blast window; `NULL` when no sample (a chart gap, not a zero) |

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
| `effective_shots_usage_kg_per_ton` | `total_refill_weight_kg ÷ (production_qty_kg ÷ 1000)`, both cumulative since commissioning. **Lower is better.** `null` (displayed as `—`) when nothing has been cast yet | `Tonnage` (latest), `Refil shots weight` running sum from Tier 2 |

> **Energy formula is final.** The client confirmed the PLC supplies energy as a correct value, so
> no voltage/power-factor conversion is applied. The previously unused `EnergyCalculation` config
> block has been removed.

#### Effective Shots Usage — read this before interpreting the number

**What it means:** kilograms of shot consumed per **tonne** of casting produced. It is a
*consumption rate*, so **lower is better** — the opposite polarity to most tiles on the dashboard.

**Unit:** `kg/T`. Tile subtitle: *"shot consumed per tonne of casting"*. Rounded to 4 decimals.

**It changed direction in this revision.** The old `effective_shots_usage` was
`production_qty_kg ÷ total_refill_weight_kg` — kg cast per kg of shot, where *higher* was better.
The new figure is its inverse, rescaled to tonnes. **They are not comparable**, and a number
recorded against the old name means something different from the same number today. The old
`plc_lifetime_parameters` row is deleted by `migration.sql` precisely so the two cannot be confused
on screen.

| | Old | New |
|---|---|---|
| `parameter_name` | `effective_shots_usage` | `effective_shots_usage_kg_per_ton` |
| Formula | `production ÷ refill_weight` | `refill_weight ÷ (production ÷ 1000)` |
| Unit | kg/kg | kg/T |
| Direction | higher is better | **lower is better** |
| Null when | refill weight is 0 | **production is 0** |

**Null guard moved to the denominator.** Production is now the divisor, so the guard is on
production: no casting recorded yet ⇒ `null` ⇒ the tile renders `—`. The old guard (on refill
weight) was removed, not kept alongside.

**No colour or trend indicator is attached to this tile.** None of the metric tiles carries one —
there was no polarity to flip. If a good/bad indicator is added later, remember this tile inverts
the usual direction.

**Inputs are unchanged.** `production_qty_kg` is still the latest raw `Tonnage` reading from Tier 1
and `total_refill_weight_kg` is still `plc_aggregation_state.total_refill_weight_kg`, folded by
`FoldRefillAsync` with its existing `> 0` glitch filter. Only the arithmetic changed.

> ⚠️ **Known asymmetry between the two inputs.** `production_qty_kg` is the **PLC's own lifetime
> `Tonnage` accumulator** — it counts everything the machine ever cast, including before this
> gateway existed. `total_refill_weight_kg` accumulates **only from gateway commissioning onward**,
> because it is folded from Tier 2 rows this gateway recorded.
>
> On a gateway installed onto an already-running machine, the numerator is therefore short relative
> to the denominator, and the figure **under-reports shot consumption**. The error shrinks as
> gateway history accumulates. On a machine and gateway commissioned together, there is no error.
>
> Fixing it would mean storing a commissioning `Tonnage` baseline in `plc_aggregation_state` and
> using `(latest_tonnage − baseline)`. **Deliberately not done** — `production_qty_kg` is a
> headline number that would visibly drop the day such a baseline was applied, and applying it to
> only the usage denominator would leave the tiles mutually inconsistent. Decide with the client
> before changing it.

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

### Section 2 — Filtered (`plc_filtered_parameters` + `plc_filtered_cycle_data` + `plc_filtered_metal_production` + `plc_filtered_amps_data`)

**Section 2 is exactly seven outputs — no more.** This is the complete list; anything not on it is
Section 1 and does not respond to any filter.

| # | Key | What it is | Formula in Section 2 | Graph opened by tapping the tile |
|---|---|---|---|---|
| 1 | `machine_utility_pct` | Machine Utility (%) | `blast_time_sec ÷ machine_on_time_sec × 100` over the window | Utility trend — **time filters only** (cycle/item filters have no time axis) |
| 2 | `production_qty_kg` | Production (kg) | `Σ` declared `Casting metal N weight` over the in-scope cycles. **Not from `Tonnage`** | Bar chart: declared weight per casting item |
| 3 | `energy_kwh_total` | Total Energy (kWh) | `Σ plc_cycles.energy_kwh` over the in-scope cycles | Bars: energy per cycle |
| 4 | `energy_per_casting_kwh_kg` | Energy per Casting | `energy_kwh_total ÷ total declared weight` (same denominator as #2) | Line: efficiency per cycle |
| 5 | `blast_time_sec` | Blast Time | Seconds `Blast ON/OFF` was true in the window | **None — scalar only** (see below) |
| 6 | `cycle_count` | Blast Cycles | Rising edges `0→1` on `Blast ON/OFF` in the window | **None — scalar only** (see below) |
| 7 | `impeller_current` | Impeller Current (×10) | `AVG(value_num)` per cycle×impeller; the tile shows a duration-weighted average | One point per cycle, per impeller |

> **Why Blast Time and Blast Cycles have no graph in either section.** Section 1 cannot plot them:
> `plc_daily_trends` has no column for either, so there is nothing to build a per-bucket series
> from. Section 2 *could* have — it holds a per-cycle row for every cycle in the filter — and it
> did until this revision. The charts were removed so the two sections behave the same way for the
> same tile: a graph icon that appears in one section and not the other reads as a bug, not a data
> limitation. Nothing is lost — the per-cycle blast window and the full cycle list are both in the
> **Cycle Breakdown** table.

`impeller_current` is a **pseudo-parameter**: it is not a `plc_filtered_parameters` row but the
`plc_filtered_amps_data` panel. It appears in the list because the toggles switch it on and off
like any other output.

**Never in Section 2 — Section 1 only, above the filter bar:**
`machine_status` · `avg_shot_refill_time_sec` · `last_refill_epoch_sec` ·
`effective_shots_usage_kg_per_ton` · the **shots breakdown** ("Blast Cycles per Refill Interval") ·
spare health.

> **Why the shots breakdown left Section 2.** A shot refill is a machine-level event. A refill
> interval spans whatever cycles happen to fall inside it, mixing casting items, so no filter
> scopes it meaningfully. It is now computed and shown once, in Section 1. `GET
> /api/filter/{id}/shots` and `section2.shotsBreakdown` in the admin API were removed with it, and
> nothing writes to `plc_filtered_shots_breakdown` any more (the table and its existing rows are
> left in place — nothing is ever dropped).

**Section 1 vs Section 2 production is a deliberate split.** Both sections now show a "Production"
tile at the same time, and they use different formulas on purpose:

| | Section 1 | Section 2 |
|---|---|---|
| Source | the PLC's `Tonnage` accumulator | declared `Casting metal N weight` values |
| Question answered | what the machine **measured** | what the plant **declared** it cast |
| Tile subtitle | *"from the PLC's Tonnage accumulator"* | *"declared casting-item weight"* |

They can legitimately disagree over the same window and **neither is wrong**. Each tile carries a
subtitle saying which it is, because both are on screen together. A slot counts only when its
weight > 0; a weight with a blank name lands in `unspecified`; a cycle that declared nothing
contributes nothing.

#### Per-parameter calculation toggles

The filter bar carries one checkbox per Section 2 parameter, plus **Select All** / **Clear All**.
All seven are ticked by default. The selection persists while the page is open and resets to "all"
on reload. **Apply is disabled when nothing is ticked** — an empty query is never sent.

Unticked parameters are **not computed at all**: the backend skips their queries and aggregation
rather than computing everything and trimming the response. Unselected parameters are then simply
absent from the Section 2 layout — not greyed out, not hidden.

**Wire format.** The selection travels as `selectedParameters: string[]` on the filter request and
is stored in `calculation_requests.selected_parameters TEXT[]`, because Section 2 is triggered
through the database. **`NULL` or omitted means "all parameters"** — which is what every row
written before this column existed means, and what an admin API caller that omits the field means.
Unknown keys are rejected with a `400`, so a stale dashboard build fails loudly instead of quietly
computing less.

The single list of valid keys is `CalculationService.Section2ParameterKeys`; the dashboard mirrors
it in `SECTION2_PARAM_KEYS` (`dashboard/src/types/index.ts`).

**What each toggle actually saves.** Parameters share queries, so the saving is not one query per
toggle:

| Selection | Work skipped |
|---|---|
| `impeller_current` off | The `AVG` over dense 1 Hz current rows — **by far the most expensive query** |
| `machine_utility_pct` off | The `Machine status` state-change read |
| `blast_time_sec`, `cycle_count` **and** `machine_utility_pct` all off | The `Blast ON/OFF` state-change read (shared by all three — utility uses it as its numerator) |
| `production_qty_kg` **and** `energy_per_casting_kwh_kg` both off | The declared-weight sum and the `plc_filtered_metal_production` writes |
| Everything cycle-derived off (only utility and/or production ticked) | The `plc_filtered_cycle_data` batch write |

The cycle set itself is **always** read — it is the scope definition every other parameter depends
on.

**Consequence for the Excel export:** a sheet is empty when its parameter was not selected, because
the data was never computed. This is expected, not a bug.

#### Item filter scoping

Filtering by casting item scopes **every selected parameter** to only the cycles that declared that
item. The predicate is applied in SQL when the cycle set is selected
(`DatabaseService.GetCyclesByMetalNameAsync`) — never by filtering rendered output. The active item
name is shown on the Section 2 header as a chip.

Two parameters take a different code path under an item filter, because the matching cycles need
not be contiguous in time:

| Parameter | Time / cycle filter | Item filter |
|---|---|---|
| `blast_time_sec` | Event replay over the window | `Σ (blastEnd − blastStart)` over the matching cycles |
| `cycle_count` | Rising edges in the window | `cycles.Count` |

The event replay is kept for time and cycle filters because those *do* describe a contiguous span,
and the replay correctly counts blast seconds at window edges that no completed cycle row covers.
Under an item filter it would sweep in every other item's cycles that happen to fall between the
first and last match — which was the original bug.

**`machine_utility_pct` cannot be scoped to an item, and is disabled under an item filter.** Its
denominator is machine on-time: the machine powered up, blasting anything or idle between jobs.
None of that is attributable to one casting item, so any per-item value would be an invention. The
checkbox is disabled with a tooltip, and the backend drops the key even if a caller sends it.

**The multi-item cycle rule.** A single cycle can declare up to four different items. A cycle
selected because it contains "Aluminium" may also carry "Iron". Under an item filter the declared
weight sum counts **only the filtered item's slots** — the other item's weight lands in neither
this item's production nor its kWh/kg denominator. The consequences are worth knowing:

- Production and the `energy_per_casting_kwh_kg` denominator are honest for the filtered item.
- The **numerator** (whole-cycle energy) is still the energy of the whole cycle, which was shared
  with the other item in it. So kWh/kg reads **high** for items that share cycles.
- The per-item production graph is a **single bar** under an item filter. That is correct, not a
  rendering fault.

The alternative — pro-rating cycle energy by weight share — was rejected: it would invent an
attribution the PLC never measured.

**Per-cycle breakdown (`plc_filtered_cycle_data`):**

| Column | Description |
|---|---|
| `production_kg` | `tonnage_kg(this cycle) − tonnage_kg(previous cycle)` |
| `energy_kwh` | `avg_amps_all_impellers × cycle_duration_hours` |

**Impeller current (`plc_filtered_amps_data`):** mirrors the Section 1 Amps tile/graph — a tile
per impeller, click to open a chart — but scoped to the filter's cycles instead of live/last-cycle
readings. Per cycle×impeller, `avg_amps = AVG(value_num)` over `plc_historical_data` within that
cycle's `blast_start`..`blast_end` window (raw 1 Hz current samples never leave Postgres); a cycle
with no in-window sample for an impeller is `NULL`, rendered as a chart gap rather than a false
zero. The tile's headline value is a **duration-weighted** average across the filter's cycles
(cycles with a `NULL` average are excluded from the weighting, not counted as zero) — a flat mean
of per-cycle averages would let a short cycle count as much as a long one, the same reason
`machine_utility_pct` is rebuilt from summed seconds rather than averaged per-bucket percentages.

### Section 2 filter modes

| `filter_by` | Uses | Example |
|---|---|---|
| `'time'` (default) | `filter_start` / `filter_end` | All cycles between two timestamps |
| `'cycle'` | `filter_cycle_from` / `filter_cycle_to` | Cycles 10 through 25 |
| `'metal'` | `filter_metal_name` | All cycles that declared "Aluminium" in any slot. **Shown in the dashboard as the "Item" tab** — see below |

`filter_start` and `filter_end` are `NOT NULL`, so cycle and item filters send `NOW()` in both as a
placeholder. That is why those two modes have no time-axis graph.

### Casting item vs casting metal — the naming rule

The dashboard says **"Item"**. Everything underneath says **"metal"**. This is deliberate, not an
oversight, and the boundary is exact:

| Layer | Word used | Why |
|---|---|---|
| Dashboard UI — filter tab, field labels, tile and chart titles, header chip, Excel headers | **Item** | What the client asked to see |
| PLC tag names (`Casting metal 1 name`, …) | **metal** | Byte-matched to the vendor address sheet. They are the primary key in `plc_current_values` and in `plc_historical_data`, which is **never-delete history** — renaming them would orphan every historical row |
| `appsettings.json` — `Tags[].Name`, `HeartbeatTags`, `CovRules.AbsoluteByName` keys | **metal** | The same strings; they must match the tag names exactly |
| DB columns — `metal_1_name`…`metal_4_weight_kg`, `filter_metal_name`, `plc_filtered_metal_production.metal_name` | **metal** | No migration was run; renaming these has no user-visible benefit |
| C# models, DTOs, TypeScript types, API routes and JSON fields (`filterBy: 'metal'`, `filterMetalName`, `metalName`, `metal1Name`, `GET /api/filter/{id}/metals`) | **metal** | Matches the DB, and keeps the admin API contract stable for the cloud consumer |

**Rule of thumb:** if a user reads it, it says *item*. If a machine reads it, it says *metal*.

When adding code here, do not "fix" the internal naming to match the UI — the mismatch is the
design. If the internals are ever renamed, the PLC tag strings and the `plc_historical_data` rows
keyed on them must stay exactly as they are.

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
| `POST /api/filter` → `GET /api/filter/{id}/status` | Submit a Section 2 request, then poll. Body may carry `selectedParameters: string[]` — omit for "all" |
| `GET /api/filter/{id}/results` · `/cycles` · `/metals` · `/amps` | Section 2 output once `done`. Only the selected parameters are present |

`GET /api/filter/{id}/shots` was **removed** — the shots breakdown is Section 1 only now; read it
from `GET /api/shotsbreakdown`. `POST /api/filter` returns `400` for an unknown key in
`selectedParameters` rather than ignoring it.

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
