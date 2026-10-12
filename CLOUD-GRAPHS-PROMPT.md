The charts on the client page don't match the gateway dashboard's charts. Rebuild every chart so it matches the gateway exactly. Tiles, tile values and the filter already work, so leave them alone.

Don't work from screenshots. The gateway dashboard's chart code is listed below, so read it and copy its behaviour. The gateway dashboard is **React + MUI + Recharts**. If this app uses the same stack, reuse the components almost as-is and replace only their data fetching. The data shapes are identical: the admin API returns the same camelCase fields the gateway's own API does.

First, read the files in section 1 and list which chart components in this app you'll replace. Then work through section 3 chart by chart. When you're done, report what you changed and anything that didn't port cleanly.

**This corrects one rule from the earlier parity prompt.** It said "straight lines, never smoothed or monotone curves". That was wrong. The gateway draws every line with Recharts `type="monotone"`, so use exactly that. Never use `natural`, `basis` or `cardinal`: those curves swing past the data between points, which is how the old Cycle Breakdown chart dipped below zero.

## 1. The gateway's chart files

The repo is at `D:\PLCGateway`. All paths below are under `D:\PLCGateway\dashboard\src\`. If you're on the same machine, read them directly; otherwise I'll paste them.

**Shared code, which every chart uses:**

| File | What it does | Port how |
|---|---|---|
| `utils\chartAxis.ts` | All axis rules: `niceScale` / `niceScaleOf` (y from 0 in round steps), `numericTicks` (round ticks on a cycle axis), `tickInterval` + `categoryXAxis` (tilted date labels, every label up to 31 points, then strided), `Y_AXIS`, `xAxisTitle` / `yAxisTitle`, `CHART_HEIGHT = 420`, `CHART_MARGIN`, `BUCKET_ADJECTIVE` | Copy as-is. It has no gateway dependencies |
| `utils\trendBuckets.ts` | Axis label and tooltip text per bucket (`formatBucketLabel`, `formatBucketFull`) | Copy, then add `timeZone: 'Asia/Kolkata'` (section 2) |
| `utils\useTrendSeries.ts` | The one fetch-and-label hook behind every trend chart; returns `{ points, bucket, loading, error }` | Copy, swapping its fetch for ours |
| `services\trendsService.ts` | The gateway's own fetch of `/api/trends` | Replace with our call (section 2) |
| `components\TrendChartFrame.tsx` | Loading, error and empty states, plus the caption line `21 Jan 2026 to 14 May 2026 · Daily` | Copy |
| `components\ExpandableMetricCard.tsx` | A tile that opens its chart in a dialog: `maxWidth="lg"`, `fullWidth`, chart mounted only after the dialog finishes opening, title = tile name + info icon | Copy the dialog behaviour |
| `components\LifetimeSection.tsx` | The `GRAPHABLE` map: which Section 1 tile opens which chart, and each chart's info text | Read for the mapping |
| `components\FilterResultsView.tsx` | `GRAPHABLE_ALL_MODES`, `GRAPHABLE_TIME_ONLY` and `renderGraph()`: which Section 2 tile opens which chart, for which filter modes | Read for the mapping |
| `types\index.ts` | `DailyTrend`, `ShotsBreakdownEntry`, `CycleAmp`, `FilteredCycle`, `FilteredMetalProduction`, `FilteredAmps` | Copy the types; they match the admin API |

**The charts:**

| File | Chart |
|---|---|
| `components\UtilityGraph.tsx` | Machine Utility (Section 1, and Section 2 over the filter window) |
| `components\ProductionGraph.tsx` | Production (Tonnage), Section 1 only |
| `components\TrendMetricGraph.tsx` | Total Energy, Blast Time and Blast Cycles: one component, three `metric` values (Section 1, plus Blast Time and Blast Cycles in Section 2) |
| `components\ShotsBreakdownChart.tsx` | Blast Cycles per Refill Interval, shown in the page rather than in a dialog |
| `components\AmpsGraph.tsx` | A live impeller tile's chart. Also exports `IMPELLER_COLORS` |
| `components\AmpsPanel.tsx` | The dialog that hosts `AmpsGraph`, and its title |
| `components\CycleDataGraph.tsx` | Section 2 Total Energy: energy per cycle |
| `components\ItemProductionGraph.tsx` | Section 2 Production (Item Weight): one bar per item |
| `components\FilteredAmpsGraph.tsx` | A filtered impeller tile's chart |
| `components\FilteredAmpsPanel.tsx` | The dialog that hosts `FilteredAmpsGraph`, and its title |

## 2. Where each chart's data comes from on our side

This is the only real difference from the gateway. Its components call the gateway's own JWT-protected API, which we can't reach. Replace each fetch with:

| Gateway fetch (in its code) | Gateway endpoint | **Use instead** |
|---|---|---|
| `fetchTrends('auto', start, end)` | `GET /api/trends?bucket=auto&start=&end=` | `GET /api/admin/trends?bucket=auto&start=&end=`: the same rows, with the resolved bucket in the `X-Trend-Bucket` response header |
| `fetchShotsBreakdown()` | `GET /api/shotsbreakdown` | `GET /api/admin/live` → `shotsBreakdown[]` |
| `fetchPerCycleAmps(n)` | `GET /api/amps/by-cycle?impeller=n` | `GET /api/admin/amps/by-cycle?impeller=n` |
| `fetchFilterCycles(id)` | `GET /api/filter/{id}/cycles` | `cycles[]` from the `POST /api/admin/filter` response. No extra call |
| `fetchFilterMetals(id)` | `GET /api/filter/{id}/metals` | `metals[]` from the `POST /api/admin/filter` response. No extra call |
| `fetchFilterAmps(id)` | `GET /api/filter/{id}/amps` | `GET /api/admin/filter/{requestId}/amps` |

Every admin call carries the `X-Api-Key` header and must run on our server side (the Cloud Function), never in the browser, or the key leaks.

**Two things to handle on our side:**

1. **Forward the `X-Trend-Bucket` header.** If the Cloud Function returns only the JSON body, the header is lost and the page can't tell hourly, daily and monthly apart. Return it alongside the rows, e.g. `{ bucket, rows }`. Always request `bucket=auto`; never choose the bucket ourselves.
2. **Format every date in plant time.** The gateway's own API sends plant-local times without a `Z` (`2026-05-14T00:00:00`). The admin API sends the same instant in UTC (`2026-05-13T18:30:00Z`). The gateway formats with `toLocaleString()`, which uses the viewer's time zone. On our side that would label every day bucket a day early for anyone outside IST. Pass `timeZone: 'Asia/Kolkata'` to every date format in the charts:
   - `formatBucketLabel` and `formatBucketFull` in `trendBuckets.ts`
   - the caption stamp in `TrendChartFrame.tsx`
   - `dateOf`, the time label and the tooltip `full` text in `ShotsBreakdownChart.tsx`
   - the `when` tooltip text in `AmpsGraph.tsx` and `FilteredAmpsGraph.tsx`
   - the `start` tooltip text in `CycleDataGraph.tsx`

   Never take the date by slicing the ISO string. In UTC, every day bucket starts on the previous date.

## 3. Each chart: how it must display

Shared settings for every dialog chart:
- MUI `Dialog` with `maxWidth="lg"` and `fullWidth`; chart body height 420; margin `{ top: 12, right: 24, left: 12, bottom: 44 }`.
- Dashed grid (`strokeDasharray="3 3"`).
- Y axis from `niceScale` / `niceScaleOf`: starts at 0, round steps, width 78.
- **Mount the chart only after the dialog has finished opening** (`slotProps={{ transition: { onEntered } }}`). A Recharts `ResponsiveContainer` mounted mid-animation measures 0 px wide and draws nothing. If our charts open blank, this is why.

### Section 1: lifetime tiles

These call `/trends?bucket=auto` with **no** `start`/`end`, which returns all history. The rows are gap-filled: a day the plant didn't run comes back as a zero row. **Plot every row, including the zeros.** The evenly spaced category axis is only honest because no day is missing.

The dialog title is the tile's name, with an info icon whose tooltip is the text in quotes.

**Machine Utility**: `UtilityGraph`. Info: "Blast time as a percentage of machine on-time".
- `LineChart` of `utilityPct`, `#1976d2`, `type="monotone"`, width 2, dots r=2 only when there are ≤ 45 points.
- X: the bucket label, using `categoryXAxis(n)` (tilted −45°, every label up to 31 points, strided beyond that). X title "Date".
- Y: **fixed 0–100** (`niceScale(100)`), never fitted to the data. Y title "Machine Utility (%)".
- Tooltip: `74.3 %`, with the full interval as its heading ("Thursday, 14 May 2026").
- No reference or target line.
- Empty: "No utility data recorded yet."

**Production (Tonnage)**: `ProductionGraph`. Info: "Daily tonnage with running total".
- `ComposedChart` with two Y axes:
  - bars of `productionKg`, green `#2e7d32`, on the left axis "Daily Production (kg)" ("Daily" follows the bucket);
  - a monotone line of `tonnageEnd`, blue `#1565c0`, no dots, `connectNulls`, on the right axis "Cumulative Production (kg)".
- Each Y axis is drawn in its series' colour. Right margin 78. Legend at the top.
- Tooltip: `1,408 kg` with the series name.
- Empty: "No production data recorded yet."

**Total Energy, Blast Time, Blast Cycles**: one component, `TrendMetricGraph`, a `BarChart` with no legend.

| Tile | `metric` | Plots | Colour | Y title | Tooltip | Info | Empty |
|---|---|---|---|---|---|---|---|
| Total Energy | `energy` | `energyKwh` | `#1565c0` | Energy (kWh) | `140.69 kWh` | "Average impeller current × cycle duration, summed per day" | "No energy data recorded yet." |
| Blast Time | `blastTime` | `blastOnSec / 3600` | `#6a1b9a` | Blast Time (hours) | `0.37 h` | "Duration the blast was ON" | "No blast time recorded yet." |
| Blast Cycles | `cycleCount` | `cycleCount` | `#00838f` | Blast Cycles | `2 cycles` | "Count of completed blast cycles" | "No blast cycles recorded yet." |

Energy per Casting has **no chart** in either section, and no chart icon.

**Blast Cycles per Refill Interval**: `ShotsBreakdownChart`. It sits **in the page**, under the lifetime tiles, 300 px tall, not in a dialog.
- Heading "Blast Cycles per Refill Interval", caption "Cycles completed between consecutive refills."
- One bar per `shotsBreakdown` row: `blastCount`, `#1976d2`, radius `[3,3,0,0]`, `barCategoryGap="12%"`.
- X: `categoryXAxis(n)`. Labels are the date only (`14 May`); add the time only where two bars share a date (`14 May 14:01`). X title "Refill Date".
- Y: `niceScaleOf(blastCount)`, `allowDecimals={false}`, title "Blast Cycles".
- Tooltip heading: `13/05/2026, 16:17:07 to 14/05/2026, 14:01:29 (0.9 days)`, from `intervalStartTimestamp` → `refillTimestamp`. When the start is null: `interval ending 14/05/2026, 14:01:29 (no earlier refill recorded)`. The value reads "Blast Cycles: N".
- Hide the whole block when there are no rows.

**Live impeller tile**: `AmpsGraph`, in a dialog titled `Impeller N: Average Current per Cycle (A)`. Data: `/amps/by-cycle?impeller=N`.
- `LineChart` of `avgAmps`, monotone, no dots, width 1.5, `connectNulls`, colour `IMPELLER_COLORS[N-1]`.
- X: **a numeric axis**: `dataKey="cycle"`, `type="number"`, `domain={[first, last]}`, `ticks={numericTicks(first, last)}`, so ticks land on round cycle numbers (100, 200, 300). X title "Cycle number". Never a category axis here: it strides by row index and prints cycles like 1, 106, 211.
- Y: `niceScale(max avgAmps)`, title "Average current (A)".
- Legend at the top: "Impeller N average current (A)".
- Tooltip: `13.82 A`, heading "Cycle 961, ended 14/05/2026, 15:29:33".
- Caption: "Average current per cycle, impeller N. All X recorded cycles are plotted. Current climbs as the blades wear and steps back down when they are replaced."
- **Plot every cycle, and never add zeros between cycles.** On a cycle axis there's nothing between cycle 960 and 961.
- Empty: "No completed cycles recorded."

### Section 2: after a filter is applied

**Machine Utility**: `UtilityGraph` with `windowStart` / `windowEnd` set to the response's `filterStart` / `filterEnd`, so it calls `/trends?bucket=auto&start=…&end=…`. Dialog title "Machine Utility". **Time filter only:** under a cycle or item filter the tile has no chart icon, because those filters carry a placeholder time window.

**Production (Item Weight)**: `ItemProductionGraph`, dialog title "Production per Casting Item". Data: `metals[]`. All filter modes.
- `BarChart` with one bar per item, in API order. `productionKg`, `#2563eb`; the "unspecified" bar is `#94a3b8`. Radius 4.
- X: `dataKey="name"`, tilted −30°, `interval={0}` (every name shown), height 72, font 12. X title "Casting item".
- Y: `niceScaleOf`, title "Declared weight (kg)".
- Horizontal grid lines only (`vertical={false}`). Legend at the top: "Declared weight (kg)".
- Tooltip: `1,954.00 kg` "Declared weight".
- Caption: "Declared casting weight per item. Total 4,790.00 kg."
- Empty: "No casting item weights were declared for the cycles in this filter."

**Total Energy**: `CycleDataGraph`, dialog title "Energy per Cycle". Data: `cycles[]`. All filter modes.
- X: a numeric cycle axis, as in `AmpsGraph`. X title "Cycle number".
- Bars of `energyKwh`, `#1565c0`, radius 2, when there are ≤ 120 cycles. **Above 120, use a monotone line instead**, since bars would be thinner than a pixel.
- Y: `niceScaleOf`, title "Energy (kWh)". Legend at the top: "Energy per cycle (kWh)".
- Tooltip: `140.688 kWh`, heading "Cycle 101, 13/05/2026, 15:53:43".
- Caption: "One point per blast cycle. All N cycles in this filter are plotted, cycles A to B."
- Empty: "No cycle data in this filter."

**Blast Time / Blast Cycles**: `TrendMetricGraph` (`blastTime` / `cycleCount`) over the filter window. Dialog titles "Blast Time per Interval" and "Blast Cycles per Interval". Time filter only.

**Filtered impeller tile**: `FilteredAmpsGraph`, in a dialog titled `Impeller N: Filtered Current (A)`. Data: `/filter/{requestId}/amps`; pick the entry whose `impellerNumber` is N, then plot its `cycles[]`.
- The same as `AmpsGraph`, except: dots r=2 when there are ≤ 60 points, line width 2, and Y from `niceScaleOf`.
- Caption: "Average current per cycle, impeller N. All X cycles in this filter are plotted."
- Empty: "No cycle data for this impeller in the filter."

**Section 2 dialogs have no info icon.**

## 4. Rules that apply to every chart

These were each added after a real complaint, so keep them all:

- **Both axes carry a title.**
- **Equal spacing means equal time.** Category axes are allowed only on the gap-filled `/trends` series. Cycle axes are `type="number"`.
- **Stride ticks with `tickInterval()`** / `categoryXAxis()`; never `interval="preserveStartEnd"`, which leaves uneven gaps.
- **Label every point while the labels fit** (up to 31 tilted labels); only stride beyond that.
- **The Y axis starts at 0 with round steps** (`niceScale`). Counts get whole numbers.
- **Plot every point in scope; never truncate.** No `.slice(-200)`.
- **No reference or target lines.**
- **Lines are `type="monotone"`**; never `natural`, `basis` or `cardinal`.

## 5. Check when done

Open each chart on the gateway (`https://testing.shotsense.com`) and on our client page side by side, and confirm:
- The same number of bars or points, the same axis labels and titles, the same tooltip text.
- Day labels match the gateway's own, even when your browser isn't set to IST.
- A Year filter shows the item bars totalling 4,790.00 kg, and the energy-per-cycle chart shows one point per cycle.
- Each impeller chart has exactly as many points as there are recorded cycles.
- No chart opens blank, and none has a target line.
