using Npgsql;
using NpgsqlTypes;
using System.Globalization;

/// <summary>
/// EXPO ONLY. Drives the dashboard from a synthetic machine that alternates RUNNING and IDLE in
/// real time, on top of whatever history is already in the demo database.
///
/// Enabled solely by Demo:Simulator:Enabled, which lives in appsettings.Demo.json. A normal
/// gateway never constructs this class — see Program.cs, where it also displaces
/// CycleTrackingService (both would write plc_cycles for the same blast, and the tracker derives
/// energy from the amp samples rather than using the figure the expo is supposed to show).
///
/// ── Why the writes are split in two ──────────────────────────────────────────────────────────
///
/// The brief is that production, energy, cycle count and blast time all step up TOGETHER when a
/// run finishes, never creeping up during it. Writing the blast events live would leak two of
/// them out early:
///
///   • blast_time_sec is `closed segments + (now - segmentStart)` for an OPEN blast
///     (CalculationService), so an in-progress blast adds a second every second.
///   • cycle_count counts the RISING edge, which happens when the blast starts.
///
/// So during a run this service writes Tier 1 ONLY — machine status and impeller current, the two
/// things the dashboard reads live. Nothing that feeds a lifetime total is written until the run
/// ends, at which point one transaction writes the whole cycle as a closed, historical fact:
/// a blast that started five minutes ago and has just finished. Aggregation then folds all four
/// values in a single pass.
///
/// Everything it writes goes through the ordinary tables, so every downstream reader — the tiles,
/// the graphs, the rollup, the Section 2 filters, the admin API — sees an ordinary machine.
/// </summary>
public sealed class ExpoSimulatorService : BackgroundService
{
    // Tier 1 is keyed on ADDRESS, so these have to match the seeded rows exactly or the upsert
    // silently creates a second row for the same tag and the dashboard keeps reading the old one.
    private const string AddrMachineStatus = "DB60.DBB0";
    private const string AddrBlast         = "DB60.DBX1.1";
    private const string AddrTonnage       = "DB60.DBD1046";
    private const int    ImpellerBaseDbd   = 1650;             // Current_imp_1; +4 bytes each

    private const string TagMachineStatus = "Machine status";
    private const string TagBlast         = "Blast ON/OFF";
    private const string TagTonnage       = "Tonnage";

    private readonly DatabaseService _db;
    private readonly CalculationService _calc;
    private readonly ILogger<ExpoSimulatorService> _logger;
    private readonly string _connectionString;
    private readonly Random _rng = new();

    private readonly TimeSpan _runFor;
    private readonly TimeSpan _idleFor;
    private readonly int _impellers;
    private readonly double _ampsMin;
    private readonly double _ampsMax;
    private readonly decimal _productionKg;
    private readonly decimal _energyKwh;
    private readonly string _itemName;
    private readonly int _ampSampleSeconds;

    public ExpoSimulatorService(
        DatabaseService db,
        CalculationService calc,
        IConfiguration config,
        ILogger<ExpoSimulatorService> logger)
    {
        _db     = db;
        _calc   = calc;
        _logger = logger;

        _connectionString = config.GetConnectionString("PostgresDb")
            ?? config["PostgreSQL:ConnectionString"]
            ?? throw new InvalidOperationException("A demo connection string is required.");

        var s = config.GetSection("Demo:Simulator");
        _runFor       = TimeSpan.FromMinutes(s.GetValue("RunMinutes",  5.0));
        _idleFor      = TimeSpan.FromMinutes(s.GetValue("IdleMinutes", 2.0));
        _impellers    = config.GetValue("Impellers:Count", 10);
        _ampsMin      = s.GetValue("AmpsMin", 19.0);
        _ampsMax      = s.GetValue("AmpsMax", 20.0);
        _productionKg = s.GetValue("ProductionKgPerCycle", 300m);
        _energyKwh    = s.GetValue("EnergyKwhPerCycle", 3.5m);
        _itemName     = s.GetValue("ItemName", "Brake Drum") ?? "Brake Drum";
        _ampSampleSeconds = Math.Max(1, s.GetValue("AmpSampleSeconds", 5));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Message templates are POSITIONAL: a name reused later in the string still consumes the
        // next argument, so every placeholder needs its own.
        _logger.LogWarning(
            "EXPO SIMULATOR ACTIVE — {run} min running / {idle} min idle, {imp} impellers at " +
            "{lo}-{hi} A. Each completed run adds {kg} kg, {kwh} kWh, 1 cycle and {blastMin} min " +
            "of blast time, all at once. This must never run on a real gateway.",
            _runFor.TotalMinutes, _idleFor.TotalMinutes, _impellers, _ampsMin, _ampsMax,
            _productionKg, _energyKwh, _runFor.TotalMinutes);

        // Start idle so the very first thing on screen is a settled machine rather than a blast
        // already half finished.
        await WriteLiveStateAsync(running: false, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var runStart = DateTime.Now;
                var runEnd   = runStart + _runFor;

                // The current shown live is REMEMBERED, not re-invented at commit time, so the
                // history a viewer inspects afterwards is the same trace they watched.
                var samples = new List<(DateTime At, decimal[] Amps)>();
                int tick = 0;

                // ── RUNNING ── Tier 1 only. Nothing here touches a lifetime total.
                while (DateTime.Now < runEnd && !stoppingToken.IsCancellationRequested)
                {
                    var now  = DateTime.Now;
                    var amps = await WriteLiveStateAsync(running: true, stoppingToken);

                    // One historical sample every AmpSampleSeconds. Tier 1 still updates every
                    // second for the live tiles; Tier 2 does not need that density, and the
                    // per-cycle average is identical either way.
                    if (tick++ % _ampSampleSeconds == 0) samples.Add((now, amps));

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                if (stoppingToken.IsCancellationRequested) break;

                // ── ROLL-UP ── the whole cycle lands as one closed historical fact.
                //
                // The blast is recorded as exactly RunMinutes long, not as the wall-clock time the
                // loop actually took. The 1 s poll overshoots its deadline by a fraction of a
                // second, which showed up as blast time rising by 300.4 s instead of the 300 the
                // expo advertises. runEnd is already in the past by the time we get here, so
                // nothing is dated into the future.
                await CommitCycleAsync(runStart, runEnd, samples, stoppingToken);
                await WriteLiveStateAsync(running: false, stoppingToken);

                // Fold it immediately rather than waiting for AggregationService's next minute,
                // so the four tiles step up as the run ends instead of at some point during the
                // idle period.
                await _calc.ComputeLifetimeParametersAsync();
                var today = DateTime.Now.Date;
                await _db.UpsertDailyTrendsAsync(today.AddDays(-1), today.AddDays(1));

                _logger.LogInformation(
                    "Expo cycle committed: {start:HH:mm:ss} to {end:HH:mm:ss}, +{kg} kg, +{kwh} kWh.",
                    runStart, runEnd, _productionKg, _energyKwh);

                // ── IDLE ── keep refreshing Tier 1 so last_updated stays current and nothing on
                // screen starts reporting a stale reading.
                var idleUntil = DateTime.Now + _idleFor;
                while (DateTime.Now < idleUntil && !stoppingToken.IsCancellationRequested)
                {
                    await WriteLiveStateAsync(running: false, stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Expo simulator loop failed; retrying in 5 s.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Expo simulator stopping.");
    }

    /// <summary>
    /// The live picture: machine status and impeller current. Tier 1 only — MachineStatusService
    /// and AmpsService both read straight from plc_current_values, so this is all the dashboard
    /// needs to show Running with fluctuating amps, or Idle at 0 A.
    /// </summary>
    private async Task<decimal[]> WriteLiveStateAsync(bool running, CancellationToken ct)
    {
        var addresses = new List<string> { AddrMachineStatus, AddrBlast };
        var names     = new List<string> { TagMachineStatus, TagBlast };
        var types     = new List<string> { "BYTE", "BOOL" };
        var nums      = new List<decimal?> { running ? 1m : 0m, null };
        var bools     = new List<bool?> { null, running };

        var written = new decimal[_impellers];
        for (int i = 1; i <= _impellers; i++)
        {
            // Idle is a true zero, not a trickle: the impellers are stopped between loads.
            double amps = running ? _ampsMin + _rng.NextDouble() * (_ampsMax - _ampsMin) : 0.0;
            written[i - 1] = Math.Round((decimal)amps, 2);

            addresses.Add(ImpellerAddress(i));
            names.Add($"Current_imp_{i}");
            types.Add("REAL");
            nums.Add(written[i - 1]);
            bools.Add(null);
        }

        const string sql = @"
            INSERT INTO plc_current_values
                (address, parameter_name, value, value_num, value_bool, value_text,
                 data_type, last_updated, is_stale)
            SELECT u.a, u.n, NULL, u.vn, u.vb, NULL, u.d, NOW(), FALSE
            FROM unnest(@a, @n, @vn, @vb, @d) AS u(a, n, vn, vb, d)
            ON CONFLICT (address) DO UPDATE SET
                parameter_name = EXCLUDED.parameter_name,
                value          = NULL,
                value_num      = EXCLUDED.value_num,
                value_bool     = EXCLUDED.value_bool,
                data_type      = EXCLUDED.data_type,
                last_updated   = NOW(),
                is_stale       = FALSE";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("a",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = addresses.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("n",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = names.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("vn", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = nums.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("vb", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = bools.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("d",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = types.ToArray() });
        await cmd.ExecuteNonQueryAsync(ct);
        return written;
    }

    /// <summary>
    /// Writes the finished run as history, in one transaction.
    ///
    /// Rows are inserted in TIMESTAMP order (both start events, then both end events), because the
    /// demo dataset is verified on `id order matches timestamp order` — the incremental aggregator
    /// walks history by id and measures durations from timestamps, so an inversion would corrupt
    /// every duration after it.
    ///
    /// previous_value is set explicitly on each event. FoldBlast reads it to identify a rising
    /// edge, which is what makes cycle_count increment by exactly one.
    /// </summary>
    private async Task CommitCycleAsync(DateTime runStart, DateTime runEnd,
                                        List<(DateTime At, decimal[] Amps)> samples,
                                        CancellationToken ct)
    {
        double durationSec = (runEnd - runStart).TotalSeconds;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Tonnage is a running accumulator on the PLC, so the new total is the old one plus this
        // load — read inside the transaction so two passes can never both add to the same base.
        decimal tonnageBefore;
        await using (var read = new NpgsqlCommand(
            "SELECT COALESCE(value_num, 0) FROM plc_current_values WHERE address = @a FOR UPDATE",
            conn, tx))
        {
            read.Parameters.AddWithValue("a", AddrTonnage);
            tonnageBefore = Convert.ToDecimal(await read.ExecuteScalarAsync(ct) ?? 0m);
        }
        decimal tonnageAfter = tonnageBefore + _productionKg;

        const string histSql = @"
            INSERT INTO plc_historical_data
                (address, parameter_name, value, value_num, value_bool, value_text,
                 data_type, storage_reason, timestamp, previous_value)
            VALUES (@a, @n, NULL, @vn, @vb, NULL, @d, @r, @ts, @prev)";

        async Task Emit(string addr, string name, string type, decimal? num, bool? flag,
                        DateTime ts, string? previous)
        {
            await using var cmd = new NpgsqlCommand(histSql, conn, tx);
            cmd.Parameters.AddWithValue("a", addr);
            cmd.Parameters.AddWithValue("n", name);
            cmd.Parameters.Add(new NpgsqlParameter("vn", NpgsqlDbType.Numeric) { Value = (object?)num ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("vb", NpgsqlDbType.Boolean) { Value = (object?)flag ?? DBNull.Value });
            cmd.Parameters.AddWithValue("d", type);
            cmd.Parameters.AddWithValue("r", "COV");
            cmd.Parameters.AddWithValue("ts", ts);
            cmd.Parameters.Add(new NpgsqlParameter("prev", NpgsqlDbType.Text) { Value = (object?)previous ?? DBNull.Value });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Start of the run: machine powers up, blast begins. The blast rising edge is the one
        // cycle_count counts.
        await Emit(AddrMachineStatus, TagMachineStatus, "BYTE", 1m, null, runStart, "0");
        await Emit(AddrBlast,         TagBlast,         "BOOL", null, true, runStart, "0");

        // The impeller current for the run, inserted BETWEEN the start and end events so id order
        // still matches timestamp order — a property the whole incremental aggregator relies on,
        // and the reason these are not written live during the run alongside Tier 1.
        foreach (var (at, amps) in samples)
        {
            for (int i = 0; i < amps.Length; i++)
                await Emit(ImpellerAddress(i + 1), $"Current_imp_{i + 1}", "REAL",
                           amps[i], null, at, null);
        }

        // End of the run: blast stops, machine powers down, and the load is discharged so the
        // Tonnage accumulator advances.
        await Emit(AddrBlast,         TagBlast,         "BOOL", null, false, runEnd, "1");
        await Emit(AddrMachineStatus, TagMachineStatus, "BYTE", 0m, null, runEnd, "1");
        await Emit(AddrTonnage,       TagTonnage,       "DINT", tonnageAfter, null, runEnd,
                   tonnageBefore.ToString(CultureInfo.InvariantCulture));

        // production_kg and energy_kwh are written as GIVEN, not derived. DatabaseService's own
        // InsertCycleAsync computes energy from the impeller samples, which for two impellers at
        // ~19.5 A over five minutes is about 1.6 kWh — not the figure the expo is meant to show.
        //
        // It also declares a casting item for the same weight, so Section 2 (which reports
        // DECLARED weight, not the Tonnage accumulator) sees the expo cycles too. Without a
        // declaration an expo cycle would contribute to Section 1 and vanish from every item
        // filter.
        const string cycleSql = @"
            INSERT INTO plc_cycles
                (blast_start, blast_end, duration_sec, tonnage_kg, production_kg, energy_kwh,
                 metal_1_name, metal_1_weight_kg)
            VALUES (@start, @end, @dur, @tonnage, @prod, @kwh, @item, @prod)";

        await using (var cmd = new NpgsqlCommand(cycleSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("start", runStart);
            cmd.Parameters.AddWithValue("end", runEnd);
            cmd.Parameters.Add(new NpgsqlParameter("dur", NpgsqlDbType.Numeric) { Value = (decimal)durationSec });
            cmd.Parameters.Add(new NpgsqlParameter("tonnage", NpgsqlDbType.Numeric) { Value = tonnageAfter });
            cmd.Parameters.Add(new NpgsqlParameter("prod", NpgsqlDbType.Numeric) { Value = _productionKg });
            cmd.Parameters.Add(new NpgsqlParameter("kwh", NpgsqlDbType.Numeric) { Value = _energyKwh });
            cmd.Parameters.AddWithValue("item", _itemName);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // production_qty_kg is read from TIER 1, so the accumulator has to move here too.
        await using (var cmd = new NpgsqlCommand(
            "UPDATE plc_current_values SET value_num = @v, value = NULL, last_updated = NOW(), " +
            "is_stale = FALSE WHERE address = @a", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Numeric) { Value = tonnageAfter });
            cmd.Parameters.AddWithValue("a", AddrTonnage);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// Impeller currents are contiguous REALs: imp 1 at the base offset, then +4 bytes each.
    private static string ImpellerAddress(int impeller) =>
        $"DB60.DBD{ImpellerBaseDbd + (impeller - 1) * 4}";
}
