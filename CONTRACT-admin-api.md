# CONTRACT — PLCGateway Admin API (cloud pull)

**Consumer:** the cloud mirror application.
**Producer:** the on-premises PLCGateway (single source of truth for all data and KPI calculations).
**Rule #1:** the cloud renders these values verbatim. It must never recompute, re-derive, re-round,
or unit-convert anything in any of these responses.

This document matches every action in `PLCGateway/Api/Controllers/AdminController.cs` exactly.
Any change to that controller must update this file and `sample-response.json` in the same commit.

---

## Endpoints

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/admin/live` | Full live snapshot — Section 1 + last completed Section 2 |
| `GET` | `/api/admin/trends` | Whole-history graph series for the 4 graphable lifetime parameters |
| `POST` | `/api/admin/filter` | Trigger a Section 2 filtered calculation synchronously, get the full result back in one response |
| `GET` | `/api/admin/history` | Per-tag raw Tier 2 pulls (row-capped, paged) — see "Not covered here" |

## Transport and authentication (all endpoints)

| Item | Value |
| --- | --- |
| Transport | HTTPS only (IIS binding, port 443) |
| Auth | Header `X-Api-Key` must equal `Admin:ApiKey` (appsettings). No IP allowlist — the cloud caller (Firebase Cloud Functions) has no fixed egress IP, so key-over-HTTPS is the whole model. |
| Failed key | `403 {"error":"forbidden"}` |
| Server failure | `500 {"error":"<endpoint-specific message>"}` |
| Success | `200`, `Content-Type: application/json; charset=utf-8` |

No JWT is involved on any `/api/admin/*` endpoint (JWT protects the local dashboard API only).

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

# `GET /api/admin/live`

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
| `section2` | object, nullable | Latest **completed** filtered calculation — from either side, see below (null until one exists) |

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

Ordered by `parameterName` ascending. One entry per parameter; exactly these ten names:

> ⚠️ **BREAKING CHANGE — `effective_shots_usage` was renamed and inverted.**
>
> It is now `effective_shots_usage_kg_per_ton`. The formula is the **inverse** of the old one and
> the unit changed from kg/kg to kg/T, so the same number means something different. A consumer
> reading the old key gets `undefined`, not an error — check for the new key explicitly.
>
> | | Old | New |
> |---|---|---|
> | Key | `effective_shots_usage` | `effective_shots_usage_kg_per_ton` |
> | Formula | `production ÷ refill_weight` | `refill_weight ÷ (production ÷ 1000)` |
> | Unit | kg/kg | kg/T |
> | Direction | higher is better | **lower is better** |


| `parameterName` | Unit | Notes |
| --- | --- | --- |
| `avg_shot_refill_time_sec` | seconds | 1 dp — elapsed time since first refill ÷ refill count |
| `blast_time_sec` | seconds | 1 decimal place |
| `cycle_count` | count | integer text |
| `effective_shots_usage_kg_per_ton` | **kg/T** — shot consumed per tonne of casting | 4 dp. `total refill weight ÷ (production_qty_kg ÷ 1000)`, both cumulative since commissioning. **LOWER IS BETTER.** `""` when nothing has been cast yet (divide-by-zero) — render as `—`, do not treat as `0` |
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

**"Latest" is shared, not local-only.** `calculation_requests` is one table used by both the local
dashboard's async flow (submit → poll) and the cloud's synchronous `POST /api/admin/filter` (below)
— there is no separate table or flag distinguishing who triggered a request. If the cloud calls
`POST /api/admin/filter`, that request becomes the new "latest completed" and is what `section2`
shows on the next `Live()` call, until either side computes a different filter. This is intentional
— one shared source of truth — not a bug to work around. The response shape below (this section)
and `POST /api/admin/filter`'s response are identical field-for-field.

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

### `section2.shotsBreakdown[]` — **REMOVED (breaking)**

This field no longer exists in `section2`. The shots breakdown became a Section 1 output only: a
refill interval spans whatever cycles fall inside it, mixing casting items, so no filter scopes it
meaningfully. Nothing writes `plc_filtered_shots_breakdown` any more.

Use the **top-level `shotsBreakdown`** in `/api/admin/live` instead — it is machine-wide and
unaffected by this change.

### `section2.metals[]`

Production per declared casting item for the in-scope cycles. The field names still say `metal`
(they match the PLC tag names and DB columns), but the local dashboard displays this as **casting
item** — see README, "Casting item vs casting metal". **Not** derived from `Tonnage`; see
`CLAUDE.md` for why Section 1 and Section 2 production intentionally answer different questions.
Ordered by `productionKg` descending (largest contributor first).

Two things now affect what is present here:

- It is written only when `production_qty_kg` is among the request's `selectedParameters`
  (see below). If it was not selected, this array is empty.
- Under a `metal`/item filter it contains **only the filtered item**, not every item declared in
  the matching cycles.

| Field | JSON type | Description |
| --- | --- | --- |
| `metalName` | string | Declared metal name, or `"unspecified"` for a weight declared with a blank name |
| `productionKg` | number | Summed declared weight across the in-scope cycles, 2 dp |

Empty array (not `null`) when no cycle in scope declared any casting-metal weight.

---

# `GET /api/admin/trends`

Whole-history graph data for the 4 graphable Section 1 lifetime parameters
(`machine_utility_pct`, `production_qty_kg`, `energy_kwh_total`, `energy_per_casting_kwh_kg`).
Exactly the local dashboard's `/api/trends` — same rollup logic, same query params — put behind
`AdminGuardMiddleware` instead of JWT so the cloud can reach it. `Live()` intentionally does **not**
carry this data: it changes at most once a minute (the `AggregationService` cadence) and the
payload is comparatively large, so folding it into every `Live()` poll would be constant waste for
data that's almost always unchanged since the last poll. Call this once per dashboard load, or on
its own slow timer — not on `Live()`'s poll cadence.

| Item | Value |
| --- | --- |
| Method / path | `GET /api/admin/trends` |
| Query params | `bucket` = `hour` \| `day` \| `month` (default `day`); `start`, `end` — ISO 8601, optional except `bucket=hour` which requires both |
| No bounds | Returns the full all-time series at the requested bucket size. Pass `bucket=month` with no `start`/`end` for the compact whole-history series (what the local dashboard's all-time graphs use) |
| Validation errors | `400 {"error": "..."}` — `start` ≥ `end`, invalid `bucket`, or `bucket=hour` missing a bound |
| Server failure | `500 {"error": "trends query failed"}` |

Response: JSON array, one entry per bucket, ordered ascending by `day`.

| Field | JSON type | Description |
| --- | --- | --- |
| `day` | string (timestamp) | Bucket start — the calendar day, or first-of-month when `bucket=month` |
| `machineOnSec` | number | Seconds `Machine status` was on, summed over the bucket |
| `blastOnSec` | number | Seconds `Blast ON/OFF` was on, summed over the bucket |
| `utilityPct` | number | `blastOnSec ÷ machineOnSec × 100`, capped at 100, **rebuilt from the summed seconds — never averaged from per-day percentages** (averaging would misweight a partial day). 0 when the machine never ran in the bucket |
| `cycleCount` | number (integer) | Blast rising edges in the bucket |
| `productionKg` | number | Sum of per-cycle `production_kg` for cycles closing in the bucket, 2 dp |
| `tonnageEnd` | number, nullable | The PLC's running `Tonnage` accumulator at the end of the bucket, carried forward across buckets with no reading. `null` only before the first-ever `Tonnage` reading |
| `energyKwh` | number | Sum of per-cycle `energy_kwh` for cycles closing in the bucket, 3 dp |
| `efficiencyKwhPerKg` | number | `energyKwh ÷ productionKg` for the bucket, 4 dp. 0 when nothing was produced in the bucket |

---

# `POST /api/admin/filter`

Cloud-triggered Section 2 filtered calculation — synchronous, no polling. Computes inline using the
same engine (`CalculationService.ComputeFilteredParametersAsync`) the local dashboard's async flow
uses, so there is exactly one implementation of the math regardless of which side triggers it.

**Request body:**

| Field | JSON type | Description |
| --- | --- | --- |
| `filterBy` | string | `"time"` \| `"cycle"` \| `"metal"` — required |
| `filterStart` / `filterEnd` | string (timestamp) | Required when `filterBy == "time"`; ignored for the other two modes (server substitutes the current time as a placeholder, matching the local dashboard's convention) |
| `periodLabel` | string, optional | Passed through verbatim into the response, e.g. `"today"` — purely descriptive, not interpreted server-side |
| `filterCycleFrom` / `filterCycleTo` | number (integer) | Required when `filterBy == "cycle"`; `filterCycleFrom` must be ≤ `filterCycleTo` |
| `filterMetalName` | string | Required, non-blank, when `filterBy == "metal"`. This is the casting **item** name in dashboard wording |
| `selectedParameters` | string[], optional | Which Section 2 parameters to compute. **Omit (or send `null`/`[]`) to compute all of them** — existing callers need no change |

**`selectedParameters` — supported keys.** Only these seven; anything else is a `400`:

| Key | Produces |
| --- | --- |
| `machine_utility_pct` | a `results[]` entry. **Silently skipped when `filterBy == "metal"`** — machine on-time is not attributable to a single casting item |
| `production_qty_kg` | a `results[]` entry **and** the `metals[]` array |
| `energy_kwh_total` | a `results[]` entry |
| `energy_per_casting_kwh_kg` | a `results[]` entry |
| `blast_time_sec` | a `results[]` entry |
| `cycle_count` | a `results[]` entry |
| `impeller_current` | the `plc_filtered_amps_data` rows (read via the local `/api/filter/{id}/amps`) |

Unselected parameters are **never computed** — they are absent from `results[]`, and `cycles[]` is
empty if nothing cycle-derived was selected. This is not an error condition.

**Validation errors** (`400 {"error": "..."}`): invalid/missing `filterBy`; `time` mode with
`filterStart` ≥ `filterEnd`; `cycle` mode missing either bound or `From` > `To`; `metal` mode with a
blank/missing `filterMetalName`; an unrecognised entry in `selectedParameters` (the response carries
`unknown` and `supported` arrays).

**Response:** `200`, identical shape to `Live().section2` (see above) — `requestId`, `filterBy`,
`filterStart`/`filterEnd`, `periodLabel`, `filterCycleFrom`/`filterCycleTo`, `filterMetalName`,
`processedAt`, `results[]`, `cycles[]`, `metals[]`. **`shotsBreakdown[]` is no longer part of this
response** (see above). No polling, no separate status check — the full result is in this one
response.

**Server failure:** `500 {"error": "...", "requestId": <id>}` if the request was recorded but
computation or read-back failed — the `requestId` lets you cross-check `Live().section2` or retry.
`500 {"error": "failed to submit filter request"}` (no `requestId`) if the request couldn't even be
recorded.

**Side effect:** per the note under `section2` above, this becomes the new "latest completed" row —
`Live().section2` will show this result on the next poll, until a different filter is computed by
either side.

**Latency:** this endpoint used to be gated by an O(N)-database-round-trips-per-cycle loop in the
`metal` case (one round trip per matching cycle, estimated at high-single-digit to low-tens of
seconds worst case). That loop no longer exists — it was removed with `shots_usage` in an earlier
change, and the remaining per-cycle work (`plc_filtered_cycle_data` inserts) is now batched into a
single round trip regardless of cycle count. Every other query in the pipeline is already O(1)
round trips (one indexed range query each), so response time no longer scales with the number of
matching cycles — the only remaining variable is total event-history size within the computed
window, which is a data-volume cost, not a round-trip-count cost. **This project's dev database
only has 5 recorded cycles, too small to produce a meaningful stress number** — I verified
correctness (all three filter modes, concurrency-safety against the background poller) but could
not empirically measure a large-N case, and won't fabricate cycle/history rows to manufacture one
(`plc_cycles` and `plc_historical_data` are production data, not something to insert-and-delete for
a benchmark). Re-benchmark against a realistic data volume before finalizing a hard timeout; a
generous timeout (30–60s) is a safe starting point given the structural fix, not a number measured
against real scale.

---

## Sample

`sample-response.json` (repo root) is a full `GET /api/admin/live` response in exactly the shape
described above, with realistic dummy values — including all 140 `spareGrid` rows, the lexicographic
`amps` ordering, and `section2.metals[]` — usable directly as a fixture in the cloud app with no
live connection. It does not include sample responses for `/api/admin/trends` or
`/api/admin/filter` — the field tables above are authoritative for those two.

## Not covered here

`GET /api/admin/history` (per-tag Tier 2 pulls, listed in the Endpoints table above for
completeness) is unchanged in this pass and intentionally not detailed in this contract.
