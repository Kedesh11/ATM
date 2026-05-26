using AtmLogAgent.Core.Interfaces;
using AtmLogAgent.Core.Models;
using Microsoft.Extensions.Options;

namespace AtmLogAgent.Service.Workers;

// ══════════════════════════════════════════════════════════════
//  TransmissionWorker — Vide le tampon local vers le serveur SFTP
// ══════════════════════════════════════════════════════════════

/// <summary>
/// Consomme en continu le tampon local et transmet les entrées de log
/// au serveur distant. Gère le backpressure réseau intelligemment.
/// </summary>
public sealed class TransmissionWorker : BackgroundService
{
    private readonly IBufferService _buffer;
    private readonly ITransmissionService _transmission;
    private readonly IHealthMonitorService _health;
    private readonly AgentConfiguration _config;
    private readonly ILogger<TransmissionWorker> _logger;
    private bool _isOnline;

    public TransmissionWorker(
        IBufferService buffer,
        ITransmissionService transmission,
        IHealthMonitorService health,
        IOptions<AgentConfiguration> config,
        ILogger<TransmissionWorker> logger)
    {
        _buffer = buffer;
        _transmission = transmission;
        _health = health;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TransmissionWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingEntriesAsync(stoppingToken);

                // Petite pause pour éviter la saturation CPU
                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TransmissionWorker error — pausing 30s before retry");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task ProcessPendingEntriesAsync(CancellationToken ct)
    {
        // Vérifier la connectivité avant de traiter
        if (!_isOnline)
        {
            _isOnline = await _transmission.TestConnectivityAsync(ct);
            if (_isOnline)
            {
                _logger.LogInformation("Network connectivity restored — resuming transmission");
                await _health.RecordAuditEventAsync(new AuditEvent
                {
                    AtmId = _config.Atm.AtmId,
                    EventType = AuditEventType.NetworkRestored,
                    Description = "Network connection restored",
                    Severity = Severity.Info
                }, ct);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return;
            }
        }

        // Traiter par lots de 50 entrées
        var entries = await _buffer.DequeuePendingEntriesAsync(50, ct);
        if (entries.Count == 0) return;

        var successCount = 0;
        var failCount = 0;

        // Transmission parallèle limitée (max 3 connexions simultanées)
        var semaphore = new SemaphoreSlim(_config.Transmission.MaxConcurrentTransfers);
        var tasks = entries.Select(async entry =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var success = await _transmission.TransmitEntryAsync(entry, ct);
                if (success)
                {
                    await _buffer.MarkEntryCompletedAsync(entry.Id, ct);
                    Interlocked.Increment(ref successCount);
                }
                else
                {
                    await _buffer.MarkEntryFailedAsync(entry.Id, "Transmission returned false", ct);
                    Interlocked.Increment(ref failCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to transmit entry {Id}", entry.Id);
                await _buffer.MarkEntryFailedAsync(entry.Id, ex.Message, ct);
                Interlocked.Increment(ref failCount);
                _isOnline = false; // Forcer re-test connectivité
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        if (successCount > 0)
            _logger.LogInformation("Transmitted {Success}/{Total} entries", successCount, entries.Count);
        if (failCount > 0)
            _logger.LogWarning("{Fail} entries failed — will retry", failCount);
    }
}

// ══════════════════════════════════════════════════════════════
//  SyncWorker — Synchronisation complète des fichiers toutes les 24h
// ══════════════════════════════════════════════════════════════

public sealed class SyncWorker : BackgroundService
{
    private readonly ILogDiscoveryService _discovery;
    private readonly IBufferService _buffer;
    private readonly ITransmissionService _transmission;
    private readonly IEncryptionService _encryption;
    private readonly AgentConfiguration _config;
    private readonly ILogger<SyncWorker> _logger;

    public SyncWorker(
        ILogDiscoveryService discovery,
        IBufferService buffer,
        ITransmissionService transmission,
        IEncryptionService encryption,
        IOptions<AgentConfiguration> config,
        ILogger<SyncWorker> logger)
    {
        _discovery = discovery;
        _buffer = buffer;
        _transmission = transmission;
        _encryption = encryption;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SyncWorker started — full sync every {Hours}h",
            _config.Transmission.FullSyncIntervalHours);

        // Attendre avant le premier cycle (laisser le système se stabiliser)
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunFullSyncAsync(stoppingToken);
                await _buffer.PurgeExpiredDataAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Full sync failed — will retry next cycle");
            }

            await Task.Delay(
                TimeSpan.FromHours(_config.Transmission.FullSyncIntervalHours),
                stoppingToken);
        }
    }

    private async Task RunFullSyncAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting full file sync");
        var paths = await _discovery.DiscoverLogPathsAsync(ct);
        var totalFiles = 0;

        foreach (var dir in paths)
        {
            if (!Directory.Exists(dir)) continue;

            var files = _config.LogDiscovery.FilePatterns
                .SelectMany(p => Directory.EnumerateFiles(dir, p,
                    _config.LogDiscovery.IncludeSubdirectories
                        ? SearchOption.AllDirectories
                        : SearchOption.TopDirectoryOnly));

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var fi = new FileInfo(file);
                var checksum = await _encryption.ComputeFileChecksumAsync(file, ct);
                var remotePath = _discovery.BuildRemotePath(file, fi.LastWriteTimeUtc);

                var record = new FileSyncRecord
                {
                    AtmId = _config.Atm.AtmId,
                    LocalPath = file,
                    RemotePath = remotePath,
                    FileSizeBytes = fi.Length,
                    LocalChecksum = checksum,
                    Compressed = _config.Transmission.CompressBeforeTransmit
                };

                await _buffer.EnqueueFileAsync(record, ct);
                totalFiles++;
            }
        }

        // Traiter les fichiers en attente
        var filesToSync = await _buffer.DequeuePendingFilesAsync(100, ct);
        var synced = 0;

        foreach (var record in filesToSync)
        {
            ct.ThrowIfCancellationRequested();
            var ok = await _transmission.TransmitFileAsync(record, ct);
            if (ok)
            {
                // Vérifier l'intégrité côté serveur
                var remoteChecksum = await _transmission.VerifyRemoteChecksumAsync(
                    record.RemotePath, record.LocalChecksum ?? "", ct)
                    ? record.LocalChecksum ?? ""
                    : "";
                await _buffer.MarkFileCompletedAsync(record.Id, remoteChecksum, ct);
                synced++;
            }
        }

        _logger.LogInformation("Full sync completed: {Synced}/{Total} file(s) synchronized", synced, totalFiles);
    }
}

// ══════════════════════════════════════════════════════════════
//  UpdateWorker — Vérification et installation des mises à jour
// ══════════════════════════════════════════════════════════════

public sealed class UpdateWorker : BackgroundService
{
    private readonly IUpdateService _updateService;
    private readonly IHealthMonitorService _health;
    private readonly AgentConfiguration _config;
    private readonly ILogger<UpdateWorker> _logger;

    public UpdateWorker(
        IUpdateService updateService,
        IHealthMonitorService health,
        IOptions<AgentConfiguration> config,
        ILogger<UpdateWorker> logger)
    {
        _updateService = updateService;
        _health = health;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.Update.EnableAutoUpdate)
        {
            _logger.LogInformation("Auto-update disabled — UpdateWorker inactive");
            return;
        }

        _logger.LogInformation("UpdateWorker started — checking every {Hours}h",
            _config.Update.CheckIntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var update = await _updateService.CheckForUpdateAsync(stoppingToken);
                if (update is not null)
                {
                    _logger.LogInformation("Update available: v{Current} → v{New}",
                        _updateService.CurrentVersion, update.Version);

                    var applied = await _updateService.ApplyUpdateAsync(update, stoppingToken);
                    await _health.RecordAuditEventAsync(new AuditEvent
                    {
                        AtmId = _config.Atm.AtmId,
                        EventType = applied ? AuditEventType.UpdateInstalled : AuditEventType.UpdateFailed,
                        Description = applied
                            ? $"Updated to v{update.Version}"
                            : $"Update to v{update.Version} failed",
                        Severity = applied ? Severity.Info : Severity.Error
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update check failed");
            }

            await Task.Delay(TimeSpan.FromHours(_config.Update.CheckIntervalHours), stoppingToken);
        }
    }
}

// ══════════════════════════════════════════════════════════════
//  HealthWorker — Heartbeats et supervision centralisée
// ══════════════════════════════════════════════════════════════

public sealed class HealthWorker : BackgroundService
{
    private readonly IHealthMonitorService _health;
    private readonly AgentConfiguration _config;
    private readonly ILogger<HealthWorker> _logger;

    public HealthWorker(
        IHealthMonitorService health,
        IOptions<AgentConfiguration> config,
        ILogger<HealthWorker> logger)
    {
        _health = health;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HealthWorker started — heartbeat every {Sec}s",
            _config.Monitoring.HeartbeatIntervalSeconds);

        await _health.RecordAuditEventAsync(new AuditEvent
        {
            AtmId = _config.Atm.AtmId,
            EventType = AuditEventType.AgentStarted,
            Description = "ATM Log Agent started",
            Severity = Severity.Info
        }, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _health.SendHeartbeatAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat failed");
            }

            await Task.Delay(
                TimeSpan.FromSeconds(_config.Monitoring.HeartbeatIntervalSeconds),
                stoppingToken);
        }

        await _health.RecordAuditEventAsync(new AuditEvent
        {
            AtmId = _config.Atm.AtmId,
            EventType = AuditEventType.AgentStopped,
            Description = "ATM Log Agent stopped gracefully",
            Severity = Severity.Info
        }, CancellationToken.None);
    }
}
