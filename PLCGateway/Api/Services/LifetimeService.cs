using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public class LifetimeService : ILifetimeService
{
    private readonly string _connectionString;
    private readonly ILogger<LifetimeService> _logger;

    public LifetimeService(IConfiguration config, ILogger<LifetimeService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<List<LifetimeParameterDto>> GetAllAsync()
    {
        var results = new List<LifetimeParameterDto>();

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT parameter_name, value, updated_at
                FROM plc_lifetime_parameters
                ORDER BY parameter_name;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                results.Add(new LifetimeParameterDto
                {
                    ParameterName = reader.GetString(0),
                    Value         = reader.IsDBNull(1) ? "" : reader.GetValue(1)?.ToString() ?? "",
                    UpdatedAt     = reader.IsDBNull(2) ? DateTime.UtcNow : reader.GetDateTime(2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read plc_lifetime_parameters");
            throw;
        }

        return results;
    }
}
