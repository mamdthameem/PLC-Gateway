using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PlcApi.Services;

/// <summary>
/// Periodically validates the client's license against the cloud (Part E5). A successful check
/// refreshes the grace anchor; if checks keep failing beyond License:GraceHours the instance is
/// locked (data API + dashboard return 402) — but the PLC pipeline and DB recording continue.
/// Last-success is persisted (gateway_license_state) so a restart does not reset the grace clock.
/// The cloud URL and key are placeholders in appsettings and must be set at deployment.
/// </summary>
public class LicenseCheckService : BackgroundService
{
    private readonly ILogger<LicenseCheckService> _logger;
    private readonly IConfiguration _config;
    private readonly LicenseState _state;
    private readonly string _connectionString;

    private readonly string? _checkUrl;
    private readonly string? _licenseKey;
    private readonly int _intervalMinutes;
    private readonly int _graceHours;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public LicenseCheckService(ILogger<LicenseCheckService> logger, IConfiguration config, LicenseState state)
    {
        _logger = logger;
        _config = config;
        _state  = state;
        _connectionString = config.GetValue<string>("PostgreSQL:ConnectionString")
            ?? config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("Connection string is required.");

        _checkUrl        = config["License:CheckUrl"];
        _licenseKey      = config["License:Key"];
        _intervalMinutes = config.GetValue<int?>("License:CheckIntervalMinutes") ?? 60;
        _graceHours      = config.GetValue<int?>("License:GraceHours") ?? 72;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load persisted last-success; on a fresh install anchor the grace window to now so a
        // valid client is never locked instantly before its first successful check.
        DateTime? lastSuccess = await LoadLastSuccessAsync();
        if (lastSuccess == null)
        {
            lastSuccess = DateTime.UtcNow;
            await PersistAsync(lastSuccess, DateTime.UtcNow, locked: false);
        }
        _state.Set(false, lastSuccess);

        while (!stoppingToken.IsCancellationRequested)
        {
            bool success = await CheckOnceAsync();
            var now = DateTime.UtcNow;

            if (success)
            {
                lastSuccess = now;
                _state.Set(false, lastSuccess);
                await PersistAsync(lastSuccess, now, locked: false);
            }
            else
            {
                bool beyondGrace = lastSuccess == null || (now - lastSuccess.Value).TotalHours > _graceHours;
                _state.Set(beyondGrace, lastSuccess);
                await PersistAsync(lastSuccess, now, locked: beyondGrace);
                if (beyondGrace)
                    _logger.LogWarning("License check failing beyond grace period — dashboard/API locked (recording continues).");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(_intervalMinutes), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task<bool> CheckOnceAsync()
    {
        if (string.IsNullOrWhiteSpace(_checkUrl))
            return true; // no license server configured → treat as unlicensed-but-open (dev)

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _checkUrl);
            if (!string.IsNullOrEmpty(_licenseKey))
                req.Headers.Add("X-License-Key", _licenseKey);
            using var resp = await Http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "License check request failed.");
            return false;
        }
    }

    private async Task<DateTime?> LoadLastSuccessAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT last_success_utc FROM gateway_license_state WHERE id = 1", conn);
            var scalar = await cmd.ExecuteScalarAsync();
            return scalar is DateTime dt ? dt : (DateTime?)null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load license state.");
            return null;
        }
    }

    private async Task PersistAsync(DateTime? lastSuccess, DateTime lastCheck, bool locked)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            const string sql = @"
                UPDATE gateway_license_state
                SET last_success_utc = @ls, last_check_utc = @lc, locked = @locked
                WHERE id = 1";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("ls", (object?)lastSuccess ?? DBNull.Value);
            cmd.Parameters.AddWithValue("lc", lastCheck);
            cmd.Parameters.AddWithValue("locked", locked);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist license state.");
        }
    }
}
