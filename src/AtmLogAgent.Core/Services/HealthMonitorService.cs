using System.Net.Http.Json;
using AtmLogAgent.Core.Interfaces;
using AtmLogAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AtmLogAgent.Core.Services;

/// <summary>
/// Service de supervision de santé.
/// Collecte les métriques de l'agent et les envoie au serveur de monitoring centralisé.
/// Journalise également tous les événements d'audit critiques.
/// </summary>
public sealed class HealthMonitorService : IHealthMonitorService, IDisposable
{
    private readonly AgentConfiguration _config;
    private readonly IBufferService _buffer;
    private readonly ILogger<HealthMonitorService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _auditLogPath;
    private readonly SemaphoreSlim _auditLock = new(1, 1);
    private DateTime? _lastSuccessfulTransmit;
    private DateTime? _lastFullSync;
    private long _totalSent;
    private long _totalBytes;
    private long _totalErrors;
    private bool _disposed;

    private static readonly string AgentVersion =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";

    public HealthMonitorService(
        IOptions<AgentConfiguration> config,
        IBufferService buffer,
        ILogger<HealthMonitorService> logger)
    {
        _config = config.Value;
        _buffer = buffer;
        _logger = logger;
        _auditLogPath = _config.Security.AuditLogPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "AtmLogAgent", "Logs", "audit.log");

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
            BaseAddress = new Uri(_config.Monitoring.HeartbeatUrl)
        };
        _httpClient.DefaultRequestHeaders.Add("X-Agent-Id", _config.Atm.AtmId);
        _httpClient.DefaultRequestHeaders.Add("X-Agent-Version", AgentVersion);
    }

    public async Task SendHeartbeatAsync(CancellationToken ct = default)
    {
        var report = await BuildHealthReportAsync(ct);

        try
        {
            var response = await _httpClient.PostAsJsonAsync("", report, ct);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Heartbeat sent successfully — pending: {Pending}", report.PendingEntriesCount);
            }
            else
            {
                _logger.LogWarning("Heartbeat rejected: HTTP {Status}", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning("Heartbeat failed: {Message}", ex.Message);
        }
    }

    public async Task RecordAuditEventAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        var line = $"{auditEvent.OccurredUtc:yyyy-MM-dd HH:mm:ss.fff} " +
                   $"[{auditEvent.Severity}] " +
                   $"[{auditEvent.EventType}] " +
                   $"ATM={auditEvent.AtmId} " +
                   $"{auditEvent.Description}" +
                   (auditEvent.Details != null ? $" | {auditEvent.Details}" : "") +
                   (auditEvent.Suspicious ? " *** SUSPICIOUS ***" : "");

        // Log via Serilog
        var level = auditEvent.Severity switch
        {
            Severity.Critical => Microsoft.Extensions.Logging.LogLevel.Critical,
            Severity.Error => Microsoft.Extensions.Logging.LogLevel.Error,
            Severity.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
            _ => Microsoft.Extensions.Logging.LogLevel.Information
        };
        _logger.Log(level, "[AUDIT] {Event}: {Description}", auditEvent.EventType, auditEvent.Description);

        if (!_config.Security.EnableAuditLog) return;

        // Écriture dans le fichier d'audit dédié (append thread-safe)
        await _auditLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_auditLogPath)!);
            await File.AppendAllTextAsync(_auditLogPath, line + Environment.NewLine, ct);
        }
        finally
        {
            _auditLock.Release();
        }
    }

    public async Task<HealthReport> BuildHealthReportAsync(CancellationToken ct = default)
    {
        var pending = await _buffer.GetPendingCountAsync(ct);
        var bufferSize = await _buffer.GetBufferSizeBytesAsync(ct);

        return new HealthReport
        {
            AtmId = _config.Atm.AtmId,
            AgentVersion = AgentVersion,
            IsOnline = true,
            IsTransmitting = pending > 0,
            PendingEntriesCount = pending,
            BufferSizeBytes = bufferSize,
            LastSuccessfulTransmitUtc = _lastSuccessfulTransmit,
            LastFullSyncUtc = _lastFullSync,
            CurrentAgentVersion = AgentVersion,
            Stats = new TransmissionStats
            {
                TotalEntriesSent = _totalSent,
                TotalBytesSent = _totalBytes,
                TotalErrors = _totalErrors
            }
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        _auditLock.Dispose();
    }
}
