using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using PlcApi.Middleware;
using PlcApi.Services;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var plcIp   = config.GetValue<string>("PLC:IpAddress");
var plcRack = config.GetValue<short>("PLC:Rack");
var plcSlot = config.GetValue<short>("PLC:Slot");
var pgConn  = config.GetValue<string>("PostgreSQL:ConnectionString");

// ── Core PLC pipeline (unchanged services) ───────────────────────────────────
builder.Services.AddSingleton(new PlcService(plcIp!, plcRack, plcSlot));
builder.Services.AddSingleton<PlcConnectionState>();
builder.Services.AddSingleton<ImpellerSelection>();
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
builder.Services.AddScoped<IUserService, UserService>();

builder.Services.AddControllers();
builder.Services.AddMemoryCache();

// Jwt:Key signs every dashboard login. The placeholder in appsettings.json is public (it is in the
// repo), so it is never used: without a real key a random one is made for this run. Logins still
// work, but everyone must sign in again after each restart (a warning says so at startup).
var configuredJwtKey = SecretConfig.Get(config, "Jwt:Key", SecretConfig.MinLength);
var jwtSigningKey = new JwtSigningKey(
    configuredJwtKey ?? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48)),
    generated: configuredJwtKey is null);
builder.Services.AddSingleton(jwtSigningKey);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
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
    });
builder.Services.AddAuthorization();

// No CORS policy is registered: the dashboard is served same-origin by this app, and the
// optional `npm run dev` setup proxies /api server-side (also same-origin to the browser).

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var app = builder.Build();

// ── One-time startup DB work ─────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var sp     = scope.ServiceProvider;
    var db     = sp.GetRequiredService<DatabaseService>();
    var users  = sp.GetRequiredService<IUserService>();
    var logger = sp.GetRequiredService<ILogger<Program>>();

    // Say it loudly when a secret is missing or still the placeholder. The gateway keeps running
    // either way (it has to: it records the PLC), but that part of it is closed or temporary.
    if (jwtSigningKey.Generated)
        logger.LogWarning("Jwt:Key is not set (or is the placeholder, or under {n} characters): using a random key for this run. Everyone must sign in again after each restart.", SecretConfig.MinLength);
    if (SecretConfig.Get(config, "Admin:ApiKey", SecretConfig.MinLength) is null)
        logger.LogWarning("Admin:ApiKey is not set (or is the placeholder, or under {n} characters): every /api/admin/* request will be refused.", SecretConfig.MinLength);

    // Seed default dashboard users on first run (credentials from config; rotate at deployment).
    try
    {
        if (!await users.AnyUsersAsync())
        {
            // Fallbacks only matter if the Seed block is missing entirely. They match
            // appsettings.json so a config-less run cannot quietly create a different account
            // from the documented one. (The "admin" below is the ROLE, not a username.)
            var adminUser = config["Seed:AdminUsername"] ?? "sreesakthi";
            var adminPass = config["Seed:AdminPassword"] ?? "sreesakthi";
            await users.InsertUserAsync(adminUser, $"{adminUser}@plc.local", "System Admin",
                PasswordHasher.Hash(adminPass), "admin", null);
            logger.LogWarning("Seeded default admin user '{u}'. CHANGE THIS PASSWORD before production.", adminUser);
        }
    }
    catch (Exception ex) { logger.LogError(ex, "User seeding failed."); }

    // Impeller selection (gateway_settings). Loaded before any hosted service starts, so the first
    // cycle closed and the first spare poll already honour it. With no row — or on a database the
    // migration has not reached yet — every impeller stays selected.
    try
    {
        var saved = await db.GetImpellerSelectionAsync();
        if (saved is not null)
            sp.GetRequiredService<ImpellerSelection>().Set(saved);
    }
    catch (Exception ex) { logger.LogError(ex, "Loading the impeller selection failed; using every impeller."); }

    // Ops recovery: reset the incremental Section 1 state to replay full history.
    if (args.Contains("--rebuild-aggregation"))
    {
        try
        {
            await db.ResetAggregationStateAsync();
            await db.ResetDailyTrendsAsync();
            logger.LogWarning("Aggregation state + daily trend rollup reset — both rebuild from full history.");
        }
        catch (Exception ex) { logger.LogError(ex, "Failed to reset aggregation state."); }
    }

    // Daily trend rollup backfill: only when empty (fresh install, or just reset above). Cost is
    // proportional to existing history, so it is a one-time job — the per-minute path afterwards
    // only ever touches yesterday+today.
    try
    {
        if (await db.DailyTrendsEmptyAsync())
            await db.BackfillDailyTrendsAsync();
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
