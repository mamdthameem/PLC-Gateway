using Npgsql;
using PlcApi.Models;

namespace PlcApi.Services;

public interface IUserService
{
    Task<AppUser?> FindByLoginAsync(string loginId);
    Task<bool> AnyUsersAsync();
    Task InsertUserAsync(string username, string email, string fullName, string passwordHash, string role, DateTime? validUntilUtc);
}

// Raw-Npgsql user store for local dashboard login (replaces the EF MasterDbContext).
public class UserService : IUserService
{
    private readonly string _connectionString;
    private readonly ILogger<UserService> _logger;

    public UserService(IConfiguration config, ILogger<UserService> logger)
    {
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("PostgresDb connection string is required.");
        _logger = logger;
    }

    public async Task<AppUser?> FindByLoginAsync(string loginId)
    {
        AppUser? user = null;
        const string sql = @"
            SELECT id, username, email, password_hash, role, is_approved, valid_until_utc
            FROM users
            WHERE lower(username) = lower(@login) OR lower(email) = lower(@login)
            LIMIT 1";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("login", loginId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (await r.ReadAsync())
        {
            user = new AppUser
            {
                Id            = r.GetInt32(0),
                Username      = r.GetString(1),
                Email         = r.GetString(2),
                PasswordHash  = r.GetString(3),
                Role          = r.GetString(4),
                IsApproved    = r.GetBoolean(5),
                ValidUntilUtc = r.IsDBNull(6) ? (DateTime?)null : r.GetDateTime(6)
            };
        }
        return user;
    }

    public async Task<bool> AnyUsersAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM users)", conn);
        var scalar = await cmd.ExecuteScalarAsync();
        return scalar is bool b && b;
    }

    public async Task InsertUserAsync(string username, string email, string fullName,
        string passwordHash, string role, DateTime? validUntilUtc)
    {
        const string sql = @"
            INSERT INTO users (username, email, full_name, password_hash, role, is_approved, valid_until_utc, created_at_utc)
            VALUES (@u, @e, @f, @p, @r, TRUE, @v, NOW())
            ON CONFLICT (username) DO NOTHING";
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("u", username);
        cmd.Parameters.AddWithValue("e", email);
        cmd.Parameters.AddWithValue("f", fullName);
        cmd.Parameters.AddWithValue("p", passwordHash);
        cmd.Parameters.AddWithValue("r", role);
        cmd.Parameters.AddWithValue("v", (object?)validUntilUtc ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }
}
