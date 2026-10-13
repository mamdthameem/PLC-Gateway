using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Npgsql;
using PlcApi.Hosting;
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
///
/// A successful answer also carries the version of the client's tag file in the cloud
/// (siteProfileVersion). When it differs from the one this run uses, the new file is downloaded and
/// saved, and the gateway stops with exit code 1 so the Windows service restarts it on the new tags
/// (see SiteProfile). No extra calls to the cloud: it rides on the hourly check.
/// </summary>
public class LicenseCheckService : BackgroundService
{
    private enum CheckResult { NotConfigured, Valid, Rejected, Unreachable }

    private static readonly TimeSpan LockedRecheck = TimeSpan.FromMinutes(5);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly ILogger<LicenseCheckService> _logger;
    private readonly LicenseState _state;
    private readonly SiteProfileState _siteProfile;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _connectionString;

    private readonly string? _checkUrl;
    private readonly string? _licenseKey;
    private readonly string? _siteProfileUrl;
    private readonly int _intervalMinutes;
    private readonly int _graceHours;

    /// <summary>The tag-file version the last successful check reported, if any.</summary>
    private string? _cloudProfileVersion;

    public LicenseCheckService(ILogger<LicenseCheckService> logger, IConfiguration config, LicenseState state,
        SiteProfileState siteProfile, IHostApplicationLifetime lifetime)
    {
        _logger      = logger;
        _state       = state;
        _siteProfile = siteProfile;
        _lifetime    = lifetime;
        _siteProfileUrl = config["SiteProfile:Url"]?.Trim() is { Length: > 0 } profileUrl ? profileUrl : null;
        _connectionString = config.GetConnectionString("PostgresDb")
            ?? config.GetValue<string>("PostgreSQL:ConnectionString")
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
                await RefreshSiteProfileAsync(stoppingToken);
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

            if (resp.IsSuccessStatusCode)
            {
                _cloudProfileVersion = await ReadProfileVersionAsync(resp, ct);
                return CheckResult.Valid;
            }
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

    /// <summary>siteProfileVersion from the licence answer; null when absent (an older cloud) or unreadable.</summary>
    private static async Task<string?> ReadProfileVersionAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("siteProfileVersion", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A newer tag file in the cloud: download it, save it, and stop with exit code 1 so the service
    /// restarts on it. Only for a gateway whose tags come from the cloud in the first place.
    /// </summary>
    private async Task RefreshSiteProfileAsync(CancellationToken ct)
    {
        if (_siteProfile.Version is null || _siteProfileUrl is null || _licenseKey is null || _siteProfile.RestartRequested) return;
        if (string.IsNullOrEmpty(_cloudProfileVersion) || _cloudProfileVersion == _siteProfile.Version) return;

        try
        {
            var profile = await SiteProfile.DownloadAsync(_siteProfileUrl, _licenseKey, ct);
            SiteProfile.SaveCache(profile, _licenseKey);
            _logger.LogWarning("A new tag file was uploaded in the cloud (version {version}, {tags} tags). " +
                               "The gateway is stopping to start again on it; as a service it is back within a minute.",
                               profile.Version, profile.TagCount);
            _siteProfile.RestartRequested = true;
            _lifetime.StopApplication();
        }
        catch (SiteProfileException ex)
        {
            _logger.LogWarning("A new tag file is in the cloud, but it could not be fetched: {reason} Keeping the current one.", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning(ex, "A new tag file was fetched but could not be saved. Keeping the current one.");
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
