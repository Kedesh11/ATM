using AtmLogAgent.Core.Models;

namespace AtmLogAgent.Core.Interfaces;

/// <summary>
/// Détecte automatiquement les répertoires de logs selon le fabricant / OS / architecture ATM.
/// </summary>
public interface ILogDiscoveryService
{
    /// <summary>Découvre les chemins de logs disponibles sur cet ATM.</summary>
    Task<IReadOnlyList<string>> DiscoverLogPathsAsync(CancellationToken ct = default);

    /// <summary>Détermine le format d'un fichier de log.</summary>
    LogFormat DetectFormat(string filePath);

    /// <summary>Calcule le chemin distant normalisé pour un fichier donné.</summary>
    string BuildRemotePath(string localFilePath, DateTime? logDate = null);
}

/// <summary>
/// Surveille les fichiers de logs en temps réel et émet les nouvelles lignes.
/// </summary>
public interface ILogWatcherService
{
    /// <summary>Démarre la surveillance d'un répertoire.</summary>
    Task StartWatchingAsync(string directoryPath, CancellationToken ct = default);

    /// <summary>Arrête toutes les surveillances actives.</summary>
    Task StopAllAsync();

    /// <summary>Événement déclenché pour chaque nouvelle ligne de log capturée.</summary>
    event EventHandler<LogEntry> LogEntryReceived;
}

/// <summary>
/// Transmet les logs vers le serveur distant (SFTP/FTPS/HTTPS).
/// </summary>
public interface ITransmissionService
{
    /// <summary>Transmet une entrée de log individuelle.</summary>
    Task<bool> TransmitEntryAsync(LogEntry entry, CancellationToken ct = default);

    /// <summary>Transmet un fichier complet (sync périodique).</summary>
    Task<bool> TransmitFileAsync(FileSyncRecord record, CancellationToken ct = default);

    /// <summary>Vérifie la connectivité avec le serveur distant.</summary>
    Task<bool> TestConnectivityAsync(CancellationToken ct = default);

    /// <summary>Vérifie l'intégrité d'un fichier déjà transféré.</summary>
    Task<bool> VerifyRemoteChecksumAsync(string remotePath, string expectedChecksum, CancellationToken ct = default);
}

/// <summary>
/// Tampon local persistant pour les logs en attente de transmission.
/// Garantit zéro perte de données en cas de coupure réseau.
/// </summary>
public interface IBufferService
{
    /// <summary>Enfile une entrée de log dans le tampon.</summary>
    Task EnqueueAsync(LogEntry entry, CancellationToken ct = default);

    /// <summary>Enfile un fichier à synchroniser.</summary>
    Task EnqueueFileAsync(FileSyncRecord record, CancellationToken ct = default);

    /// <summary>Récupère les prochaines entrées en attente.</summary>
    Task<IReadOnlyList<LogEntry>> DequeuePendingEntriesAsync(int maxCount, CancellationToken ct = default);

    /// <summary>Récupère les prochains fichiers en attente de synchronisation.</summary>
    Task<IReadOnlyList<FileSyncRecord>> DequeuePendingFilesAsync(int maxCount, CancellationToken ct = default);

    /// <summary>Marque une entrée comme transmise avec succès.</summary>
    Task MarkEntryCompletedAsync(Guid entryId, CancellationToken ct = default);

    /// <summary>Marque une entrée comme échouée (pour retry).</summary>
    Task MarkEntryFailedAsync(Guid entryId, string errorMessage, CancellationToken ct = default);

    /// <summary>Marque un fichier comme synchronisé.</summary>
    Task MarkFileCompletedAsync(Guid recordId, string remoteChecksum, CancellationToken ct = default);

    /// <summary>Retourne la taille totale du tampon en octets.</summary>
    Task<long> GetBufferSizeBytesAsync(CancellationToken ct = default);

    /// <summary>Retourne le nombre d'entrées en attente.</summary>
    Task<long> GetPendingCountAsync(CancellationToken ct = default);

    /// <summary>Nettoie les données expirées selon la politique de rétention.</summary>
    Task PurgeExpiredDataAsync(CancellationToken ct = default);
}

/// <summary>
/// Chiffrement AES-256-GCM pour les données sensibles en transit interne et au repos.
/// </summary>
public interface IEncryptionService
{
    /// <summary>Chiffre un tableau d'octets.</summary>
    byte[] Encrypt(byte[] plaintext);

    /// <summary>Déchiffre un tableau d'octets.</summary>
    byte[] Decrypt(byte[] ciphertext);

    /// <summary>Chiffre une chaîne (retourne Base64).</summary>
    string EncryptString(string plaintext);

    /// <summary>Déchiffre une chaîne depuis Base64.</summary>
    string DecryptString(string ciphertext);

    /// <summary>Calcule le SHA-256 d'un fichier.</summary>
    Task<string> ComputeFileChecksumAsync(string filePath, CancellationToken ct = default);

    /// <summary>Vérifie l'intégrité d'un fichier.</summary>
    Task<bool> VerifyFileIntegrityAsync(string filePath, string expectedChecksum, CancellationToken ct = default);
}

/// <summary>
/// Gestion des mises à jour automatiques avec rollback.
/// </summary>
public interface IUpdateService
{
    /// <summary>Vérifie la disponibilité d'une nouvelle version.</summary>
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>Télécharge et applique une mise à jour.</summary>
    Task<bool> ApplyUpdateAsync(UpdateInfo update, CancellationToken ct = default);

    /// <summary>Restaure la version précédente en cas d'échec.</summary>
    Task<bool> RollbackAsync(CancellationToken ct = default);

    /// <summary>Retourne la version actuelle de l'agent.</summary>
    string CurrentVersion { get; }
}

/// <summary>
/// Supervision de santé et envoi de heartbeats au serveur central.
/// </summary>
public interface IHealthMonitorService
{
    /// <summary>Collecte et envoie un rapport de santé.</summary>
    Task SendHeartbeatAsync(CancellationToken ct = default);

    /// <summary>Enregistre un événement d'audit.</summary>
    Task RecordAuditEventAsync(AuditEvent auditEvent, CancellationToken ct = default);

    /// <summary>Construit le rapport de santé courant.</summary>
    Task<HealthReport> BuildHealthReportAsync(CancellationToken ct = default);
}

/// <summary>
/// Métadonnées d'une mise à jour disponible.
/// </summary>
public sealed class UpdateInfo
{
    public required string Version { get; init; }
    public required string DownloadUrl { get; init; }
    public required string Checksum { get; init; }
    public required string Signature { get; init; }    // Signature RSA de l'éditeur
    public string? ReleaseNotes { get; init; }
    public bool IsCritical { get; init; }
    public DateTime ReleasedUtc { get; init; }
}

/// <summary>
/// Résout de manière autonome l'identité complète de l'ATM sans intervention humaine.
///
/// Stratégie de détection (ordre de priorité décroissant) :
///
///   AtmId    → Numéro de série hardware (DMI/BIOS)
///            → Adresse MAC de l'interface réseau principale
///            → Nom d'hôte machine
///
///   Country  → Géolocalisation IP (ip-api.com, sans clé API)
///   City     → Géolocalisation IP
///            → Fallback : fuseau horaire système → pays
///
///   BankName → Fichier de provisionnement déposé par le technicien bancaire :
///              Windows : C:\ProgramData\AtmLogAgent\provisioning.conf
///              Linux   : /etc/atm-agent/provisioning.conf
///            → Fallback : dérivé du hostname SFTP (sftp.bgfi.com → BGFI)
///
/// La SEULE intervention humaine requise est le fichier de provisionnement
/// ou la configuration SFTP (serveur distant).
/// </summary>
public interface IAtmIdentityResolver
{
    /// <summary>
    /// Résout et retourne l'identité complète de cet ATM.
    /// Appelé une seule fois au démarrage de l'agent.
    /// </summary>
    Task<AtmIdentityResolution> ResolveAsync(CancellationToken ct = default);
}

/// <summary>
/// Résultat de la résolution d'identité ATM avec traçabilité des sources.
/// </summary>
public sealed class AtmIdentityResolution
{
    public required string AtmId     { get; init; }
    public required string BankName  { get; init; }
    public required string Country   { get; init; }
    public required string City      { get; init; }
    public string? Manufacturer      { get; init; }
    public string? Model             { get; init; }

    // Traçabilité : d'où vient chaque valeur
    public required string AtmIdSource    { get; init; }   // "hardware_serial"|"mac_address"|"hostname"
    public required string LocationSource { get; init; }   // "ip_geolocation"|"timezone"|"config"
    public required string BankSource     { get; init; }   // "provisioning_file"|"sftp_hostname"|"config"

    public bool IsFullyResolved =>
        !string.IsNullOrWhiteSpace(AtmId) &&
        !string.IsNullOrWhiteSpace(BankName) &&
        !string.IsNullOrWhiteSpace(Country) &&
        !string.IsNullOrWhiteSpace(City);
}
