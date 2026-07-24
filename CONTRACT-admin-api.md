# CONTRACT — `/api/admin/live` (cloud pull, extended)

**Consumer:** the cloud mirror application.
**Producer:** the on-premises PLCGateway (single source of truth for all data and KPI calculations).
**Rule #1:** the cloud renders these values verbatim. It must never recompute, re-derive, re-round,
or unit-convert anything in this payload.

This document matches `PLCGateway/Api/Controllers/AdminController.cs` (`Live()` action) exactly.
Any change to that action must update this file and `sample-response.json` in the same commit.

---

## Transport and authentication

| Item | Value |
| --- | --- |
| Method / path | `GET /api/admin/live` |
| Transport | HTTPS only (IIS binding, port 443) |
| Gate 1 — IP | Caller's source IP must be listed in `Admin:AllowedIps` (appsettings) |
| Gate 2 — key | Header `X-Api-Key` must equal `Admin:ApiKey` (appsettings) |
| Both gates required | Failing either → `403 {"error":"forbidden"}` |
| Server failure | `500 {"error":"live snapshot failed"}` |
| Success | `200`, `Content-Type: application/json; charset=utf-8` |

No JWT is involved on this endpoint (JWT protects the local dashboard API only).

## Timestamp convention

- Every timestamp in the payload is **UTC, ISO 8601, `Z`-suffixed**, e.g. `"2026-07-11T06:42:15.123Z"`.
  Fractional seconds vary in length (System.Text.Json trims trailing zeros); parse as ISO 8601, do
  not assume a fixed digit count.
- Storage is gateway-local wall time; the endpoint converts to UTC using the gateway server's
  timezone at response time.
- Fields that can be `null` are marked *nullable* below. Non-nullable timestamps are always present.

## Value-string convention

Several `value` fields are **JSON strings containing a decimal number** (they come from PostgreSQL
`NUMERIC` / typed tag columns rendered to text):

- Numeric tags/parameters → decimal text, `.` separator, no thousands grouping, may carry trailing
  zeros (e.g. `"12.5"`, `"5321.744"`, `"0"`).
- BOOL tags → `"1"` / `"0"`.
- Missing value → `""` for lifetime/Section 2 parameters, `"0"` for amps.

Render as delivered. Parse to number only for formatting/plotting — never for further math.

---

## Top-level shape

```json
{
  "generatedAtUtc":  "…",
  "plcConnected":    true,
  "lastScanAt":      "…",
  "changedAt":       "…",
  "machineStatus":   { … } | null,
  "lifetime":        [ … ],
  "shotsBreakdown":  [ … ],
  "amps":            [ … ],
  "spareGrid":       [ … ],
  "spareAlerts":     [ … ],
  "section2":        { … } | null
}
```

| Field | JSON type | Description |
| --- | --- | --- |
| `generatedAtUtc` | string (timestamp) | When this snapshot was assembled on the gateway |
| `plcConnected` | boolean | Live PLC link state (`gateway_status.plc_connected`) |
| `lastScanAt` | string (timestamp), nullable | Time of the last successful PLC scan |
| `changedAt` | string (timestamp), nullable | When `plcConnected` last flipped |
| `machineStatus` | object, nullable | Running/stopped tile (null only before the first-ever PLC scan) |
| `lifetime` | array | Section 1 lifetime parameters (all-time KPIs) |
| `shotsBreakdown` | array | Section 1 shots-per-refill table (chart data) |
| `amps` | array | Live current per impeller, 10 entries |
| `spareGrid` | array | Full spare-health grid, 140 entries (10 impellers × 14 spares) |
| `spareAlerts` | array | Subset of `spareGrid` where `triggerActive` is true and `thresholdHours > 0` |
| `section2` | object, nullable | Latest **completed** filtered calculation (null until one exists) |

**Disconnected semantics:** when `plcConnected` is `false`, `machineStatus.value` is an
authoritative forced `"0"` (machine treated as OFF), and `amps` / `spareGrid` hold the last values
before disconnect. The cloud must show a disconnected indicator, exactly like the local dashboard.

---

## `machineStatus`

| Field | JSON type | Description |
| --- | --- | --- |
| `value` | string | Machine-status byte as decimal text, `"0"`–`"255"` |
| `running` | boolean | `value != "0"` — same rule as the local tile; use this, do not re-derive |
| `isStale` | boolean | True while the PLC is disconnected (Tier 1 flagged stale) |
| `lastUpdated` | string (timestamp) | When the value last changed in Tier 1 |

## `lifetime[]` — Section 1 parameters

Ordered by `parameterName` ascending. One entry per parameter; exactly these nine names:

| `parameterName` | Unit | Notes |
| --- | --- | --- |
| `avg_shot_refill_time_sec` | seconds | 1 dp — elapsed time since first refill ÷ refill count |
| `blast_time_sec` | seconds | 1 decimal place |
| `cycle_count` | count | integer text |
| `energy_kwh_total` | kWh (nominal) | 3 dp. **Current formula is avg-amps × hours (client decision pending). Label as delivered; do not convert.** |
| `energy_per_casting_kwh_kg` | kWh/kg (nominal) | 4 dp; same energy caveat |
| `last_refill_epoch_sec` | Unix epoch seconds | integer text |
| `machine_status` | 0/1 | `"1"` running, `"0"` stopped |
| `machine_utility_pct` | percent 0–100 | 2 dp |
| `production_qty_kg` | kg | 2 dp |

Entry shape:

| Field | JSON type |
| --- | --- |
| `parameterName` | string |
| `value` | string (decimal text; `""` if never computed) |
| `updatedAt` | string (timestamp) |

## `shotsBreakdown[]`

Ordered by `refillTimestamp` ascending. Blast count between consecutive shot refills.

| Field | JSON type | Description |
| --- | --- | --- |
| `refillTimestamp` | string (timestamp) | Time of the refill event |
| `blastCount` | number (integer) | Rising edges of `Blast ON/OFF` until the next refill |

## `amps[]`

10 entries, one per impeller. **Ordered lexicographically by `parameterName`**, i.e.
`Current_imp_1`, `Current_imp_10`, `Current_imp_2`, … `Current_imp_9` — sort client-side by the
numeric suffix if you need 1…10 display order.

| Field | JSON type | Description |
| --- | --- | --- |
| `parameterName` | string | `Current_imp_1` … `Current_imp_10` |
| `value` | string | Amperes, decimal text (`"0"` when absent) |
| `lastUpdated` | string (timestamp) | Tier 1 last-change time |

## `spareGrid[]` and `spareAlerts[]`

Identical entry shape. `spareGrid` has all 140 rows ordered by (`impellerNum`, `spareIndex`);
`spareAlerts` repeats the rows where `triggerActive == true && thresholdHours > 0`.

| Field | JSON type | Description |
| --- | --- | --- |
| `impellerNum` | number (integer) | 1–10 |
| `spareIndex` | number (integer) | 0–13 |
| `spareName` | string | From config, by index: Blade, Blade Mounting Piece, Narrow Plate, Curved Plate, Feeding End, Bearing End, Impeller, Wall Plate, Control Gauge, Disc Spacer, Doom Nut 1/2in, Doom Nut 5/8, Disc, Guide Plate |
| `thresholdHours` | number | Replacement threshold; `0` means "not monitored" (spareIndex 9) |
| `currentRunHours` | number | Accumulated run hours (PLC resets on replacement) |
| `triggerActive` | boolean | PLC-set flag: threshold crossed |
| `lastReplacedAt` | string (timestamp), nullable | Last observed REPLACED rising edge; null if never observed |
| `lastUpdatedAt` | string (timestamp) | Last upsert of this row |

## `section2` — latest completed filtered calculation

`null` until the first `calculation_requests` row reaches status `done`. Otherwise the request with
the **highest id** whose status is `done`, mirrored with the same read paths the local
FilterResultsView uses.

### Request metadata

| Field | JSON type | Description |
| --- | --- | --- |
| `requestId` | number (integer) | `calculation_requests.id` |
| `filterBy` | string | `"time"` \| `"cycle"` \| `"metal"` |
| `filterStart` / `filterEnd` | string (timestamp) | Always present (placeholder `NOW()` for cycle/metal filters) |
| `periodLabel` | string, nullable | e.g. `"today"`; set for time presets |
| `filterCycleFrom` / `filterCycleTo` | number (integer), nullable | Set when `filterBy == "cycle"` |
| `filterMetalName` | string, nullable | Set when `filterBy == "metal"` |
| `processedAt` | string (timestamp), nullable | When the backend finished computing |

### `section2.results[]`

Ordered by `parameterName` ascending. Exactly these five names (no `machine_status`,
no `last_refill_epoch_sec`, no scalar production — production appears per cycle below):

`blast_time_sec`, `cycle_count`, `energy_kwh_total`, `energy_per_casting_kwh_kg`,
`machine_utility_pct` — units and rounding identical to the `lifetime` table above.

| Field | JSON type |
| --- | --- |
| `parameterName` | string |
| `value` | string (decimal text; `""` if null) |

### `section2.cycles[]`

Ordered by `cycleNumber` ascending. One row per blast cycle in the filter scope.

| Field | JSON type | Description |
| --- | --- | --- |
| `cycleNumber` | number (integer) | Global cycle number |
| `blastStart` / `blastEnd` | string (timestamp) | Cycle window |
| `metal1Name` … `metal4Name` | string, nullable | Casting metal name. **An empty slot is always `null`, never `""`** — names are trimmed and blank values normalized to null at recording time |
| `metal1WeightKg` … `metal4WeightKg` | number, nullable | Declared weight in kg; `null` when absent or ≤ 0 at recording. Nullable independently of the name |
| `productionKg` | number | kg, 2 dp (tonnage delta, floor 0) |
| `energyKwh` | number | 3 dp — same energy-formula caveat as above |
| `shotsUsage` | number | 4 dp — refill weight in cycle ÷ production kg |

### `section2.shotsBreakdown[]`

Same shape and ordering as the top-level `shotsBreakdown`, restricted to the filter window.

---

## Sample

`sample-response.json` (repo root) is a full response in exactly this shape with realistic dummy
values — including all 140 `spareGrid` rows and the lexicographic `amps` ordering — usable directly
as a fixture in the cloud app with no live connection.

## Not covered here

`GET /api/admin/history` (per-tag Tier 2 pulls) is unchanged in this pass and intentionally not
part of this contract.
