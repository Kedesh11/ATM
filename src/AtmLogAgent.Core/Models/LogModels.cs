namespace AtmLogAgent.Core.Models;

/// <summary>
/// Représente une unité de log capturée depuis un fichier ATM.
/// </summary>
public sealed class LogEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string AtmId { get; init; }
    public required string SourceFilePath { get; init; }
    public required string Content { get; init; }
    public required LogFormat Format { get; init; }
    public DateTime CapturedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? LogTimestamp { get; init; }          // Timestamp extrait du log
    public string? RemotePath { get; init; }              // Chemin cible normalisé
    public string? Checksum { get; init; }                // SHA-256 du contenu
    public TransmissionStatus Status { get; set; } = TransmissionStatus.Pending;
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Représente un fichier complet à synchroniser (sync périodique 24h).
/// </summary>
public sealed class FileSyncRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string AtmId { get; init; }
    public required string LocalPath { get; init; }
    public required string RemotePath { get; init; }
    public long FileSizeBytes { get; init; }
    public string? LocalChecksum { get; init; }
    public string? RemoteChecksum { get; set; }
    public DateTime ScheduledUtc { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public TransmissionStatus Status { get; set; } = TransmissionStatus.Pending;
    public bool Compressed { get; set; }
    public int RetryCount { get; set; }
}

/// <summary>
/// Enregistrement d'audit pour journalisation des événements critiques de l'agent.
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string AtmId { get; init; }
    public required AuditEventType EventType { get; init; }
    public required string Description { get; init; }
    public string? Details { get; init; }
    public Severity Severity { get; init; } = Severity.Info;
    public DateTime OccurredUtc { get; init; } = DateTime.UtcNow;
    public string? Username { get; init; }
    public string? IpAddress { get; init; }
    public bool Suspicious { get; init; }
}

/// <summary>
/// Statut de santé de l'agent à envoyer au serveur de supervision.
/// </summary>
public sealed class HealthReport
{
    public required string AtmId { get; init; }
    public required string AgentVersion { get; init; }
    public DateTime ReportedUtc { get; init; } = DateTime.UtcNow;
    public bool IsOnline { get; init; }
    public bool IsTransmitting { get; init; }
    public long PendingEntriesCount { get; init; }
    public long PendingFilesCount { get; init; }
    public long BufferSizeBytes { get; init; }
    public DateTime? LastSuccessfulTransmitUtc { get; init; }
    public DateTime? LastFullSyncUtc { get; init; }
    public string? CurrentAgentVersion { get; init; }
    public Dictionary<string, DeviceStatus> DeviceStatuses { get; init; } = [];
    public TransmissionStats Stats { get; init; } = new();
}

public sealed class TransmissionStats
{
    public long TotalEntriesSent { get; set; }
    public long TotalBytesSent { get; set; }
    public long TotalErrors { get; set; }
    public long TotalRetries { get; set; }
    public double UptimePercent { get; set; }
}

public sealed class DeviceStatus
{
    public required string DeviceName { get; init; }
    public int Status { get; init; }
    public int Supply { get; init; }
    public DateTime LastSeenUtc { get; init; } = DateTime.UtcNow;
}

public enum TransmissionStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Abandoned
}

public enum LogFormat
{
    PlainText,
    Xml,
    Json,
    Csv,
    Proprietary,
    Unknown
}

public enum AuditEventType
{
    AgentStarted,
    AgentStopped,
    AgentCrash,
    TransmissionSuccess,
    TransmissionFailure,
    SyncCompleted,
    SyncFailed,
    UpdateInstalled,
    UpdateFailed,
    Rollback,
    TamperDetected,
    UnauthorizedAccess,
    CertificateExpiring,
    ConfigurationChanged,
    NetworkOffline,
    NetworkRestored,
    BufferThresholdExceeded
}

public enum Severity
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}
