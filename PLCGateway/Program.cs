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
builder.Services.AddSingleton<DatabaseService>(sp =>
    new DatabaseService(pgConn!, sp.GetRequiredService<ILogger<DatabaseService>>()));
builder.Services.AddSingleton<CovDetectionService>(sp =>
    new CovDetectionService(sp.GetRequiredService<ILogger<CovDetectionService>>(), config));
builder.Services.AddSingleton<CalculationService>(sp =>
    new CalculationService(sp.GetRequiredService<DatabaseService>(),
        sp.GetRequiredService<ILogger<CalculationService>>(), config));
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
builder.Services.AddScoped<ICyclesService, CyclesService>();
builder.Services.AddScoped<IUserService, UserService>();

builder.Services.AddControllers();
builder.Services.AddMemoryCache();

var jwtKey = config["Jwt:Key"] ?? "YourSuperSecretKeyWithAtLeast32Chars!!";
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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
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

    // Seed default dashboard users on first run (credentials from config; rotate at deployment).
    try
    {
        if (!await users.AnyUsersAsync())
        {
            var adminUser = config["Seed:AdminUsername"] ?? "admin";
            var adminPass = config["Seed:AdminPassword"] ?? "admin123";
            await users.InsertUserAsync(adminUser, $"{adminUser}@plc.local", "System Admin",
                PasswordHasher.Hash(adminPass), "admin", null);
            logger.LogWarning("Seeded default admin user '{u}'. CHANGE THIS PASSWORD before production.", adminUser);
        }
    }
    catch (Exception ex) { logger.LogError(ex, "User seeding failed."); }

    // Ops recovery: reset the incremental Section 1 state to replay full history.
    if (args.Contains("--rebuild-aggregation"))
    {
        try
        {
            await db.ResetAggregationStateAsync();
            logger.LogWarning("Aggregation state reset — Section 1 will rebuild from full history.");
        }
        catch (Exception ex) { logger.LogError(ex, "Failed to reset aggregation state."); }
    }

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
// SPA static assets (React build in wwwroot)
app.UseDefaultFiles();
app.UseStaticFiles();

// /api/admin/* is gated by IP allowlist + API key (before auth; not JWT-protected)
app.UseMiddleware<AdminGuardMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// Data API + dashboard return 402 when the license is locked (admin/auth/health exempt)
app.UseMiddleware<LicenseLockMiddleware>();

app.MapControllers();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

// SPA fallback: client-side routes (BrowserRouter) resolve to index.html
app.MapFallbackToFile("index.html");

app.Run();
