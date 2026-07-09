using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public class SpareStatusService : ISpareStatusService
{
    private readonly string _connectionString;
    private readonly ILogger<SpareStatusService> _logger;

    public SpareStatusService(IConfiguration config, ILogger<SpareStatusService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<List<SpareStatusDto>> GetAllAsync()
    {
        return await QuerySpares(null);
    }

    public async Task<List<SpareStatusDto>> GetAlertsAsync()
    {
        return await QuerySpares(alertsOnly: true);
    }

    private async Task<List<SpareStatusDto>> QuerySpares(bool? alertsOnly)
    {
        var results = new List<SpareStatusDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var whereClause = alertsOnly == true
                ? "WHERE trigger_active = TRUE AND threshold_hours > 0"
                : string.Empty;

            var sql = $@"
                SELECT impeller_num, spare_index, spare_name, threshold_hours,
                       current_run_hours, trigger_active, last_replaced_at, last_updated_at
                FROM plc_spare_status
                {whereClause}
                ORDER BY impeller_num, spare_index;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                results.Add(new SpareStatusDto
                {
                    ImpellerNum     = reader.GetInt32(0),
                    SpareIndex      = reader.GetInt32(1),
                    SpareName       = reader.GetString(2),
                    ThresholdHours  = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                    CurrentRunHours = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                    TriggerActive   = !reader.IsDBNull(5) && reader.GetBoolean(5),
                    LastReplacedAt  = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                    LastUpdatedAt   = reader.IsDBNull(7) ? DateTime.UtcNow : reader.GetDateTime(7)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read plc_spare_status");
            throw;
        }
        return results;
    }
}
