using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// Build Host
var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, cfg) =>
    {
        cfg.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    })
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        // Read PLC config
        var plcIp = config.GetValue<string>("PLC:IpAddress");
        var plcRack = config.GetValue<short>("PLC:Rack");
        var plcSlot = config.GetValue<short>("PLC:Slot");

        // Read Postgres connection string
        var pgConn = config.GetValue<string>("PostgreSQL:ConnectionString");

        // Register services
        services.AddSingleton(new PlcService(plcIp, plcRack, plcSlot));
        
        services.AddSingleton<DatabaseService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DatabaseService>>();
            return new DatabaseService(pgConn, logger);
        });

        services.AddSingleton<CovDetectionService>(sp =>
        {
            var dbService = sp.GetRequiredService<DatabaseService>();
            var logger = sp.GetRequiredService<ILogger<CovDetectionService>>();
            var configuration = sp.GetRequiredService<IConfiguration>();
            return new CovDetectionService(dbService, logger, configuration);
        });

        services.AddSingleton<CalculationService>(sp =>
        {
            var dbService = sp.GetRequiredService<DatabaseService>();
            var logger = sp.GetRequiredService<ILogger<CalculationService>>();
            return new CalculationService(dbService, logger);
        });

        // Register background services
        services.AddHostedService<GatewayWorker>();
        services.AddHostedService<AggregationService>();
        services.AddHostedService<DataAggregationService>();
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .UseWindowsService()   // <-- support Windows Service (run on boot)
    .Build();

await host.RunAsync();
