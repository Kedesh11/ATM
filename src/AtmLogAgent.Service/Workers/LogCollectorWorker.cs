using AtmLogAgent.Core.Interfaces;
using AtmLogAgent.Core.Models;

namespace AtmLogAgent.Service.Workers;

/// <summary>
/// Worker de collecte des logs en temps réel.
/// Orchestre la découverte des répertoires, la surveillance des fichiers
/// et l'enfilement des nouvelles entrées dans le tampon local.
/// Redémarre automatiquement en cas d'erreur.
/// </summary>
public sealed class LogCollectorWorker : BackgroundService
{
    private readonly ILogDiscoveryService _discovery;
    private readonly ILogWatcherService _watcher;
    private readonly IBufferService _buffer;
    private readonly IHealthMonitorService _health;
    private readonly ILogger<LogCollectorWorker> _logger;

    public LogCollectorWorker(
        ILogDiscoveryService discovery,
        ILogWatcherService watcher,
        IBufferService buffer,
        IHealthMonitorService health,
        ILogger<LogCollectorWorker> logger)
    {
        _discovery = discovery;
        _watcher = watcher;
        _buffer = buffer;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LogCollectorWorker started");

        // S'abonner aux nouvelles lignes de log
        _watcher.LogEntryReceived += OnLogEntryReceived;

        try
        {
            await StartWatchingAllPathsAsync(stoppingToken);

            // Rester actif — la surveillance est event-driven
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("LogCollectorWorker stopping gracefully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LogCollectorWorker encountered a fatal error");
            await _health.RecordAuditEventAsync(new AuditEvent
            {
                AtmId = "UNKNOWN",
                EventType = AuditEventType.AgentCrash,
                Description = "LogCollectorWorker crashed",
                Details = ex.Message,
                Severity = Severity.Critical
            }, CancellationToken.None);
            throw;
        }
        finally
        {
            _watcher.LogEntryReceived -= OnLogEntryReceived;
            await _watcher.StopAllAsync();
        }
    }

    private async Task StartWatchingAllPathsAsync(CancellationToken ct)
    {
        var paths = await _discovery.DiscoverLogPathsAsync(ct);

        if (paths.Count == 0)
        {
            _logger.LogWarning("No log paths discovered — check configuration");
            return;
        }

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            await _watcher.StartWatchingAsync(path, ct);
        }

        _logger.LogInformation("Watching {Count} log director(y/ies)", paths.Count);
    }

    private void OnLogEntryReceived(object? sender, LogEntry entry)
    {
        // Enfilement asynchrone sans bloquer le thread de surveillance
        _ = Task.Run(async () =>
        {
            try
            {
                await _buffer.EnqueueAsync(entry);
                _logger.LogTrace("Buffered entry from {File}", Path.GetFileName(entry.SourceFilePath));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to buffer log entry from {File}", entry.SourceFilePath);
            }
        });
    }
}
