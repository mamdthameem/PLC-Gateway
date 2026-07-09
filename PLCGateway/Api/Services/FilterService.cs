using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public class FilterService : IFilterService
{
    private readonly string _connectionString;
    private readonly ILogger<FilterService> _logger;

    public FilterService(IConfiguration config, ILogger<FilterService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<int> SubmitRequestAsync(FilterRequestInput input)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                INSERT INTO calculation_requests
                    (filter_start, filter_end, period_label, filter_by,
                     filter_cycle_from, filter_cycle_to, filter_metal_name)
                VALUES
                    (@start, @end, @label, @filterBy,
                     @cycleFrom, @cycleTo, @metalName)
                RETURNING id;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("start",     input.FilterStart);
            cmd.Parameters.AddWithValue("end",       input.FilterEnd);
            cmd.Parameters.AddWithValue("label",     input.PeriodLabel      is null ? DBNull.Value : (object)input.PeriodLabel);
            cmd.Parameters.AddWithValue("filterBy",  input.FilterBy);
            cmd.Parameters.AddWithValue("cycleFrom", input.FilterCycleFrom  is null ? DBNull.Value : (object)input.FilterCycleFrom);
            cmd.Parameters.AddWithValue("cycleTo",   input.FilterCycleTo    is null ? DBNull.Value : (object)input.FilterCycleTo);
            cmd.Parameters.AddWithValue("metalName", input.FilterMetalName  is null ? DBNull.Value : (object)input.FilterMetalName);

            var id = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert calculation_request");
            throw;
        }
    }

    public async Task<FilterStatusDto?> GetStatusAsync(int requestId)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT status, processed_at
                FROM calculation_requests
                WHERE id = @id;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", requestId);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (!await reader.ReadAsync()) return null;
            return new FilterStatusDto
            {
                Status      = reader.GetString(0),
                ProcessedAt = reader.IsDBNull(1) ? null : reader.GetDateTime(1)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read status for request {Id}", requestId);
            throw;
        }
    }

    public async Task<List<FilterResultDto>> GetResultsAsync(int requestId)
    {
        var results = new List<FilterResultDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT parameter_name, value
                FROM plc_filtered_parameters
                WHERE request_id = @id
                ORDER BY parameter_name;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", requestId);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                results.Add(new FilterResultDto
                {
                    ParameterName = reader.GetString(0),
                    Value         = reader.IsDBNull(1) ? "" : reader.GetValue(1)?.ToString() ?? ""
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read results for request {Id}", requestId);
            throw;
        }
        return results;
    }

    public async Task<List<FilteredCycleDto>> GetCycleDataAsync(int requestId)
    {
        var results = new List<FilteredCycleDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT
                    cycle_number, blast_start, blast_end,
                    metal_1_name, metal_1_weight_kg,
                    metal_2_name, metal_2_weight_kg,
                    metal_3_name, metal_3_weight_kg,
                    metal_4_name, metal_4_weight_kg,
                    production_kg, energy_kwh, shots_usage
                FROM plc_filtered_cycle_data
                WHERE request_id = @id
                ORDER BY cycle_number ASC;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", requestId);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                results.Add(new FilteredCycleDto
                {
                    CycleNumber     = reader.GetInt32(0),
                    BlastStart      = reader.GetDateTime(1),
                    BlastEnd        = reader.GetDateTime(2),
                    Metal1Name      = reader.IsDBNull(3)  ? null : reader.GetString(3),
                    Metal1WeightKg  = reader.IsDBNull(4)  ? null : reader.GetDouble(4),
                    Metal2Name      = reader.IsDBNull(5)  ? null : reader.GetString(5),
                    Metal2WeightKg  = reader.IsDBNull(6)  ? null : reader.GetDouble(6),
                    Metal3Name      = reader.IsDBNull(7)  ? null : reader.GetString(7),
                    Metal3WeightKg  = reader.IsDBNull(8)  ? null : reader.GetDouble(8),
                    Metal4Name      = reader.IsDBNull(9)  ? null : reader.GetString(9),
                    Metal4WeightKg  = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                    ProductionKg    = reader.IsDBNull(11) ? 0    : reader.GetDouble(11),
                    EnergyKwh       = reader.IsDBNull(12) ? 0    : reader.GetDouble(12),
                    ShotsUsage      = reader.IsDBNull(13) ? 0    : reader.GetDouble(13)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read cycle data for request {Id}", requestId);
            throw;
        }
        return results;
    }

    public async Task<List<ShotsBreakdownDto>> GetShotsBreakdownAsync(int requestId)
    {
        var results = new List<ShotsBreakdownDto>();
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT refill_timestamp, blast_count
                FROM plc_filtered_shots_breakdown
                WHERE request_id = @id
                ORDER BY refill_timestamp ASC;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", requestId);
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
            _logger.LogError(ex, "Failed to read shots breakdown for request {Id}", requestId);
            throw;
        }
        return results;
    }
}
