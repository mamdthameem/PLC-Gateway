using System.Net.Sockets;
using Npgsql;

namespace PlcApi.Hosting;

// Creating and migrating the gateway's database, so an installed gateway needs no psql and no
// manual steps.
//
//   InitializeAsync  — installer only. Connects as the PostgreSQL superuser, creates the gateway's
//                      own login role and database if missing (or resets the role's password to
//                      the one in the settings file), then migrates as that role, so the role owns
//                      every table. Never drops or empties anything.
//   MigrateAsync     — every start (Database:AutoMigrate) and --migrate-only. Runs the embedded
//                      migration.sql, which is idempotent: re-running it on a current database
//                      changes nothing.
public static class DatabaseSetup
{
    /// <summary>The connection string every part of the gateway uses (ConnectionStrings:PostgresDb).</summary>
    /// <remarks>
    /// An empty value counts as missing: the appsettings.json shipped by the installer has its
    /// development connection string blanked, so an install whose own settings lack one stops with
    /// this message instead of quietly connecting somewhere else.
    /// </remarks>
    public static string ConnectionString(IConfiguration config)
    {
        foreach (var value in new[] { config.GetConnectionString("PostgresDb"), config["PostgreSQL:ConnectionString"] })
            if (!string.IsNullOrWhiteSpace(value)) return value;
        throw new InvalidOperationException(
            "No database connection string is configured (ConnectionStrings:PostgresDb). " +
            $"An installed gateway reads it from {GatewayPaths.SettingsFile}.");
    }

    public static string LoadMigrationScript()
    {
        using var stream = typeof(DatabaseSetup).Assembly.GetManifestResourceStream("migration.sql")
            ?? throw new InvalidOperationException("migration.sql is not embedded in this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static async Task MigrateAsync(string connectionString, CancellationToken ct = default)
    {
        string sql = LoadMigrationScript();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        // One command, so Npgsql sends every statement in a single implicit transaction: a failure
        // part-way leaves the schema as it was, not half-migrated.
        await using var cmd = new NpgsqlCommand(sql, conn) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Migrates, retrying while PostgreSQL is still starting — at boot the gateway service can come up
    /// seconds before the database accepts connections.
    /// </summary>
    public static async Task MigrateWithRetryAsync(string connectionString, ILogger logger, CancellationToken ct = default)
    {
        const int attempts = 20;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await MigrateAsync(connectionString, ct);
                return;
            }
            catch (Exception ex) when (attempt < attempts && IsStillStarting(ex))
            {
                logger.LogWarning("Database not reachable yet ({reason}); retrying in 3 s ({attempt}/{max}).",
                    ex.Message, attempt, attempts);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
        }
    }

    private static bool IsStillStarting(Exception ex) => ex switch
    {
        NpgsqlException { InnerException: SocketException } => true,
        PostgresException pg => pg.SqlState is "57P03"   // cannot_connect_now: starting up / in recovery
                                            or "53300",  // too_many_connections
        NpgsqlException npg => npg.IsTransient,
        SocketException => true,
        _ => false,
    };

    public static async Task InitializeAsync(string connectionString, string superuser, string superuserPassword,
        Action<string> report, CancellationToken ct = default)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrEmpty(target.Database) || string.IsNullOrEmpty(target.Username))
            throw new InvalidOperationException("The connection string must name a Database and a Username.");

        var adminCs = new NpgsqlConnectionStringBuilder
        {
            Host = target.Host, Port = target.Port, Database = "postgres",
            Username = superuser, Password = superuserPassword,
        }.ConnectionString;

        await using (var admin = new NpgsqlConnection(adminCs))
        {
            await admin.OpenAsync(ct);

            if (!string.Equals(target.Username, superuser, StringComparison.Ordinal))
            {
                bool roleExists = await ExistsAsync(admin, "SELECT 1 FROM pg_roles WHERE rolname = @n", target.Username, ct);
                string verb = roleExists ? "ALTER" : "CREATE";
                // DDL cannot take parameters; the name and password are quoted/escaped instead.
                await ExecAsync(admin, $"{verb} ROLE {Ident(target.Username)} WITH LOGIN PASSWORD {Literal(target.Password ?? "")}", ct);
                report(roleExists ? $"Updated the password of role '{target.Username}'." : $"Created role '{target.Username}'.");
            }

            if (await ExistsAsync(admin, "SELECT 1 FROM pg_database WHERE datname = @n", target.Database, ct))
            {
                report($"Database '{target.Database}' already exists; keeping it and its data.");
            }
            else
            {
                await ExecAsync(admin,
                    $"CREATE DATABASE {Ident(target.Database)} OWNER {Ident(target.Username)} ENCODING 'UTF8' TEMPLATE template0", ct);
                report($"Created database '{target.Database}'.");
            }
        }

        await MigrateAsync(connectionString, ct);
        report("Database schema is up to date.");
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection conn, string sql, string name, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("n", name);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Ident(string name)   => "\"" + name.Replace("\"", "\"\"") + "\"";
    private static string Literal(string text) => "'" + text.Replace("'", "''") + "'";
}
