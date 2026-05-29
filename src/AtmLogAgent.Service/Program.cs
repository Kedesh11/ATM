using AtmLogAgent.Core.Interfaces;
using AtmLogAgent.Core.Models;
using AtmLogAgent.Core.Services;
using AtmLogAgent.Service.Workers;
using Serilog;
using Serilog.Events;

// ══════════════════════════════════════════════════════════════
//  ATM Log Agent — Point d'entrée du service
//  Compatible Windows Service (SCM) et Linux systemd
// ══════════════════════════════════════════════════════════════

// Serilog bootstrap logger (avant la configuration complète)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("ATM Log Agent starting...");

    var builder = Host.CreateApplicationBuilder(args);

    // ── Configuration ───────────────────────────────────────
    var configDir = args.SkipWhile(a => a != "--configdir").Skip(1).FirstOrDefault() ?? AppContext.BaseDirectory;
    Log.Information("Configuration directory set to: {ConfigDir}", configDir);
    Log.Information("appsettings.json exists: {Exists}", File.Exists(Path.Combine(configDir, "appsettings.json")));
    
    builder.Configuration.SetBasePath(configDir);

    builder.Configuration
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
        .AddEnvironmentVariables("ATMAGENT_")
        .AddCommandLine(args);

    // ── Hosting (Windows Service / Systemd) ─────────────────
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "AtmLogAgent";
    });
    builder.Services.AddSystemd();

    // ── Serilog complet ──────────────────────────────────────
    builder.Services.AddSerilog((services, logConfig) =>
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AtmLogAgent", "Logs", "agent-.log");

        logConfig
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithThreadId()
            .Enrich.WithEnvironmentName()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    });

    // ── Options ──────────────────────────────────────────────
    builder.Services.Configure<AgentConfiguration>(builder.Configuration);

    // ── Services Core ────────────────────────────────────────
    builder.Services.AddSingleton<IEncryptionService>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<EncryptionService>>();
        var config = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentConfiguration>>();
        var keyPath = config.Value.Security.LocalEncryptionKeyId;  // Chemin ou référence
        return new EncryptionService(logger, File.Exists(keyPath) ? keyPath : null);
    });

    builder.Services.AddSingleton<ILogDiscoveryService, LogDiscoveryService>();
    builder.Services.AddSingleton<ILogWatcherService, LogWatcherService>();
    builder.Services.AddSingleton<IBufferService, LocalBufferService>();
    builder.Services.AddSingleton<ITransmissionService, SftpTransmissionService>();
    builder.Services.AddSingleton<IHealthMonitorService, HealthMonitorService>();
    builder.Services.AddSingleton<IUpdateService, UpdateService>();

    // ── Résolution autonome de l'identité ATM ─────────────────
    // Aucune saisie humaine requise pour BankName, Country, City, AtmId :
    // ils sont détectés via le matériel, la géolocalisation et le fichier
    // de provisionnement.  Seule la configuration SFTP (serveur distant)
    // reste à renseigner par un humain.
    builder.Services.AddSingleton<IAtmIdentityResolver, AtmIdentityResolverService>();

    // ── Workers (background tasks) ───────────────────────────
    builder.Services.AddHostedService<LogCollectorWorker>();   // Collecte temps réel
    builder.Services.AddHostedService<TransmissionWorker>();   // Transmission depuis tampon
    builder.Services.AddHostedService<SyncWorker>();           // Sync complète 24h
    builder.Services.AddHostedService<UpdateWorker>();         // Mises à jour automatiques
    builder.Services.AddHostedService<HealthWorker>();         // Heartbeats supervision

    // ── Support natif Windows Service et systemd ─────────────
    if (OperatingSystem.IsWindows())
        builder.Services.AddWindowsService(opts => opts.ServiceName = "AtmLogAgent");
    else if (OperatingSystem.IsLinux())
        builder.Services.AddSystemd();

    // ── Résolution autonome de l'identité ATM (avant Build) ────────
    // On résoud l'identité ICI, avant builder.Build(), pour que
    // PostConfigure injecte les valeurs dans IOptions<AgentConfiguration>
    // avant que n'importe quel service (watcher, buffer...) soit construit.
    //
    // Timeout de 15 secondes : si le réseau n'est pas disponible au boot,
    // on démarre avec les valeurs partielles (AtmId matériel toujours dispo).
    AtmIdentityResolution? preResolvedIdentity = null;
    try
    {
        using var preResolveCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Résolution temporaire avec la config brute (avant DI complète)
        var rawConfig = builder.Configuration
            .GetSection("AtmAgent")
            .Get<AgentConfiguration>() ?? new AgentConfiguration
            {
                Atm          = new AtmIdentity(),
                Transmission = new TransmissionConfig { Host = "", Port = 22, Username = "", Protocol = "SFTP" },
                Security     = new SecurityConfig { LocalEncryptionKeyId = "" },
                LogDiscovery = new LogDiscoveryConfig(),
                Update       = new UpdateConfig { UpdateServerUrl = "" },
                Monitoring   = new MonitoringConfig { HeartbeatUrl = "" },
                Retention    = new RetentionConfig()
            };

        var preLogger = LoggerFactory.Create(b => b.AddSerilog()).CreateLogger<AtmIdentityResolverService>();
        var preResolver = new AtmIdentityResolverService(
            Microsoft.Extensions.Options.Options.Create(rawConfig), preLogger);

        preResolvedIdentity = await preResolver.ResolveAsync(preResolveCts.Token);

        Log.Information(
            "ATM identity pre-resolved — Bank={Bank} ({BSrc}) | {Country}/{City} ({LSrc}) | AtmId={Id} ({ISrc})",
            preResolvedIdentity.BankName, preResolvedIdentity.BankSource,
            preResolvedIdentity.Country, preResolvedIdentity.City, preResolvedIdentity.LocationSource,
            preResolvedIdentity.AtmId, preResolvedIdentity.AtmIdSource);
    }
    catch (OperationCanceledException)
    {
        Log.Warning("ATM identity resolution timed out (15s) — starting with partial identity");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "ATM identity resolution failed — starting with fallback identity");
    }

    // Injecter l'identité résolue dans IOptions via PostConfigure
    // (s'exécute après Configure<AgentConfiguration>, avant toute consommation)
    if (preResolvedIdentity is not null)
    {
        var resolved = preResolvedIdentity; // capture pour closure
        builder.Services.PostConfigure<AgentConfiguration>(cfg =>
        {
            if (cfg.Atm == null)
            {
                Log.Warning("Configuration 'Atm' manquante dans appsettings.json. Initialisation par défaut.");
                cfg.Atm = new AtmIdentity();
            }
            cfg.Atm = cfg.Atm.WithResolution(resolved);
        });
    }

    // ─────────────────────────────────────────────────

    var host = builder.Build();
    Log.Information("ATM Log Agent configured — Starting host");
    await host.RunAsync();
}
catch (Exception ex) when (ex is not OperationCanceledException && ex is not HostAbortedException)
{
    Log.Fatal(ex, "ATM Log Agent terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;
