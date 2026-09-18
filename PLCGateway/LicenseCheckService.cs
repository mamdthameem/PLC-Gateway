using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PlcApi.Services;

/// <summary>
/// Checks the client's licence against the Shot Sense cloud (Part E5):
/// GET License:CheckUrl with header "X-License-Key: License:Key".
///
///   2xx           → valid: the dashboard is open and the grace clock restarts.
///   401/402/403   → the key is wrong, expired or revoked: the dashboard LOCKS AT ONCE.
///   anything else → the server did not really answer (network down, timeout, 5xx, wrong URL):
///                   the dashboard stays open for License:GraceHours after the last real "yes",
///                   then locks — so an internet outage at the plant does not lock anyone out.
///   no CheckUrl   → the licence check is off and the dashboard stays open. This is NOT a "yes":
///                   it never moves the grace clock.
///
/// A lock closes only the dashboard and its data API (LicenseLockMiddleware). The PLC scan and every
/// recording and calculation service keep running, and /api/admin, /api/auth, /api/license and
/// /api/health stay reachable. While locked it checks again every 5 minutes, so a fixed key opens
/// the dashboard again quickly.
///
/// The grace anchor is saved (gateway_license_state) together with the URL it belongs to, so a
/// restart does not reset it and a success from a different licence server does not carry over.
/// </summary>
public class LicenseCheckService : BackgroundService
{
    private enum CheckResult { NotConfigured, Valid, Rejected, Unreachable }

    private static readonly TimeSpan LockedRecheck = TimeSpan.FromMinutes(5);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly ILogger<LicenseCheckService> _logger;
    private readonly LicenseState _state;
    private readonly string _connectionString;

    private readonly string? _checkUrl;
    private readonly string? _licenseKey;
    private readonly int _intervalMinutes;
    private readonly int _graceHours;

    public LicenseCheckService(ILogger<LicenseCheckService> logger, IConfiguration config, LicenseState state)
    {
        _logger = logger;
        _state  = state;
        _connectionString = config.GetValue<string>("PostgreSQL:ConnectionString")
            ?? config.GetConnectionString("PostgresDb")
            ?? throw new InvalidOperationException("Connection string is required.");

        var url = config["License:CheckUrl"]?.Trim();
        _checkUrl        = string.IsNullOrEmpty(url) ? null : url;
        _licenseKey      = SecretConfig.Get(config, "License:Key");
        _intervalMinutes = config.GetValue<int?>("License:CheckIntervalMinutes") ?? 60;
        _graceHours      = config.GetValue<int?>("License:GraceHours") ?? 72;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTime? graceAnchor = null;

        if (_checkUrl is null)
        {
            _logger.LogWarning("License:CheckUrl is empty: the licence check is OFF and the dashboard stays open.");
        }
        else
        {
            if (_licenseKey is null)
                _logger.LogWarning("License:Key is not set (or is the placeholder): the licence server will reject the check and the dashboard will lock.");

            // The grace clock only counts a "yes" from THIS licence server. With no anchor for this
            // URL (fresh install, a new URL, or history from runs with no URL) one starts now, so a
            // plant that is offline at its first start still gets the full grace period.
            var (anchor, anchorUrl) = await LoadAnchorAsync();
            graceAnchor = anchor is not null && anchorUrl == _checkUrl ? anchor : null;
            if (graceAnchor is null)
            {
                graceAnchor = DateTime.UtcNow;
                await SaveAnchorAsync(graceAnchor.Value);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            CheckResult result;
            try { result = await CheckOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }

            var now = DateTime.UtcNow;
            if (result == CheckResult.Valid)
            {
                graceAnchor = now;
                await SaveAnchorAsync(now);
            }

            var status = result switch
            {
                CheckResult.NotConfigured => new LicenseStatus(false, "not_configured", null, null),
                CheckResult.Valid         => new LicenseStatus(false, "ok", graceAnchor, null),
                CheckResult.Rejected      => new LicenseStatus(true, "rejected", graceAnchor, null),
                _                         => Unreachable(now, graceAnchor)
            };
            ReportChange(_state.Current, status);
            _state.Set(status);
            await SaveCheckAsync(now, status.Locked);

            try { await Task.Delay(status.Locked ? LockedRecheck : TimeSpan.FromMinutes(_intervalMinutes), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private LicenseStatus Unreachable(DateTime now, DateTime? graceAnchor)
    {
        var lockAfter = (graceAnchor ?? now).AddHours(_graceHours);
        return new LicenseStatus(now >= lockAfter, "unreachable", graceAnchor, lockAfter);
    }

    private async Task<CheckResult> CheckOnceAsync(CancellationToken ct)
    {
        if (_checkUrl is null) return CheckResult.NotConfigured;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _checkUrl);
            if (_licenseKey is not null)
                req.Headers.Add("X-License-Key", _licenseKey);
            using var resp = await Http.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode) return CheckResult.Valid;
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.Forbidden)
                return CheckResult.Rejected;

            _logger.LogWarning("Licence server answered {status}; counted as no answer (the grace period applies).", (int)resp.StatusCode);
            return CheckResult.Unreachable;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Licence server could not be reached.");
            return CheckResult.Unreachable;
        }
    }

    // Logs only when the lock state or its reason changes, so a long outage is one line, not one
    // line per check.
    private void ReportChange(LicenseStatus before, LicenseStatus now)
    {
        if (before.Locked == now.Locked && before.Reason == now.Reason) return;

        if (now.Reason == "rejected")
            _logger.LogWarning("Licence key REJECTED by the licence server: dashboard locked. PLC recording continues.");
        else if (now.Reason == "unreachable" && now.Locked)
            _logger.LogWarning("Licence server unreachable for more than {hours} hours: dashboard locked. PLC recording continues.", _graceHours);
        else if (now.Reason == "unreachable")
            _logger.LogWarning("Licence server unreachable: the dashboard stays open until {lockAfter:u}.", now.LockAfterUtc);
        else if (now.Reason == "ok" && before.Locked)
            _logger.LogInformation("Licence valid again: dashboard unlocked.");
        else if (now.Reason == "ok")
            _logger.LogInformation("Licence valid.");
    }

    private async Task<(DateTime? Anchor, string? Url)> LoadAnchorAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT last_success_utc, last_success_url FROM gateway_license_state WHERE id = 1", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return (null, null);
            // Stored as a UTC wall-clock value (see ExecAsync), so mark it UTC on the way back.
            return (r.IsDBNull(0) ? null : DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc),
                    r.IsDBNull(1) ? null : r.GetString(1));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the licence state.");
            return (null, null);
        }
    }

    // The anchor and its URL are always written together, so a later start knows which licence
    // server the anchor belongs to.
    private Task SaveAnchorAsync(DateTime anchorUtc) => ExecAsync(
        "UPDATE gateway_license_state SET last_success_utc = @at, last_success_url = @url WHERE id = 1",
        ("at", anchorUtc), ("url", (object?)_checkUrl ?? DBNull.Value));

    private Task SaveCheckAsync(DateTime checkedUtc, bool locked) => ExecAsync(
        "UPDATE gateway_license_state SET last_check_utc = @at, locked = @locked WHERE id = 1",
        ("at", checkedUtc), ("locked", locked));

    private async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            foreach (var (name, value) in args)
            {
                // The *_utc columns are TIMESTAMP (no time zone), so a time goes in as a plain UTC
                // wall-clock value. Sent as a UTC-marked DateTime, Npgsql would pass it as
                // timestamptz and PostgreSQL would shift it into the server's local time zone —
                // which, read back as UTC, silently added the UTC offset (5 h 30 min in India) to
                // the grace period.
                if (value is DateTime dt)
                    cmd.Parameters.Add(new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Timestamp)
                        { Value = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified) });
                else
                    cmd.Parameters.AddWithValue(name, value);
            }
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the licence state.");
        }
    }
}
