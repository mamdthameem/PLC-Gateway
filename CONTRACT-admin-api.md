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
| `GET` | `/api/admin/trends` | Bucketed graph series behind every tile chart except the per-cycle and per-item ones |
| `GET` | `/api/admin/amps/by-cycle` | One impeller's average current per completed cycle, all history — the chart behind a live impeller tile |
| `POST` | `/api/admin/filter` | Trigger a Section 2 filtered calculation synchronously, get the full result back in one response |
| `GET` | `/api/admin/filter/{id}/amps` | One filtered calculation's impeller current — tile averages plus the per-cycle chart points |
| `GET` | `/api/admin/history` | Raw recorded readings of one tag, oldest first, in pages |

## Changes on 2026-09-19

All field and endpoint changes are **additive** — nothing existing was renamed or removed, so a
consumer written against the previous version keeps working unchanged.

| Change | Where |
| --- | --- |
| New `ampsLastCycle[]` | `/live` |
| New `intervalStartTimestamp` on each `shotsBreakdown[]` row | `/live` |
| New `selectedParameters` and `amps[]` | `section2` in `/live` and the `POST /filter` response |
| New endpoints `GET amps/by-cycle` and `GET filter/{id}/amps` | — |
| `bucket=auto` accepted, resolved bucket in the `X-Trend-Bucket` header (default still `day`) | `/trends` |
| Corrected: the chartable parameters, and `section2.results[]` has **six** names (it includes `production_qty_kg`) | docs only |

Two **values** move as a result of gateway-side fixes (the cloud renders them verbatim, so no code
change is needed, but the numbers will differ from before):

- **Section 2 `cycle_count` and `blast_time_sec`** (and slightly `machine_utility_pct`) for time and
  cycle filters. The filtered calculation used to skip the first cycle in scope, so every such
  filter was one cycle short. A filter covering all history now equals the Section 1 figures
  exactly. Results computed before the fix are stored snapshots and keep their old numbers until
  that filter is applied again.
- **Section 1 `avg_shot_refill_time_sec` and `shotsBreakdown[]`.** Refill-weight rows that carry no
  numeric value (written by an early gateway build) are no longer counted as refills. A gateway
  with no such rows is unaffected.

The gateway also makes one call the other way, to the cloud — the licence check. The cloud must
provide that endpoint; see "Licence check" near the end.

## Transport and authentication (all endpoints)

| Item | Value |
| --- | --- |
| Transport | HTTPS only (IIS binding, port 443) |
| Auth | Header `X-Api-Key` must equal `Admin:ApiKey` (set in the gateway's `appsettings.Production.json`). No IP allowlist — the cloud caller (Firebase Cloud Functions) has no fixed egress IP, so key-over-HTTPS is the whole model. Store the key in the cloud's secret store, never in code |
| Key rules | At least 32 characters. The `REPLACE_WITH…` placeholder shipped in `appsettings.json` is never accepted: until a real key is set, **every** request gets `403`. The comparison is constant-time |
| Failed key | `403 {"error":"forbidden"}` — no header, a wrong key, a key in the query string, or two `X-Api-Key` headers |
| Licence lock | `/api/admin/*` is never locked by the licence check, so the cloud keeps receiving data while a client's dashboard is locked |
| Server failure | `500 {"error":"<endpoint-specific message>"}` |
| Success | `200`, `Content-Type: application/json; charset=utf-8` |

No JWT is involved on any `/api/admin/*` endpoint (JWT protects the local dashboard API only).

## Timestamp convention

- Every timestamp in the payload is **UTC, ISO 8601, `Z`-suffixed**, e.g. `"2026-07-11T06:42:15.123Z"`.
  Fractional seconds vary in length (System.Text.Json trims trailing zeros); parse as ISO 8601, do
  not assume a fixed digit count.
- Storage is gateway-local wall time; the endpoint converts to UTC using the gateway server's
  timezone at response time.
- **One exception: `/api/admin/history`** takes and returns gateway-local time with no `Z`. See that
  section.
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
  "impellers":       { "selected": [ … ] },
  "amps":            [ … ],
  "ampsLastCycle":   [ … ],
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
| `impellers` | object | `{ "selected": [1, 2, …] }` — the site's impeller selection (`gateway_settings`), ascending integers 1–10. `amps`, `spareGrid` and `spareAlerts` hold **only** these impellers, and every energy figure (lifetime, trends, Section 2) counts only these. **So when impellers are hidden these lists get shorter**: with 9 selected, `amps` has 9 entries and `spareGrid` 126 rows (9 × 14). Build the display from the arrays, never from a fixed count of 10. Added 2026-09-15; additive |
| `amps` | array | Live current per **selected** impeller — one entry each, up to 10 |
| `ampsLastCycle` | array | Each selected impeller's average current over the **last completed cycle** — the "ran at N A" line. Added 2026-09-19; additive |
| `spareGrid` | array | Spare-health grid, 14 entries per **selected** impeller (140 with all ten) |
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

**Each row is keyed by the refill that CLOSED its interval**, not the one that opened it: the row at
`refillTimestamp` T counts the cycles run between the previous refill and T. So the interval
currently in progress has **no row** (it has no closing refill yet), and the last row is the last
*completed* interval — never "cycles since the last refill". Do not derive a "since refill" figure
from it.

| Field | JSON type | Description |
| --- | --- | --- |
| `refillTimestamp` | string (timestamp) | The refill that closed this interval |
| `intervalStartTimestamp` | string (timestamp), nullable | The refill that opened it. For every row but the first this equals the previous row's `refillTimestamp`; the first row's opener has no row of its own, so the gateway looks it up. `null` only if no earlier refill is on record. Added 2026-09-19; additive |
| `blastCount` | number (integer) | Rising edges of `Blast ON/OFF` between `intervalStartTimestamp` and `refillTimestamp` |

A refill-weight row with no numeric value is not counted as a refill (see "Changes on 2026-09-19").

## `amps[]`

One entry per selected impeller (see `impellers`), up to 10 — do not assume ten. **Ordered by
impeller number**: `Current_imp_1`, `Current_imp_2`, … `Current_imp_10`, skipping hidden impellers
(e.g. `…_6`, `…_8` when 7 is hidden). The gateway sorts on the number at the end of the name, so no
client-side sorting is needed. (Earlier versions of this document said the order was by text —
`1, 10, 2, …` — which was wrong.)

| Field | JSON type | Description |
| --- | --- | --- |
| `parameterName` | string | `Current_imp_1` … `Current_imp_10` |
| `value` | string | Amperes, decimal text (`"0"` when absent) |
| `lastUpdated` | string (timestamp) | Tier 1 last-change time |

## `ampsLastCycle[]`

Each selected impeller's average current over the last completed blast cycle
(`AVG` of the recorded samples inside that cycle's `[blastStart, blastEnd]`). The local dashboard
shows it under a tile whose live reading is below 1 A, as "ran at N A" — context for an idle zero,
never a substitute for the live headline. Same shape as `amps[]`.

| Field | JSON type | Description |
| --- | --- | --- |
| `parameterName` | string | `Current_imp_N` |
| `value` | string | Amperes, decimal text, 2 dp |
| `lastUpdated` | string (timestamp) | `blastEnd` of that last cycle |

**Match entries to `amps[]` by `parameterName`, not by position:** an impeller with no recorded
sample in the last cycle is absent here. Empty before the first cycle completes.

## `spareGrid[]` and `spareAlerts[]`

Identical entry shape. `spareGrid` has 14 rows per selected impeller (140 with all ten), ordered by (`impellerNum`, `spareIndex`);
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
| `selectedParameters` | string[], nullable | The parameter keys the request asked for (see `POST /api/admin/filter`). `null` means all of them. Decides which tables and panels exist for this request — e.g. no `impeller_current` ⇒ no filtered amps panel. `machine_utility_pct` is never computed under a `metal` filter even when listed. Added 2026-09-19; additive |

### `section2.results[]`

Ordered by `parameterName` ascending. At most these six names — only the ones the request selected
(no `machine_status`, `avg_shot_refill_time_sec`, `last_refill_epoch_sec` or
`effective_shots_usage_kg_per_ton`; those are Section 1 only):

`blast_time_sec`, `cycle_count`, `energy_kwh_total`, `energy_per_casting_kwh_kg`,
`machine_utility_pct`, `production_qty_kg` — units and rounding identical to the `lifetime` table
above, **except `production_qty_kg`**: in Section 2 it is the sum of *declared* casting-item weights
(the total of `metals[]`), not the PLC's `Tonnage` accumulator. The local dashboard names the two
tiles differently for that reason — "Production (Tonnage)" for Section 1, "Production (Item
Weight)" for Section 2. `energy_per_casting_kwh_kg` divides by the same declared total.

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
| `metal1WeightKg` … `metal4WeightKg` | number, nullable | Declared weight in kg; `null` when absent or ≤ 0 at recording. Nullable independently of the name — **a weight with a `null` name is real and is counted as `"unspecified"` in `metals[]`**; show it as `unspecified`, not as an empty slot |
| `productionKg` | number | kg, 2 dp — the cycle's **Tonnage delta** (measured, floor 0), not the sum of the declared weights. The local dashboard titles this column "Tonnage Produced (kg)" |
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

### `section2.amps[]`

The "Impeller Current (Filtered)" tiles: one entry per impeller, ordered by `impellerNumber`.
Empty when `impeller_current` was not selected. The per-cycle points behind each tile's chart are
**not** here — fetch them from `GET /api/admin/filter/{requestId}/amps` when a tile is opened (over
all history that is ~14 000 points, too many for every `Live()` poll). Added 2026-09-19; additive.

| Field | JSON type | Description |
| --- | --- | --- |
| `impellerNumber` | number (integer) | 1–10 — the impellers that were selected when the filter was computed |
| `overallAvgAmps` | number, nullable | Duration-weighted average current across the in-scope cycles, 2 dp. `null` when no cycle had a sample for this impeller |

---

# `GET /api/admin/trends`

The bucketed series behind the tile charts. The local dashboard draws these charts from it:

| Tile | Section 1 (no bounds — all history) | Section 2 (`start`/`end` = the filter window; **time filter only**) | Field(s) |
| --- | --- | --- | --- |
| `machine_utility_pct` | yes | yes | `utilityPct` |
| `production_qty_kg` | yes — bars `productionKg`, line `tonnageEnd` | no — Section 2 draws `metals[]` as one bar per item | `productionKg`, `tonnageEnd` |
| `energy_kwh_total` | yes | no — Section 2 draws `section2.cycles[].energyKwh`, one bar per cycle | `energyKwh` |
| `blast_time_sec` | yes | yes | `blastOnSec` |
| `cycle_count` | yes | yes | `cycleCount` |

`energy_per_casting_kwh_kg` has **no chart in either section** — kWh/kg varies by thousandths across
a bucket, so any axis fitted to it turns rounding into an apparent trend. (`efficiencyKwhPerKg` is
still returned; the local dashboard does not plot it.) Under a cycle or item filter no Section 2
tile uses this endpoint: those filters carry a placeholder time window.

Exactly the local dashboard's `/api/trends` — same rollup logic, same query params — put behind
`AdminGuardMiddleware` instead of JWT so the cloud can reach it. **Pass `bucket=auto` to match the
local dashboard**: the gateway picks the granularity (≤ 2 days of window ⇒ `hour`; otherwise ≤ 400
days of recorded history ⇒ `day`, else `month`) and returns its choice in the `X-Trend-Bucket`
response header, for titling the axis. Only the gateway knows how much history exists, so do not
choose the bucket client-side. The default is still `day`, so existing callers are unaffected.
`Live()` intentionally does **not**
carry this data: it changes at most once a minute (the `AggregationService` cadence) and the
payload is comparatively large, so folding it into every `Live()` poll would be constant waste for
data that's almost always unchanged since the last poll. Call this once per dashboard load, or on
its own slow timer — not on `Live()`'s poll cadence.

| Item | Value |
| --- | --- |
| Method / path | `GET /api/admin/trends` |
| Query params | `bucket` = `auto` \| `hour` \| `day` \| `month` (default `day`); `start`, `end` — ISO 8601, optional except `bucket=hour` which requires both |
| Response header | `X-Trend-Bucket: hour` \| `day` \| `month` — the bucket actually used (useful with `auto`) |
| No bounds | Returns the full all-time series at the requested bucket size. Pass `bucket=month` with no `start`/`end` for the compact whole-history series |
| **Gap-filled** | **Behaviour change.** Every bucket in the range is now returned, including buckets with no underlying data (all numeric fields `0`; `tonnageEnd` carried forward from the last known reading). Previously only buckets that had data were returned. A consumer that plots the array in order now gets a series where equal spacing means equal elapsed time; a consumer that COUNTS entries will see more of them for the same range, and one that treats every entry as "a day the plant ran" must now check `machineOnSec > 0` |
| Validation errors | `400 {"error": "..."}` — `start` ≥ `end`, invalid `bucket`, or `bucket=hour` missing a bound |
| Server failure | `500 {"error": "trends query failed"}` |
| Timestamps | `day` is UTC like everything else. Buckets are gateway-local calendar days/months, so a day bucket starts at local midnight — `18:30Z` the previous day in IST. Label buckets in the gateway's local time |

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
| `impeller_current` | `section2.amps[]` (tile averages) and the per-cycle points served by `GET /api/admin/filter/{id}/amps` |

Unselected parameters are **never computed** — they are absent from `results[]`, and `cycles[]` is
empty if nothing cycle-derived was selected. This is not an error condition.

**Validation errors** (`400 {"error": "..."}`): invalid/missing `filterBy`; `time` mode with
`filterStart` ≥ `filterEnd`; `cycle` mode missing either bound or `From` > `To`; `metal` mode with a
blank/missing `filterMetalName`; an unrecognised entry in `selectedParameters` (the response carries
`unknown` and `supported` arrays).

**Response:** `200`, identical shape to `Live().section2` (see above) — `requestId`, `filterBy`,
`filterStart`/`filterEnd`, `periodLabel`, `filterCycleFrom`/`filterCycleTo`, `filterMetalName`,
`processedAt`, `selectedParameters`, `results[]`, `cycles[]`, `metals[]`, `amps[]`. **`shotsBreakdown[]` is no longer part of this
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

# `GET /api/admin/amps/by-cycle`

One impeller's average current for **every** completed cycle — the chart that opens from a live
impeller tile. Same service as the local dashboard's `/api/amps/by-cycle`. Plot every point: the
x-axis is the cycle number (a numeric axis, not time), one point per cycle.

| Item | Value |
| --- | --- |
| Query | `impeller` — integer 1–10, required |
| Validation error | `400 {"error": "impeller must be between 1 and 10"}` |
| Server failure | `500 {"error": "per-cycle amps query failed"}` |

Response: JSON array ordered by `cycleNumber` ascending.

| Field | JSON type | Description |
| --- | --- | --- |
| `cycleNumber` | number (integer) | Global cycle number |
| `blastEnd` | string (timestamp) | When the cycle ended |
| `avgAmps` | number, nullable | Average current inside that cycle's blast window. `null` when the cycle has no recorded sample |

---

# `GET /api/admin/filter/{id}/amps`

One filtered calculation's impeller current, including the per-cycle points behind each
"Impeller Current (Filtered)" tile's chart. `{id}` is a `requestId` — from `Live().section2` or from
a `POST /api/admin/filter` response. Same service as the local `/api/filter/{id}/amps`.

| Item | Value |
| --- | --- |
| Unknown or unselected request | `200 []` — nothing was computed for it |
| Server failure | `500 {"error": "filtered amps query failed"}` |

Response: JSON array, one entry per impeller, ordered by `impellerNumber`.

| Field | JSON type | Description |
| --- | --- | --- |
| `impellerNumber` | number (integer) | 1–10 |
| `overallAvgAmps` | number, nullable | Same value as `section2.amps[].overallAvgAmps` |
| `cycles[]` | array | One point per in-scope cycle, ordered by `cycleNumber`: `{ cycleNumber, blastEnd, avgAmps }`, `avgAmps` 2 dp and nullable. Plot **all** of them — the tile averages every one |

---

# `GET /api/admin/history`

Raw recorded readings of **one tag** from `plc_historical_data`, oldest first, in pages. Use it for
detail the other endpoints do not carry, such as one tag's readings across a day. These are raw tag
readings, not calculated parameters.

| Item | Value |
| --- | --- |
| Method / path | `GET /api/admin/history` |
| `metric` | **Required.** The tag name exactly as recorded, e.g. `Tonnage`, `Blast ON/OFF`, `Current_imp_3`. URL-encode spaces and `/` |
| `from`, `to` | ISO 8601, e.g. `2026-05-01T00:00:00`. **Gateway-local time, not UTC** (see below). Both ends are included. Always send both: a missing one defaults to year 1 and the result is empty |
| `limit` | Rows per page. Default `5000`; clamped to `1`…`20000` |
| `offset` | Rows to skip. Default `0`. Page by adding `limit` to `offset` until `count < limit` |
| Missing `metric` | `400` in ASP.NET's validation-problem shape — `{"title": "One or more validation errors occurred.", "status": 400, "errors": {"metric": ["The metric field is required."]}}` — **not** the `{"error": …}` shape used elsewhere |
| Server failure | `500 {"error": "history query failed"}` |

**Timestamps — the one exception to the UTC rule.** `from` and `to` are compared directly with the
stored wall-clock times, and `from`, `to` and every `points[].timestamp` come back in the gateway
server's local time with **no `Z`** (at this site India Standard Time, UTC+05:30 — subtract 5 h 30 min
for UTC). Do not add a `Z` to these values.

Response (real output, `metric=Tonnage&from=2026-05-01T00:00:00&to=2026-05-15T00:00:00&limit=2`):

```json
{
  "metric": "Tonnage",
  "from": "2026-05-01T00:00:00",
  "to": "2026-05-15T00:00:00",
  "count": 2,
  "limit": 2,
  "offset": 0,
  "points": [
    { "value": "0",    "timestamp": "2026-05-05T15:22:38.10097",  "reason": "PERIODIC" },
    { "value": "4225", "timestamp": "2026-05-05T15:24:19.479489", "reason": "PERIODIC" }
  ]
}
```

| Field | JSON type | Description |
| --- | --- | --- |
| `metric`, `from`, `to`, `offset` | as sent | Echo of the request |
| `limit` | number (integer) | The page size actually used, after clamping |
| `count` | number (integer) | Rows in this page |
| `points[].value` | string, nullable | The reading as text (value-string convention: numbers as decimal text, BOOL as `"1"`/`"0"`, STRING as-is) |
| `points[].timestamp` | string | When the reading was stored — gateway-local time, no `Z` |
| `points[].reason` | string | Why it was stored, e.g. `COV` / `STATE_CHANGE` (value changed), `PERIODIC` (60 s heartbeat), `BLAST_ON` (per-second current during a blast), `DISCONNECT` (forced OFF when the PLC link dropped) |

---

# Licence check (gateway → Shot Sense cloud)

The one call in the other direction: the gateway asks the cloud whether its licence is valid. **The
cloud must provide this endpoint.** Until it exists, leave the gateway's `License:CheckUrl` empty —
the check is then off and the dashboard stays open.

| Item | Value |
| --- | --- |
| Request | `GET <License:CheckUrl>` — the full URL, exactly as set on the gateway |
| Header | `X-License-Key: <License:Key>` |
| Body | None. The response body is ignored too — `{"valid": true}` is plenty |
| Timeout | 20 seconds |
| When | At gateway start, then every 60 minutes (`License:CheckIntervalMinutes`); every 5 minutes while locked |

What the cloud answers, and what the gateway does:

| Cloud answers | Gateway does |
| --- | --- |
| Any `2xx` | Licence valid. Dashboard open; the 72-hour grace clock restarts |
| `401`, `402` or `403` | Key wrong, expired or revoked. **Dashboard locks at once** |
| Anything else, or nothing (timeout, DNS failure, `404`, `5xx`) | Counted as "could not reach". The dashboard stays open until 72 hours (`License:GraceHours`) after the last `2xx`, then locks |

Answer an unknown or revoked key with a real `401`/`403`. A `404` or `500` for a bad key would be read
as "could not reach" and would give that client 72 more hours.

A lock closes only the gateway's own dashboard. PLC recording and every calculation keep running, and
`/api/admin/*` stays open, so the cloud keeps receiving data while a client is locked.

---

## Sample

`sample-response.json` (repo root) is a full `GET /api/admin/live` response in exactly the shape
described above, with realistic dummy values — including `impellers.selected`, all 140 `spareGrid`
rows (all ten impellers selected), `amps` in impeller-number order, `ampsLastCycle`,
`shotsBreakdown[].intervalStartTimestamp`, and `section2.metals[]` / `amps[]` /
`selectedParameters` — usable
directly as a fixture in the cloud app with no live connection. It does not include sample responses
for `/api/admin/trends`, `/api/admin/filter` or `/api/admin/history` — the field tables above are
authoritative for those.
