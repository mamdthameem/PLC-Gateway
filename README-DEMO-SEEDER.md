# Demo data seeder

`PLCGateway.DemoSeeder` generates one month of realistic machine history so the dashboard can be
demonstrated with no PLC attached (expo, offline review, UI work).

It is a **separate console project**. The IIS publish targets `PLCGateway.csproj` only, so nothing
in this tool can run in normal operation.

---

## The rule it follows

**It writes raw PLC tag readings and nothing else.**

No KPI value is ever inserted. `plc_aggregation_state` is never written directly. Every lifetime
parameter, every graph series, every filter result is produced by the *real* calculation pipeline
folding over the seeded rows — `CalculationService.ComputeLifetimeParametersAsync` with its
`FoldBlast` / `FoldMachine` / `FoldRefillAsync` accumulators, `DatabaseService.UpsertDailyTrendsAsync`
for the rollup, and `ComputeFilteredParametersAsync` for Section 2.

If a number looks wrong on the dashboard, it is wrong in the pipeline. That is the point.

### The two places this could not hold, and why


| Table              | Why the live service cannot build it                                                                                                                                                                                | What the seeder does instead                                                                                                                                                                                                                                                             |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `plc_cycles`       | `CycleTrackingService` reads casting names/weights/tonnage **from Tier 1** at cycle close, and Tier 1 only ever holds *now*. Replayed over backdated history it would stamp every cycle with the same final values. | Calls the production `DatabaseService.InsertCycleAsync` once per cycle with the values that were true at that moment. `production_kg` (tonnage delta) and `energy_kwh` (`Σ AVG(Current_imp_N) × duration_h`, read back out of the seeded amp rows) are still computed by production SQL. |
| `plc_spare_status` | `SpareMonitoringService` early-returns while `PlcConnectionState` reports no PLC — with no PLC it never writes at all.                                                                                              | Feeds the same values into the same production method, `DatabaseService.UpsertSpareStatusAsync`. Only the polling loop is replaced.                                                                                                                                                      |


Everything else — `plc_lifetime_parameters`, `plc_aggregation_state`, `plc_shots_breakdown`,
`plc_daily_trends`, and all four `plc_filtered_*` tables — is produced entirely by the running
pipeline.

---



## Safety

- **Separate database.** Defaults to `sreesakthi_gateway_demo`. The target database name must end
in `_demo` or the tool refuses to run. No `is_synthetic` column was added, because that would
mean altering the append-forever production schema.
- **Refuses a non-empty target.** `seed` aborts if `plc_historical_data` or `plc_cycles` already
holds rows. `--force` wipes first.
- `--force` **is required** to override either guard.
- **One-command wipe.** `wipe` truncates every seeded table and resets `plc_aggregation_state`
(including `total_refill_weight_kg`) and `gateway_status`.
- **Deterministic.** `--seed 20260905` by default; the same seed reproduces the same dataset.

The never-delete rule for `plc_historical_data` is untouched: it applies to the production
gateway database, which this tool cannot write to without `--force` on a deliberately misspelled
connection string.

---



## Commands

```bash
# Generate the month, then run the real pipeline and print the verification report
dotnet run --project PLCGateway.DemoSeeder -- seed

# Re-seed over an existing dataset (wipes first)
dotnet run --project PLCGateway.DemoSeeder -- seed --force

# Purge everything and reset the aggregation state
dotnet run --project PLCGateway.DemoSeeder -- wipe

# Re-run the KPI report against whatever is already seeded
dotnet run --project PLCGateway.DemoSeeder -- verify
```


| Flag                   | Default                                | Meaning                                                  |
| ---------------------- | -------------------------------------- | -------------------------------------------------------- |
| `--db <conn>`          | `sreesakthi_gateway_demo` on localhost | Target database                                          |
| `--seed <n>`           | `20260905`                             | RNG seed                                                 |
| `--amp-interval <sec>` | `5`                                    | `Current_imp_N` sampling interval                        |
| `--refill-heartbeats`  | off                                    | Emit 60 s heartbeats on `Refil shots weight` (see below) |
| `--force`              | off                                    | Override the `_demo` and non-empty guards                |


---



## Running the dashboard against the seeded data

```bash
dotnet run --project PLCGateway -c Release --no-launch-profile -- --environment Demo --urls http://localhost:5210
```

Then open <http://localhost:5210> and log in with `admin` / `admin123`.

> ⚠️ **The `--` separator is mandatory.** Without it `dotnet run` swallows `--environment Demo`
> instead of passing it to the app: `appsettings.Demo.json` is never loaded, so the app starts
> against the **production** `sreesakthi_gateway` database with the PLC scan loop running. It fails
> silently — the app starts and serves pages, just from the wrong data. `--no-launch-profile` is
> needed too, because `launchSettings.json` otherwise forces `ASPNETCORE_ENVIRONMENT=Development`.
>
> The environment-variable form is equivalent, if you prefer it (PowerShell:
> `$env:ASPNETCORE_ENVIRONMENT='Demo'` first).

`appsettings.Demo.json` sets `Demo:Enabled = true`, which skips **only** the PLC scan loop and the
startup gap handler. Without that, a failing PLC connection would mark every Tier 1 row stale,
force machine status OFF, and write `DISCONNECT` rows over the seeded history — the dashboard would
show a "PLC Disconnected" chip and staleness banners on the amps and spare panels.

Everything else runs unchanged: aggregation every minute, cycle tracking, the filtered-calculation
processor, the whole API.

> ⚠️ The connection string lives under **two** keys — `PostgreSQL:ConnectionString` (read by
> `DatabaseService`) and `ConnectionStrings:PostgresDb` (read by every API service). Setting only
> one silently splits the app across two databases. `appsettings.Demo.json` sets both.

> ⚠️ Never deploy `appsettings.Demo.json` to a real gateway.

---



## What gets written

**Directly by the seeder (raw tags only):**


| Table                 | Columns                                                                                                                                         |
| --------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- |
| `plc_historical_data` | `address, parameter_name, value (NULL — stays frozen), value_num, value_bool, value_text, data_type, storage_reason, timestamp, previous_value` |
| `plc_current_values`  | `address, parameter_name, value (NULL), value_num, value_bool, value_text, data_type, last_updated, last_stored_historical, is_stale (FALSE)`   |
| `gateway_status`      | `plc_connected, changed_at, last_scan_at`                                                                                                       |


`storage_reason` values used, exactly as the poller emits them: `INITIAL`, `COV`, `STATE_CHANGE`,
`VALUE_CHANGE`, `HEARTBEAT`, `BLAST_ON`.

**Via production methods:** `plc_cycles` (`InsertCycleAsync`), `plc_spare_status`
(`UpsertSpareStatusAsync`), `calculation_requests` (`InsertClaimedRequestAsync`, verify step only).

**Never written by the seeder:** `plc_lifetime_parameters`, `plc_aggregation_state`,
`plc_shots_breakdown`, `plc_daily_trends`, `plc_filtered_parameters`, `plc_filtered_cycle_data`,
`plc_filtered_metal_production`, `plc_filtered_amps_data`.

---



## The profile

One month ending on the last completed shift boundary, treated as the machine's **first month of
recording** — so lifetime totals and the all-time graphs cover exactly this window.

- 30 calendar days, Sundays off, one planned maintenance half-day (morning shift dark)
- Two shifts, 06:00–14:00 and 14:00–22:00; 25–32 cycles per shift, varying daily
- 8–12 min blast, 2.5–4.3 min load/unload; 3 poor days with stretched changeovers
- 4 fault events of 10–40 min — machine **powered but not blasting**, so they cost utility, which
is what a real fault does
- Reblasts are DISABLED (`ReblastProbability = 0`). The model still supports them — a reblast is a
  real second pass with its own rising edge that spends energy and declares nothing — but in a demo
  they made `cycle_count` disagree with the number of loads actually cast, so every reading of the
  Blast Cycles tile needed a caveat. Raise the constant to model a plant that reblasts.
- 4 casting parts — Brake Drum 120 kg, Pump Casing 180 kg, Gear Housing 260 kg, Flywheel 340 kg.
30 % of days run a single part, so the item filter has clean days to show
- Shot consumption ~4.2–6.0 kg per tonne cast; refills of 100–180 kg triggered when the hopper
runs low, never on a schedule
- Impeller current ramps from a true 0 A at blast start to a ~20 A plateau over 20 s, fluctuates in
  a steady band for the rest of the blast, and returns to 0 A after it, drifting up ~6 % over
each blade life and stepping down at replacement

**Measured vs declared is deliberately not identical.** The `Tonnage` accumulator advances by the
measured load weight (nominal ±3 %) while the casting slots declare the nominal weight, so any
individual cycle's measured and declared figures disagree by up to 3 %. Across a full month the
symmetric spread averages out and the two totals land within ~0.01 % of each other — which is what
a well-run plant looks like, and still exercises the documented split rather than papering over it.
Widen `MeasuredWeightSpread`, or make it asymmetric, if you want a visible aggregate gap.

---



## Deliberate deviations from true poller output

All three are volume decisions. None of them changes a KPI.


| Stream                            | Poller                                      | Seeder                                         | Why                                                                                                                                                                                                                                                        |
| --------------------------------- | ------------------------------------------- | ---------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Current_imp_N`                   | 1 Hz while blasting (~8.6 M rows/month)     | every 5 s (~1.7 M)                             | Every consumer is an `AVG()` — per-cycle energy, the Section 2 amps panel — so the sample rate only changes how densely the per-cycle amps trace is drawn. 5 s still gives ~120 points across a 10-minute blast. Use `--amp-interval 1` for full fidelity. |
| `Casting metal N name` / `weight` | 60 s heartbeat (~200 k rows/month)          | change events only (~3 k)                      | Nothing reads these from Tier 2; cycles read Tier 1.                                                                                                                                                                                                       |
| `Spares_Runhour_impN[M]`          | 0.1 h absolute deadband (~339 k rows/month) | hourly + every trigger/replaced change (~34 k) | Nothing reads these from Tier 2; `plc_spare_status` is built from Tier 1.                                                                                                                                                                                  |


**What is *not* optional: the 60 s heartbeat on** `Machine status`**,** `Blast ON/OFF` **and** `Tonnage`**.**
`plc_daily_trends` and the hourly trend query both treat any segment longer than 300 s as a
*recording gap* and credit only its first 5 minutes. An 8–12 minute blast with no intermediate row
would report as 5 minutes in every graph while the Section 1 scalar reported the full duration. The
verify step cross-checks the two totals agree within 1 % precisely to catch this.

---



## Findings

Four defects surfaced while building this. Two are in production code.

### 1. `InsertCycleAsync` always failed at runtime — **fixed**

`DatabaseService.InsertCycleAsync` computed:

```sql
ROUND( (...sum over impellers...) * (@duration_sec / 3600.0), 6)
```

`@duration_sec` is bound from a C# `double`, so Npgsql sends `float8`. In PostgreSQL
`numeric * float8` yields `float8`, and `round(double precision, integer) does not exist` — so
every call threw. `RetryAsync` retries three times, logs, and returns without rethrowing, so
`CycleTrackingService` recorded nothing and `plc_cycles` silently stayed empty.

It was masked because `migration.sql`'s backfill does the same arithmetic against the **NUMERIC**
`duration_sec` **column**, where it stays in `numeric` and works. That backfill is why the dev
database's five cycles have `energy_kwh` values despite the runtime path being broken.

Fixed by casting the duration term to `numeric`, which keeps the arithmetic in `numeric` throughout
and matches the backfill exactly. The seeder now also aborts if `InsertCycleAsync` returns no cycle
number, so a swallowed insert failure can never again look like a successful run.

### 2. `last_refill_epoch_sec` is pinned to the newest row of any kind — **not fixed, needs a decision**

`FoldRefillAsync` updates `LastRefillAnyTs` on **every** `Refil shots weight` event and only
*afterwards* filters to real change reasons:

```csharp
if (s.LastRefillAnyTs == null || ev.Timestamp > s.LastRefillAnyTs.Value)
    s.LastRefillAnyTs = ev.Timestamp;

if (!RefillChangeReasons.Contains(ev.StorageReason)) return;   // ← gate is below the update
```

`Refil shots weight` is in `DataCollection:HeartbeatTags`, so a live gateway writes a `HEARTBEAT`
row for it every 60 s. The "Last Refill" tile therefore shows **roughly now, forever**, instead of
the last actual refill — while `CLAUDE.md` documents it as "epoch of latest `Refil shots weight`
**change**".

The one-line fix is to move the `LastRefillAnyTs` update below the reason gate. That changes a
long-standing lifetime parameter, so it is your call, not mine.

**Meanwhile the seeder omits the heartbeat for that one tag**, so the demo tile shows the real last
refill. `--refill-heartbeats` reproduces the live-gateway behaviour if you want to see the defect.

### 3. Equal consecutive refill weights vanish — **guarded in the generator**

`Refil shots weight` carries the weight of the latest refill and is stored on change only (absolute
1 kg deadband). Two consecutive refills of the same weight produce **no row at all** — the refill
disappears from `effective_shots_usage_kg_per_ton`, from `avg_shot_refill_time_sec`, and from the
shots-breakdown chart.

This is a property of the PLC tag design, not a code defect, and it would bite a real machine
topping up 150 kg twice in a row. The generator forces consecutive refills at least 2 kg apart and
the verify step asserts no two are within the deadband.

### 4. Section 2 amps can exceed the default command timeout on a cold cache — **observed, not fixed**

`InsertFilteredAmpsDataAsync` sets no `CommandTimeout`, so it inherits Npgsql's 30 s default. On the
first filter run against a freshly bulk-loaded month it timed out **twice** before succeeding on the
third attempt; `RetryAsync` swallowed both, so the only symptom was a slow filter.

The plan is not the problem — it is an index-only scan with zero heap fetches. It was purely cold
disk reads. Warm, the same aggregation across **all** 1,491 cycles × 10 impellers runs in **781 ms**.

This is a measured answer to the open question in `CLAUDE.md` ("the metal-filter latency number in
that doc is a structural argument, not a measured one … re-benchmark before the cloud finalizes a
hard timeout"): sub-second warm, but capable of blowing a 30 s budget cold. A cloud-side hard
timeout should account for the cold case, and an explicit `CommandTimeout` on that call would turn
a silent triple-retry into an honest error.

**Storage, measured:** 1.84 M rows = **428 MB** (206 MB heap + 223 MB indexes) for one month at 5 s
amp sampling. At the poller's true 1 Hz that month would be ~8.6 M rows — roughly 2 GB/month, or
~24 GB/year, which is the concrete number behind the partitioning recommendation in the README.

### 5. `ResetAggregationStateAsync` does not reset `total_refill_weight_kg` — **not fixed**

`--rebuild-aggregation` zeroes every accumulator except `total_refill_weight_kg`, so a rebuild
double-counts refill weight and `effective_shots_usage_kg_per_ton` roughly doubles. The seeder's
own `wipe` resets it explicitly. The production one-line fix is to add the column to that `UPDATE`.

---



## Verification

`seed` finishes by running `verify`, which prints:

- all ten Section 1 parameters, formatted the way their tiles render them
- `plc_aggregation_state` — the watermark and accumulators, proving the fold ran rather than an insert
- table counts, raw row mix by `storage_reason`, and the full `plc_daily_trends` rollup
- the shots-breakdown table and a spare-health summary
- a Section 2 one-week time filter and a single-item filter, with parameters, per-item split,
cycle breakdown and amps rows
- **cross-checks**: `cycle_count` vs `COUNT(plc_cycles)`; lifetime `blast_time_sec` vs the rollup's
sum within 1 %; every refill COV row folded; no refill swallowed by the deadband; shots-breakdown
row count equals refills − 1; no future-dated rows; `plc_historical_data` id order matches
timestamp order (the incremental fold reads `ORDER BY id` but measures durations from timestamps,
so an inversion would corrupt every total)
- **sanity bands**, printed as PASS/FAIL: shot usage 3–8 kg/T, utility 62–80 %, energy per casting
0.15–0.30 kWh/kg

A value outside its band prints `FAIL`. It is never smoothed over.