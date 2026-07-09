using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public class HistoricalService : IHistoricalService
{
    private readonly string _connectionString;
    private readonly ILogger<HistoricalService> _logger;

    public HistoricalService(IConfiguration config, ILogger<HistoricalService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<List<HistoricalPointDto>> GetPointsAsync(
        string parameterName, DateTime start, DateTime end)
    {
        var results = new List<HistoricalPointDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            string sql = $@"
                SELECT {SqlExpressions.TypedValue()}, timestamp
                FROM plc_historical_data
                WHERE parameter_name = @name
                  AND timestamp >= @start
                  AND timestamp <= @end
                  AND storage_reason IN ('COV','STATE_CHANGE','VALUE_CHANGE','BLAST_ON','INITIAL','HEARTBEAT')
                ORDER BY timestamp ASC;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("name",  parameterName);
            cmd.Parameters.AddWithValue("start", DateTime.SpecifyKind(start, DateTimeKind.Utc));
            cmd.Parameters.AddWithValue("end",   DateTime.SpecifyKind(end,   DateTimeKind.Utc));
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                results.Add(new HistoricalPointDto
                {
                    Value     = reader.GetValue(0)?.ToString() ?? "",
                    Timestamp = reader.GetDateTime(1)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read historical data for {Name}", parameterName);
            throw;
        }
        return results;
    }
}
