using System.Globalization;
using System.Text;
using Npgsql;

namespace PLCGateway.DemoSeeder;

/// <summary>
/// Prints everything needed to sanity-check the seeded dataset BEFORE trusting it.
///
/// Nothing here recomputes a KPI independently — it reads back exactly what the production
/// pipeline produced and cross-checks the numbers against each other and against believable
/// engineering ranges. A value outside its band is printed as a FAIL line, never smoothed over.
/// </summary>
public sealed class Verifier
{
    private readonly string _conn;
    private readonly DatabaseService _db;
    private readonly CalculationService _calc;

    // Sanity bands. Outside these, the generator profile is wrong and the report says so.
    private const double ShotUsageMin = 3.0, ShotUsageMax = 8.0;
    private const double UtilityMin = 62.0, UtilityMax = 80.0;
    private const double KwhPerKgMin = 0.15, KwhPerKgMax = 0.30;

    public Verifier(string connectionString, DatabaseService db, CalculationService calc)
    {
        _conn = connectionString;
        _db = db;
        _calc = calc;
    }

    public async Task ReportAsync()
    {
        await using var conn = new NpgsqlConnection(_conn);
        await conn.OpenAsync();

        Header("SECTION 1 — lifetime parameters (plc_lifetime_parameters)");
        var lifetime = await ReadLifetimeAsync(conn);
        foreach (var (name, value) in lifetime.OrderBy(p => Order(p.Key)))
            Console.WriteLine($"  {name,-34} {Format(name, value)}");

        Header("AGGREGATION STATE (plc_aggregation_state) — proof the fold ran");
        await PrintRowAsync(conn, @"
            SELECT last_hist_id, cycle_count, ROUND(blast_closed_sec::numeric,1) AS blast_closed_sec,
                   ROUND(machine_closed_sec::numeric,1) AS machine_closed_sec, refill_count,
                   total_refill_weight_kg, ROUND(energy_total,2) AS energy_total, last_cycle_number,
                   first_blast_ts, last_refill_any_ts
            FROM plc_aggregation_state WHERE id = 1");

        Header("TABLE COUNTS");
        await PrintTableAsync(conn, @"
            SELECT 'plc_historical_data' AS table, COUNT(*)::text AS rows,
                   MIN(timestamp)::text AS first, MAX(timestamp)::text AS last FROM plc_historical_data
            UNION ALL SELECT 'plc_current_values', COUNT(*)::text, NULL, MAX(last_updated)::text FROM plc_current_values
            UNION ALL SELECT 'plc_cycles', COUNT(*)::text, MIN(blast_start)::text, MAX(blast_end)::text FROM plc_cycles
            UNION ALL SELECT 'plc_daily_trends', COUNT(*)::text, MIN(day)::text, MAX(day)::text FROM plc_daily_trends
            UNION ALL SELECT 'plc_shots_breakdown', COUNT(*)::text, MIN(refill_timestamp)::text, MAX(refill_timestamp)::text FROM plc_shots_breakdown
            UNION ALL SELECT 'plc_spare_status', COUNT(*)::text, NULL, NULL FROM plc_spare_status");

        Header("RAW ROW MIX (storage_reason)");
        await PrintTableAsync(conn, @"
            SELECT storage_reason, COUNT(*)::text AS rows, COUNT(DISTINCT parameter_name)::text AS tags
            FROM plc_historical_data GROUP BY storage_reason ORDER BY COUNT(*) DESC");

        Header("DAILY ROLLUP (plc_daily_trends) — what the all-time graphs plot");
        await PrintTableAsync(conn, @"
            SELECT day::text,
                   ROUND(machine_on_sec/3600,2)::text AS machine_h,
                   ROUND(blast_on_sec/3600,2)::text   AS blast_h,
                   CASE WHEN machine_on_sec > 0
                        THEN ROUND(blast_on_sec/machine_on_sec*100,1)::text ELSE '-' END AS utility_pct,
                   cycle_count::text,
                   ROUND(production_kg,0)::text AS production_kg,
                   ROUND(energy_kwh,0)::text    AS energy_kwh,
                   COALESCE(ROUND(tonnage_end,0)::text,'-') AS tonnage_end
            FROM plc_daily_trends ORDER BY day");

        Header("SHOTS BREAKDOWN (plc_shots_breakdown) — blast cycles per refill interval");
        await PrintTableAsync(conn, @"
            SELECT refill_timestamp::text, blast_count::text FROM plc_shots_breakdown ORDER BY refill_timestamp");

        Header("SPARE HEALTH (plc_spare_status) — live triggers and recent replacements");
        await PrintTableAsync(conn, @"
            SELECT spare_name, threshold_hours::text,
                   COUNT(*)::text AS rows,
                   ROUND(MIN(current_run_hours)::numeric,1)::text AS min_hours,
                   ROUND(MAX(current_run_hours)::numeric,1)::text AS max_hours,
                   COUNT(*) FILTER (WHERE trigger_active)::text AS triggered,
                   COUNT(*) FILTER (WHERE last_replaced_at IS NOT NULL)::text AS replaced
            FROM plc_spare_status GROUP BY spare_name, threshold_hours, spare_index ORDER BY spare_index");

        // ── Section 2 ───────────────────────────────────────────────────────────────────────
        var (weekStart, weekEnd) = await LastFullWeekAsync(conn);
        int weekRequest = await RunFilterAsync("time", weekStart, weekEnd,
            $"Verify: week {weekStart:yyyy-MM-dd} to {weekEnd:yyyy-MM-dd}");
        Header($"SECTION 2 — time filter, {weekStart:yyyy-MM-dd} .. {weekEnd:yyyy-MM-dd} (request {weekRequest})");
        await PrintFilterAsync(conn, weekRequest);

        string topItem = await TopItemAsync(conn);
        int itemRequest = await RunFilterAsync("metal", DateTime.Now, DateTime.Now, $"Verify: item {topItem}", topItem);
        Header($"SECTION 2 — item filter '{topItem}' (request {itemRequest})");
        await PrintFilterAsync(conn, itemRequest);
        Console.WriteLine("  (machine_utility_pct is expected to be ABSENT above: it is dropped under an item filter)");

        Header("CROSS-CHECKS");
        await CrossChecksAsync(conn, lifetime);

        Header("SANITY BANDS");
        Band("effective_shots_usage_kg_per_ton", lifetime.GetValueOrDefault("effective_shots_usage_kg_per_ton"),
             ShotUsageMin, ShotUsageMax, "kg/T");
        Band("machine_utility_pct", lifetime.GetValueOrDefault("machine_utility_pct"),
             UtilityMin, UtilityMax, "%");
        Band("energy_per_casting_kwh_kg", lifetime.GetValueOrDefault("energy_per_casting_kwh_kg"),
             KwhPerKgMin, KwhPerKgMax, "kWh/kg");
        await DailyUtilityBandAsync(conn);
    }

    // ── Section 2 helpers ───────────────────────────────────────────────────────────────────

    /// Runs a filtered calculation exactly the way FilteredCalculationService does: claim a request
    /// row, compute, mark it completed. No shortcut around the production code.
    private async Task<int> RunFilterAsync(string filterBy, DateTime start, DateTime end, string label,
                                           string? itemName = null)
    {
        int id = await _db.InsertClaimedRequestAsync(start, end, label, filterBy, null, null, itemName);
        await _calc.ComputeFilteredParametersAsync(id, start, end, filterBy, null, null, itemName);
        // "done" is the terminal status FilteredCalculationService writes, and what the dashboard
        // polls for — not "completed".
        await _db.SetRequestStatusAsync(id, "done");
        return id;
    }

    private async Task PrintFilterAsync(NpgsqlConnection conn, int requestId)
    {
        Console.WriteLine("  parameters:");
        await PrintTableAsync(conn,
            "SELECT parameter_name, COALESCE(value::text,'(null)') AS value FROM plc_filtered_parameters " +
            $"WHERE request_id = {requestId} ORDER BY parameter_name", indent: "    ");

        Console.WriteLine("  production by casting item:");
        await PrintTableAsync(conn,
            "SELECT metal_name AS item, ROUND(production_kg,0)::text AS kg FROM plc_filtered_metal_production " +
            $"WHERE request_id = {requestId} ORDER BY production_kg DESC", indent: "    ");

        Console.WriteLine("  cycle breakdown (first 3 / last 3):");
        await PrintTableAsync(conn, $@"
            (SELECT cycle_number::text, blast_start::text, ROUND(production_kg,0)::text AS prod_kg,
                    ROUND(energy_kwh,1)::text AS kwh, COALESCE(metal_1_name,'-') AS item
             FROM plc_filtered_cycle_data WHERE request_id = {requestId} ORDER BY cycle_number LIMIT 3)
            UNION ALL
            (SELECT cycle_number::text, blast_start::text, ROUND(production_kg,0)::text,
                    ROUND(energy_kwh,1)::text, COALESCE(metal_1_name,'-')
             FROM plc_filtered_cycle_data WHERE request_id = {requestId} ORDER BY cycle_number DESC LIMIT 3)",
            indent: "    ");

        await PrintRowAsync(conn,
            "SELECT COUNT(*)::text AS cycle_rows FROM plc_filtered_cycle_data " +
            $"WHERE request_id = {requestId}", indent: "    ");

        await PrintRowAsync(conn, $@"
            SELECT COUNT(*)::text AS amps_rows,
                   COUNT(DISTINCT cycle_number)::text AS cycles,
                   ROUND(AVG(avg_amps),2)::text AS mean_amps
            FROM plc_filtered_amps_data WHERE request_id = {requestId}", indent: "    ");
    }

    private async Task<(DateTime Start, DateTime End)> LastFullWeekAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand("SELECT MAX(blast_end) FROM plc_cycles", conn);
        var last = (DateTime)(await cmd.ExecuteScalarAsync())!;
        var end = last.Date.AddDays(1);
        return (end.AddDays(-7), end);
    }

    private async Task<string> TopItemAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT name FROM (
                SELECT metal_1_name AS name, SUM(metal_1_weight_kg) AS kg FROM plc_cycles
                WHERE metal_1_name IS NOT NULL GROUP BY 1) t
            ORDER BY kg DESC LIMIT 1", conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    // ── Cross-checks ────────────────────────────────────────────────────────────────────────

    private async Task CrossChecksAsync(NpgsqlConnection conn, Dictionary<string, decimal?> lifetime)
    {
        decimal? cycleCount = lifetime.GetValueOrDefault("cycle_count");
        decimal cycleRows = await ScalarAsync(conn, "SELECT COUNT(*) FROM plc_cycles");
        Check("cycle_count == COUNT(plc_cycles)", cycleCount == cycleRows,
              $"lifetime {cycleCount} vs table {cycleRows}");

        decimal? blastSec = lifetime.GetValueOrDefault("blast_time_sec");
        decimal rollupBlast = await ScalarAsync(conn, "SELECT COALESCE(SUM(blast_on_sec),0) FROM plc_daily_trends");
        // The rollup caps any segment over 5 minutes as a recording gap while the lifetime scalar
        // does not, so these agree only while the heartbeat rows are dense enough. A gap here means
        // the heartbeat seeding is wrong — which is exactly what this check exists to catch.
        double drift = blastSec is > 0 ? (double)Math.Abs(rollupBlast - blastSec.Value) / (double)blastSec.Value * 100 : 0;
        Check("blast_time_sec ≈ SUM(rollup blast_on_sec) within 1%", drift < 1.0,
              $"lifetime {blastSec:N0}s vs rollup {rollupBlast:N0}s ({drift:F2}% apart)");

        decimal? production = lifetime.GetValueOrDefault("production_qty_kg");
        decimal declared = await ScalarAsync(conn, @"
            SELECT COALESCE(SUM(COALESCE(metal_1_weight_kg,0) + COALESCE(metal_2_weight_kg,0)
                              + COALESCE(metal_3_weight_kg,0) + COALESCE(metal_4_weight_kg,0)), 0)
            FROM plc_cycles");
        double gap = production is > 0 ? (double)(declared - production.Value) / (double)production.Value * 100 : 0;
        Console.WriteLine($"  INFO  Section 1 measured {production:N0} kg vs Section 2 declared {declared:N0} kg " +
                          $"({gap:+0.00;-0.00}%) — a small gap here is by design, not an error");

        decimal reblasts = await ScalarAsync(conn, "SELECT COUNT(*) FROM plc_cycles WHERE COALESCE(production_kg,0) = 0");
        Console.WriteLine($"  INFO  {reblasts:N0} zero-production cycles (reblasts) out of {cycleRows:N0}");

        decimal refills = await ScalarAsync(conn, "SELECT COUNT(*) FROM plc_historical_data " +
            "WHERE parameter_name = 'Refil shots weight' AND storage_reason IN ('COV','VALUE_CHANGE','STATE_CHANGE')");
        decimal folded = await ScalarAsync(conn, "SELECT refill_count FROM plc_aggregation_state WHERE id = 1");
        Check("every refill COV row was folded", refills == folded, $"{refills} rows vs {folded} folded");

        // The trap: a refill weight repeated back-to-back produces no COV row at all, so the refill
        // silently disappears from both refill parameters and the shots-breakdown chart.
        decimal duplicates = await ScalarAsync(conn, @"
            SELECT COUNT(*) FROM (
                SELECT value_num, LAG(value_num) OVER (ORDER BY timestamp) AS prev
                FROM plc_historical_data
                WHERE parameter_name = 'Refil shots weight'
                  AND storage_reason IN ('COV','VALUE_CHANGE','STATE_CHANGE')) t
            WHERE prev IS NOT NULL AND ABS(value_num - prev) < 1");
        Check("no consecutive refill weights within the 1 kg deadband", duplicates == 0,
              $"{duplicates} refill(s) would have been swallowed by COV");

        decimal breakdownRows = await ScalarAsync(conn, "SELECT COUNT(*) FROM plc_shots_breakdown");
        Check("shots breakdown has one row per refill interval", breakdownRows == Math.Max(folded - 1, 0),
              $"{breakdownRows} rows for {folded} refills (intervals = refills - 1)");

        decimal futureRows = await ScalarAsync(conn,
            "SELECT COUNT(*) FROM plc_historical_data WHERE timestamp > LOCALTIMESTAMP");
        Check("no rows dated in the future", futureRows == 0, $"{futureRows} future rows");

        decimal outOfOrder = await ScalarAsync(conn, @"
            SELECT COUNT(*) FROM (
                SELECT timestamp, LAG(timestamp) OVER (ORDER BY id) AS prev FROM plc_historical_data) t
            WHERE prev IS NOT NULL AND timestamp < prev");
        // The incremental fold reads events ORDER BY id and measures durations from timestamps,
        // so any inversion between the two would corrupt every segment total.
        Check("plc_historical_data id order matches timestamp order", outOfOrder == 0,
              $"{outOfOrder} inversions");
    }

    private async Task DailyUtilityBandAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(@"
            SELECT MIN(u), MAX(u), AVG(u) FROM (
                SELECT blast_on_sec / NULLIF(machine_on_sec,0) * 100 AS u
                FROM plc_daily_trends WHERE machine_on_sec > 0) t", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync() || r.IsDBNull(0)) return;

        double min = Convert.ToDouble(r.GetValue(0)), max = Convert.ToDouble(r.GetValue(1));
        double avg = Convert.ToDouble(r.GetValue(2));
        bool ok = min >= UtilityMin && max <= UtilityMax;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  daily utility spread {min:F1}–{max:F1}% (mean {avg:F1}%), " +
                          $"target {UtilityMin:F0}–{UtilityMax:F0}%");
    }

    // ── Output helpers ──────────────────────────────────────────────────────────────────────

    private static void Check(string what, bool ok, string detail) =>
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what} — {detail}");

    private static void Band(string name, decimal? value, double min, double max, string unit)
    {
        if (value is null) { Console.WriteLine($"  FAIL  {name} is null"); return; }
        double v = (double)value.Value;
        bool ok = v >= min && v <= max;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name} = {v:F4} {unit} (expected {min}–{max})");
    }

    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine("══ " + title + " " + new string('═', Math.Max(0, 92 - title.Length)));
    }

    private async Task<Dictionary<string, decimal?>> ReadLifetimeAsync(NpgsqlConnection conn)
    {
        var map = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand("SELECT parameter_name, value FROM plc_lifetime_parameters", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            map[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetDecimal(1);
        return map;
    }

    private static int Order(string name)
    {
        string[] order =
        {
            "machine_status", "machine_utility_pct", "production_qty_kg", "energy_kwh_total",
            "energy_per_casting_kwh_kg", "blast_time_sec", "cycle_count", "avg_shot_refill_time_sec",
            "last_refill_epoch_sec", "effective_shots_usage_kg_per_ton",
        };
        int i = Array.IndexOf(order, name);
        return i < 0 ? int.MaxValue : i;
    }

    /// Renders each parameter the way its dashboard tile does, so the report can be compared
    /// against the screen directly.
    private static string Format(string name, decimal? value)
    {
        if (value is null) return "— (null)";
        decimal v = value.Value;
        return name switch
        {
            "machine_status" => v != 0 ? "1  (Running)" : "0  (Stopped)",
            "machine_utility_pct" => $"{v:F2} %",
            "production_qty_kg" => $"{v:N0} kg   ({v / 1000:F1} T)",
            "energy_kwh_total" => $"{v:N1} kWh",
            "energy_per_casting_kwh_kg" => $"{v:F4} kWh/kg",
            "blast_time_sec" => $"{v:N0} s   ({v / 3600:F1} h)",
            "cycle_count" => $"{v:N0}",
            "avg_shot_refill_time_sec" => $"{v:N0} s   ({v / 86400:F1} days)",
            "last_refill_epoch_sec" => $"{v:N0}   ({DateTimeOffset.FromUnixTimeSeconds((long)v).LocalDateTime:yyyy-MM-dd HH:mm})",
            "effective_shots_usage_kg_per_ton" => $"{v:F4} kg/T",
            _ => v.ToString(CultureInfo.InvariantCulture),
        };
    }

    private async Task<decimal> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToDecimal(value);
    }

    private async Task PrintRowAsync(NpgsqlConnection conn, string sql, string indent = "  ")
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) { Console.WriteLine(indent + "(no rows)"); return; }
        for (int i = 0; i < r.FieldCount; i++)
            Console.WriteLine($"{indent}{r.GetName(i),-24} {(r.IsDBNull(i) ? "-" : r.GetValue(i))}");
    }

    private async Task PrintTableAsync(NpgsqlConnection conn, string sql, string indent = "  ")
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();

        var columns = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
        var rows = new List<string[]>();
        while (await r.ReadAsync())
            rows.Add(Enumerable.Range(0, r.FieldCount)
                .Select(i => r.IsDBNull(i) ? "-" : r.GetValue(i)?.ToString() ?? "-").ToArray());

        if (rows.Count == 0) { Console.WriteLine(indent + "(no rows)"); return; }

        var widths = columns.Select((c, i) => Math.Max(c.Length, rows.Max(row => row[i].Length))).ToArray();
        Console.WriteLine(indent + string.Join("  ", columns.Select((c, i) => c.PadRight(widths[i]))));
        Console.WriteLine(indent + string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
            Console.WriteLine(indent + string.Join("  ", row.Select((cell, i) => cell.PadRight(widths[i]))));
    }
}
