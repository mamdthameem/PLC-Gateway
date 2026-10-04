The client page in this app mirrors the Shot Sense gateway dashboard of one client. It pulls everything from the gateway's `/api/admin/*` API. It must look and read **exactly like the gateway's own dashboard**, and at the moment it doesn't. The numbers already match: every value we show equals the gateway's. What differs is layout, tile names, number formats, charts, some items only we have, and a few things we couldn't show until the gateway added new API fields.

Make the changes below. First, find where the client page is built and list the files you'll change. Then work through the sections in order. When you're done, report what you changed, anything you couldn't do, and why.

## Ground rules

- **Show values exactly as the API sends them.** Formatting is allowed (units, decimals, thousands separators, seconds shown as `23h 32m`). Recalculating, re-deriving or re-rounding anything is not.
- **API timestamps are UTC ISO strings.** Show them in plant time, IST (UTC+05:30).
- **The new API fields below exist only once the gateway is updated.** Until then they're missing. If a field is missing, hide the piece that needs it. Never let the page break.
- Date-time format everywhere: `DD/MM/YYYY, HH:MM:SS`, 24-hour (e.g. `14/05/2026, 15:29:33`).
- **Tile timestamp rule** (every tile that shows one): time only when it's today (`15:29:33`); otherwise put the date in front (`14 May, 15:29:33`), adding the year when it isn't this year.

## 1. New API fields (all additive; nothing was renamed or removed)

`GET /api/admin/live`:
- `ampsLastCycle[]`: `{ parameterName: "Current_imp_1", value: "13.8", lastUpdated }`. It holds each impeller's average current over the last completed cycle. **Match entries to `amps[]` by `parameterName`, not by position.** An impeller with no sample in that cycle is absent.
- `shotsBreakdown[]` gains `intervalStartTimestamp` (UTC, nullable). It's the refill that OPENED the interval; `refillTimestamp` is the refill that CLOSED it.
- `section2` gains `selectedParameters` (`string[] | null`, where `null` means all were computed) and `amps[]`: `{ impellerNumber: 1, overallAvgAmps: 10.24 | null }`.

`POST /api/admin/filter`: the response gains the same `selectedParameters` and `amps[]`. The request already accepts `selectedParameters: string[]`. The valid keys are `machine_utility_pct`, `production_qty_kg`, `energy_kwh_total`, `energy_per_casting_kwh_kg`, `blast_time_sec`, `cycle_count` and `impeller_current`.

New endpoints (same `X-Api-Key` auth):
- `GET /api/admin/amps/by-cycle?impeller=N` → `[{ cycleNumber, blastEnd, avgAmps | null }]`: one impeller's average current for every completed cycle.
- `GET /api/admin/filter/{requestId}/amps` → `[{ impellerNumber, overallAvgAmps, cycles: [{ cycleNumber, blastEnd, avgAmps | null }] }]`: the filtered impeller current, including the per-cycle chart points.

`GET /api/admin/trends` now accepts `bucket=auto`. The gateway picks `hour`, `day` or `month` and returns its choice in the `X-Trend-Bucket` response header. If our calls go through a Cloud Function, pass that header, or its value, through to the page. Always use `bucket=auto`; never choose the bucket ourselves.

## 2. Remove

- The **"Cycles Since Refill"** tile. It showed the cycles run *before* the last refill, not since.
- The **"Lower is better"** caption on Effective Shots Usage.
- The **page-wide "PLC disconnected" banner** and the separate **"PLC Link"** tile. The Machine Status tile carries this now (section 4).
- The **spare-parts alert summary banner** ("4 active maintenance alerts: …").
- The **blue note above the filter** ("Applying a filter here also updates the 'Latest Filtered Calculation'…"). It's no longer true.
- The **"Latest Filtered Calculation"** heading, its period chip and the "Request #… · computed …" line.
- The two-column **"Parameter / Value" table** after a filter. It only repeats the tiles.
- The **"Cycle Breakdown" line chart** (energy and production on two axes, smoothed curves).

**Keep** the client header (name, status, URL, pulled time, Auto 30s, raw-JSON toggle, Refresh), **Historical Data** and **Recent Events**. Move Historical Data and Recent Events to the very end of the page.

## 3. Page order (top to bottom, below the client header)

1. Machine Status (one tile)
2. Lifetime Parameters (9 tiles), then the Blast Cycles per Refill Interval chart
3. Impeller Current
4. Spare Part Life
5. Filters
6. Filtered Parameters (6 tiles), followed, only after a filter is applied, by Production by Item, Cycle Log and Impeller Current (Filtered)
7. Historical Data, Recent Events

## 4. Machine Status

One tile titled "Machine Status" with a chip showing one of three states:
- **Running**: `machineStatus.running` is true. Green chip, `#22C55E`.
- **Loading**: not running, and `plcConnected` is true. Red chip, `#EF4444`. This is the normal state between batches.
- **Stopped**: `plcConnected` is false. Red chip, `#EF4444`.

When `plcConnected` is false, also show an amber outlined chip "PLC Disconnected" next to the state chip. Its tooltip reads `Last successful PLC scan: <lastScanAt>`. Under the chips goes a small amber line: "Machine reported OFF because the gateway cannot reach the PLC. Recording continues." The bottom of the tile shows `machineStatus.lastUpdated`, using the tile timestamp rule.

## 5. Lifetime Parameters

The heading is "Lifetime Parameters", with no subtitle. On the right: "Last updated HH:MM:SS" and a refresh icon. The tiles go four per row on wide screens, **in exactly this order** (the API sends them alphabetically, so sort them yourself):

| Tile name | API key | Format | Example | Chart |
|---|---|---|---|---|
| Machine Utility | `machine_utility_pct` | 2 decimals + ` %` | `100.00 %` | yes |
| Production (Tonnage) | `production_qty_kg` | thousands separator, 2 decimals + ` kg` | `214.00 kg` | yes |
| Total Energy | `energy_kwh_total` | thousands separator, 1 decimal + ` kWh` | `8,840.6 kWh` | yes |
| Energy per Casting | `energy_per_casting_kwh_kg` | 4 decimals + ` kWh/kg` | `41.3112 kWh/kg` | **no** |
| Blast Time | `blast_time_sec` | seconds → `Xh Ym`; `Xd Yh Zm` from 1 day; `Z min` under 1 h | `23h 32m` | yes |
| Blast Cycles | `cycle_count` | whole number, thousands separator, no unit | `5` | yes |
| Avg Shot Refill Time | `avg_shot_refill_time_sec` | same as Blast Time | `42d 23h 49m` | no |
| Last Shot Refill | `last_refill_epoch_sec` | Unix seconds → `DD/MM/YYYY, HH:MM:SS` | `14/05/2026, 14:04:31` | no |
| Effective Shots Usage | `effective_shots_usage_kg_per_ton` | 4 decimals + ` kg/T`; `—` when empty | `2803.7383 kg/T` | no |

- `machine_status` is not a tile here; it feeds section 4.
- Each tile shows the name (small, grey), the value (large, bold) and `updatedAt` underneath, using the timestamp rule.
- A tile with a chart shows a small bar-chart icon top-right, and **the whole tile is clickable**. It opens a wide dialog titled with the tile's name.

## 6. Tile charts (lifetime)

The data comes from `GET /api/admin/trends?bucket=auto` with no `start`/`end`, which returns all history. The X-axis title is "Date". Above each chart goes a one-line caption like `21 Jan 2026 to 14 May 2026 · Daily`, where the word follows `X-Trend-Bucket`: Hourly, Daily or Monthly.

| Tile | Chart | Y-axis title |
|---|---|---|
| Machine Utility | line of `utilityPct` | Machine Utility (%) |
| Production (Tonnage) | bars of `productionKg` + a line of `tonnageEnd` (running total) | Daily Production (kg); "Daily" follows the bucket |
| Total Energy | bars of `energyKwh` | Energy (kWh) |
| Blast Time | bars of `blastOnSec / 3600` | Blast Time (hours) |
| Blast Cycles | bars of `cycleCount` | Blast Cycles |

**Energy per Casting has no chart, on purpose.** Its value barely moves, so a chart turns rounding noise into a fake trend.

**Rules for every chart on the page:**
- Both axes have a title.
- The Y axis starts at 0 with round steps; counts get whole-number ticks (never 0.75).
- Plot every point and never truncate.
- Label every point when the labels fit; otherwise label every Nth point at a fixed interval.
- Straight lines between points, never smoothed or monotone curves.
- No target or reference lines.
- Charts open in a wide dialog, about 1,200 px wide and 420 px tall.

## 7. Blast Cycles per Refill Interval

This goes directly under the lifetime tiles. The heading is "Blast Cycles per Refill Interval", with the caption "Cycles completed between consecutive refills."
- One bar per `shotsBreakdown` row; the bar height is `blastCount`.
- Bar labels are the date only (`14 May`). Add the time only where two bars share a date (`14 May 14:01`).
- The X-axis title is "Refill Date" and the Y-axis title is "Blast Cycles", with whole-number ticks.
- The tooltip reads `<intervalStartTimestamp> to <refillTimestamp> (<days, 1 decimal> days)`, then `Blast Cycles: N`. If `intervalStartTimestamp` is null, show `interval ending <refillTimestamp> (no earlier refill recorded)`.
- Each bar is a **finished** interval that ends at its refill. The interval running now has no bar. Never present the last bar as "cycles since the last refill".

## 8. Impeller Current

The heading is "Impeller Current". Keep our one-line "Showing impellers 1, 2, 3, …" note under it; the gateway has selector buttons there, which we can't offer.
- When `plcConnected` is false, show an amber warning inside this section: `PLC disconnected. Showing the last values read at <lastScanAt>. These are not live.`
- **Five tiles per row**, fixed width of about 200 px, centred as a block, so 9 impellers sit as 5 + 4.
- Each tile shows "Impeller N", a chart icon, and the value to 2 decimals: `14.00 A`.
- Live value ≥ 1 A: the value is blue, with the timestamp underneath.
- Live value < 1 A: the value is grey. Underneath, show `ran at 13.8 A` (from `ampsLastCycle`, 1 decimal) plus a small "Idle" chip. The headline always stays the live reading.
- Clicking a tile opens a dialog titled `Impeller N: Average Current per Cycle (A)`. It's a line chart from `GET /api/admin/amps/by-cycle?impeller=N`. X is "Cycle number" on a numeric axis with round ticks (100, 200, 300…). Y is "Average current (A)". Plot every cycle and skip null points.

## 9. Spare Part Life

The heading is "Spare Part Life", with the caption "Run hours / replacement limit". Rename it from "Spare Parts Health".
- When `plcConnected` is false, show an amber warning: `PLC disconnected. Run hours below are the last values read at <lastScanAt> and are not advancing.`
- The columns are "Spare Part", then "Impeller 1", "Impeller 2", … (not "IMP 1") for each impeller present in `spareGrid`.
- Cells read `0.0 hrs / 2,000.0 hrs` (1 decimal, thousands separator, "hrs"). When `thresholdHours` is 0, show the run hours only: `0.0 hrs`.
- `triggerActive` → red cell, bold text, small red "!" chip. `lastReplacedAt` set and not triggered → small green "✓" chip.
- Centre the table, cap its width at about 190 px per column, and let it shrink to fit. No sideways scroll on a desktop screen.

## 10. Filters

The heading is "Filters". The tabs are "Time Range", "Cycle Range" and **"Item"** (not "Metal").

Time Range buttons, with windows in plant time (IST):
- Hour: last 60 min to now (`periodLabel: "hour"`)
- Shift: last 8 h to now (`"shift"`)
- **Yesterday**, renamed from "Day": the whole previous calendar day, 00:00–24:00 IST (`"day"`). **Selected by default.** Applied on 19 Sep 2026, it sends `2026-09-17T18:30:00Z` → `2026-09-18T18:30:00Z`.
- Week: last 7 days to now (`"week"`)
- Month: same date last month to now (`"month"`)
- Year: same date last year to now (`"year"`)
- Custom: "Start" and "End" date-time pickers, with no `periodLabel`.

Cycle Range: number fields "Cycle From" and "Cycle To".

Item: a text field "Item Name" with the placeholder "e.g. Aluminium" and this caption under it: "Matches the declared casting item name exactly. Every selected parameter is then computed from only the cycles that declared it." It sends `filterBy: "metal"` with `filterMetalName`.

**New Parameters panel** on the right: the title "Parameters" plus a count chip (`7/7`), then checkboxes that are all ticked on load, in this order:
- Machine Utility (`machine_utility_pct`): unticked and disabled on the Item tab, where the count shows `x/6`
- Production (Item Weight) (`production_qty_kg`)
- Total Energy (`energy_kwh_total`)
- Energy per Casting (`energy_per_casting_kwh_kg`)
- Blast Time (`blast_time_sec`)
- Blast Cycles (`cycle_count`)
- Impeller Current (`impeller_current`)

"Select All" and "Clear All" buttons go under the list. Send the ticked keys as `selectedParameters`.

Apply and Reset:
- "Apply Filter" is disabled when nothing is ticked. While waiting it reads "Calculating…", with a progress bar and "Calculating, please wait…".
- Once a filter is applied, show a chip with its name ("Yesterday", "Year", "Cycles 1–104", "Item: Aluminium" or "Custom range") and a "Reset" button that returns to the unfiltered view.
- Messages: "Enter both cycle from and to numbers." · "Cycle From must be ≤ Cycle To." · "Enter an item name." · "Calculation failed. Please try again."

## 11. Filtered Parameters

**Before a filter is applied (page load, and after Reset):**
- The heading is "Filtered Parameters", with the subtitle "No filter applied".
- Show six tiles with the **lifetime values** from `/live` (same names, formats, timestamps and charts as sections 5–6): Machine Utility, Production (Tonnage), Total Energy, Energy per Casting, Blast Time, Blast Cycles.
- **Do not show `/live.section2` anywhere, and do not send a filter on page load.** Nothing is calculated until Apply is pressed.

**After Apply (use the `POST /api/admin/filter` response):**
- The heading stays "Filtered Parameters". Under it goes the time window (`19/09/2025, 10:53:42 to 19/09/2026, 10:53:42`), or, for the cycle and item filters, the filter's name.
- Under an item filter, add an outlined chip "Casting item: <name>" and this line: "Every value below is computed from only the cycles that declared this item. Machine Utility is not shown: machine on-time is not attributable to a single item."
- An "Export" button on the right downloads an .xlsx file with three sheets: "Parameters" (name, formatted value), "Item Production" (Item, Weight (kg)) and "Cycles" (the Cycle Log columns, with each item's name and kg in separate columns). Do this last if it's a big job.
- Tiles come from `results[]` in the section 5 order. The names are the same except production, which is **"Production (Item Weight)"**. These tiles have no timestamps.

Charts for these tiles:
- Machine Utility: line, `/trends?bucket=auto&start=<filterStart>&end=<filterEnd>`. **Time filter only.**
- Production (Item Weight): dialog titled "Production per Casting Item", one bar per `metals[]` entry. X is "Casting item", Y is "Declared weight (kg)". All filters.
- Total Energy: dialog titled "Energy per Cycle", one bar per `cycles[]` entry (a line when there are more than 120). X is "Cycle number" (numeric), Y is "Energy (kWh)". All filters.
- Energy per Casting: no chart.
- Blast Time: dialog titled "Blast Time per Interval", bars of `blastOnSec / 3600` from `/trends` over the filter window. Time filter only.
- Blast Cycles: dialog titled "Blast Cycles per Interval", bars of `cycleCount` from `/trends` over the filter window. Time filter only.

## 12. Tables after a filter

Show these only when their parameters were selected (`selectedParameters` null means all):
- **Production by Item**: if `production_qty_kg` was selected.
- **Cycle Log**: if any of `energy_kwh_total`, `energy_per_casting_kwh_kg`, `blast_time_sec`, `cycle_count` or `impeller_current` was selected.
- **Impeller Current (Filtered)**: if `impeller_current` was selected.

**Production by Item** (not "metal"):
- The columns are "Item" and "Weight (kg)", in `metals[]` order.
- Weights use a thousands separator and 2 decimals: `1,954.00`. Show "unspecified" as an outlined chip.
- The last row is a bold "Total" showing the Production (Item Weight) value (`4,790.00`).
- Empty: "No casting item weights were declared for the cycles in this filter."

**Cycle Log** (renamed from "Cycle Breakdown"):
- The heading is "Cycle Log", with an outlined chip "5 cycles".
- The columns are: Cycle No. · Start Time · End Time · Item 1 · Item 2 · Item 3 · Item 4 · **Tonnage Produced (kg)** · Energy (kWh).
- Item cells read `hkhl · 255.0 kg`. **A weight with a null name reads `unspecified · 144.0 kg`.** Show `—` only when both the name and the weight are empty.
- Tonnage Produced (`productionKg`) has 2 decimals (`1408.00`); Energy (`energyKwh`) has 3 (`140.688`).
- The table scrolls inside itself beyond about 520 px of height, with a sticky header.
- Empty: "No completed blast cycles fall within this filter."

**Impeller Current (Filtered)** (new):
- The same tile grid as section 8: 5 per row, centred.
- Each tile shows `section2.amps[].overallAvgAmps` to 2 decimals (`10.24 A`), with the caption "Filter average". When the value is null, show `—`.
- Clicking a tile opens a dialog titled `Impeller N: Filtered Current (A)`. It's a line chart from `GET /api/admin/filter/{requestId}/amps`: X is "Cycle number", Y is "Average current (A)", with every cycle plotted.

## 13. Check when done

- Every heading, tile name, tile order, format and value matches the gateway dashboard.
- On page load, Filtered Parameters shows "No filter applied" and the lifetime values, and no filter request was sent.
- Applying "Year" gives 5 blast cycles, 23h 32m, 4,790.00 kg, 8,840.6 kWh and 1.8456 kWh/kg, the same as the gateway once it's updated.
- With the PLC disconnected: Stopped + the "PLC Disconnected" chip, and old readings show their date.
- Nine impeller tiles sit as 5 + 4, and the spare table shows every impeller without sideways scroll.
- The refill chart has whole-number ticks, both axis titles, and date-only labels.
- Nothing from section 2 is still on the page.
