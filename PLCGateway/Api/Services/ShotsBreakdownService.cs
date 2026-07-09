using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public class ShotsBreakdownService : IShotsBreakdownService
{
    private readonly string _connectionString;
    private readonly ILogger<ShotsBreakdownService> _logger;

    public ShotsBreakdownService(IConfiguration config, ILogger<ShotsBreakdownService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<List<ShotsBreakdownDto>> GetAllAsync()
    {
        var results = new List<ShotsBreakdownDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT refill_timestamp, blast_count
                FROM plc_shots_breakdown
                ORDER BY refill_timestamp ASC;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                results.Add(new ShotsBreakdownDto
                {
                    RefillTimestamp = reader.GetDateTime(0),
                    BlastCount      = reader.GetInt32(1)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read plc_shots_breakdown");
            throw;
        }
        return results;
    }
}
