using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using PLCGateway.DemoSeeder;

// ════════════════════════════════════════════════════════════════════════════════════════════
// PLCGateway.DemoSeeder — expo demo dataset generator.
//
// Writes RAW PLC TAG ROWS ONLY. Every KPI, graph and filter result is then produced by the real
// calculation pipeline folding over those rows — no computed value is ever inserted, and
// plc_aggregation_state is never written directly.
//
//   seed    generate one month of history into the demo database, then run the real pipeline
//   wipe    purge every seeded row and reset the aggregation state
//   verify  re-run the KPI report against whatever is already seeded
//
// Guarded three ways: the target database name must end in _demo, a non-empty target is refused,
// and both require --force to override.
// ════════════════════════════════════════════════════════════════════════════════════════════

const string DefaultConn =
    "Host=localhost;Port=5432;Database=sreesakthi_gateway_demo;Username=postgres;Password=Pass";

// Seeding takes minutes and is usually watched through a redirected pipe, where .NET buffers
// stdout until exit. Unbuffered output makes progress visible while it runs.
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

var cli = new CommandLine(args);
string command = cli.Command ?? "help";
string connectionString = cli.Value("--db") ?? DefaultConn;
int seed = int.Parse(cli.Value("--seed") ?? "20260905", CultureInfo.InvariantCulture);
int ampInterval = int.Parse(cli.Value("--amp-interval") ?? DemoProfile.AmpSampleSecondsDefault.ToString(), CultureInfo.InvariantCulture);
bool force = cli.Flag("--force");
bool refillHeartbeats = cli.Flag("--refill-heartbeats");

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
var db = new DatabaseService(connectionString, loggerFactory.CreateLogger<DatabaseService>());
var calc = new CalculationService(db, loggerFactory.CreateLogger<CalculationService>(),
                                  new ConfigurationBuilder().Build());

switch (command)
{
    case "seed":
        await SeedAsync();
        break;
    case "wipe":
        await WipeAsync(announce: true);
        break;
    case "verify":
        await new Verifier(connectionString, db, calc).ReportAsync();
        break;
    default:
        Console.WriteLine("""
            PLCGateway.DemoSeeder

              seed    [--db <conn>] [--seed <n>] [--amp-interval <sec>] [--refill-heartbeats] [--force]
              wipe    [--db <conn>] [--force]
              verify  [--db <conn>]

            Defaults: --db <sreesakthi_gateway_demo on localhost>, --seed 20260905, --amp-interval 5
            """);
        break;
}

return;

// ── Commands ────────────────────────────────────────────────────────────────────────────────

async Task SeedAsync()
{
    GuardTarget();
    if (!await IsEmptyAsync())
    {
        if (!force)
        {
            Console.Error.WriteLine(
                "REFUSED: the target database already holds PLC data. Run 'wipe' first, or pass --force.");
            Environment.ExitCode = 2;
            return;
        }
        await WipeAsync(announce: true);
    }

    var config = LoadGatewayConfig();
    var tags = LoadTags(config);
    var rng = new Random(seed);

    // Data always ends on a completed shift boundary at or before now, so nothing is ever written
    // into the future (which would make the open-segment clamps in the rollup behave oddly).
    var endOfWindow = LastCompletedShiftBoundary(DateTime.Now);

    Console.WriteLine($"Seeding {DemoProfile.WindowDays} days ending {endOfWindow:yyyy-MM-dd HH:mm} " +
                      $"(seed {seed}, amp interval {ampInterval}s)");

    var schedule = PlantModel.Build(rng, endOfWindow);
    Console.WriteLine($"  schedule: {schedule.Shifts.Count} shifts, {schedule.AllCycles.Count()} cycles, " +
                      $"{schedule.Refills.Count} refills, maintenance {schedule.MaintenanceDay:yyyy-MM-dd}");

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    // Blade trips in the last stretch of the month are left standing, so the spare table ends with
    // live maintenance alerts. It has to reach back far enough to catch the second wave of blade
    // crossings (~200 blast hours in), which lands around a week before the window closes.
    var seeder = new RawSeeder(conn, tags, rng, ampInterval, refillHeartbeats,
                               holdRepairsAfter: endOfWindow.AddDays(-8));
    seeder.Spares.Names = config.GetSection("MaintenanceThresholds:SpareNames").Get<string[]>() ?? Array.Empty<string>();
    seeder.Spares.Thresholds = config.GetSection("MaintenanceThresholds:SpareLifeBlastHours").Get<double[]>() ?? Array.Empty<double>();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    seeder.Seed(schedule);
    seeder.WriteCurrentValues(endOfWindow);
    Console.WriteLine($"  raw tag rows written: {seeder.RowsWritten:N0} ({sw.Elapsed.TotalSeconds:F0}s)");

    // plc_cycles: the one table the live service cannot rebuild from history, because it samples
    // Tier 1 at cycle close and Tier 1 only ever holds 'now'. The production insert method is used
    // unchanged, so production_kg (tonnage delta) and energy_kwh (AVG over the seeded amp rows)
    // are still computed by production SQL rather than supplied.
    // Fresh statistics before the cycle inserts: each one runs 10 correlated subqueries over the
    // amp rows, and a planner still thinking the table is empty picks sequential scans.
    await using (var analyze = new NpgsqlCommand("ANALYZE plc_historical_data", conn))
    {
        analyze.CommandTimeout = 300;
        await analyze.ExecuteNonQueryAsync();
    }

    sw.Restart();
    int inserted = 0;
    var plans = schedule.AllCycles.OrderBy(c => c.BlastStart).ToList();
    foreach (var cycle in plans)
    {
        int cycleNumber = await db.InsertCycleAsync(
            cycle.BlastStart, cycle.BlastEnd, cycle.DurationSeconds,
            cycle.SlotNames[0], cycle.SlotKg[0] > 0 ? cycle.SlotKg[0] : null,
            cycle.SlotNames[1], cycle.SlotKg[1] > 0 ? cycle.SlotKg[1] : null,
            cycle.SlotNames[2], cycle.SlotKg[2] > 0 ? cycle.SlotKg[2] : null,
            cycle.SlotNames[3], cycle.SlotKg[3] > 0 ? cycle.SlotKg[3] : null,
            cycle.TonnageAfterKg);

        // InsertCycleAsync returns 0 when the insert failed: RetryAsync logs and swallows the
        // exception rather than throwing. Without this check a broken insert produces an empty
        // plc_cycles and a seemingly successful run — which is exactly how the round(float8)
        // defect in that method hid for so long.
        if (cycleNumber <= 0)
            throw new InvalidOperationException(
                $"InsertCycleAsync returned no cycle_number for the blast at {cycle.BlastStart:u}. " +
                "The insert failed and DatabaseService swallowed the error — check the gateway log.");

        if (++inserted % 250 == 0)
            Console.WriteLine($"    {inserted:N0}/{plans.Count:N0} cycles ({sw.Elapsed.TotalSeconds:F0}s)");
    }
    Console.WriteLine($"  cycles inserted: {inserted:N0} ({sw.Elapsed.TotalSeconds:F0}s)");

    // plc_spare_status via the same production method SpareMonitoringService calls.
    foreach (var (imp, idx) in seeder.Spares.All())
        await db.UpsertSpareStatusAsync(
            imp, idx, seeder.Spares.Name(idx), seeder.Spares.Threshold(idx),
            seeder.Spares.RunHours(imp, idx), seeder.Spares.TriggerActive(imp, idx),
            seeder.Spares.ReplacedAt(imp, idx));

    await using (var cmd = new NpgsqlCommand(
        "UPDATE gateway_status SET plc_connected = TRUE, changed_at = @start, last_scan_at = @end WHERE id = 1", conn))
    {
        cmd.Parameters.AddWithValue("start", schedule.WindowStart);
        cmd.Parameters.AddWithValue("end", endOfWindow);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Hand over to the real pipeline ──────────────────────────────────────────────────────
    Console.WriteLine("  running the real Section 1 fold (CalculationService.ComputeLifetimeParametersAsync)...");
    sw.Restart();
    await calc.ComputeLifetimeParametersAsync();
    Console.WriteLine($"    done ({sw.Elapsed.TotalSeconds:F0}s)");

    Console.WriteLine("  building the daily rollup (DatabaseService.BackfillDailyTrendsAsync)...");
    sw.Restart();
    await db.BackfillDailyTrendsAsync();
    Console.WriteLine($"    done ({sw.Elapsed.TotalSeconds:F0}s)");

    await new Verifier(connectionString, db, calc).ReportAsync();
}

async Task WipeAsync(bool announce)
{
    GuardTarget();

    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();

    // plc_daily_trends and plc_aggregation_state are DERIVED — rebuildable from raw history at any
    // time — which is the only reason clearing them is safe. This tool only ever runs against a
    // _demo database; plc_historical_data in production is never deleted.
    const string sql = @"
        TRUNCATE plc_filtered_amps_data, plc_filtered_cycle_data, plc_filtered_metal_production,
                 plc_filtered_parameters, plc_filtered_shots_breakdown, calculation_requests,
                 plc_historical_data, plc_current_values, plc_cycles, plc_daily_trends,
                 plc_lifetime_parameters, plc_shots_breakdown, plc_spare_status
        RESTART IDENTITY CASCADE;

        UPDATE plc_aggregation_state SET
            last_hist_id = 0, blast_seeded = FALSE, blast_on = FALSE, blast_seg_start = NULL,
            blast_closed_sec = 0, first_blast_ts = NULL, cycle_count = 0, machine_seeded = FALSE,
            machine_on = FALSE, machine_seg_start = NULL, machine_closed_sec = 0, refill_count = 0,
            first_refill_change_ts = NULL, prev_refill_change_ts = NULL, last_refill_any_ts = NULL,
            energy_total = 0, last_cycle_number = 0, total_refill_weight_kg = 0
        WHERE id = 1;

        UPDATE gateway_status SET plc_connected = FALSE, changed_at = NULL, last_scan_at = NULL WHERE id = 1;";

    await using var cmd = new NpgsqlCommand(sql, conn);
    await cmd.ExecuteNonQueryAsync();

    if (announce) Console.WriteLine("Wiped: all seeded rows removed, aggregation state reset to zero.");
}

// ── Safety ──────────────────────────────────────────────────────────────────────────────────

void GuardTarget()
{
    var builder = new NpgsqlConnectionStringBuilder(connectionString);
    string database = builder.Database ?? "";

    if (!database.EndsWith("_demo", StringComparison.OrdinalIgnoreCase) && !force)
    {
        Console.Error.WriteLine(
            $"REFUSED: '{database}' is not a _demo database. This tool writes synthetic data and " +
            "must never touch a production gateway database. Pass --force only if you are certain.");
        Environment.Exit(2);
    }
}

async Task<bool> IsEmptyAsync()
{
    await using var conn = new NpgsqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new NpgsqlCommand(
        "SELECT EXISTS (SELECT 1 FROM plc_historical_data) OR EXISTS (SELECT 1 FROM plc_cycles)", conn);
    return !(bool)(await cmd.ExecuteScalarAsync())!;
}

// ── Config ──────────────────────────────────────────────────────────────────────────────────

IConfigurationRoot LoadGatewayConfig() =>
    new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

/// Tag definitions come from the gateway's own appsettings.json, so a seeded row can never carry
/// an address or data type the poller would not have produced.
Dictionary<string, TagDef> LoadTags(IConfiguration config)
{
    var map = new Dictionary<string, TagDef>(StringComparer.Ordinal);
    foreach (var section in config.GetSection("Tags").GetChildren())
    {
        string? name = section["Name"];
        string? address = section["Address"];
        string? type = section["DataType"];
        string mode = section["Mode"] ?? "read";
        if (name is null || address is null || type is null) continue;
        if (!mode.Equals("read", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("readwrite", StringComparison.OrdinalIgnoreCase)) continue;

        map[name] = new TagDef(name, address, type.ToUpperInvariant().Trim());
    }

    if (map.Count == 0) throw new InvalidOperationException("No readable tags found in appsettings.json.");
    return map;
}

/// The end of the most recent shift that has actually finished, so the dataset never contains a
/// timestamp in the future.
DateTime LastCompletedShiftBoundary(DateTime now)
{
    var day = now.Date;
    for (int back = 0; back < 8; back++, day = day.AddDays(-1))
    {
        if (day.DayOfWeek == DemoProfile.WeeklyOff) continue;
        foreach (var (_, endHour) in DemoProfile.Shifts.OrderByDescending(s => s.EndHour))
        {
            var boundary = day.AddHours(endHour);
            if (boundary <= now) return boundary;
        }
    }
    return now;
}

// ── Tiny arg parser ─────────────────────────────────────────────────────────────────────────

sealed class CommandLine
{
    private readonly string[] _args;
    public CommandLine(string[] args) => _args = args;
    public string? Command => _args.Length > 0 && !_args[0].StartsWith('-') ? _args[0] : null;

    public string? Value(string name)
    {
        int i = Array.IndexOf(_args, name);
        return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null;
    }

    public bool Flag(string name) => _args.Contains(name);
}
