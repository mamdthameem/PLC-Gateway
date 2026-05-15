using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, cfg) =>
    {
        cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    })
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        var plcIp   = config.GetValue<string>("PLC:IpAddress");
        var plcRack = config.GetValue<short>("PLC:Rack");
        var plcSlot = config.GetValue<short>("PLC:Slot");
        var pgConn  = config.GetValue<string>("PostgreSQL:ConnectionString");

        services.AddSingleton(new PlcService(plcIp, plcRack, plcSlot));

        services.AddSingleton<DatabaseService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DatabaseService>>();
            return new DatabaseService(pgConn, logger);
        });

        services.AddSingleton<CovDetectionService>(sp =>
            new CovDetectionService(
                sp.GetRequiredService<ILogger<CovDetectionService>>(),
                sp.GetRequiredService<IConfiguration>()));

        services.AddSingleton<CalculationService>(sp =>
            new CalculationService(
                sp.GetRequiredService<DatabaseService>(),
                sp.GetRequiredService<ILogger<CalculationService>>(),
                sp.GetRequiredService<IConfiguration>()));

        // Background services
        services.AddHostedService<GatewayWorker>();               // PLC scan loop → Tier 1 + Tier 2
        services.AddHostedService<AggregationService>();           // Section 1: lifetime parameters every 1 min
        services.AddHostedService<CycleTrackingService>();         // Cycle log: writes plc_cycles on blast end
        services.AddHostedService<FilteredCalculationService>();   // Section 2: on-demand filter requests
        services.AddHostedService<SpareMonitoringService>();       // Spare alerts: updates plc_spare_status
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .UseWindowsService()
    .Build();

await host.RunAsync();
