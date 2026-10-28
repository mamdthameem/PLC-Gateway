using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;
using PlcApi.Auth;
using PlcApi.Hosting;
using PlcApi.Middleware;
using PlcApi.Services;

// Installed, the gateway runs as a Windows service (ShotSenseGateway). Windows starts services in
// C:\Windows\System32, so the content root is pinned to the exe's own folder — otherwise neither
// wwwroot (the dashboard) nor appsettings.json would be found. From a terminal or `dotnet run`
// nothing changes.
bool runningAsService = WindowsServiceHelpers.IsWindowsService();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args            = args,
    ContentRootPath = runningAsService ? AppContext.BaseDirectory : null,
});
builder.Host.UseWindowsService(o => o.ServiceName = GatewayPaths.ServiceName);

// An installed gateway's own settings and secrets live in C:\ProgramData\ShotSense (GatewayPaths),
// where an upgrade cannot overwrite them. Absent on a dev PC, so nothing changes there.
GatewayPaths.AddInstallSettings(builder.Configuration);
var config = builder.Configuration;

// One-shot commands (--init-database, --migrate-only, --set-local-admin) run and exit here, before
// any PLC scanning or web serving is set up. See GatewayCli.
if (await GatewayCli.TryRunAsync(args, config) is int exitCode)
    return exitCode;

// The client's tag list and recording rules (SiteProfile): from the cloud on an installed gateway,
// from appsettings.json on a dev PC. Loaded before anything reads its settings. With no tag file at
// all there is nothing to read the PLC with, so the gateway stops; as a service it retries in a minute.
SiteProfileState siteProfile;
using (var bootLogs = LoggerFactory.Create(logging =>
{
    logging.AddConsole();
    if (runningAsService || config.GetValue("Logging:File:Enabled", false))
        logging.AddProvider(new FileLoggerProvider(GatewayPaths.LogsDir));
}))
{
    if (await SiteProfile.LoadAsync(builder.Configuration, bootLogs.CreateLogger("SiteProfile")) is not { } loaded)
        return 1;
    siteProfile = loaded;
}
builder.Services.AddSingleton(siteProfile);

var plcIp   = config.GetValue<string>("PLC:IpAddress");
var plcRack = config.GetValue<short>("PLC:Rack");
var plcSlot = config.GetValue<short>("PLC:Slot");
var pgConn  = DatabaseSetup.ConnectionString(config);

// ── Core PLC pipeline (unchanged services) ───────────────────────────────────
builder.Services.AddSingleton(new PlcService(plcIp!, plcRack, plcSlot));
builder.Services.AddSingleton<PlcConnectionState>();
// Knows which impellers exist from the tag file's tag names (loaded just above).
builder.Services.AddSingleton(new ImpellerSelection(config));
builder.Services.AddSingleton<DatabaseService>(sp =>
    new DatabaseService(pgConn!, sp.GetRequiredService<ILogger<DatabaseService>>(),
                        sp.GetRequiredService<ImpellerSelection>()));
builder.Services.AddSingleton<CovDetectionService>(sp =>
    new CovDetectionService(sp.GetRequiredService<ILogger<CovDetectionService>>(), config));
builder.Services.AddSingleton<CalculationService>(sp =>
    new CalculationService(sp.GetRequiredService<DatabaseService>(),
        sp.GetRequiredService<ILogger<CalculationService>>(),
        sp.GetRequiredService<ImpellerSelection>(), config));
builder.Services.AddSingleton<LicenseState>();

builder.Services.AddHostedService<GatewayWorker>();
builder.Services.AddHostedService<AggregationService>();
builder.Services.AddHostedService<CycleTrackingService>();
builder.Services.AddHostedService<FilteredCalculationService>();
builder.Services.AddHostedService<SpareMonitoringService>();
builder.Services.AddHostedService<LicenseCheckService>();

// ── API layer (dashboard + admin) ────────────────────────────────────────────
builder.Services.AddScoped<ILifetimeService, LifetimeService>();
builder.Services.AddScoped<IFilterService, FilterService>();
builder.Services.AddScoped<IShotsBreakdownService, ShotsBreakdownService>();
builder.Services.AddScoped<IAmpsService, AmpsService>();
builder.Services.AddScoped<ISpareStatusService, SpareStatusService>();
builder.Services.AddScoped<IMachineStatusService, MachineStatusService>();
builder.Services.AddScoped<IHistoricalService, HistoricalService>();
builder.Services.AddScoped<ITrendsService, TrendsService>();
builder.Services.AddScoped<ICyclesService, CyclesService>();

builder.Services.AddControllers();
builder.Services.AddMemoryCache();

// Brute-force defence for the emergency local admin login (AuthController.LocalAdmin), in two halves:
//   per visitor IP — 5 attempts a minute (rate limiter policy "login");
//   per account    — LoginThrottle: 10 failures lock the login for 15 minutes.
// Email sign-in has its own limits at Firebase.
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Too many sign-in attempts. Wait a minute and try again." }, ct);
    };
    options.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        ClientAddress.Of(http),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// Jwt:Key signs every dashboard login. The placeholder in appsettings.json is public (it is in the
// repo), so it is never used: without a real key a random one is made for this run. Logins still
// work, but everyone must sign in again after each restart (a warning says so at startup).
var configuredJwtKey = SecretConfig.Get(config, "Jwt:Key", SecretConfig.MinLength);
var jwtSigningKey = new JwtSigningKey(
    configuredJwtKey ?? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)),
    generated: configuredJwtKey is null);
builder.Services.AddSingleton(jwtSigningKey);

// Dashboard sign-in: Firebase email logins (one per person, managed from the cloud), plus the
// gateway's own token for the emergency local admin. See GatewayAuth.
var firebaseLogin = FirebaseLogin.FromConfig(config);
builder.Services.AddSingleton(firebaseLogin);
var authBuilder = builder.Services.AddAuthentication(GatewayAuth.Scheme)
    .AddPolicyScheme(GatewayAuth.Scheme, "Gateway", options =>
        options.ForwardDefaultSelector = http => GatewayAuth.SelectScheme(http, firebaseLogin))
    .AddJwtBearer(GatewayAuth.LocalScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = config["Jwt:Issuer"],
            ValidAudience = config["Jwt:Audience"],
            IssuerSigningKey = jwtSigningKey.SecurityKey
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = GatewayAuth.ValidateLocalToken,
            OnChallenge      = GatewayAuth.WriteChallengeAsync,
        };
    });
if (firebaseLogin.Enabled)
{
    authBuilder.AddJwtBearer(GatewayAuth.FirebaseScheme, options =>
    {
        // Google's signing keys are found through the issuer's discovery document and cached, so a
        // token is checked here without calling the cloud on every request.
        options.Authority        = firebaseLogin.Issuer;
        options.MapInboundClaims = false;   // keep Firebase's own claim names: role, clientId, email, name
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = firebaseLogin.Issuer,
            ValidateAudience = true,
            ValidAudience = firebaseLogin.ProjectId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx => GatewayAuth.ValidateFirebaseToken(ctx, firebaseLogin),
            OnChallenge      = GatewayAuth.WriteChallengeAsync,
        };
    });
}
builder.Services.AddAuthorization();

// No CORS policy is registered: the dashboard is served same-origin by this app, and the
// optional `npm run dev` setup proxies /api server-side (also same-origin to the browser).

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// A service has no console, so an installed gateway also writes C:\ProgramData\ShotSense\logs\.
// Logging:File:Enabled=true turns the same files on for a terminal run.
if (runningAsService || config.GetValue("Logging:File:Enabled", false))
    builder.Logging.AddProvider(new FileLoggerProvider(GatewayPaths.LogsDir));

var app = builder.Build();

// ── Schema ───────────────────────────────────────────────────────────────────
// The gateway brings its own database up to date on every start (Database:AutoMigrate, default
// on), so an upgrade needs no separate migration step and no psql. migration.sql is idempotent, so
// on a current database this changes nothing. If the database stays unreachable the gateway exits
// with an error instead of half-running; as a service, Windows then restarts it.
var startupLog = app.Services.GetRequiredService<ILogger<Program>>();

// Name the database out loud (never the password). Settings are layered — appsettings.json, then the
// install's ProgramData file — so a malformed entry in the later file silently falls back to the
// earlier one; this line is how anyone reading the log can tell which database the gateway is on.
{
    var target = new Npgsql.NpgsqlConnectionStringBuilder(pgConn);
    startupLog.LogInformation("Database: {db} on {host}:{port} as {user}.", target.Database, target.Host, target.Port, target.Username);
}

if (config.GetValue("Database:AutoMigrate", true))
{
    try
    {
        await DatabaseSetup.MigrateWithRetryAsync(pgConn, startupLog);
        startupLog.LogInformation("Database schema is up to date.");
    }
    catch (Exception ex)
    {
        startupLog.LogCritical(ex, "Could not bring the database schema up to date. The gateway is stopping.");
        return 1;
    }
}

// ── One-time startup DB work ─────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var sp     = scope.ServiceProvider;
    var db     = sp.GetRequiredService<DatabaseService>();
    var logger = sp.GetRequiredService<ILogger<Program>>();

    // Say it loudly when a secret is missing or still the placeholder. The gateway keeps running
    // either way (it has to: it records the PLC), but that part of it is closed or temporary.
    if (jwtSigningKey.Generated)
        logger.LogWarning("Jwt:Key is not set (or is the placeholder, or under {n} characters): using a random key for this run. Everyone must sign in again after each restart.", SecretConfig.MinLength);
    if (SecretConfig.Get(config, "Admin:ApiKey", SecretConfig.MinLength) is null)
        logger.LogWarning("Admin:ApiKey is not set (or is the placeholder, or under {n} characters): every /api/admin/* request will be refused.", SecretConfig.MinLength);

    // Which sign-ins this gateway accepts, said out loud (see GatewayAuth).
    if (firebaseLogin.Enabled && firebaseLogin.ClientId.Length > 0)
        logger.LogInformation("Email sign-in: on, for client {clientId} (from {source}).", firebaseLogin.ClientId, firebaseLogin.ClientIdSource);
    else if (firebaseLogin.Enabled)
        logger.LogWarning("Email sign-in: refused for everyone: this gateway has no client id (set Firebase:ClientId).");
    else
        logger.LogWarning("Email sign-in: off (Firebase:ProjectId / Firebase:ApiKey not set).");

    logger.LogInformation("Local admin login: {state}.",
        GatewayAuth.LocalAdminHash(config) is null
            ? "not set up (PLCGateway.exe --set-local-admin)"
            : "set up, plant network only");

    // Impeller selection (gateway_settings). Loaded before any hosted service starts, so the first
    // cycle closed and the first spare poll already honour it. With no row — or on a database the
    // migration has not reached yet — every impeller the tag file has stays selected.
    var impellers = sp.GetRequiredService<ImpellerSelection>();
    try
    {
        var saved = await db.GetImpellerSelectionAsync();
        if (saved is not null)
            impellers.Set(saved);
    }
    catch (Exception ex) { logger.LogError(ex, "Loading the impeller selection failed; using every impeller."); }
    logger.LogInformation("Impellers in the tag file: {available}; in use: {inUse}.",
        impellers.Available() is { Length: > 0 } available ? string.Join(",", available) : "none",
        impellers.Snapshot() is { Length: > 0 } inUse ? string.Join(",", inUse) : "none");

    // The PLC address set from the cloud, if any, wins over PLC:* in the settings file. Applied by the
    // scan loop before its first connection attempt.
    try
    {
        if (await db.GetSavedPlcEndpointAsync() is { } savedPlc)
        {
            var plc = sp.GetRequiredService<PlcService>();
            logger.LogInformation("PLC address set from the cloud: {plc} (the settings file says {file}).", savedPlc, plc.Endpoint);
            plc.RequestEndpoint(savedPlc);
        }
    }
    catch (Exception ex) { logger.LogError(ex, "Loading the PLC address saved from the cloud failed; using the settings file."); }

    // Ops recovery: reset the incremental Section 1 state to replay full history.
    if (args.Contains("--rebuild-aggregation"))
    {
        try
        {
            await db.ResetAggregationStateAsync();
            await db.ResetDailyTrendsAsync();
            await db.ResetShotsBreakdownAsync();
            logger.LogWarning("Aggregation state, daily trend rollup and shots breakdown reset — all rebuild from full history.");
        }
        catch (Exception ex) { logger.LogError(ex, "Failed to reset aggregation state."); }
    }

    // Daily trend rollup backfill: only when empty (fresh install, or just reset above). Cost is
    // proportional to existing history, so it is a one-time job — the per-minute path afterwards
    // only ever touches yesterday+today.
    // The rollup is also rebuilt once when the formula behind it changed (RollupVersion). It is
    // derived data, fully reconstructible from Tier 2 + plc_cycles, so clearing it loses nothing.
    try
    {
        bool empty = await db.DailyTrendsEmptyAsync();
        if (!empty && await db.GetRollupVersionAsync() < DatabaseService.RollupVersion)
        {
            await db.ResetDailyTrendsAsync();
            logger.LogWarning("Daily trend rollup cleared to rebuild it: blast time now comes from the completed cycles.");
            empty = true;
        }
        if (empty)
            await db.BackfillDailyTrendsAsync();
        await db.SetRollupVersionAsync(DatabaseService.RollupVersion);
    }
    catch (Exception ex) { logger.LogError(ex, "Daily trend backfill failed."); }

    // Offline-gap handling: force machine/blast OFF backdated to the last recorded scan so the
    // unobserved gap contributes zero to every duration and accumulator.
    try
    {
        var lastScan = await db.GetGatewayLastScanAtAsync();
        await db.RecordPlcDisconnectAsync(lastScan ?? DateTime.Now,
            GatewayWorker.MACHINE_STATUS_TAG_NAME, GatewayWorker.BLAST_TAG_NAME);
        logger.LogInformation("Startup gap handled (last recorded scan: {last}).", lastScan);
    }
    catch (Exception ex) { logger.LogError(ex, "Startup gap handling failed."); }
}

// ── HTTP pipeline ────────────────────────────────────────────────────────────
// SPA static assets (React build in wwwroot).
//
// Caching is set EXPLICITLY here because the defaults silently serve a stale dashboard after
// every deployment. UseStaticFiles sends no Cache-Control at all, so a browser falls back to
// heuristic freshness (~10% of the file's age) — an index.html first loaded hours ago stays
// "fresh" for hours. Since `npm run build` empties wwwroot and emits a NEW content-hashed
// bundle name, that cached index.html points at a bundle the build deleted: the panel keeps
// running yesterday's JavaScript from disk cache and never asks the server, or 404s on the
// script and renders an empty page. Neither failure is visible from the server side.
//
// So: /assets/* is content-hashed and immutable (a changed file always has a new name), while
// index.html — the one filename that never changes — must be revalidated on every load.
var spaStaticFiles = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
            headers.CacheControl = "public, max-age=31536000, immutable";
        else if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            headers.CacheControl = "no-cache, no-store, must-revalidate";
    }
};

app.UseDefaultFiles();
app.UseStaticFiles(spaStaticFiles);

// /api/admin/* is gated by the X-Api-Key check (before auth; not JWT-protected)
app.UseMiddleware<AdminGuardMiddleware>();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Data API + dashboard return 402 when the licence is locked (admin/auth/license/health exempt)
app.UseMiddleware<LicenseLockMiddleware>();

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// Licence status for the dashboard lock screen. Anonymous and never locked itself, so a locked
// dashboard can still say why. It shows only the lock state and the reason.
app.MapGet("/api/license", (LicenseState license) =>
{
    var s = license.Current;
    return Results.Ok(new { locked = s.Locked, reason = s.Reason, lockAfterUtc = s.LockAfterUtc });
});

// SPA fallback: client-side routes (BrowserRouter) resolve to index.html. Same options as
// above — this is the path that serves /dashboard, so without them the no-cache header would
// apply only to a bare "/" request and the stale-bundle problem would survive on every deep link.
app.MapFallbackToFile("index.html", spaStaticFiles);

app.Run();

// A newer tag file was saved while running (LicenseCheckService): exit 1, so the Windows service's
// failure action starts the gateway again a minute later, on the new tags.
return siteProfile.RestartRequested ? 1 : 0;
