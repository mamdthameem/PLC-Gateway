using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public class AmpsService : IAmpsService
{
    private readonly string _connectionString;
    private readonly ILogger<AmpsService> _logger;

    private static readonly string[] ImpellerNames =
    [
        "Current_imp_1","Current_imp_2","Current_imp_3","Current_imp_4","Current_imp_5",
        "Current_imp_6","Current_imp_7","Current_imp_8","Current_imp_9","Current_imp_10"
    ];

    public AmpsService(IConfiguration config, ILogger<AmpsService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<List<AmpReadingDto>> GetImpellerAmpsAsync()
    {
        var results = new List<AmpReadingDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            string sql = $@"
                SELECT parameter_name, {SqlExpressions.TypedValue()}, last_updated
                FROM plc_current_values
                WHERE parameter_name = ANY(@names)
                ORDER BY parameter_name;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("names", ImpellerNames);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                results.Add(new AmpReadingDto
                {
                    ParameterName = reader.GetString(0),
                    Value         = reader.IsDBNull(1) ? "0" : reader.GetValue(1)?.ToString() ?? "0",
                    LastUpdated   = reader.IsDBNull(2) ? DateTime.UtcNow : reader.GetDateTime(2)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read impeller amps from plc_current_values");
            throw;
        }
        return results;
    }
}
