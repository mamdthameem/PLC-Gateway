using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public class MachineStatusService : IMachineStatusService
{
    private readonly string _connectionString;
    private readonly ILogger<MachineStatusService> _logger;

    public MachineStatusService(IConfiguration config, ILogger<MachineStatusService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<MachineStatusDto?> GetStatusAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Join the single gateway_status row so the dashboard sees the live PLC link state
            // alongside the machine value. When disconnected, the value is a forced 0 and stale.
            string sql = $@"
                SELECT {SqlExpressions.TypedValue("cv")},
                       cv.last_updated, cv.is_stale,
                       COALESCE(gs.plc_connected, FALSE), gs.last_scan_at
                FROM plc_current_values cv
                LEFT JOIN gateway_status gs ON gs.id = 1
                WHERE cv.address = 'DB60.DBB0'
                LIMIT 1;";

            await using var cmd    = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync()) return null;
            return new MachineStatusDto
            {
                Value        = reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString() ?? "",
                LastUpdated  = reader.GetDateTime(1),
                IsStale      = !reader.IsDBNull(2) && reader.GetBoolean(2),
                PlcConnected = reader.GetBoolean(3),
                LastScanAt   = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read machine status from plc_current_values");
            throw;
        }
    }
}
