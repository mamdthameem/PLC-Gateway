using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace PLCGateway.DemoSeeder;

/// <summary>
/// Renders a PlantSchedule as RAW PLC TAG ROWS — the same shape GatewayWorker's poller writes
/// into plc_historical_data (Tier 2) and plc_current_values (Tier 1), with backdated timestamps.
///
/// It writes nothing derived. No KPI, no aggregation state, no rollup: every one of those is left
/// for the real calculation pipeline to produce by folding over these rows.
///
/// Why it does not call DatabaseService.WriteScanBatchAsync: that method stamps every row with
/// NOW() in SQL, which is correct for a live poller and useless for backdated history. The row
/// SHAPE here is identical to what it writes — frozen `value` column left NULL, typed columns
/// authoritative, same storage_reason vocabulary, same previous_value semantics.
/// </summary>
public sealed class RawSeeder
{
    private const string HistoryCopy =
        "COPY plc_historical_data (address, parameter_name, value, value_num, value_bool, " +
        "value_text, data_type, storage_reason, timestamp, previous_value) FROM STDIN (FORMAT BINARY)";

    private readonly NpgsqlConnection _conn;
    private readonly Dictionary<string, TagDef> _tags;
    private readonly Random _rng;
    private readonly int _ampIntervalSeconds;
    private readonly bool _refillHeartbeats;

    // Blade trips after this moment are left standing rather than changed, so the spare table
    // ends the month with live alerts on it instead of an all-green board.
    private readonly DateTime _holdRepairsAfter;
    private readonly HashSet<int> _heldImpellers = new();

    // Per-tag rolling state, exactly what the poller keeps in its scan cache.
    private readonly Dictionary<string, string> _lastValue = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastStored = new(StringComparer.Ordinal);

    private List<Tier2Row> _buffer = new();
    public long RowsWritten { get; private set; }

    // Impeller state carried across the whole month.
    private readonly double[] _ampBase = new double[DemoProfile.ImpellerCount + 1];
    private readonly double[] _bladeHours = new double[DemoProfile.ImpellerCount + 1];
    private readonly double[] _spareBaseHours = new double[DemoProfile.ImpellerCount + 1];
    private double _blastHoursTotal;
    private double _nextRunHourMark = 1.0;

    public SpareState Spares { get; } = new();

    public RawSeeder(NpgsqlConnection conn, Dictionary<string, TagDef> tags, Random rng,
                     int ampIntervalSeconds, bool refillHeartbeats, DateTime holdRepairsAfter)
    {
        _conn = conn;
        _tags = tags;
        _rng = rng;
        _ampIntervalSeconds = ampIntervalSeconds;
        _refillHeartbeats = refillHeartbeats;
        _holdRepairsAfter = holdRepairsAfter;

        for (int i = 1; i <= DemoProfile.ImpellerCount; i++)
        {
            _ampBase[i] = DemoProfile.AmpsBaseMin +
                          _rng.NextDouble() * (DemoProfile.AmpsBaseMax - DemoProfile.AmpsBaseMin);
            // Commissioning trials leave the blades a few hours apart, so the replacement waves
            // stagger across the month instead of every impeller tripping on the same shift.
            _bladeHours[i] = _rng.NextDouble() * DemoProfile.BladeStartHoursSpread;
            _spareBaseHours[i] = _bladeHours[i];
        }
    }

    public void Seed(PlantSchedule schedule)
    {
        EmitCommissioningSnapshot(schedule.Shifts[0].MachineOn.AddMinutes(-2));
        Flush();

        var refills = new Queue<RefillEvent>(schedule.Refills.OrderBy(r => r.At));

        foreach (var block in schedule.Shifts)
        {
            SeedBlock(block, refills);
            Flush();
        }
    }

    // ── One shift block ─────────────────────────────────────────────────────────────────────
    private void SeedBlock(ShiftBlock block, Queue<RefillEvent> refills)
    {
        // Machine status: ON for the whole powered block, OFF at the end. A fault leaves the
        // machine powered (blast stops, machine does not), which is what costs utility.
        EmitChange("Machine status", "1", block.MachineOn);
        EmitChange("Machine status", "0", block.MachineOff);

        foreach (var cycle in block.Cycles)
        {
            EmitDeclarations(cycle);
            EmitChange("Blast ON/OFF", "1", cycle.BlastStart);
            EmitChange("Blast ON/OFF", "0", cycle.BlastEnd);
            EmitAmps(cycle);

            if (!cycle.IsReblast)
            {
                // The accumulator advances as the load is discharged, at cycle end.
                EmitChange("Tonnage", cycle.TonnageAfterKg.ToString(CultureInfo.InvariantCulture), cycle.BlastEnd);
                ClearDeclarations(cycle);
            }

            AccumulateSpareHours(cycle);
        }

        while (refills.Count > 0 && refills.Peek().At <= block.MachineOff)
        {
            var refill = refills.Dequeue();
            EmitChange("Refil shots weight", refill.WeightKg.ToString(CultureInfo.InvariantCulture), refill.At);
        }

        ReplaceTriggeredBlades(block.MachineOff);
        EmitHeartbeats(block);
    }

    /// First-ever reading of every configured tag, exactly as the poller records it on its first
    /// scan (storage_reason INITIAL, previous_value NULL).
    private void EmitCommissioningSnapshot(DateTime at)
    {
        foreach (var tag in _tags.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
            Emit(tag, InitialValue(tag), at, "INITIAL");
    }

    private string InitialValue(TagDef tag)
    {
        if (tag.Name.StartsWith("Spares_Runhour_", StringComparison.Ordinal))
        {
            var (imp, _) = ParseSpareTag(tag.Name);
            return Real(_spareBaseHours[imp]);
        }

        return tag.DataType switch
        {
            "BOOL" => "0",
            "STRING" => "",
            "REAL" => Real(DemoProfile.AmpsIdle),
            _ => "0",
        };
    }

    // ── Casting declarations ────────────────────────────────────────────────────────────────
    private void EmitDeclarations(CyclePlan cycle)
    {
        if (cycle.IsReblast) return;   // nothing re-declared: same load going back in

        for (int slot = 0; slot < 4; slot++)
        {
            string name = cycle.SlotNames[slot] ?? "";
            string kg = cycle.SlotKg[slot].ToString(CultureInfo.InvariantCulture);
            EmitChange($"Casting metal {slot + 1} name", name, cycle.BlastStart);
            EmitChange($"Casting metal {slot + 1} weight", kg, cycle.BlastStart);
        }
    }

    /// Weights drop to zero once the load is discharged. This is what makes a reblast declare
    /// nothing: by the time it goes back in, its weight tags already read 0, so the reblast cycle
    /// cannot double-count the load it already declared.
    private void ClearDeclarations(CyclePlan cycle)
    {
        var at = cycle.BlastEnd.AddSeconds(2);
        for (int slot = 0; slot < 4; slot++)
            if (cycle.SlotKg[slot] > 0)
                EmitChange($"Casting metal {slot + 1} weight", "0", at);
    }

    // ── Impeller current ────────────────────────────────────────────────────────────────────
    private void EmitAmps(CyclePlan cycle)
    {
        double loadFactor = cycle.DeclaredKg > 0
            ? 1.0 + DemoProfile.AmpsLoadFactor * (cycle.DeclaredKg - 120.0) / 220.0
            : 1.0;

        for (int imp = 1; imp <= DemoProfile.ImpellerCount; imp++)
        {
            double wear = 1.0 + DemoProfile.AmpsWearPerBladeHour * _bladeHours[imp];
            double target = _ampBase[imp] * wear * loadFactor;
            double phase = _rng.NextDouble() * Math.PI * 2;

            // The blast opens with an explicit zero at t=0: the impellers are stationary at the
            // instant the blast turns on, and without this sample the trace began mid-ramp at
            // ~7 A, so the chart never showed the spin-up it exists to show.
            Emit(_tags[$"Current_imp_{imp}"], Real(0), cycle.BlastStart, "BLAST_ON");

            for (double s = _ampIntervalSeconds; s <= cycle.DurationSeconds; s += _ampIntervalSeconds)
            {
                // Linear ramp from a standstill, a slow wander so no two cycles trace the same
                // line, and noise. The ramp starts at 0 rather than 18 % of running current —
                // a motor at rest draws no current.
                double ramp = s < DemoProfile.AmpsStartupSeconds
                    ? s / DemoProfile.AmpsStartupSeconds
                    : 1.0;
                double wander = 1.0 + 0.02 * Math.Sin(phase + s / 95.0);
                double noise = (_rng.NextDouble() * 2 - 1) * DemoProfile.AmpsNoise;
                double amps = target * ramp * wander + noise;

                Emit(_tags[$"Current_imp_{imp}"], Real(Math.Max(amps, 0)),
                     cycle.BlastStart.AddSeconds(s), "BLAST_ON");
            }

            // Back to a true zero after the blast stops. Placed AFTER blast_end so it never lands
            // inside the cycle window and drags the per-cycle average down.
            EmitChange($"Current_imp_{imp}",
                       Real(DemoProfile.AmpsIdle),
                       cycle.BlastEnd.AddSeconds(3));
        }
    }

    // ── Spares ──────────────────────────────────────────────────────────────────────────────
    /// Run hours accumulate with blast time, identically for every spare on an impeller.
    /// Sampled at whole-hour marks rather than at the configured 0.1 h deadband: nothing reads
    /// these from Tier 2 (plc_spare_status is built from Tier 1), and 0.1 h would emit ~339 k
    /// rows of pure noise for the month.
    private void AccumulateSpareHours(CyclePlan cycle)
    {
        double hours = cycle.DurationSeconds / 3600.0;
        double before = _blastHoursTotal;
        _blastHoursTotal += hours;

        for (int imp = 1; imp <= DemoProfile.ImpellerCount; imp++)
        {
            _bladeHours[imp] += hours;
            if (_bladeHours[imp] >= Spares.BladeThresholdHours && !Spares.TriggerActive(imp, DemoProfile.BladeSpareIndex))
            {
                Spares.SetTrigger(imp, DemoProfile.BladeSpareIndex, true);
                EmitChange($"Spares trigger imp{imp}[{DemoProfile.BladeSpareIndex}]", "1", cycle.BlastEnd);
            }
        }

        while (_nextRunHourMark <= _blastHoursTotal)
        {
            double intoCycle = (_nextRunHourMark - before) * 3600.0;
            var at = cycle.BlastStart.AddSeconds(Math.Clamp(intoCycle, 0, cycle.DurationSeconds));
            EmitRunHours(at);
            _nextRunHourMark += 1.0;
        }
    }

    private void EmitRunHours(DateTime at)
    {
        for (int imp = 1; imp <= DemoProfile.ImpellerCount; imp++)
            for (int idx = 0; idx < SpareState.SpareCount; idx++)
            {
                double value = idx == DemoProfile.BladeSpareIndex
                    ? _bladeHours[imp]
                    : _spareBaseHours[imp] + _blastHoursTotal;
                Spares.SetRunHours(imp, idx, value);
                EmitChange($"Spares_Runhour_imp{imp}[{idx}]", Real(value), at);
            }
    }

    /// Blades that tripped during this shift are changed at the end of it: REPLACED pulses, the
    /// PLC zeroes the run-hour counter and drops the trigger, and the fresh blades pull the
    /// impeller current back down. A few of the final crossings are deliberately left standing so
    /// the spare table shows live alerts rather than an all-green board.
    private void ReplaceTriggeredBlades(DateTime at)
    {
        foreach (int imp in Spares.PendingBladeReplacements(DemoProfile.BladeSpareIndex).ToList())
        {
            bool holdThisOne = _heldImpellers.Contains(imp) ||
                               (at >= _holdRepairsAfter && _heldImpellers.Count < DemoProfile.UnreplacedFinalTriggers);
            if (holdThisOne)
            {
                _heldImpellers.Add(imp);
                continue;
            }

            EmitChange($"REPLACED_{imp}[{DemoProfile.BladeSpareIndex}]", "1", at);
            EmitChange($"REPLACED_{imp}[{DemoProfile.BladeSpareIndex}]", "0", at.AddMinutes(5));
            EmitChange($"Spares trigger imp{imp}[{DemoProfile.BladeSpareIndex}]", "0", at.AddSeconds(30));
            EmitChange($"Spares_Runhour_imp{imp}[{DemoProfile.BladeSpareIndex}]", Real(0), at.AddSeconds(30));

            _bladeHours[imp] = 0;
            Spares.SetTrigger(imp, DemoProfile.BladeSpareIndex, false);
            Spares.SetRunHours(imp, DemoProfile.BladeSpareIndex, 0);
            Spares.RecordReplacement(imp, DemoProfile.BladeSpareIndex, at);
        }
    }

    // ── Heartbeats ──────────────────────────────────────────────────────────────────────────
    /// The 60 s heartbeat is not cosmetic: plc_daily_trends and the hourly trend query both treat
    /// any segment longer than 300 s as a RECORDING GAP and credit only its first 5 minutes. An
    /// 8–12 minute blast with no intermediate row would report as 5 minutes in every graph while
    /// the Section 1 scalar reported the full duration. These rows keep the two agreeing.
    private void EmitHeartbeats(ShiftBlock block)
    {
        foreach (var tagName in HeartbeatTags())
        {
            var tag = _tags[tagName];
            var changes = _buffer
                .Where(r => r.ParameterName == tagName && r.Timestamp >= block.MachineOn && r.Timestamp <= block.MachineOff)
                .OrderBy(r => r.Timestamp)
                .ToList();

            int ci = 0;
            string current = changes.Count > 0 && changes[0].Timestamp <= block.MachineOn
                ? changes[0].RawValue
                : _lastValue.GetValueOrDefault(tagName, "0");
            var lastStored = _lastStored.GetValueOrDefault(tagName, DateTime.MinValue);

            for (var t = block.MachineOn; t <= block.MachineOff; t = t.AddSeconds(DemoProfile.PeriodicHeartbeatSeconds))
            {
                while (ci < changes.Count && changes[ci].Timestamp <= t)
                {
                    current = changes[ci].RawValue;
                    lastStored = changes[ci].Timestamp;
                    ci++;
                }

                if ((t - lastStored).TotalSeconds < DemoProfile.PeriodicHeartbeatSeconds) continue;

                _buffer.Add(BuildRow(tag, current, t, "HEARTBEAT", current));
                lastStored = t;
            }

            _lastStored[tagName] = lastStored;
        }
    }

    private IEnumerable<string> HeartbeatTags()
    {
        yield return "Machine status";
        yield return "Blast ON/OFF";
        yield return "Tonnage";

        // Impeller currents ARE heartbeat tags on a real gateway (appsettings.json HeartbeatTags),
        // so it writes a row per impeller every 60 s even while they sit at 0 A between loads.
        // Leaving them out here meant a 3-4 minute changeover carried just TWO rows — the zero at
        // blast end and the zero at the next blast start — so the amps trace crossed the gap on a
        // single straight segment and a tooltip anywhere along it reported the same timestamp.
        // The blast periods are unaffected: the 5 s samples are denser than the heartbeat, and the
        // emitter skips any beat within PeriodicHeartbeatSeconds of an existing row.
        for (int imp = 1; imp <= DemoProfile.ImpellerCount; imp++)
            yield return $"Current_imp_{imp}";

        // 'Refil shots weight' is heartbeat-excluded by default. last_refill_epoch_sec is folded
        // from the NEWEST row of this tag regardless of storage reason, so a 60 s heartbeat would
        // pin the "Last Refill" tile to the end of the dataset instead of the last actual refill.
        // See README-DEMO-SEEDER.md; --refill-heartbeats reproduces the live-gateway behaviour.
        if (_refillHeartbeats) yield return "Refil shots weight";
    }

    // ── Emission primitives ─────────────────────────────────────────────────────────────────
    private void EmitChange(string tagName, string value, DateTime at)
    {
        var tag = _tags[tagName];
        string reason = tag.DataType switch
        {
            "BOOL" => "STATE_CHANGE",
            "STRING" => "VALUE_CHANGE",
            _ => "COV",
        };

        // The poller only stores a row when the value actually moved past its deadband. Emitting
        // an unchanged value here would fabricate a row the real gateway never writes.
        if (_lastValue.TryGetValue(tagName, out var previous) && previous == value) return;

        Emit(tag, value, at, reason);
    }

    private void Emit(TagDef tag, string value, DateTime at, string reason)
    {
        _lastValue.TryGetValue(tag.Name, out var previous);
        _buffer.Add(BuildRow(tag, value, at, reason, previous));
        _lastValue[tag.Name] = value;
        _lastStored[tag.Name] = at;
    }

    private static Tier2Row BuildRow(TagDef tag, string value, DateTime at, string reason, string? previous)
    {
        Classify(value, tag.DataType, out var num, out var flag, out var text);
        return new Tier2Row
        {
            Address = tag.Address,
            ParameterName = tag.Name,
            DataType = tag.DataType,
            StorageReason = reason,
            Num = num,
            Bool = flag,
            Text = text,
            Timestamp = at,
            PreviousValue = previous,
            RawValue = value,
        };
    }

    /// plc_historical_data.id is a SERIAL and the incremental fold reads events ORDER BY id while
    /// measuring durations from their timestamps — so id order MUST equal timestamp order or every
    /// segment calculation breaks. Rows are therefore sorted before every COPY, and each flush
    /// covers one shift block, which never overlaps the next.
    private void Flush()
    {
        if (_buffer.Count == 0) return;

        _buffer.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        using (var writer = _conn.BeginBinaryImport(HistoryCopy))
        {
            foreach (var row in _buffer)
            {
                writer.StartRow();
                writer.Write(row.Address, NpgsqlDbType.Varchar);
                writer.Write(row.ParameterName, NpgsqlDbType.Varchar);
                writer.WriteNull();                                    // frozen `value` column
                if (row.Num.HasValue) writer.Write(row.Num.Value, NpgsqlDbType.Numeric); else writer.WriteNull();
                if (row.Bool.HasValue) writer.Write(row.Bool.Value, NpgsqlDbType.Boolean); else writer.WriteNull();
                if (row.Text is not null) writer.Write(row.Text, NpgsqlDbType.Text); else writer.WriteNull();
                writer.Write(row.DataType, NpgsqlDbType.Varchar);
                writer.Write(row.StorageReason, NpgsqlDbType.Varchar);
                writer.Write(row.Timestamp, NpgsqlDbType.Timestamp);
                if (row.PreviousValue is not null) writer.Write(row.PreviousValue, NpgsqlDbType.Text); else writer.WriteNull();
            }
            writer.Complete();
        }

        RowsWritten += _buffer.Count;
        _buffer = new List<Tier2Row>();
    }

    // ── Tier 1 final state ──────────────────────────────────────────────────────────────────
    /// One row per tag holding the last value the poller would have seen. Written directly rather
    /// than through WriteScanBatchAsync because last_updated must be the final scan time of the
    /// seeded month, not NOW().
    public void WriteCurrentValues(DateTime lastScanAt)
    {
        var addresses = new List<string>();
        var names = new List<string>();
        var types = new List<string>();
        var nums = new List<decimal?>();
        var bools = new List<bool?>();
        var texts = new List<string?>();

        foreach (var tag in _tags.Values)
        {
            string value = _lastValue.GetValueOrDefault(tag.Name, InitialValue(tag));
            Classify(value, tag.DataType, out var num, out var flag, out var text);
            addresses.Add(tag.Address);
            names.Add(tag.Name);
            types.Add(tag.DataType);
            nums.Add(num);
            bools.Add(flag);
            texts.Add(text);
        }

        const string sql = @"
            INSERT INTO plc_current_values
                (address, parameter_name, value, value_num, value_bool, value_text, data_type,
                 last_updated, last_stored_historical, is_stale)
            SELECT u.a, u.n, NULL, u.vn, u.vb, u.vt, u.d, @ts, @ts, FALSE
            FROM unnest(@a, @n, @vn, @vb, @vt, @d) AS u(a, n, vn, vb, vt, d)
            ON CONFLICT (address) DO UPDATE SET
                parameter_name = EXCLUDED.parameter_name,
                value          = NULL,
                value_num      = EXCLUDED.value_num,
                value_bool     = EXCLUDED.value_bool,
                value_text     = EXCLUDED.value_text,
                data_type      = EXCLUDED.data_type,
                last_updated   = EXCLUDED.last_updated,
                is_stale       = FALSE";

        using var cmd = new NpgsqlCommand(sql, _conn);
        cmd.Parameters.AddWithValue("ts", lastScanAt);
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = addresses.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("vn", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = nums.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("vb", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = bools.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("vt", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = texts.ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = types.ToArray() });
        cmd.ExecuteNonQuery();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────
    /// Mirrors DatabaseService.ClassifyValue: the typed columns are authoritative and the TEXT
    /// `value` column stays frozen/NULL on every new row.
    private static void Classify(string value, string dataType, out decimal? num, out bool? flag, out string? text)
    {
        num = null; flag = null; text = null;
        switch (dataType)
        {
            case "BOOL":
            case "BOOLEAN":
                flag = value == "1";
                break;
            case "STRING":
            case "CHAR":
            case "VARCHAR":
                text = value;
                break;
            default:
                if (decimal.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands,
                                     CultureInfo.InvariantCulture, out var d)) num = d;
                break;
        }
    }

    /// REAL tags are formatted exactly as TagParser renders them, so a seeded row is
    /// indistinguishable from a polled one.
    private static string Real(double v) => ((float)v).ToString(CultureInfo.InvariantCulture);

    public static (int Impeller, int Index) ParseSpareTag(string name)
    {
        int impStart = name.IndexOf("imp", StringComparison.Ordinal) + 3;
        int bracket = name.IndexOf('[', impStart);
        int imp = int.Parse(name[impStart..bracket], CultureInfo.InvariantCulture);
        int close = name.IndexOf(']', bracket);
        int idx = int.Parse(name[(bracket + 1)..close], CultureInfo.InvariantCulture);
        return (imp, idx);
    }
}

public sealed record TagDef(string Name, string Address, string DataType);

public sealed class Tier2Row
{
    public string Address = "";
    public string ParameterName = "";
    public string DataType = "";
    public string StorageReason = "";
    public decimal? Num;
    public bool? Bool;
    public string? Text;
    public DateTime Timestamp;
    public string? PreviousValue;
    public string RawValue = "";
}
