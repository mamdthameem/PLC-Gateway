# Dashboard Integration Prompt

Read this document completely before writing any code. Every table name, column name, parameter name, and SQL query in here is exact — do not guess or rename anything.

---

## What this system is

A shot-blast machine (foundry) monitored by a Siemens S7-1200 PLC. A backend service called PLCGateway reads the PLC every second and stores data in PostgreSQL. The dashboard is a **separate application** that reads from that database. The dashboard never talks to the PLC and never calls a REST API — all communication is through the database.

```
PLC → PLCGateway (backend) → PostgreSQL ← Dashboard (you)
```

---

## Database connection

```
Host:     localhost
Port:     5432
Database: sreesakthi_gateway
Username: postgres
Password: Pass
```

---

## Time filter — controls Section 1 vs Section 2

Place the date/time range selector at the **very top of the dashboard**, above all parameter tiles.

| State | Behaviour |
|---|---|
| **No filter applied** | Section 1 — live / all-time view. Read scalars from `plc_lifetime_parameters`. |
| **Filter applied** | Section 2 — windowed view. Submit a `calculation_requests` row and read results from the `plc_filtered_*` tables. |

- Parameters marked **Section 1 only** must be **hidden completely** when a filter is applied.
- Parameters that appear in **both sections** must recalculate for the selected window when a filter is applied.

---

## How to trigger Section 2 (filter applied)

### Step 1 — Submit a request

**Filter by time range (default):**
```sql
INSERT INTO calculation_requests (filter_start, filter_end, period_label, filter_by)
VALUES ('2026-04-01 06:00:00', '2026-04-01 14:00:00', 'shift', 'time')
RETURNING id;
```

**Filter by cycle number range:**
```sql
INSERT INTO calculation_requests
    (filter_start, filter_end, filter_by, filter_cycle_from, filter_cycle_to)
VALUES (NOW(), NOW(), 'cycle', 10, 25)
RETURNING id;
```

**Filter by casting metal:**
```sql
INSERT INTO calculation_requests
    (filter_start, filter_end, filter_by, filter_metal_name)
VALUES (NOW(), NOW(), 'metal', 'Aluminium')
RETURNING id;
```

`filter_start` and `filter_end` are NOT NULL — always required. Pass `NOW()` as placeholder for cycle/metal filters.

`period_label` accepted values: `hour`, `shift`, `day`, `week`, `month`, `year`, or `NULL` for custom range.

### Step 2 — Poll until done

```sql
SELECT status, processed_at
FROM calculation_requests
WHERE id = <your_request_id>;
```

Status lifecycle: `pending` → `processing` → `done` (or `error`). Poll every 2 seconds. Stop when `done` or `error`.

### Step 3 — Read results

Scalar results:
```sql
SELECT parameter_name, value
FROM plc_filtered_parameters
WHERE request_id = <your_request_id>;
```

Per-cycle breakdown:
```sql
SELECT cycle_number, blast_start, blast_end,
       metal_1_name, metal_1_weight_kg, metal_2_name, metal_2_weight_kg,
       metal_3_name, metal_3_weight_kg, metal_4_name, metal_4_weight_kg,
       production_kg, energy_kwh, shots_usage
FROM plc_filtered_cycle_data
WHERE request_id = <your_request_id>
ORDER BY cycle_number ASC;
```

Shots breakdown:
```sql
SELECT refill_timestamp, blast_count
FROM plc_filtered_shots_breakdown
WHERE request_id = <your_request_id>
ORDER BY refill_timestamp ASC;
```

---

## Table overview

| Table | Dashboard role |
|---|---|
| `plc_current_values` | Read. Live tag values — amps per impeller, machine status. |
| `plc_historical_data` | Read. Full COV time-series — used for graphs drawn from raw records. |
| `plc_lifetime_parameters` | Read. Section 1 scalar parameters, updated every minute. |
| `plc_shots_breakdown` | Read. Section 1 shots-per-refill breakdown, updated every minute. |
| `plc_cycles` | Read. One row per completed blast cycle. |
| `calculation_requests` | **Write** to trigger Section 2. Read back for status. |
| `plc_filtered_parameters` | Read. Section 2 scalar results per request. |
| `plc_filtered_cycle_data` | Read. Section 2 per-cycle breakdown per request. |
| `plc_filtered_shots_breakdown` | Read. Section 2 shots breakdown per request. |
| `plc_spare_status` | Read. 140-row spare health table, updated every 10 seconds. |

The dashboard must **never** write to any table other than `calculation_requests`.

---

## Parameters

---

### Parameter 1 — Machine Status

**Section 1 only. Hide when filter is applied.**

**Data source:**
```sql
SELECT value FROM plc_current_values WHERE address = 'DB60.DBB0';
```

| Raw value | Display |
|---|---|
| `'0'` | Stopped |
| any other value | Running |

**Display:** Tile only. No graph. Shows one of: `Running` / `Stopped`.
**Refresh:** Every 5 seconds (live value).

---

### Parameter 2 — Machine Utility %

**Both sections.**

**Section 1 tile:**
```sql
SELECT value FROM plc_lifetime_parameters WHERE parameter_name = 'machine_utility_pct';
```

**Section 2 tile:**
```sql
SELECT value FROM plc_filtered_parameters
WHERE request_id = <id> AND parameter_name = 'machine_utility_pct';
```

**Section 1 graph — hourly utility trend:**
The dashboard must compute this from raw records. Fetch Blast ON/OFF and Machine status state-change records for the period you want to graph:

```sql
-- Blast ON/OFF records:
SELECT value, previous_value, timestamp FROM plc_historical_data
WHERE parameter_name = 'Blast ON/OFF'
  AND timestamp >= <period_start> AND timestamp <= <period_end>
ORDER BY timestamp ASC;

-- Machine status records:
SELECT value, previous_value, timestamp FROM plc_historical_data
WHERE parameter_name = 'Machine status'
  AND timestamp >= <period_start> AND timestamp <= <period_end>
ORDER BY timestamp ASC;
```

Walk both record sets to compute on-time seconds per hour bucket (same logic as backend):
- Machine is ON when `value != '0'`
- Blast is ON when `value = '1'` or `value = 'true'` (case-insensitive)
- Utility % for each hour bucket = blast_seconds_in_bucket / machine_on_seconds_in_bucket × 100

**Section 2 graph:** Same calculation, scoped to `windowStart` and `windowEnd` of the request.

**Display:** Tile + Graph (tap tile to open). Graph is a line chart: X axis = time (hourly buckets), Y axis = utility %.

---

### Parameter 3 — Production Quantity (kg)

**Both sections, but logic differs.**

**Section 1:**

Tile — latest raw accumulator value:
```sql
SELECT value FROM plc_lifetime_parameters WHERE parameter_name = 'production_qty_kg';
```

Graph — cumulative tonnage over time (line chart from COV records):
```sql
SELECT value::numeric, timestamp FROM plc_historical_data
WHERE parameter_name = 'Tonnage'
ORDER BY timestamp ASC;
```
X axis = timestamp, Y axis = tonnage value. Shows the running accumulator growing over time.

**Section 2:**

Tile — delta (last minus first tonnage in the window):
```sql
SELECT value FROM plc_filtered_parameters
WHERE request_id = <id> AND parameter_name = 'production_qty_kg';
```

Graph — tonnage COV records within the window:
```sql
SELECT value::numeric, timestamp FROM plc_historical_data
WHERE parameter_name = 'Tonnage'
  AND timestamp >= <filter_start> AND timestamp <= <filter_end>
ORDER BY timestamp ASC;
```

Table — always visible in Section 2. One row per blast cycle in the window:
```sql
SELECT cycle_number, blast_start, blast_end, duration_sec,
       metal_1_name, metal_1_weight_kg,
       metal_2_name, metal_2_weight_kg,
       metal_3_name, metal_3_weight_kg,
       metal_4_name, metal_4_weight_kg,
       production_kg
FROM plc_filtered_cycle_data
WHERE request_id = <id>
ORDER BY cycle_number ASC;
```

**Display:** Tile + Graph (tap to open) + Table (always visible in Section 2).

---

### Parameter 4 — Energy Consumption (kWh)

**Both sections.**

**Section 1 tile:**
```sql
SELECT value FROM plc_lifetime_parameters WHERE parameter_name = 'energy_kwh_total';
```

**Section 2 tile:**
```sql
SELECT value FROM plc_filtered_parameters
WHERE request_id = <id> AND parameter_name = 'energy_kwh_total';
```

**Bar chart (both sections) — energy per cycle:**

For Section 2, read directly from the per-cycle data:
```sql
SELECT cycle_number, energy_kwh FROM plc_filtered_cycle_data
WHERE request_id = <id>
ORDER BY cycle_number ASC;
```

For Section 1 (all-time bar chart), trigger a Section 2 request automatically with the full time range:
```sql
INSERT INTO calculation_requests (filter_start, filter_end, filter_by)
VALUES ('2000-01-01', NOW(), 'time')
RETURNING id;
```
Then read `plc_filtered_cycle_data` for that request_id.

**How energy_kwh is calculated per cycle (for reference — backend does this):**
For each blast cycle, collect all `Current_imp_1` through `Current_imp_10` readings stored in `plc_historical_data` between `blast_start` and `blast_end`. Compute arithmetic mean amps per impeller. Multiply mean amps by cycle duration in hours. Sum across all 10 impellers. Result is kWh for that cycle.

The backend stores `energy_kwh` per cycle in `plc_filtered_cycle_data`.

**Display:** Tile + Graph (tap to open). Graph is a bar chart: X axis = cycle number, Y axis = kWh consumed in that cycle.

---

### Parameter 5 — Energy per Casting (kWh/kg)

**Both sections.**

**Section 1 tile:**
```sql
SELECT value FROM plc_lifetime_parameters WHERE parameter_name = 'energy_per_casting_kwh_kg';
```

**Section 2 tile:**
```sql
SELECT value FROM plc_filtered_parameters
WHERE request_id = <id> AND parameter_name = 'energy_per_casting_kwh_kg';
```

**Graph — per-cycle efficiency (line chart):**

From Section 2 `plc_filtered_cycle_data`:
```sql
SELECT cycle_number,
       CASE WHEN production_kg > 0 THEN energy_kwh / production_kg ELSE 0 END AS kwh_per_kg
FROM plc_filtered_cycle_data
WHERE request_id = <id>
ORDER BY cycle_number ASC;
```

For Section 1 (all-time), use the same auto-triggered all-time Section 2 request described in Parameter 4.

**Display:** Tile + Graph (tap to open). Graph is a line chart: X axis = cycle number, Y axis = kWh/kg. Shows efficiency trend across cycles.

---

### Parameter 6 — Total Blast Time

**Both sections.**

**Section 1:**
```sql
SELECT value FROM plc_lifetime_parameters WHERE parameter_name = 'blast_time_sec';
```
Convert seconds → "Xh Ym" format. Example: 12081.7 → "3h 21m".

**Section 2:**
```sql
SELECT value FROM plc_filtered_parameters
WHERE request_id = <id> AND parameter_name = 'blast_time_sec';
```

**Display:** Tile only. No graph.

---

### Parameter 7 — Effective Shots Usage

**Section 1 only. Hide when filter is applied.**

**What it shows:** For each refill interval (between two consecutive actual `Refil shots weight` value changes), the number of blast cycles that occurred in that interval.

**Tile — latest value (blast cycles since last refill):**
```sql
SELECT blast_count FROM plc_shots_breakdown
ORDER BY refill_timestamp DESC
LIMIT 1;
```

**Full breakdown data (for future graph/table — backend stores this):**
```sql
SELECT refill_timestamp, blast_count FROM plc_shots_breakdown
ORDER BY refill_timestamp ASC;
```

**Display:** Tile only for now. Tile shows the single `blast_count` value from the most recent row.

**Important:** Only actual value changes on `Refil shots weight` count as refill events. Periodic heartbeat records (where value equals previous value) are excluded by the backend.

**Refresh:** 60 seconds (table is rewritten every minute).

---

### Parameter 8 — Average Shot Refill Interval

**Section 1 only. Hide when filter is applied.**

**Formula:** Total machine-on time (seconds) ÷ number of actual refill events.

```sql
SELECT value FROM plc_lifetime_parameters WHERE parameter_name = 'avg_shot_refill_time_sec';
```

Convert seconds to minutes for display: value / 60 → "X.X min".

**Display:** Tile only. No graph.

**Refresh:** 60 seconds.

---

### Parameter 9 — Cycle Count

**Both sections.**

**Section 1:**
```sql
SELECT value FROM plc_lifetime_parameters WHERE parameter_name = 'cycle_count';
```

**Section 2:**
```sql
SELECT value FROM plc_filtered_parameters
WHERE request_id = <id> AND parameter_name = 'cycle_count';
```

**Display:** Tile only. No graph.

---

### Parameter 10 — Last Refill Time

**Section 1 only. Hide when filter is applied.**

```sql
SELECT value FROM plc_lifetime_parameters WHERE parameter_name = 'last_refill_epoch_sec';
```

`value` is a Unix epoch in seconds. Convert to local datetime string for display.

**Display:** Tile only. No graph.

**Refresh:** 60 seconds.

---

### Parameter 11 — Maintenance Alert (Spare Health)

**Section 1 only. Hide when filter is applied.**

#### Permanent table — always visible in Section 1

10 impellers × 14 spare types = 140 cells. Display as a grid:
- Rows = spare types (spare_index 0–13, using `spare_name`)
- Columns = impellers (1–10)
- Each cell = `current_run_hours` / `threshold_hours` (e.g. "847 / 2000 hrs")

```sql
SELECT impeller_num, spare_index, spare_name,
       threshold_hours, current_run_hours,
       trigger_active, last_replaced_at, last_updated_at
FROM plc_spare_status
ORDER BY impeller_num ASC, spare_index ASC;
```

Cell display rules:
- `threshold_hours = 0` → show run hours only (no threshold, not tracked)
- `trigger_active = TRUE` → highlight cell in red / warning colour
- `last_replaced_at IS NOT NULL` → show a replacement-confirmed indicator in the cell (run hours reset)

#### Alert popup — triggered when any spare threshold is exceeded

```sql
SELECT impeller_num, spare_index, spare_name,
       current_run_hours, threshold_hours, last_replaced_at
FROM plc_spare_status
WHERE trigger_active = TRUE AND threshold_hours > 0;
```

- Show a popup/banner for each row returned.
- **Minimum interval between successive popups for the same spare:** 30 minutes (configurable). Do not re-fire the popup for the same `(impeller_num, spare_index)` within that window. Track last-shown time in dashboard state.
- The popup should identify: impeller number, spare name, current run hours, threshold.

**Refresh:** Every 10 seconds.

---

### Parameter 12 — Spare Life Reminder (Replacement Confirmation)

**Section 1 only. Hide when filter is applied.**

Shares the **same table** as Parameter 11 — do not create a separate table.

When a spare is replaced, the PLC sets the `REPLACED_N[M]` BOOL, which the backend detects and writes `last_replaced_at` in `plc_spare_status`. The run-hour counter resets in the PLC.

```sql
SELECT impeller_num, spare_index, spare_name, last_replaced_at, current_run_hours
FROM plc_spare_status
WHERE last_replaced_at IS NOT NULL
  AND last_replaced_at >= NOW() - INTERVAL '24 hours';
```

- Show a confirmation popup: "Spare [spare_name] on Impeller [N] has been replaced. Run hours reset."
- Update the corresponding cell in the maintenance table to show "Replaced" status.
- Apply the same minimum popup interval logic (30 minutes per spare) to avoid duplicate popups.

**Refresh:** Every 10 seconds (same poll as Parameter 11).

---

### Parameter 13 — Amps per Impeller

**Section 1 only. Hide when filter is applied.**

#### Tile (live values — all 10 impellers together)

```sql
SELECT parameter_name, value::numeric, last_updated
FROM plc_current_values
WHERE parameter_name IN (
    'Current_imp_1','Current_imp_2','Current_imp_3','Current_imp_4','Current_imp_5',
    'Current_imp_6','Current_imp_7','Current_imp_8','Current_imp_9','Current_imp_10'
)
ORDER BY parameter_name ASC;
```

Display as a grouped tile panel showing all 10 values simultaneously.

**Refresh:** Every 1 second (these values are stored every second while blast is active).

#### Graph — historical amps per impeller (all 10 on one chart)

**Important storage behaviour:** `Current_imp_1` through `Current_imp_10` are stored in `plc_historical_data` **every second while `Blast ON/OFF = True`** (storage_reason = `'BLAST_ON'`). Outside a blast cycle, they are stored only on COV (≥2% change). This means dense data exists within blast cycles and sparse data outside.

```sql
-- For each impeller separately, fetch the time-series data:
SELECT value::numeric, timestamp FROM plc_historical_data
WHERE parameter_name = 'Current_imp_1'  -- change N for each impeller
  AND timestamp >= <start> AND timestamp <= <end>
ORDER BY timestamp ASC;
```

Display as a line chart:
- X axis = timestamp
- Y axis = Amps
- One line per impeller (10 lines total), colour-coded with a legend (Impeller 1–10)

Tap the tile to open the graph. For Section 1, default the graph to the last completed blast cycle:
```sql
SELECT blast_start, blast_end FROM plc_cycles
ORDER BY blast_end DESC LIMIT 1;
```
Then query `plc_historical_data` between `blast_start` and `blast_end` for all 10 impellers.

---

## Unit conversions for display

| Parameter | Raw | Display |
|---|---|---|
| `blast_time_sec` | seconds | `Xh Ym` (e.g. "3h 21m") |
| `avg_shot_refill_time_sec` | seconds | `X.X min` |
| `last_refill_epoch_sec` | Unix epoch (seconds) | Local datetime string |
| `machine_utility_pct` | 0–100 decimal | `XX.XX %` |
| `energy_kwh_total` | kWh | `X,XXX.XXX kWh` |
| `energy_per_casting_kwh_kg` | decimal | `X.XXXX kWh/kg` |
| `production_qty_kg` (Section 1) | kg | `X,XXX.XX kg` (always kg, no auto-conversion to tonnes) |
| `production_qty_kg` (Section 2) | kg | `X,XXX.XX kg` |
| `current_run_hours` | hours decimal | `X,XXX.X hrs` |
| `value` (amps) | numeric | `X.XX A` |

---

## Section 1 refresh intervals

| Data | Interval |
|---|---|
| `plc_lifetime_parameters` (all scalar parameters) | 60 seconds |
| `plc_shots_breakdown` (Parameters 7 & 8) | 60 seconds |
| `plc_current_values` — amps (Parameter 13 tile) | 1 second |
| `plc_spare_status` (Parameters 11 & 12) | 10 seconds |
| Machine status tile (Parameter 1) | 5 seconds |

---

## Section 1 vs Section 2 parameter visibility

| Parameter | Section 1 | Section 2 |
|---|---|---|
| 1 — Machine Status | ✓ Show | ✗ Hide |
| 2 — Machine Utility % | ✓ Show | ✓ Recalculate |
| 3 — Production Quantity | ✓ Show | ✓ Recalculate |
| 4 — Energy Consumption | ✓ Show | ✓ Recalculate |
| 5 — Energy per Casting | ✓ Show | ✓ Recalculate |
| 6 — Total Blast Time | ✓ Show | ✓ Recalculate |
| 7 — Effective Shots Usage | ✓ Show | ✗ Hide |
| 8 — Avg Shot Refill Interval | ✓ Show | ✗ Hide |
| 9 — Cycle Count | ✓ Show | ✓ Recalculate |
| 10 — Last Refill Time | ✓ Show | ✗ Hide |
| 11 — Maintenance Alert | ✓ Show | ✗ Hide |
| 12 — Spare Life Reminder | ✓ Show | ✗ Hide |
| 13 — Amps per Impeller | ✓ Show | ✗ Hide |

---

## What the dashboard must NOT do

- Do NOT write to any table other than `calculation_requests`
- Do NOT delete any rows from any table — data is preserved forever
- Do NOT expect a REST API — there is none
- Do NOT show production in tonnes — always display in kg
- Do NOT query `plc_historical_data` for periodic heartbeat records as data points for graphs — filter by `storage_reason IN ('COV', 'STATE_CHANGE', 'VALUE_CHANGE', 'BLAST_ON', 'INITIAL')` if you want actual value changes only
- Do NOT re-fire maintenance alert popups for the same spare within the 30-minute minimum window

---

## Quick reference

| Dashboard action | Table | Query pattern |
|---|---|---|
| Machine status (live) | `plc_current_values` | `WHERE address = 'DB60.DBB0'` |
| Section 1 scalar values | `plc_lifetime_parameters` | `SELECT parameter_name, value` |
| Section 1 shots breakdown | `plc_shots_breakdown` | `ORDER BY refill_timestamp ASC` |
| Production graph raw data | `plc_historical_data` | `WHERE parameter_name = 'Tonnage' ORDER BY timestamp` |
| Machine utility graph data | `plc_historical_data` | `WHERE parameter_name IN ('Blast ON/OFF', 'Machine status')` |
| Amps per impeller (live) | `plc_current_values` | `WHERE parameter_name LIKE 'Current_imp_%'` |
| Amps history graph | `plc_historical_data` | `WHERE parameter_name = 'Current_imp_N' AND timestamp BETWEEN ...` |
| Spare health table | `plc_spare_status` | `ORDER BY impeller_num, spare_index` |
| Spare alerts | `plc_spare_status` | `WHERE trigger_active = TRUE AND threshold_hours > 0` |
| Replacement confirmation | `plc_spare_status` | `WHERE last_replaced_at >= NOW() - INTERVAL '24 hours'` |
| Trigger Section 2 | `calculation_requests` | `INSERT … RETURNING id` |
| Poll Section 2 status | `calculation_requests` | `WHERE id = ? SELECT status` |
| Section 2 scalar results | `plc_filtered_parameters` | `WHERE request_id = ?` |
| Section 2 per-cycle table | `plc_filtered_cycle_data` | `WHERE request_id = ? ORDER BY cycle_number` |
| Section 2 shots graph | `plc_filtered_shots_breakdown` | `WHERE request_id = ? ORDER BY refill_timestamp` |
