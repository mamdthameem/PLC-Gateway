using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public class CyclesService : ICyclesService
{
    private readonly string _connectionString;
    private readonly ILogger<CyclesService> _logger;

    public CyclesService(IConfiguration config, ILogger<CyclesService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<LatestCycleDto?> GetLatestAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT blast_start, blast_end
                FROM plc_cycles
                ORDER BY blast_end DESC
                LIMIT 1;";

            await using var cmd    = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync()) return null;
            return new LatestCycleDto
            {
                BlastStart = reader.GetDateTime(0),
                BlastEnd   = reader.GetDateTime(1)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read latest cycle");
            throw;
        }
    }
}
