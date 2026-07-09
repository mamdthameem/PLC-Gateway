using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Logging;
using PLCGateway.Models;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;
    private readonly int _maxRetries = 5;
    private readonly int _baseDelayMs = 500;

    // Reconstructs the legacy string value from the typed columns (see SqlExpressions.TypedValue).
    private static readonly string ValueExpr = SqlExpressions.TypedValue();

    public DatabaseService(string connectionString, ILogger<DatabaseService> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════════════
    // TIER 1: Real-Time Current Values (plc_current_values)
    // ════════════════════════════════════════════════════════════════════════

    // Routes a scan value into exactly one typed column based on its PLC data type.
    // Numeric values that fail to parse (legacy junk) leave all typed columns null; the
    // frozen `value` column is not written for new rows, so such a row simply carries no value.
    private static void ClassifyValue(string? value, string dataType,
        out decimal? num, out bool? boolean, out string? text)
    {
        num = null; boolean = null; text = null;
        if (value == null) return;

        switch ((dataType ?? "").ToUpperInvariant().Trim())
        {
            case "BOOL": case "BOOLEAN":
                boolean = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                break;
            case "STRING": case "CHAR": case "VARCHAR":
                text = value;
                break;
            default:
                if (decimal.TryParse(value,
                        System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                    num = d;
                break;
        }
    }

    // Seeds the scan loop's in-memory previous-value cache at startup. Returns the last known
    // value and last Tier 2 store time per address, so COV and heartbeat decisions survive
    // restarts without a per-tag SELECT on every scan.
    public async Task<List<(string Address, string? Value, DateTime? LastStoredHistorical)>> GetCacheSeedAsync()
    {
        var results = new List<(string, string?, DateTime?)>();
        string sql = $"SELECT address, {ValueExpr}, last_stored_historical FROM plc_current_values";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? (DateTime?)null : reader.GetDateTime(2)));
            }
        }, "GetCacheSeed");

        return results;
    }

    // Writes one scan pass in a single transaction: all Tier 1 upserts as one multi-row
    // statement, all Tier 2 inserts as one multi-row statement, and one last_stored_historical
    // touch for the stored addresses. Replaces the old ~3 round-trips-per-tag pattern.
    public async Task WriteScanBatchAsync(
        IReadOnlyList<Tier1Write> tier1,
        IReadOnlyList<Tier2Write> tier2)
    {
        if (tier1.Count == 0 && tier2.Count == 0) return;

        await RetryAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync();

            if (tier1.Count > 0)
            {
                var addr = new string[tier1.Count];
                var name = new string[tier1.Count];
                var dt   = new string[tier1.Count];
                var vnum = new decimal?[tier1.Count];
                var vbit = new bool?[tier1.Count];
                var vtxt = new string?[tier1.Count];
                for (int i = 0; i < tier1.Count; i++)
                {
                    addr[i] = tier1[i].Address;
                    name[i] = tier1[i].ParameterName;
                    dt[i]   = tier1[i].DataType;
                    ClassifyValue(tier1[i].Value, tier1[i].DataType, out vnum[i], out vbit[i], out vtxt[i]);
                }

                // Frozen `value` column is set to NULL on new writes; typed columns are authoritative.
                const string sql = @"
                    INSERT INTO plc_current_values
                        (address, parameter_name, value, value_num, value_bool, value_text, data_type, last_updated, is_stale)
                    SELECT u.a, u.n, NULL, u.vn, u.vb, u.vt, u.d, NOW(), FALSE
                    FROM unnest(@a, @n, @vn, @vb, @vt, @d) AS u(a, n, vn, vb, vt, d)
                    ON CONFLICT (address) DO UPDATE SET
                        parameter_name = EXCLUDED.parameter_name,
                        value          = NULL,
                        value_num      = EXCLUDED.value_num,
                        value_bool     = EXCLUDED.value_bool,
                        value_text     = EXCLUDED.value_text,
                        data_type      = EXCLUDED.data_type,
                        last_updated   = NOW(),
                        is_stale       = FALSE";

                await using var cmd = new NpgsqlCommand(sql, conn, tx);
                cmd.Parameters.Add(new NpgsqlParameter("a",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = addr });
                cmd.Parameters.Add(new NpgsqlParameter("n",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = name });
                cmd.Parameters.Add(new NpgsqlParameter("vn", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = vnum });
                cmd.Parameters.Add(new NpgsqlParameter("vb", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = vbit });
                cmd.Parameters.Add(new NpgsqlParameter("vt", NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = vtxt });
                cmd.Parameters.Add(new NpgsqlParameter("d",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = dt });
                await cmd.ExecuteNonQueryAsync();
            }

            if (tier2.Count > 0)
            {
                var addr = new string[tier2.Count];
                var name = new string[tier2.Count];
                var dt   = new string[tier2.Count];
                var rsn  = new string[tier2.Count];
                var prev = new string?[tier2.Count];
                var vnum = new decimal?[tier2.Count];
                var vbit = new bool?[tier2.Count];
                var vtxt = new string?[tier2.Count];
                for (int i = 0; i < tier2.Count; i++)
                {
                    addr[i] = tier2[i].Address;
                    name[i] = tier2[i].ParameterName;
                    dt[i]   = tier2[i].DataType;
                    rsn[i]  = tier2[i].StorageReason;
                    prev[i] = tier2[i].PreviousValue;
                    ClassifyValue(tier2[i].Value, tier2[i].DataType, out vnum[i], out vbit[i], out vtxt[i]);
                }

                const string insertSql = @"
                    INSERT INTO plc_historical_data
                        (address, parameter_name, value, value_num, value_bool, value_text,
                         data_type, storage_reason, timestamp, previous_value)
                    SELECT u.a, u.n, NULL, u.vn, u.vb, u.vt, u.d, u.r, NOW(), u.p
                    FROM unnest(@a, @n, @vn, @vb, @vt, @d, @r, @p) AS u(a, n, vn, vb, vt, d, r, p)";

                await using (var cmd = new NpgsqlCommand(insertSql, conn, tx))
                {
                    cmd.Parameters.Add(new NpgsqlParameter("a",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = addr });
                    cmd.Parameters.Add(new NpgsqlParameter("n",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = name });
                    cmd.Parameters.Add(new NpgsqlParameter("vn", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = vnum });
                    cmd.Parameters.Add(new NpgsqlParameter("vb", NpgsqlDbType.Array | NpgsqlDbType.Boolean) { Value = vbit });
                    cmd.Parameters.Add(new NpgsqlParameter("vt", NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = vtxt });
                    cmd.Parameters.Add(new NpgsqlParameter("d",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = dt });
                    cmd.Parameters.Add(new NpgsqlParameter("r",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = rsn });
                    cmd.Parameters.Add(new NpgsqlParameter("p",  NpgsqlDbType.Array | NpgsqlDbType.Text)    { Value = prev });
                    await cmd.ExecuteNonQueryAsync();
                }

                const string touchSql =
                    "UPDATE plc_current_values SET last_stored_historical = NOW() WHERE address = ANY(@addrs)";
                await using (var cmd = new NpgsqlCommand(touchSql, conn, tx))
                {
                    cmd.Parameters.Add(new NpgsqlParameter("addrs", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = addr });
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();
        }, $"WriteScanBatch t1={tier1.Count} t2={tier2.Count}");
    }

    public async Task<PlcCurrentValue?> GetCurrentValueAsync(string address)
    {
        if (string.IsNullOrEmpty(address)) return null;

        PlcCurrentValue? result = null;
        string sql = $@"
            SELECT address, parameter_name, {ValueExpr}, data_type, last_updated, last_stored_historical, last_heartbeat, is_stale
            FROM plc_current_values
            WHERE address = @address";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("address", address);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                result = ReadCurrentValueRow(reader);
        }, $"GetCurrentValue {address}");

        return result;
    }

    public async Task<PlcCurrentValue?> GetCurrentValueByNameAsync(string parameterName)
    {
        if (string.IsNullOrEmpty(parameterName)) return null;

        PlcCurrentValue? result = null;
        string sql = $@"
            SELECT address, parameter_name, {ValueExpr}, data_type, last_updated, last_stored_historical, last_heartbeat, is_stale
            FROM plc_current_values
            WHERE parameter_name = @name
            LIMIT 1";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name", parameterName);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                result = ReadCurrentValueRow(reader);
        }, $"GetCurrentValueByName {parameterName}");

        return result;
    }

    // ════════════════════════════════════════════════════════════════════════
    // QUERY HELPERS (used by CalculationService and CycleTrackingService)
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<PlcHistoricalData>> GetStateChangesAsync(string parameterName, DateTime start, DateTime end)
    {
        var results = new List<PlcHistoricalData>();

        string sql = $@"
            SELECT id, address, parameter_name, {ValueExpr}, data_type, storage_reason, timestamp, previous_value
            FROM plc_historical_data
            WHERE parameter_name = @name
              AND timestamp > @start AND timestamp <= @end
            ORDER BY timestamp ASC";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name", parameterName);
            cmd.Parameters.AddWithValue("start", start);
            cmd.Parameters.AddWithValue("end", end);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                results.Add(ReadHistoricalRow(reader));
        }, $"GetStateChanges {parameterName}");

        return results;
    }

    // Returns the timestamp of the last TRUE value for a BOOL tag before a given moment.
    // Used by CycleTrackingService to find blast_start when a falling edge is detected.
    public async Task<DateTime?> GetLastTrueTimestampBeforeAsync(string parameterName, DateTime before)
    {
        DateTime? result = null;

        const string sql = @"
            SELECT timestamp FROM plc_historical_data
            WHERE parameter_name = @name
              AND (value_bool IS TRUE OR value = '1' OR value = 'True' OR value = 'true')
              AND timestamp < @before
            ORDER BY timestamp DESC LIMIT 1";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name", parameterName);
            cmd.Parameters.AddWithValue("before", before);
            var scalar = await cmd.ExecuteScalarAsync();
            if (scalar != null && scalar != DBNull.Value)
                result = Convert.ToDateTime(scalar);
        }, $"GetLastTrueTimestampBefore {parameterName}");

        return result;
    }

    // ════════════════════════════════════════════════════════════════════════
    // SECTION 1: Lifetime Parameters (plc_lifetime_parameters)
    // ════════════════════════════════════════════════════════════════════════

    public async Task UpsertLifetimeParameterAsync(string parameterName, decimal? value)
    {
        const string sql = @"
            INSERT INTO plc_lifetime_parameters (parameter_name, value, updated_at)
            VALUES (@name, @value, NOW())
            ON CONFLICT (parameter_name)
            DO UPDATE SET value = EXCLUDED.value, updated_at = NOW()";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name", parameterName);
            cmd.Parameters.AddWithValue("value", value ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }, $"UpsertLifetimeParameter {parameterName}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // CYCLE LOG (plc_cycles)
    // ════════════════════════════════════════════════════════════════════════

    public async Task<int> InsertCycleAsync(
        DateTime blastStart, DateTime blastEnd, double durationSec,
        string? metal1Name, double? metal1Wt,
        string? metal2Name, double? metal2Wt,
        string? metal3Name, double? metal3Wt,
        string? metal4Name, double? metal4Wt,
        double? tonnageKg)
    {
        int cycleNumber = 0;

        // production_kg and energy_kwh are computed once here at cycle close:
        //   production_kg = accumulated tonnage delta vs the previous cycle, floored at 0
        //   energy_kwh    = Σ over 10 impellers of (avg amps in window, else last amp before
        //                   the cycle, else 0) × duration hours
        // This is the single source of truth for lifetime energy (summed) and windowed
        // per-metal production (split by metal weight), so no per-cycle amp re-query is needed
        // during aggregation.
        const string sql = @"
            INSERT INTO plc_cycles
                (blast_start, blast_end, duration_sec,
                 metal_1_name, metal_1_weight_kg,
                 metal_2_name, metal_2_weight_kg,
                 metal_3_name, metal_3_weight_kg,
                 metal_4_name, metal_4_weight_kg,
                 tonnage_kg, production_kg, energy_kwh)
            VALUES
                (@blast_start, @blast_end, @duration_sec,
                 @m1n, @m1w, @m2n, @m2w, @m3n, @m3w, @m4n, @m4w,
                 @tonnage,
                 CASE WHEN @tonnage IS NULL THEN NULL
                      ELSE GREATEST(@tonnage - COALESCE(
                           (SELECT tonnage_kg FROM plc_cycles ORDER BY cycle_number DESC LIMIT 1), 0), 0)
                 END,
                 ROUND((SELECT COALESCE(SUM(
                        COALESCE(
                            (SELECT AVG(h.value_num) FROM plc_historical_data h
                              WHERE h.parameter_name = 'Current_imp_' || g
                                AND h.timestamp > @blast_start AND h.timestamp <= @blast_end
                                AND h.value_num IS NOT NULL),
                            (SELECT h2.value_num FROM plc_historical_data h2
                              WHERE h2.parameter_name = 'Current_imp_' || g
                                AND h2.timestamp <= @blast_start AND h2.value_num IS NOT NULL
                              ORDER BY h2.timestamp DESC LIMIT 1),
                            0)
                     ), 0)
                  FROM generate_series(1, 10) g) * (@duration_sec / 3600.0), 6))
            RETURNING cycle_number";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("blast_start",  blastStart);
            cmd.Parameters.AddWithValue("blast_end",    blastEnd);
            cmd.Parameters.AddWithValue("duration_sec", durationSec);
            cmd.Parameters.AddWithValue("m1n", string.IsNullOrWhiteSpace(metal1Name) ? (object)DBNull.Value : metal1Name);
            cmd.Parameters.AddWithValue("m1w", metal1Wt.HasValue ? (object)metal1Wt.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("m2n", string.IsNullOrWhiteSpace(metal2Name) ? (object)DBNull.Value : metal2Name);
            cmd.Parameters.AddWithValue("m2w", metal2Wt.HasValue ? (object)metal2Wt.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("m3n", string.IsNullOrWhiteSpace(metal3Name) ? (object)DBNull.Value : metal3Name);
            cmd.Parameters.AddWithValue("m3w", metal3Wt.HasValue ? (object)metal3Wt.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("m4n", string.IsNullOrWhiteSpace(metal4Name) ? (object)DBNull.Value : metal4Name);
            cmd.Parameters.AddWithValue("m4w", metal4Wt.HasValue ? (object)metal4Wt.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("tonnage", tonnageKg.HasValue ? (object)tonnageKg.Value : DBNull.Value);
            var scalar = await cmd.ExecuteScalarAsync();
            if (scalar != null && scalar != DBNull.Value)
                cycleNumber = Convert.ToInt32(scalar);
        }, "InsertCycle");

        return cycleNumber;
    }

    // Returns the blast_end of the most recently logged cycle (watermark for CycleTrackingService).
    public async Task<DateTime?> GetMaxCycleBlastEndAsync()
    {
        DateTime? result = null;

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand("SELECT MAX(blast_end) FROM plc_cycles", conn);
            var scalar = await cmd.ExecuteScalarAsync();
            if (scalar != null && scalar != DBNull.Value)
                result = Convert.ToDateTime(scalar);
        }, "GetMaxCycleBlastEnd");

        return result;
    }

    public async Task<List<PlcCycle>> GetCyclesByTimeRangeAsync(DateTime start, DateTime end)
    {
        var results = new List<PlcCycle>();
        const string sql = @"
            SELECT cycle_number, blast_start, blast_end, duration_sec,
                   metal_1_name, metal_1_weight_kg, metal_2_name, metal_2_weight_kg,
                   metal_3_name, metal_3_weight_kg, metal_4_name, metal_4_weight_kg,
                   tonnage_kg, production_kg, energy_kwh
            FROM plc_cycles
            WHERE blast_start >= @start AND blast_end <= @end
            ORDER BY cycle_number ASC";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("start", start);
            cmd.Parameters.AddWithValue("end",   end);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                results.Add(ReadCycleRow(reader));
        }, "GetCyclesByTimeRange");

        return results;
    }

    public async Task<List<PlcCycle>> GetCyclesByNumberRangeAsync(int from, int to)
    {
        var results = new List<PlcCycle>();
        const string sql = @"
            SELECT cycle_number, blast_start, blast_end, duration_sec,
                   metal_1_name, metal_1_weight_kg, metal_2_name, metal_2_weight_kg,
                   metal_3_name, metal_3_weight_kg, metal_4_name, metal_4_weight_kg,
                   tonnage_kg, production_kg, energy_kwh
            FROM plc_cycles
            WHERE cycle_number >= @from AND cycle_number <= @to
            ORDER BY cycle_number ASC";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("from", from);
            cmd.Parameters.AddWithValue("to",   to);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                results.Add(ReadCycleRow(reader));
        }, "GetCyclesByNumberRange");

        return results;
    }

    public async Task<List<PlcCycle>> GetCyclesByMetalNameAsync(string metalName)
    {
        var results = new List<PlcCycle>();
        const string sql = @"
            SELECT cycle_number, blast_start, blast_end, duration_sec,
                   metal_1_name, metal_1_weight_kg, metal_2_name, metal_2_weight_kg,
                   metal_3_name, metal_3_weight_kg, metal_4_name, metal_4_weight_kg,
                   tonnage_kg, production_kg, energy_kwh
            FROM plc_cycles
            WHERE metal_1_name = @name OR metal_2_name = @name
               OR metal_3_name = @name OR metal_4_name = @name
            ORDER BY cycle_number ASC";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name", metalName);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                results.Add(ReadCycleRow(reader));
        }, "GetCyclesByMetalName");

        return results;
    }

    // ════════════════════════════════════════════════════════════════════════
    // SECTION 2: Calculation Requests (calculation_requests)
    // ════════════════════════════════════════════════════════════════════════

    public async Task<CalculationRequest?> GetNextPendingRequestAsync()
    {
        CalculationRequest? result = null;

        const string sql = @"
            SELECT id, filter_start, filter_end, period_label,
                   filter_by, filter_cycle_from, filter_cycle_to, filter_metal_name,
                   status, created_at, processed_at
            FROM calculation_requests
            WHERE status = 'pending'
            ORDER BY created_at ASC
            LIMIT 1";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                result = new CalculationRequest
                {
                    Id              = reader.GetInt32(0),
                    FilterStart     = reader.GetDateTime(1),
                    FilterEnd       = reader.GetDateTime(2),
                    PeriodLabel     = reader.IsDBNull(3)  ? null : reader.GetString(3),
                    FilterBy        = reader.IsDBNull(4)  ? "time" : reader.GetString(4),
                    FilterCycleFrom = reader.IsDBNull(5)  ? null : reader.GetInt32(5),
                    FilterCycleTo   = reader.IsDBNull(6)  ? null : reader.GetInt32(6),
                    FilterMetalName = reader.IsDBNull(7)  ? null : reader.GetString(7),
                    Status          = reader.GetString(8),
                    CreatedAt       = reader.GetDateTime(9),
                    ProcessedAt     = reader.IsDBNull(10) ? null : reader.GetDateTime(10)
                };
        }, "GetNextPendingRequest");

        return result;
    }

    public async Task SetRequestStatusAsync(int requestId, string status)
    {
        const string sql = @"
            UPDATE calculation_requests
            SET status = @status,
                processed_at = CASE WHEN @status IN ('done','error') THEN NOW() ELSE processed_at END
            WHERE id = @id";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id",     requestId);
            cmd.Parameters.AddWithValue("status", status);
            await cmd.ExecuteNonQueryAsync();
        }, $"SetRequestStatus {requestId}={status}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SECTION 2: Aggregate Results (plc_filtered_parameters)
    // ════════════════════════════════════════════════════════════════════════

    public async Task InsertFilteredParameterAsync(int requestId, string parameterName, decimal? value)
    {
        const string sql = @"
            INSERT INTO plc_filtered_parameters (request_id, parameter_name, value, calculated_at)
            VALUES (@request_id, @name, @value, NOW())";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("request_id", requestId);
            cmd.Parameters.AddWithValue("name",       parameterName);
            cmd.Parameters.AddWithValue("value",      value ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }, $"InsertFilteredParameter {parameterName}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SECTION 2: Per-Cycle Breakdown (plc_filtered_cycle_data)
    // ════════════════════════════════════════════════════════════════════════

    public async Task InsertFilteredCycleDataAsync(int requestId, PlcCycle cycle,
        decimal productionKg, decimal energyKwh, decimal shotsUsage)
    {
        const string sql = @"
            INSERT INTO plc_filtered_cycle_data
                (request_id, cycle_number, blast_start, blast_end,
                 metal_1_name, metal_1_weight_kg, metal_2_name, metal_2_weight_kg,
                 metal_3_name, metal_3_weight_kg, metal_4_name, metal_4_weight_kg,
                 production_kg, energy_kwh, shots_usage)
            VALUES
                (@request_id, @cycle_number, @blast_start, @blast_end,
                 @m1n, @m1w, @m2n, @m2w, @m3n, @m3w, @m4n, @m4w,
                 @production, @energy, @shots)";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("request_id",   requestId);
            cmd.Parameters.AddWithValue("cycle_number", cycle.CycleNumber);
            cmd.Parameters.AddWithValue("blast_start",  cycle.BlastStart);
            cmd.Parameters.AddWithValue("blast_end",    cycle.BlastEnd);
            cmd.Parameters.AddWithValue("m1n", cycle.Metal1Name is null ? (object)DBNull.Value : cycle.Metal1Name);
            cmd.Parameters.AddWithValue("m1w", cycle.Metal1WeightKg.HasValue ? (object)(decimal)cycle.Metal1WeightKg.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("m2n", cycle.Metal2Name is null ? (object)DBNull.Value : cycle.Metal2Name);
            cmd.Parameters.AddWithValue("m2w", cycle.Metal2WeightKg.HasValue ? (object)(decimal)cycle.Metal2WeightKg.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("m3n", cycle.Metal3Name is null ? (object)DBNull.Value : cycle.Metal3Name);
            cmd.Parameters.AddWithValue("m3w", cycle.Metal3WeightKg.HasValue ? (object)(decimal)cycle.Metal3WeightKg.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("m4n", cycle.Metal4Name is null ? (object)DBNull.Value : cycle.Metal4Name);
            cmd.Parameters.AddWithValue("m4w", cycle.Metal4WeightKg.HasValue ? (object)(decimal)cycle.Metal4WeightKg.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("production", productionKg);
            cmd.Parameters.AddWithValue("energy",     energyKwh);
            cmd.Parameters.AddWithValue("shots",      shotsUsage);
            await cmd.ExecuteNonQueryAsync();
        }, $"InsertFilteredCycleData cycle={cycle.CycleNumber}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SHOTS BREAKDOWN — Section 1 (plc_shots_breakdown)
    // Maintained in place by upsert as the incremental engine discovers new refill intervals.
    // Shared dataset for parameters #7 (shots usage) and #8 (refill time).
    // ════════════════════════════════════════════════════════════════════════

    public async Task InsertLifetimeShotsBreakdownAsync(DateTime refillTimestamp, int blastCount)
    {
        // Upsert keyed on refill_timestamp (Part C3): the table is maintained in place instead
        // of TRUNCATE+rewrite, so dashboard reads never hit an empty or partial table.
        const string sql = @"
            INSERT INTO plc_shots_breakdown (refill_timestamp, blast_count, calculated_at)
            VALUES (@ts, @count, NOW())
            ON CONFLICT (refill_timestamp)
            DO UPDATE SET blast_count = EXCLUDED.blast_count, calculated_at = NOW()";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("ts",    refillTimestamp);
            cmd.Parameters.AddWithValue("count", blastCount);
            await cmd.ExecuteNonQueryAsync();
        }, $"InsertLifetimeShotsBreakdown {refillTimestamp:s}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SHOTS BREAKDOWN — Section 2 (plc_filtered_shots_breakdown)
    // One row per refill interval per calculation request.
    // ════════════════════════════════════════════════════════════════════════

    public async Task InsertFilteredShotsBreakdownAsync(int requestId, DateTime refillTimestamp, int blastCount)
    {
        const string sql = @"
            INSERT INTO plc_filtered_shots_breakdown (request_id, refill_timestamp, blast_count, calculated_at)
            VALUES (@request_id, @ts, @count, NOW())";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("request_id", requestId);
            cmd.Parameters.AddWithValue("ts",         refillTimestamp);
            cmd.Parameters.AddWithValue("count",      blastCount);
            await cmd.ExecuteNonQueryAsync();
        }, $"InsertFilteredShotsBreakdown req={requestId} {refillTimestamp:s}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SPARE STATUS (plc_spare_status)
    // ════════════════════════════════════════════════════════════════════════

    public async Task UpsertSpareStatusAsync(
        int impellerNum, int spareIndex, string spareName, double thresholdHours,
        double currentRunHours, bool triggerActive, DateTime? lastReplacedAt)
    {
        const string sql = @"
            INSERT INTO plc_spare_status
                (impeller_num, spare_index, spare_name, threshold_hours,
                 current_run_hours, trigger_active, last_replaced_at, last_updated_at)
            VALUES
                (@imp, @idx, @name, @threshold, @run_hours, @trigger, @replaced_at, NOW())
            ON CONFLICT (impeller_num, spare_index)
            DO UPDATE SET
                current_run_hours = EXCLUDED.current_run_hours,
                trigger_active    = EXCLUDED.trigger_active,
                last_replaced_at  = COALESCE(EXCLUDED.last_replaced_at, plc_spare_status.last_replaced_at),
                last_updated_at   = NOW()";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("imp",         impellerNum);
            cmd.Parameters.AddWithValue("idx",         spareIndex);
            cmd.Parameters.AddWithValue("name",        spareName);
            cmd.Parameters.AddWithValue("threshold",   thresholdHours);
            cmd.Parameters.AddWithValue("run_hours",   currentRunHours);
            cmd.Parameters.AddWithValue("trigger",     triggerActive);
            cmd.Parameters.AddWithValue("replaced_at", lastReplacedAt.HasValue ? (object)lastReplacedAt.Value : DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }, $"UpsertSpareStatus imp{impellerNum}[{spareIndex}]");
    }

    // ════════════════════════════════════════════════════════════════════════
    // SECTION 1: Incremental aggregation state (plc_aggregation_state)
    // ════════════════════════════════════════════════════════════════════════

    public async Task<AggregationState> GetAggregationStateAsync()
    {
        var s = new AggregationState();
        const string sql = @"
            SELECT last_hist_id, blast_seeded, blast_on, blast_seg_start, blast_closed_sec,
                   first_blast_ts, cycle_count, machine_seeded, machine_on, machine_seg_start,
                   machine_closed_sec, refill_count, first_refill_change_ts, prev_refill_change_ts,
                   last_refill_any_ts, energy_total, last_cycle_number
            FROM plc_aggregation_state WHERE id = 1";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                s.LastHistId          = r.GetInt64(0);
                s.BlastSeeded         = r.GetBoolean(1);
                s.BlastOn             = r.GetBoolean(2);
                s.BlastSegStart       = r.IsDBNull(3)  ? null : r.GetDateTime(3);
                s.BlastClosedSec      = r.GetDouble(4);
                s.FirstBlastTs        = r.IsDBNull(5)  ? null : r.GetDateTime(5);
                s.CycleCount          = r.GetInt64(6);
                s.MachineSeeded       = r.GetBoolean(7);
                s.MachineOn           = r.GetBoolean(8);
                s.MachineSegStart     = r.IsDBNull(9)  ? null : r.GetDateTime(9);
                s.MachineClosedSec    = r.GetDouble(10);
                s.RefillCount         = r.GetInt64(11);
                s.FirstRefillChangeTs = r.IsDBNull(12) ? null : r.GetDateTime(12);
                s.PrevRefillChangeTs  = r.IsDBNull(13) ? null : r.GetDateTime(13);
                s.LastRefillAnyTs     = r.IsDBNull(14) ? null : r.GetDateTime(14);
                s.EnergyTotal         = r.GetDecimal(15);
                s.LastCycleNumber     = r.GetInt32(16);
            }
        }, "GetAggregationState");

        return s;
    }

    public async Task SaveAggregationStateAsync(AggregationState s)
    {
        const string sql = @"
            UPDATE plc_aggregation_state SET
                last_hist_id = @last_hist_id, blast_seeded = @blast_seeded, blast_on = @blast_on,
                blast_seg_start = @blast_seg_start, blast_closed_sec = @blast_closed_sec,
                first_blast_ts = @first_blast_ts, cycle_count = @cycle_count,
                machine_seeded = @machine_seeded, machine_on = @machine_on,
                machine_seg_start = @machine_seg_start, machine_closed_sec = @machine_closed_sec,
                refill_count = @refill_count, first_refill_change_ts = @first_refill_change_ts,
                prev_refill_change_ts = @prev_refill_change_ts, last_refill_any_ts = @last_refill_any_ts,
                energy_total = @energy_total, last_cycle_number = @last_cycle_number
            WHERE id = 1";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("last_hist_id", s.LastHistId);
            cmd.Parameters.AddWithValue("blast_seeded", s.BlastSeeded);
            cmd.Parameters.AddWithValue("blast_on", s.BlastOn);
            cmd.Parameters.AddWithValue("blast_seg_start", (object?)s.BlastSegStart ?? DBNull.Value);
            cmd.Parameters.AddWithValue("blast_closed_sec", s.BlastClosedSec);
            cmd.Parameters.AddWithValue("first_blast_ts", (object?)s.FirstBlastTs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("cycle_count", s.CycleCount);
            cmd.Parameters.AddWithValue("machine_seeded", s.MachineSeeded);
            cmd.Parameters.AddWithValue("machine_on", s.MachineOn);
            cmd.Parameters.AddWithValue("machine_seg_start", (object?)s.MachineSegStart ?? DBNull.Value);
            cmd.Parameters.AddWithValue("machine_closed_sec", s.MachineClosedSec);
            cmd.Parameters.AddWithValue("refill_count", s.RefillCount);
            cmd.Parameters.AddWithValue("first_refill_change_ts", (object?)s.FirstRefillChangeTs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("prev_refill_change_ts", (object?)s.PrevRefillChangeTs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("last_refill_any_ts", (object?)s.LastRefillAnyTs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("energy_total", s.EnergyTotal);
            cmd.Parameters.AddWithValue("last_cycle_number", s.LastCycleNumber);
            await cmd.ExecuteNonQueryAsync();
        }, "SaveAggregationState");
    }

    // Resets the incremental state so the next pass replays the full history (--rebuild-aggregation).
    public async Task ResetAggregationStateAsync()
    {
        const string sql = @"
            UPDATE plc_aggregation_state SET
                last_hist_id = 0, blast_seeded = FALSE, blast_on = FALSE, blast_seg_start = NULL,
                blast_closed_sec = 0, first_blast_ts = NULL, cycle_count = 0, machine_seeded = FALSE,
                machine_on = FALSE, machine_seg_start = NULL, machine_closed_sec = 0, refill_count = 0,
                first_refill_change_ts = NULL, prev_refill_change_ts = NULL, last_refill_any_ts = NULL,
                energy_total = 0, last_cycle_number = 0
            WHERE id = 1";
        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }, "ResetAggregationState");
    }

    // New Tier 2 events (id > lastId) for the given tags, in id order, for the incremental fold.
    public async Task<List<AggEvent>> GetNewAggregationEventsAsync(long lastId, string[] tagNames, int limit)
    {
        var events = new List<AggEvent>();
        const string sql = @"
            SELECT id, parameter_name, timestamp,
                   CASE WHEN value_bool IS NOT NULL THEN value_bool
                        WHEN value_num IS NOT NULL THEN value_num <> 0
                        WHEN value IS NOT NULL THEN lower(value) IN ('1','true')
                        ELSE NULL END AS is_on,
                   previous_value, storage_reason
            FROM plc_historical_data
            WHERE id > @lastId AND parameter_name = ANY(@names)
            ORDER BY id ASC
            LIMIT @limit";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("lastId", lastId);
            cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = tagNames });
            cmd.Parameters.AddWithValue("limit", limit);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                events.Add(new AggEvent
                {
                    Id            = r.GetInt64(0),
                    ParameterName = r.GetString(1),
                    Timestamp     = r.GetDateTime(2),
                    ValueBool     = r.IsDBNull(3) ? (bool?)null : r.GetBoolean(3),
                    PreviousValue = r.IsDBNull(4) ? null : r.GetString(4),
                    StorageReason = r.GetString(5)
                });
            }
        }, "GetNewAggregationEvents");

        return events;
    }

    // Blast rising edges (off→on) strictly after @prev and up to and including @curr.
    public async Task<int> CountBlastRisingEdgesBetweenAsync(string blastTag, DateTime prev, DateTime curr)
    {
        int count = 0;
        const string sql = @"
            SELECT COUNT(*) FROM plc_historical_data
            WHERE parameter_name = @name
              AND timestamp > @prev AND timestamp <= @curr
              AND (value_bool IS TRUE OR value = '1' OR lower(value) = 'true')
              AND (previous_value IS NULL OR previous_value = '0' OR lower(previous_value) = 'false')";
        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name", blastTag);
            cmd.Parameters.AddWithValue("prev", prev);
            cmd.Parameters.AddWithValue("curr", curr);
            var scalar = await cmd.ExecuteScalarAsync();
            count = Convert.ToInt32(scalar);
        }, "CountBlastRisingEdgesBetween");
        return count;
    }

    // Sum of energy_kwh and max cycle_number for cycles newer than the given watermark.
    public async Task<(decimal EnergyDelta, int MaxCycleNumber)> GetCycleEnergyAboveAsync(int lastCycleNumber)
    {
        decimal energy = 0; int maxNum = lastCycleNumber;
        const string sql = @"
            SELECT ROUND(COALESCE(SUM(energy_kwh), 0), 6), COALESCE(MAX(cycle_number), @last)
            FROM plc_cycles WHERE cycle_number > @last";
        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("last", lastCycleNumber);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                energy = r.GetDecimal(0);
                maxNum = r.GetInt32(1);
            }
        }, "GetCycleEnergyAbove");
        return (energy, maxNum);
    }

    // ════════════════════════════════════════════════════════════════════════
    // SECTION 2: Per-metal production (plc_filtered_metal_production)
    // ════════════════════════════════════════════════════════════════════════

    public async Task InsertFilteredMetalProductionAsync(int requestId, string metalName, decimal productionKg)
    {
        const string sql = @"
            INSERT INTO plc_filtered_metal_production (request_id, metal_name, production_kg, calculated_at)
            VALUES (@request_id, @metal, @prod, NOW())";
        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("request_id", requestId);
            cmd.Parameters.AddWithValue("metal", metalName);
            cmd.Parameters.AddWithValue("prod", productionKg);
            await cmd.ExecuteNonQueryAsync();
        }, $"InsertFilteredMetalProduction req={requestId} {metalName}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // GATEWAY STATUS (gateway_status) — PLC connection state + disconnect handling
    // ════════════════════════════════════════════════════════════════════════

    public async Task UpdateGatewayScanHeartbeatAsync(DateTime scanAt)
    {
        const string sql = "UPDATE gateway_status SET last_scan_at = @scan_at WHERE id = 1";
        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("scan_at", scanAt);
            await cmd.ExecuteNonQueryAsync();
        }, "UpdateGatewayScanHeartbeat");
    }

    public async Task SetGatewayConnectedAsync(bool connected, DateTime changedAt)
    {
        const string sql = "UPDATE gateway_status SET plc_connected = @connected, changed_at = @changed_at WHERE id = 1";
        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("connected", connected);
            cmd.Parameters.AddWithValue("changed_at", changedAt);
            await cmd.ExecuteNonQueryAsync();
        }, $"SetGatewayConnected {connected}");
    }

    // Last moment the gateway actually observed the PLC. Falls back to the newest Tier 1
    // write for databases that predate gateway_status, so a startup gap is never backdated
    // to "now" (which would fabricate ON-time across the gap).
    public async Task<DateTime?> GetGatewayLastScanAtAsync()
    {
        DateTime? result = null;
        const string sql = @"
            SELECT COALESCE(
                (SELECT last_scan_at FROM gateway_status WHERE id = 1),
                (SELECT MAX(last_updated) FROM plc_current_values))";
        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var scalar = await cmd.ExecuteScalarAsync();
            if (scalar != null && scalar != DBNull.Value)
                result = Convert.ToDateTime(scalar);
        }, "GetGatewayLastScanAt");
        return result;
    }

    /// <summary>
    /// Records a PLC disconnect in one transaction, backdated to the last moment the PLC
    /// state was actually known:
    ///   1. every Tier 1 row is marked stale (last-known values must not be treated as live),
    ///   2. for each state tag (machine status / blast) whose last known value was ON, a
    ///      forced-OFF record is written to Tier 2 with storage_reason = 'DISCONNECT' so all
    ///      duration/edge calculations see the gap as OFF (zero contribution),
    ///   3. Tier 1 for those tags is forced to '0' (authoritative, not stale),
    ///   4. plc_lifetime_parameters.machine_status is set to 0 immediately,
    ///   5. gateway_status is flipped to disconnected.
    /// Safe to call when the tags are already OFF or unknown — those steps become no-ops.
    /// </summary>
    public async Task RecordPlcDisconnectAsync(DateTime disconnectAt, params string[] forceOffTagNames)
    {
        await RetryAsync(async conn =>
        {
            await using var tx = await conn.BeginTransactionAsync();

            await using (var stale = new NpgsqlCommand(
                "UPDATE plc_current_values SET is_stale = TRUE", conn, tx))
                await stale.ExecuteNonQueryAsync();

            foreach (var tagName in forceOffTagNames)
            {
                string? address = null, lastValue = null, dataType = null;
                await using (var read = new NpgsqlCommand(
                    $"SELECT address, {ValueExpr}, data_type FROM plc_current_values WHERE parameter_name = @name LIMIT 1",
                    conn, tx))
                {
                    read.Parameters.AddWithValue("name", tagName);
                    await using var reader = await read.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        address   = reader.GetString(0);
                        lastValue = reader.IsDBNull(1) ? null : reader.GetString(1);
                        dataType  = reader.GetString(2);
                    }
                }

                if (address == null) continue; // tag never seen — nothing to force off

                bool isBool = string.Equals(dataType, "BOOL", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(dataType, "BOOLEAN", StringComparison.OrdinalIgnoreCase);

                bool wasOn = !string.IsNullOrEmpty(lastValue)
                             && lastValue != "0"
                             && !string.Equals(lastValue, "false", StringComparison.OrdinalIgnoreCase);
                if (wasOn)
                {
                    // Synthetic forced-OFF event: 0 into value_bool (BOOL) or value_num (numeric).
                    await using (var hist = new NpgsqlCommand(@"
                        INSERT INTO plc_historical_data
                            (address, parameter_name, value, value_num, value_bool, data_type, storage_reason, timestamp, previous_value)
                        VALUES (@address, @name, NULL, @vnum, @vbool, @data_type, 'DISCONNECT', @ts, @prev)", conn, tx))
                    {
                        hist.Parameters.AddWithValue("address", address);
                        hist.Parameters.AddWithValue("name", tagName);
                        hist.Parameters.AddWithValue("vnum",  isBool ? (object)DBNull.Value : 0m);
                        hist.Parameters.AddWithValue("vbool", isBool ? (object)false : DBNull.Value);
                        hist.Parameters.AddWithValue("data_type", dataType ?? "UNKNOWN");
                        hist.Parameters.AddWithValue("ts", disconnectAt);
                        hist.Parameters.AddWithValue("prev", (object?)lastValue ?? DBNull.Value);
                        await hist.ExecuteNonQueryAsync();
                    }
                }

                // Forced OFF is an authoritative statement (disconnected ⇒ treated as OFF), not stale.
                await using (var force = new NpgsqlCommand(@"
                    UPDATE plc_current_values
                    SET value = NULL, value_num = @vnum, value_bool = @vbool, value_text = NULL,
                        last_updated = @ts, is_stale = FALSE
                    WHERE address = @address", conn, tx))
                {
                    force.Parameters.AddWithValue("vnum",  isBool ? (object)DBNull.Value : 0m);
                    force.Parameters.AddWithValue("vbool", isBool ? (object)false : DBNull.Value);
                    force.Parameters.AddWithValue("ts", disconnectAt);
                    force.Parameters.AddWithValue("address", address);
                    await force.ExecuteNonQueryAsync();
                }
            }

            await using (var param = new NpgsqlCommand(@"
                INSERT INTO plc_lifetime_parameters (parameter_name, value, updated_at)
                VALUES ('machine_status', 0, NOW())
                ON CONFLICT (parameter_name)
                DO UPDATE SET value = 0, updated_at = NOW()", conn, tx))
                await param.ExecuteNonQueryAsync();

            await using (var status = new NpgsqlCommand(
                "UPDATE gateway_status SET plc_connected = FALSE, changed_at = @ts WHERE id = 1", conn, tx))
            {
                status.Parameters.AddWithValue("ts", disconnectAt);
                await status.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }, "RecordPlcDisconnect");
    }

    // ════════════════════════════════════════════════════════════════════════
    // INTERNAL HELPERS
    // ════════════════════════════════════════════════════════════════════════

    private static PlcCurrentValue ReadCurrentValueRow(NpgsqlDataReader reader) => new()
    {
        Address              = reader.GetString(0),
        ParameterName        = reader.GetString(1),
        Value                = reader.IsDBNull(2) ? null : reader.GetString(2),
        DataType             = reader.GetString(3),
        LastUpdated          = reader.GetDateTime(4),
        LastStoredHistorical = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
        LastHeartbeat        = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
        IsStale              = !reader.IsDBNull(7) && reader.GetBoolean(7)
    };

    private static PlcHistoricalData ReadHistoricalRow(NpgsqlDataReader reader) => new()
    {
        Id            = reader.GetInt32(0),
        Address       = reader.GetString(1),
        ParameterName = reader.GetString(2),
        Value         = reader.IsDBNull(3) ? null : reader.GetString(3),
        DataType      = reader.GetString(4),
        StorageReason = reader.GetString(5),
        Timestamp     = reader.GetDateTime(6),
        PreviousValue = reader.IsDBNull(7) ? null : reader.GetString(7)
    };

    private static PlcCycle ReadCycleRow(NpgsqlDataReader reader) => new()
    {
        CycleNumber     = reader.GetInt32(0),
        BlastStart      = reader.GetDateTime(1),
        BlastEnd        = reader.GetDateTime(2),
        DurationSec     = reader.IsDBNull(3)  ? 0    : (double)reader.GetDecimal(3),
        Metal1Name      = reader.IsDBNull(4)  ? null : reader.GetString(4),
        Metal1WeightKg  = reader.IsDBNull(5)  ? null : (double?)reader.GetDecimal(5),
        Metal2Name      = reader.IsDBNull(6)  ? null : reader.GetString(6),
        Metal2WeightKg  = reader.IsDBNull(7)  ? null : (double?)reader.GetDecimal(7),
        Metal3Name      = reader.IsDBNull(8)  ? null : reader.GetString(8),
        Metal3WeightKg  = reader.IsDBNull(9)  ? null : (double?)reader.GetDecimal(9),
        Metal4Name      = reader.IsDBNull(10) ? null : reader.GetString(10),
        Metal4WeightKg  = reader.IsDBNull(11) ? null : (double?)reader.GetDecimal(11),
        TonnageKg       = reader.IsDBNull(12) ? null : (double?)reader.GetDecimal(12),
        ProductionKg    = reader.IsDBNull(13) ? null : (double?)reader.GetDecimal(13),
        EnergyKwh       = reader.IsDBNull(14) ? null : (double?)reader.GetDecimal(14)
    };

    private async Task RetryAsync(Func<NpgsqlConnection, Task> action, string context)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                await action(conn);
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= _maxRetries)
                {
                    _logger?.LogError(ex, "DB operation '{context}' failed after {n} attempts.", context, attempt);
                    return;
                }
                _logger?.LogWarning(ex, "DB operation '{context}' attempt {n} failed, retrying.", context, attempt);
                int delay = _baseDelayMs * (int)Math.Pow(2, attempt - 1) + new Random().Next(0, 200);
                await Task.Delay(delay);
            }
        }
    }
}
