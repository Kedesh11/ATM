namespace AtmLogAgent.Core.Models;

/// <summary>
/// Configuration principale de l'agent ATM.
/// Chargée depuis un fichier JSON chiffré au démarrage.
/// </summary>
public sealed class AgentConfiguration
{
    public required AtmIdentity Atm { get; set; }  // set (au lieu de init) permet PostConfigure sans réflexion
    public required TransmissionConfig Transmission { get; init; }
    public required SecurityConfig Security { get; init; }
    public required LogDiscoveryConfig LogDiscovery { get; init; }
    public required UpdateConfig Update { get; init; }
    public required MonitoringConfig Monitoring { get; init; }
    public required RetentionConfig Retention { get; init; }
}

/// <summary>
/// Identité unique de l'ATM — utilisée pour construire l'arborescence de stockage.
/// Exemple : BGFI/GABON/LIBREVILLE/ATM_10/...
///
/// ── REMPLISSAGE AUTOMATIQUE (aucune saisie humaine requise) ────────────────
///   BankName → fichier de provisionnement bancaire (provisioning.conf)
///   Country  → géolocalisation IP automatique (ip-api.com)
///   City     → géolocalisation IP automatique
///   AtmId    → numéro de série hardware / adresse MAC / hostname
///
/// Utiliser la valeur spéciale "AUTO" (ou laisser vide) pour la détection
/// automatique. Une valeur explicite sera toujours prioritaire sur la détection.
/// </summary>
public sealed class AtmIdentity
{
    /// <summary>Nom de la banque. "AUTO" = détecté depuis provisioning.conf.</summary>
    public string BankName  { get; init; } = "AUTO";

    /// <summary>Pays de l'ATM. "AUTO" = détecté par géolocalisation IP.</summary>
    public string Country   { get; init; } = "AUTO";

    /// <summary>Ville de l'ATM. "AUTO" = détectée par géolocalisation IP.</summary>
    public string City      { get; init; } = "AUTO";

    /// <summary>Identifiant unique ATM. "AUTO" = numéro de série hardware ou MAC.</summary>
    public string AtmId     { get; init; } = "AUTO";

    /// <summary>Fabricant ATM (optionnel, améliore la découverte des logs).</summary>
    public string? Manufacturer { get; init; }

    /// <summary>Modèle ATM (optionnel, informatif).</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Construit le préfixe de chemin normalisé pour cet ATM.
    /// Ex : BGFI/GABON/LIBREVILLE/ATM-001A2B3C4D5E
    /// </summary>
    public string GetBasePath() =>
        Path.Combine(
            Sanitize(BankName),
            Sanitize(Country),
            Sanitize(City),
            Sanitize(AtmId)
        ).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// Retourne une copie avec les champs mis à jour depuis la résolution autonome.
    /// Toute valeur non-AUTO dans la config originale est conservée (priorité config).
    /// </summary>
    public AtmIdentity WithResolution(AtmLogAgent.Core.Interfaces.AtmIdentityResolution r) => new()
    {
        BankName     = IsAuto(BankName)  ? r.BankName  : BankName,
        Country      = IsAuto(Country)   ? r.Country   : Country,
        City         = IsAuto(City)      ? r.City      : City,
        AtmId        = IsAuto(AtmId)     ? r.AtmId     : AtmId,
        Manufacturer = Manufacturer ?? r.Manufacturer,
        Model        = Model        ?? r.Model
    };

    private static bool IsAuto(string val) =>
        string.IsNullOrWhiteSpace(val) || val.Equals("AUTO", StringComparison.OrdinalIgnoreCase);

    private static string Sanitize(string value) =>
        value.Replace(" ", "_")
             .Replace("/", "-")
             .Replace("\\", "-")
             .Replace(":", "-")
             .Replace("*", "")
             .Replace("?", "")
             .Replace("\"", "")
             .Replace("<", "")
             .Replace(">", "")
             .Replace("|", "")
             .ToUpperInvariant()
             .Trim();
}

public sealed class TransmissionConfig
{
    public required string Protocol { get; init; }       // "SFTP" | "FTPS" | "HTTPS"
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Username { get; init; }
    public string? PrivateKeyPath { get; init; }         // Pour SFTP par clé
    public string? PrivateKeyPassphrase { get; init; }   // Chiffrée AES en mémoire
    public string? Password { get; init; }               // Chiffrée AES en mémoire
    public string? RemoteBasePath { get; init; }
    public string? TlsCertificatePath { get; init; }
    public string? TlsCertificateThumbprint { get; init; }
    public int ConnectionTimeoutSeconds { get; init; } = 30;
    public int KeepAliveIntervalSeconds { get; init; } = 60;
    public bool CompressBeforeTransmit { get; init; } = true;
    public int MaxConcurrentTransfers { get; init; } = 3;
    public int MaxRetryAttempts { get; init; } = 10;
    public int RetryDelaySeconds { get; init; } = 30;
    public int FullSyncIntervalHours { get; init; } = 24;
}

public sealed class SecurityConfig
{
    public required string LocalEncryptionKeyId { get; init; } // Référence HSM ou fichier
    public bool EnableIntegrityChecks { get; init; } = true;
    public bool EnableTamperDetection { get; init; } = true;
    public bool ValidateServerCertificate { get; init; } = true;
    public string? ServerCertificatePinning { get; init; }    // SHA256 du cert serveur
    public bool EnableAuditLog { get; init; } = true;
    public string? AuditLogPath { get; init; }
}

public sealed class LogDiscoveryConfig
{
    public List<string> WatchPaths { get; init; } = [];
    public List<string> FilePatterns { get; init; } = ["*.jrn", "*.log", "*.txt", "*.xml", "*.json"];
    public bool AutoDiscoverAtmPaths { get; init; } = true;
    public bool IncludeSubdirectories { get; init; } = true;
    public List<string> ExcludedPaths { get; init; } = [];
    public int PollingIntervalMs { get; init; } = 500;
}

public sealed class UpdateConfig
{
    public required string UpdateServerUrl { get; init; }
    public string? UpdatePublicKeyPath { get; init; }          // Clé de vérification de signature
    public int CheckIntervalHours { get; init; } = 6;
    public bool EnableAutoUpdate { get; init; } = true;
    public bool AllowHotReload { get; init; } = true;          // Mise à jour sans redémarrage
    public int MaxRollbackVersions { get; init; } = 3;
}

public sealed class MonitoringConfig
{
    public required string HeartbeatUrl { get; init; }
    public int HeartbeatIntervalSeconds { get; init; } = 60;
    public bool ReportDeviceStatuses { get; init; } = true;
    public bool ReportTransactionStats { get; init; } = true;
    public int AlertThresholdBufferSizeMb { get; init; } = 100;
    public int AlertThresholdOfflineMinutes { get; init; } = 30;
}

public sealed class RetentionConfig
{
    public int LocalLogRetentionDays { get; init; } = 30;
    public int BufferedDataRetentionDays { get; init; } = 7;
    public long MaxLocalBufferSizeMb { get; init; } = 500;
    public bool CompressArchivedLogs { get; init; } = true;
}
