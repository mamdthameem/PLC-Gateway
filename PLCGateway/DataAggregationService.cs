using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

public class DataAggregationService : BackgroundService
{
    private readonly ILogger<DataAggregationService> _logger;
    private readonly IConfiguration _configuration;
    private readonly string _connectionString;
    private readonly int[] _aggregationDays = { 30, 60, 90, 365 };

    public DataAggregationService(
        ILogger<DataAggregationService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _connectionString = configuration.GetValue<string>("PostgreSQL:ConnectionString") 
            ?? throw new InvalidOperationException("PostgreSQL connection string not found");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DataAggregationService starting for long-term data compression.");

        // Run daily at 2 AM to check for data ready for aggregation
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;

                // Run daily at 2 AM
                if (now.Hour == 2 && now.Minute == 0 && now.Second < 60)
                {
                    _logger.LogInformation("Starting daily data aggregation check.");

                    foreach (var ageDays in _aggregationDays)
                    {
                        await AggregateDataForAgeAsync(ageDays);
                    }

                    _logger.LogInformation("Daily data aggregation check completed.");
                }

                // Check every hour
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in data aggregation service loop");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        _logger.LogInformation("DataAggregationService stopping.");
    }

    /// <summary>
    /// Aggregate historical data to hourly min/max/avg for data older than specified days.
    /// </summary>
    private async Task AggregateDataForAgeAsync(int ageDays)
    {
        try
        {
            _logger.LogInformation("Aggregating data older than {ageDays} days.", ageDays);

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Aggregate numeric data to hourly min/max/avg
            var sql = $@"
                INSERT INTO plc_aggregated_hourly (
                    address, parameter_name, hour_timestamp, 
                    min_value, max_value, avg_value, sample_count, 
                    aggregation_date, data_age_days
                )
                SELECT 
                    address,
                    parameter_name,
                    date_trunc('hour', timestamp) as hour_timestamp,
                    MIN(CAST(value AS NUMERIC)) as min_value,
                    MAX(CAST(value AS NUMERIC)) as max_value,
                    AVG(CAST(value AS NUMERIC)) as avg_value,
                    COUNT(*) as sample_count,
                    CURRENT_DATE as aggregation_date,
                    {ageDays} as data_age_days
                FROM plc_historical_data
                WHERE timestamp < NOW() - INTERVAL '{ageDays} days'
                  AND data_type IN ('INT', 'REAL', 'DINT', 'WORD', 'DWORD')
                  AND value ~ '^[0-9]+\.?[0-9]*$'  -- Only numeric values
                  AND NOT EXISTS (
                      SELECT 1 FROM plc_aggregated_hourly 
                      WHERE plc_aggregated_hourly.address = plc_historical_data.address
                        AND plc_aggregated_hourly.parameter_name = plc_historical_data.parameter_name
                        AND plc_aggregated_hourly.hour_timestamp = date_trunc('hour', plc_historical_data.timestamp)
                        AND plc_aggregated_hourly.data_age_days = {ageDays}
                  )
                GROUP BY address, parameter_name, date_trunc('hour', timestamp)
                ON CONFLICT DO NOTHING";

            await using var cmd = new NpgsqlCommand(sql, conn);

            var rowsAffected = await cmd.ExecuteNonQueryAsync();
            _logger.LogInformation("Aggregated {rows} hourly records for {ageDays} day old data.", rowsAffected, ageDays);

            // Delete original detailed data after successful aggregation
            if (rowsAffected > 0)
            {
                var deleteSql = $@"
                    DELETE FROM plc_historical_data
                    WHERE timestamp < NOW() - INTERVAL '{ageDays} days'
                      AND data_type IN ('INT', 'REAL', 'DINT', 'WORD', 'DWORD')
                      AND value ~ '^[0-9]+\.?[0-9]*$'";

                await using var deleteCmd = new NpgsqlCommand(deleteSql, conn);

                var deletedRows = await deleteCmd.ExecuteNonQueryAsync();
                _logger.LogInformation("Deleted {deleted} original records after aggregation for {ageDays} day old data.", 
                    deletedRows, ageDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error aggregating data for {ageDays} days", ageDays);
        }
    }
}
