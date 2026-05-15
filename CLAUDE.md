# PLCGateway — Claude Context

## Project summary

College project. Backend Windows Service that reads a Siemens S7-1200 PLC every second and stores data in PostgreSQL. A separate dashboard application reads from the same database. There is no REST API — all backend↔dashboard communication goes through the database.

**Stack:** .NET 10, C#, S7.NetPlus (PLC driver), Npgsql (PostgreSQL), Microsoft.Extensions.Hosting

**PLC:** Siemens S7-1200 at 192.168.0.180, Rack 0, Slot 1. Data Block DB60. ~150 tags.

**Database:** `sreesakthi_gateway` on localhost:5432, user=postgres, password=Pass

---

## Non-negotiable constraints

- **Never delete historical data.** `plc_historical_data` must grow forever. No compression, archiving, or deletion — ever. This is a hard client requirement.
- **No REST API.** The dashboard communicates only through the database. Do not add HTTP endpoints.
- **Discuss architecture before coding.** For any non-trivial change, draft the plan as a table or diagram and wait for go-ahead before writing code.
- **Terminology is strict:** `tags` = raw PLC readings stored in `plc_historical_data`; `parameters` = calculated business values stored in `plc_lifetime_parameters` and related tables. Never confuse these in code, comments, or conversation.

---

## Terminology

| Term | Meaning |
|---|---|
| **Tag** | A raw value read from the PLC. Stored in `plc_current_values` (latest) and `plc_historical_data` (COV history). |
| **Parameter** | A calculated business value. Stored in `plc_lifetime_parameters` (Section 1) or `plc_filtered_parameters` (Section 2). |
| **Section 1** | All-time cumulative parameters, updated every minute. Dashboard reads directly. |
| **Section 2** | On-demand filtered parameters. Dashboard triggers by writing to `calculation_requests`. |
| **COV** | Change of value. Tier 2 storage is triggered only when a tag changes by ≥2% (numeric) or changes state (BOOL). |
| **Tier 1** | `plc_current_values` — one row per tag, always the latest value. |
| **Tier 2** | `plc_historical_data` — full time-series, never deleted. Source of truth for all calculations. |

---

## Database tables (10 total)

| Table | Owner | Purpose |
|---|---|---|
| `plc_current_values` | Backend | Tier 1 — latest tag value per address |
| `plc_historical_data` | Backend | Tier 2 — COV-based history, never deleted |
| `plc_lifetime_parameters` | Backend writes, Dashboard reads | Section 1 scalar parameters (1 row per param) |
| `plc_shots_breakdown` | Backend writes, Dashboard reads | Section 1 shots/refill breakdown table (cleared+rewritten each minute) |
| `plc_cycles` | Backend | One row per completed blast cycle |
| `calculation_requests` | Dashboard writes, Backend processes | Section 2 trigger — dashboard inserts here |
| `plc_filtered_parameters` | Backend writes, Dashboard reads | Section 2 scalar results per request |
| `plc_filtered_cycle_data` | Backend writes, Dashboard reads | Section 2 per-cycle breakdown per request |
| `plc_filtered_shots_breakdown` | Backend writes, Dashboard reads | Section 2 shots breakdown per request |
| `plc_spare_status` | Backend | 140 rows — spare health per impeller per spare |

---

## Services

| Service | Interval | Role |
|---|---|---|
| `GatewayWorker` | 1s | PLC scan loop → Tier 1 always, Tier 2 via COV |
| `CovDetectionService` | per tag | ≥2% change for numeric, state-change for BOOL, 60s heartbeat |
| `AggregationService` | 1 min | Calls `ComputeLifetimeParametersAsync()` → `plc_lifetime_parameters` + `plc_shots_breakdown` |
| `CycleTrackingService` | 2s | Falling edge on `Blast ON/OFF` → reads Tier 1 for metal/tonnage → writes `plc_cycles` |
| `FilteredCalculationService` | 5s poll | Picks up pending `calculation_requests`, runs all Section 2 calculations |
| `SpareMonitoringService` | 10s | Reads 140 trigger/runhour/replaced tags → upserts `plc_spare_status` |
| `CalculationService` | shared lib | Math: `ComputeLifetimeParametersAsync`, `ComputeFilteredParametersAsync` |
| `PlcService` | — | S7.NetPlus wrapper |

---

## Parameters

### Section 1 — `plc_lifetime_parameters` (scalar, updated every minute)

| `parameter_name` | Formula | Source tags |
|---|---|---|
| `machine_status` | `value ≠ "0" → 1, else 0` | `Machine status` BYTE at `DB60.DBB0`, live from Tier 1 |
| `machine_utility_pct` | `blast_time_sec ÷ machine_on_time_sec × 100` | `Blast ON/OFF`, `Machine status` from Tier 2 |
| `production_qty_kg` | Latest raw `Tonnage` value | `Tonnage` from Tier 1 (PLC is running accumulator) |
| `energy_kwh_total` | `Σ (avg_amps_per_impeller × cycle_duration_hours)` across 10 impellers × all cycles | `plc_cycles` + `Current_imp_1`…`10` from Tier 2 |
| `energy_per_casting_kwh_kg` | `energy_kwh_total ÷ production_qty_kg` | derived |
| `blast_time_sec` | Total seconds where `Blast ON/OFF = true` | `Blast ON/OFF` Tier 2 |
| `cycle_count` | Rising edges `0→1` on `Blast ON/OFF` | `Blast ON/OFF` Tier 2 |
| `last_refill_epoch_sec` | Unix epoch of latest `Refil shots weight` change | `Refil shots weight` Tier 2 |

### Section 1 — `plc_shots_breakdown` (table, cleared+rewritten every minute)

For each pair of consecutive `Refil shots weight` COV events: count `Blast ON/OFF` rising edges between them.
Output: `(refill_timestamp, blast_count)`. This is the shared dataset for parameters #7 and #8.

### Section 2 differences

| Parameter | Section 2 formula |
|---|---|
| `production_qty_kg` | `last Tonnage in window − first Tonnage in window` (NOT the live accumulator value) |
| `energy_kwh_total` | Same formula, cycles in filter window only |
| `machine_status` | Not included |

### Per-cycle breakdown (`plc_filtered_cycle_data`)

| Column | Formula |
|---|---|
| `production_kg` | `tonnage_kg(this cycle) − tonnage_kg(prev cycle)`, floor 0 |
| `energy_kwh` | `avg_amps_all_impellers × cycle_duration_hours` |
| `shots_usage` | `refill_weight_in_cycle ÷ production_kg` |

### Live display (not stored as parameters — dashboard reads directly)

| What | Source | Query pattern |
|---|---|---|
| Amps per impeller (×10) | `plc_current_values` | `WHERE parameter_name = 'Current_imp_N'` |
| Spare health (140 rows) | `plc_spare_status` | All rows, or `WHERE trigger_active = TRUE AND threshold_hours > 0` for alerts |

---

## Key tag names (as they appear in `parameter_name` column)

| Tag | Type | Address |
|---|---|---|
| `Machine status` | BYTE | DB60.DBB0 |
| `Blast ON/OFF` | BOOL | — |
| `Reblast ON/OFF` | BOOL | — |
| `Tonnage` | DINT | — |
| `Refil shots weight` | REAL | — |
| `Current_imp_1` … `Current_imp_10` | REAL | — |
| `Casting metal 1-4 name` | STRING | — |
| `Casting metal 1-4 weight` | DINT | — |
| `Spares trigger imp{N}[{M}]` | BOOL | — (140 tags) |
| `Spares_Runhour_imp{N}[{M}]` | REAL | — (140 tags) |
| `REPLACED_{N}[{M}]` | BOOL | — (140 tags) |

---

## Section 2 filter modes

| `filter_by` | Columns used |
|---|---|
| `'time'` (default) | `filter_start`, `filter_end` |
| `'cycle'` | `filter_cycle_from`, `filter_cycle_to` |
| `'metal'` | `filter_metal_name` |

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

| File | Purpose |
|---|---|
| `PLCGateway/migration.sql` | Idempotent DB migration — run once before starting |
| `PLCGateway/appsettings.json` | PLC IP, DB connection string, impeller count, spare thresholds |
| `PLCGateway/Program.cs` | Host setup, DI registration for all services |
| `PLCGateway/GatewayWorker.cs` | PLC scan loop |
| `PLCGateway/AggregationService.cs` | Section 1 parameter computation trigger |
| `PLCGateway/CalculationService.cs` | All parameter math (Section 1 + Section 2) |
| `PLCGateway/CycleTrackingService.cs` | Blast cycle detection and logging |
| `PLCGateway/FilteredCalculationService.cs` | Section 2 request processor |
| `PLCGateway/SpareMonitoringService.cs` | Spare health monitoring |
| `PLCGateway/DatabaseService.cs` | All PostgreSQL queries |
| `PLCGateway/PlcService.cs` | S7.NetPlus wrapper |
| `PLCGateway/CovDetectionService.cs` | COV logic (2% deadband, state-change, 60s heartbeat) |
| `PLCGateway/Models/PlcCycle.cs` | Cycle model |
| `PLCGateway/Models/CalculationRequest.cs` | Section 2 request model |
| `README.md` | Full architecture, schema, formulas, run instructions |
| `DASHBOARD_PROMPT.md` | Complete briefing for dashboard developer — exact SQL, what to read/write |

---

## Pending items (not yet implemented — need client confirmation)

- **Energy meter address** — currently using `Current_imp_N` AMPS tags. Client to confirm if direct energy meter address should be used instead.
- **Empty casting metal slot format** — need to confirm whether empty slots come from PLC as blank string or null (affects cycle recording).

---

## How to run migration

```powershell
psql -U postgres -d sreesakthi_gateway -f PLCGateway/migration.sql
```

Safe to re-run on existing databases — all statements are idempotent.
