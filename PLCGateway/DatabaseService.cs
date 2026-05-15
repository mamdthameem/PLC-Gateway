using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Npgsql;
using Microsoft.Extensions.Logging;
using PLCGateway.Models;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;
    private readonly int _maxRetries = 5;
    private readonly int _baseDelayMs = 500;

    public DatabaseService(string connectionString, ILogger<DatabaseService> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════════════
    // TIER 1: Real-Time Current Values (plc_current_values)
    // ════════════════════════════════════════════════════════════════════════

    public async Task UpsertCurrentValueAsync(string address, string parameterName, string value, string dataType)
    {
        if (string.IsNullOrEmpty(address)) throw new ArgumentNullException(nameof(address));
        string val = value ?? "";

        const string sql = @"
            INSERT INTO plc_current_values (address, parameter_name, value, data_type, last_updated)
            VALUES (@address, @parameter_name, @value, @data_type, NOW())
            ON CONFLICT (address)
            DO UPDATE SET
                parameter_name = EXCLUDED.parameter_name,
                value          = EXCLUDED.value,
                data_type      = EXCLUDED.data_type,
                last_updated   = NOW()";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("address", address);
            cmd.Parameters.AddWithValue("parameter_name", parameterName);
            cmd.Parameters.AddWithValue("value", val);
            cmd.Parameters.AddWithValue("data_type", dataType);
            await cmd.ExecuteNonQueryAsync();
        }, $"UpsertCurrentValue {address}");
    }

    public async Task<PlcCurrentValue?> GetCurrentValueAsync(string address)
    {
        if (string.IsNullOrEmpty(address)) return null;

        PlcCurrentValue? result = null;
        const string sql = @"
            SELECT address, parameter_name, value, data_type, last_updated, last_stored_historical, last_heartbeat
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
        const string sql = @"
            SELECT address, parameter_name, value, data_type, last_updated, last_stored_historical, last_heartbeat
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

    public async Task UpdateLastStoredHistoricalAsync(string address)
    {
        const string sql = "UPDATE plc_current_values SET last_stored_historical = NOW() WHERE address = @address";
        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("address", address);
            await cmd.ExecuteNonQueryAsync();
        }, $"UpdateLastStoredHistorical {address}");
    }

    public async Task UpdateLastHeartbeatAsync(string address)
    {
        const string sql = "UPDATE plc_current_values SET last_heartbeat = NOW() WHERE address = @address";
        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("address", address);
            await cmd.ExecuteNonQueryAsync();
        }, $"UpdateLastHeartbeat {address}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // TIER 2: Historical Time-Series (plc_historical_data) — never deleted
    // ════════════════════════════════════════════════════════════════════════

    public async Task InsertHistoricalDataAsync(string address, string parameterName, string value, string dataType, string storageReason, string? previousValue = null)
    {
        if (string.IsNullOrEmpty(address)) throw new ArgumentNullException(nameof(address));
        string val = value ?? "";

        const string sql = @"
            INSERT INTO plc_historical_data
                (address, parameter_name, value, data_type, storage_reason, timestamp, previous_value)
            VALUES
                (@address, @parameter_name, @value, @data_type, @storage_reason, NOW(), @previous_value)";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("address", address);
            cmd.Parameters.AddWithValue("parameter_name", parameterName);
            cmd.Parameters.AddWithValue("value", val);
            cmd.Parameters.AddWithValue("data_type", dataType);
            cmd.Parameters.AddWithValue("storage_reason", storageReason);
            cmd.Parameters.AddWithValue("previous_value", previousValue ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }, $"InsertHistoricalData {address}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // QUERY HELPERS (used by CalculationService and CycleTrackingService)
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<PlcHistoricalData>> GetStateChangesAsync(string parameterName, DateTime start, DateTime end)
    {
        var results = new List<PlcHistoricalData>();

        const string sql = @"
            SELECT id, address, parameter_name, value, data_type, storage_reason, timestamp, previous_value
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

    public async Task<PlcHistoricalData?> GetLatestHistoricalAsync(string parameterName)
    {
        PlcHistoricalData? result = null;

        const string sql = @"
            SELECT id, address, parameter_name, value, data_type, storage_reason, timestamp, previous_value
            FROM plc_historical_data
            WHERE parameter_name = @name
            ORDER BY timestamp DESC LIMIT 1";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name", parameterName);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                result = ReadHistoricalRow(reader);
        }, $"GetLatestHistorical {parameterName}");

        return result;
    }

    public async Task<PlcHistoricalData?> GetLatestHistoricalBeforeAsync(string parameterName, DateTime before)
    {
        PlcHistoricalData? result = null;

        const string sql = @"
            SELECT id, address, parameter_name, value, data_type, storage_reason, timestamp, previous_value
            FROM plc_historical_data
            WHERE parameter_name = @name AND timestamp <= @before
            ORDER BY timestamp DESC LIMIT 1";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name",   parameterName);
            cmd.Parameters.AddWithValue("before", before);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                result = ReadHistoricalRow(reader);
        }, $"GetLatestHistoricalBefore {parameterName}");

        return result;
    }

    // Returns the timestamp of the last TRUE value for a BOOL tag before a given moment.
    // Used by CycleTrackingService to find blast_start when a falling edge is detected.
    public async Task<DateTime?> GetLastTrueTimestampBeforeAsync(string parameterName, DateTime before)
    {
        DateTime? result = null;

        const string sql = @"
            SELECT timestamp FROM plc_historical_data
            WHERE parameter_name = @name
              AND (value = '1' OR value = 'True' OR value = 'true')
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

        const string sql = @"
            INSERT INTO plc_cycles
                (blast_start, blast_end, duration_sec,
                 metal_1_name, metal_1_weight_kg,
                 metal_2_name, metal_2_weight_kg,
                 metal_3_name, metal_3_weight_kg,
                 metal_4_name, metal_4_weight_kg,
                 tonnage_kg)
            VALUES
                (@blast_start, @blast_end, @duration_sec,
                 @m1n, @m1w, @m2n, @m2w, @m3n, @m3w, @m4n, @m4w,
                 @tonnage)
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
                   tonnage_kg
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
                   tonnage_kg
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
                   tonnage_kg
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

    // Returns the cycle immediately before the given cycle number (for tonnage delta).
    public async Task<PlcCycle?> GetCyclePrecedingAsync(int cycleNumber)
    {
        PlcCycle? result = null;
        const string sql = @"
            SELECT cycle_number, blast_start, blast_end, duration_sec,
                   metal_1_name, metal_1_weight_kg, metal_2_name, metal_2_weight_kg,
                   metal_3_name, metal_3_weight_kg, metal_4_name, metal_4_weight_kg,
                   tonnage_kg
            FROM plc_cycles
            WHERE cycle_number < @num
            ORDER BY cycle_number DESC LIMIT 1";

        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("num", cycleNumber);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                result = ReadCycleRow(reader);
        }, "GetCyclePreceding");

        return result;
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
    // Cleared and rewritten each minute by AggregationService.
    // Shared dataset for parameters #7 (shots usage) and #8 (refill time).
    // ════════════════════════════════════════════════════════════════════════

    public async Task ClearLifetimeShotsBreakdownAsync()
    {
        await RetryAsync(async conn =>
        {
            await using var cmd = new NpgsqlCommand("TRUNCATE TABLE plc_shots_breakdown", conn);
            await cmd.ExecuteNonQueryAsync();
        }, "ClearLifetimeShotsBreakdown");
    }

    public async Task InsertLifetimeShotsBreakdownAsync(DateTime refillTimestamp, int blastCount)
    {
        const string sql = @"
            INSERT INTO plc_shots_breakdown (refill_timestamp, blast_count, calculated_at)
            VALUES (@ts, @count, NOW())";

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
        LastHeartbeat        = reader.IsDBNull(6) ? null : reader.GetDateTime(6)
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
        TonnageKg       = reader.IsDBNull(12) ? null : (double?)reader.GetDecimal(12)
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
