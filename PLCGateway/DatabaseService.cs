using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Npgsql;
using Microsoft.Extensions.Logging;
using PLCGateway.Models;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;
    private readonly int _maxRetries = 5;
    private readonly int _baseDelayMs = 500; // exponential backoff base

    public DatabaseService(string connectionString, ILogger<DatabaseService> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger;
    }

    #region Tier 1: Real-Time Current Values

    /// <summary>
    /// Upsert (insert or update) current value in Tier 1 table.
    /// Always updates the latest value for real-time lookups.
    /// </summary>
    public async Task UpsertCurrentValueAsync(string address, string parameterName, string value, string dataType)
    {
        if (string.IsNullOrEmpty(address)) throw new ArgumentNullException(nameof(address));
        string val = value ?? "";

        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO plc_current_values (address, parameter_name, value, data_type, last_updated)
                    VALUES (@address, @parameter_name, @value, @data_type, NOW())
                    ON CONFLICT (address) 
                    DO UPDATE SET 
                        parameter_name = EXCLUDED.parameter_name,
                        value = EXCLUDED.value,
                        data_type = EXCLUDED.data_type,
                        last_updated = NOW()";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("address", address);
                cmd.Parameters.AddWithValue("parameter_name", parameterName);
                cmd.Parameters.AddWithValue("value", val);
                cmd.Parameters.AddWithValue("data_type", dataType);

                await cmd.ExecuteNonQueryAsync();
                return; // success
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Tier 1 upsert attempt {attempt} failed for {address}", attempt, address);

                if (attempt >= _maxRetries)
                {
                    _logger?.LogError(ex, "Tier 1 upsert failed after {attempts} attempts.", attempt);
                    return;
                }

                int delay = _baseDelayMs * (int)Math.Pow(2, attempt - 1);
                var jitter = new Random().Next(0, 200);
                await Task.Delay(delay + jitter);
            }
        }
    }

    /// <summary>
    /// Get current value for a tag from Tier 1 table.
    /// </summary>
    public async Task<PlcCurrentValue?> GetCurrentValueAsync(string address)
    {
        if (string.IsNullOrEmpty(address)) return null;

        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    SELECT address, parameter_name, value, data_type, last_updated, last_stored_historical, last_heartbeat
                    FROM plc_current_values
                    WHERE address = @address";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("address", address);

                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    return new PlcCurrentValue
                    {
                        Address = reader.GetString(0),
                        ParameterName = reader.GetString(1),
                        Value = reader.IsDBNull(2) ? null : reader.GetString(2),
                        DataType = reader.GetString(3),
                        LastUpdated = reader.GetDateTime(4),
                        LastStoredHistorical = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                        LastHeartbeat = reader.IsDBNull(6) ? null : reader.GetDateTime(6)
                    };
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get current value for {address} (attempt {attempt})", address, attempt);

                if (attempt >= _maxRetries)
                {
                    _logger?.LogError(ex, "Failed to get current value after {attempts} attempts.", attempt);
                    return null;
                }

                int delay = _baseDelayMs * (int)Math.Pow(2, attempt - 1);
                var jitter = new Random().Next(0, 200);
                await Task.Delay(delay + jitter);
            }
        }
    }

    /// <summary>
    /// Update last stored historical timestamp in Tier 1.
    /// </summary>
    public async Task UpdateLastStoredHistoricalAsync(string address)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    UPDATE plc_current_values 
                    SET last_stored_historical = NOW()
                    WHERE address = @address";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("address", address);

                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to update last stored historical for {address} (attempt {attempt})", address, attempt);

                if (attempt >= _maxRetries)
                {
                    return;
                }

                int delay = _baseDelayMs * (int)Math.Pow(2, attempt - 1);
                var jitter = new Random().Next(0, 200);
                await Task.Delay(delay + jitter);
            }
        }
    }

    /// <summary>
    /// Update last heartbeat timestamp in Tier 1.
    /// </summary>
    public async Task UpdateLastHeartbeatAsync(string address)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    UPDATE plc_current_values 
                    SET last_heartbeat = NOW()
                    WHERE address = @address";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("address", address);

                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to update last heartbeat for {address} (attempt {attempt})", address, attempt);

                if (attempt >= _maxRetries)
                {
                    return;
                }

                int delay = _baseDelayMs * (int)Math.Pow(2, attempt - 1);
                var jitter = new Random().Next(0, 200);
                await Task.Delay(delay + jitter);
            }
        }
    }

    #endregion

    #region Tier 2: Historical Time-Series Data

    /// <summary>
    /// Insert historical data into Tier 2 table with storage reason.
    /// </summary>
    public async Task InsertHistoricalDataAsync(string address, string parameterName, string value, string dataType, string storageReason, string? previousValue = null)
    {
        if (string.IsNullOrEmpty(address)) throw new ArgumentNullException(nameof(address));
        string val = value ?? "";

        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO plc_historical_data (address, parameter_name, value, data_type, storage_reason, timestamp, previous_value)
                    VALUES (@address, @parameter_name, @value, @data_type, @storage_reason, NOW(), @previous_value)";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("address", address);
                cmd.Parameters.AddWithValue("parameter_name", parameterName);
                cmd.Parameters.AddWithValue("value", val);
                cmd.Parameters.AddWithValue("data_type", dataType);
                cmd.Parameters.AddWithValue("storage_reason", storageReason);
                cmd.Parameters.AddWithValue("previous_value", previousValue ?? (object)DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
                return; // success
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Tier 2 insert attempt {attempt} failed for {address}", attempt, address);

                if (attempt >= _maxRetries)
                {
                    _logger?.LogError(ex, "Tier 2 insert failed after {attempts} attempts.", attempt);
                    return;
                }

                int delay = _baseDelayMs * (int)Math.Pow(2, attempt - 1);
                var jitter = new Random().Next(0, 200);
                await Task.Delay(delay + jitter);
            }
        }
    }

    /// <summary>
    /// Get historical data for a time period.
    /// </summary>
    public async Task<List<PlcHistoricalData>> GetHistoricalDataForPeriodAsync(string address, DateTime startTime, DateTime endTime)
    {
        var results = new List<PlcHistoricalData>();

        if (string.IsNullOrEmpty(address)) return results;

        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    SELECT id, address, parameter_name, value, data_type, storage_reason, timestamp, previous_value
                    FROM plc_historical_data
                    WHERE address = @address 
                      AND timestamp >= @start_time 
                      AND timestamp <= @end_time
                    ORDER BY timestamp ASC";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("address", address);
                cmd.Parameters.AddWithValue("start_time", startTime);
                cmd.Parameters.AddWithValue("end_time", endTime);

                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new PlcHistoricalData
                    {
                        Id = reader.GetInt32(0),
                        Address = reader.GetString(1),
                        ParameterName = reader.GetString(2),
                        Value = reader.IsDBNull(3) ? null : reader.GetString(3),
                        DataType = reader.GetString(4),
                        StorageReason = reader.GetString(5),
                        Timestamp = reader.GetDateTime(6),
                        PreviousValue = reader.IsDBNull(7) ? null : reader.GetString(7)
                    });
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to get historical data for {address} (attempt {attempt})", address, attempt);

                if (attempt >= _maxRetries)
                {
                    _logger?.LogError(ex, "Failed to get historical data after {attempts} attempts.", attempt);
                    return results;
                }

                int delay = _baseDelayMs * (int)Math.Pow(2, attempt - 1);
                var jitter = new Random().Next(0, 200);
                await Task.Delay(delay + jitter);
            }
        }
    }

    #endregion

    #region Query Helpers for Calculations

    /// <summary>
    /// Get all STATE_CHANGE / COV records for a tag within a time window, ordered by timestamp.
    /// Used to reconstruct blast durations, refill events, cycle counts.
    /// </summary>
    public async Task<List<PlcHistoricalData>> GetStateChangesAsync(string parameterName, DateTime start, DateTime end)
    {
        var results = new List<PlcHistoricalData>();
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                const string sql = @"
                    SELECT id, address, parameter_name, value, data_type, storage_reason, timestamp, previous_value
                    FROM plc_historical_data
                    WHERE parameter_name = @name
                      AND timestamp >= @start AND timestamp <= @end
                    ORDER BY timestamp ASC";
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("name", parameterName);
                cmd.Parameters.AddWithValue("start", start);
                cmd.Parameters.AddWithValue("end", end);
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    results.Add(new PlcHistoricalData
                    {
                        Id = reader.GetInt32(0),
                        Address = reader.GetString(1),
                        ParameterName = reader.GetString(2),
                        Value = reader.IsDBNull(3) ? null : reader.GetString(3),
                        DataType = reader.GetString(4),
                        StorageReason = reader.GetString(5),
                        Timestamp = reader.GetDateTime(6),
                        PreviousValue = reader.IsDBNull(7) ? null : reader.GetString(7)
                    });
                }
                return results;
            }
            catch (Exception ex)
            {
                if (attempt >= _maxRetries) { _logger?.LogError(ex, "GetStateChangesAsync failed after {n} attempts.", attempt); return results; }
                await Task.Delay(_baseDelayMs * (int)Math.Pow(2, attempt - 1) + new Random().Next(0, 200));
            }
        }
    }

    /// <summary>
    /// Get the most recent historical record for a tag (used for Last Refill Time, etc.)
    /// </summary>
    public async Task<PlcHistoricalData?> GetLatestHistoricalAsync(string parameterName)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                const string sql = @"
                    SELECT id, address, parameter_name, value, data_type, storage_reason, timestamp, previous_value
                    FROM plc_historical_data
                    WHERE parameter_name = @name
                    ORDER BY timestamp DESC LIMIT 1";
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("name", parameterName);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                    return new PlcHistoricalData
                    {
                        Id = reader.GetInt32(0), Address = reader.GetString(1),
                        ParameterName = reader.GetString(2),
                        Value = reader.IsDBNull(3) ? null : reader.GetString(3),
                        DataType = reader.GetString(4), StorageReason = reader.GetString(5),
                        Timestamp = reader.GetDateTime(6),
                        PreviousValue = reader.IsDBNull(7) ? null : reader.GetString(7)
                    };
                return null;
            }
            catch (Exception ex)
            {
                if (attempt >= _maxRetries) { _logger?.LogError(ex, "GetLatestHistoricalAsync failed."); return null; }
                await Task.Delay(_baseDelayMs * (int)Math.Pow(2, attempt - 1) + new Random().Next(0, 200));
            }
        }
    }

    /// <summary>
    /// Get the earliest historical record for Machine status != 0 after a given time.
    /// Used to determine shift start.
    /// </summary>
    public async Task<DateTime?> GetShiftStartAsync(DateTime since)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                const string sql = @"
                    SELECT timestamp FROM plc_historical_data
                    WHERE parameter_name = 'Machine status'
                      AND value != '0'
                      AND timestamp >= @since
                    ORDER BY timestamp ASC LIMIT 1";
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("since", since);
                var result = await cmd.ExecuteScalarAsync();
                return result == DBNull.Value || result == null ? null : (DateTime?)Convert.ToDateTime(result);
            }
            catch (Exception ex)
            {
                if (attempt >= _maxRetries) { _logger?.LogError(ex, "GetShiftStartAsync failed."); return null; }
                await Task.Delay(_baseDelayMs * (int)Math.Pow(2, attempt - 1) + new Random().Next(0, 200));
            }
        }
    }

    /// <summary>
    /// Upsert a calculated metric — updates existing row if same metric+period+period_value exists.
    /// Uses two separate SQL statements because PostgreSQL partial indexes require
    /// the WHERE clause to be specified explicitly in ON CONFLICT.
    /// </summary>
    public async Task UpsertCalculatedMetricAsync(string metricName, decimal? metricValue, string periodType, DateTime? periodValue = null, string? metadataJson = null)
    {
        if (string.IsNullOrEmpty(metricName)) return;
        int attempt = 0;

        // Two different SQL depending on whether period_value is null
        // (realtime/cumulative have NULL period_value, all others have a timestamp)
        string sql = periodValue.HasValue
            ? @"INSERT INTO calculated_metrics (metric_name, metric_value, period_type, period_value, calculation_timestamp, metadata)
                VALUES (@metric_name, @metric_value, @period_type, @period_value, NOW(), @metadata::jsonb)
                ON CONFLICT (metric_name, period_type, period_value) WHERE period_value IS NOT NULL
                DO UPDATE SET metric_value = EXCLUDED.metric_value, calculation_timestamp = NOW(), metadata = EXCLUDED.metadata"
            : @"INSERT INTO calculated_metrics (metric_name, metric_value, period_type, period_value, calculation_timestamp, metadata)
                VALUES (@metric_name, @metric_value, @period_type, NULL, NOW(), @metadata::jsonb)
                ON CONFLICT (metric_name, period_type) WHERE period_value IS NULL
                DO UPDATE SET metric_value = EXCLUDED.metric_value, calculation_timestamp = NOW(), metadata = EXCLUDED.metadata";

        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("metric_name", metricName);
                cmd.Parameters.AddWithValue("metric_value", metricValue ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("period_type", periodType);
                if (periodValue.HasValue)
                    cmd.Parameters.AddWithValue("period_value", periodValue.Value);
                cmd.Parameters.AddWithValue("metadata", metadataJson ?? (object)DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= _maxRetries) { _logger?.LogError(ex, "UpsertCalculatedMetricAsync failed for {m}.", metricName); return; }
                await Task.Delay(_baseDelayMs * (int)Math.Pow(2, attempt - 1) + new Random().Next(0, 200));
            }
        }
    }

    #endregion

    #region Calculated Metrics

    /// <summary>
    /// Insert calculated metric.
    /// </summary>
    public async Task InsertCalculatedMetricAsync(string metricName, decimal? metricValue, string periodType, DateTime? periodValue = null, string? metadataJson = null)
    {
        if (string.IsNullOrEmpty(metricName)) throw new ArgumentNullException(nameof(metricName));

        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO calculated_metrics (metric_name, metric_value, period_type, period_value, calculation_timestamp, metadata)
                    VALUES (@metric_name, @metric_value, @period_type, @period_value, NOW(), @metadata::jsonb)";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("metric_name", metricName);
                cmd.Parameters.AddWithValue("metric_value", metricValue ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("period_type", periodType);
                cmd.Parameters.AddWithValue("period_value", periodValue ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("metadata", metadataJson ?? (object)DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
                return;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to insert calculated metric {metric} (attempt {attempt})", metricName, attempt);

                if (attempt >= _maxRetries)
                {
                    _logger?.LogError(ex, "Failed to insert calculated metric after {attempts} attempts.", attempt);
                    return;
                }

                int delay = _baseDelayMs * (int)Math.Pow(2, attempt - 1);
                var jitter = new Random().Next(0, 200);
                await Task.Delay(delay + jitter);
            }
        }
    }

    #endregion

    #region Legacy Methods (for backward compatibility)

    /// <summary>
    /// Legacy method - kept for backward compatibility.
    /// Now uses Tier 1 and Tier 2 architecture.
    /// </summary>
    [Obsolete("Use UpsertCurrentValueAsync and InsertHistoricalDataAsync instead")]
    public async Task InsertDataAsync(string tagName, object value, string direction = "Read")
    {
        // For writes, this is handled separately
        if (direction == "Write")
        {
            await Task.CompletedTask;
            return;
        }

        // For reads, update Tier 1 (this is a simplified version)
        // Convert value to string using default conversion
        string valueStr = value?.ToString() ?? "";
        await UpsertCurrentValueAsync(tagName, tagName, valueStr, "UNKNOWN");
    }

    #endregion
}
