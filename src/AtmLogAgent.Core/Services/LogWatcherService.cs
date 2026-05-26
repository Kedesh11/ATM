using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using AtmLogAgent.Core.Interfaces;
using AtmLogAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AtmLogAgent.Core.Services;

/// <summary>
/// Surveille les fichiers de logs en temps réel via FileSystemWatcher.
/// Lit les nouvelles lignes au fil de l'eau, gère la rotation des fichiers,
/// et reprend la lecture à la bonne position après un redémarrage.
///
/// CORRECTION BUG CRITIQUE — Deadlock FileSystemWatcher :
///   Les handlers Changed/Created de FileSystemWatcher s'exécutent sur un
///   thread ThreadPool synchrone. Appeler .GetAwaiter().GetResult() depuis
///   ce contexte bloque le thread et peut provoquer un deadlock sous
///   SynchronizationContext (Windows Service, ASP.NET) ou une famine du
///   ThreadPool si de nombreux événements arrivent simultanément.
///
///   SOLUTION : Pattern Producer/Consumer via System.Threading.Channels.
///   Le handler FSW se contente de TryWrite (non-bloquant, O(1), thread-safe).
///   Une boucle async dédiée (ConsumeFileEventsAsync) traite les chemins de
///   fichiers de manière entièrement asynchrone sans aucun blocage.
/// </summary>
public sealed class LogWatcherService : ILogWatcherService, IDisposable
{
    private readonly AgentConfiguration _config;
    private readonly ILogDiscoveryService _discovery;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<LogWatcherService> _logger;

    // FileSystemWatcher par répertoire surveillé
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();

    // Position de lecture mémorisée par fichier (pour reprise après crash)
    private readonly ConcurrentDictionary<string, long> _filePositions = new();

    // ── CHANNEL : découplage sync/async ──────────────────────────────────────
    // BoundedCapacity = 1000 : limite mémoire + backpressure naturelle.
    // DropOldest : si saturé, on abandonne l'ancien event (le prochain Changed
    //              sur le même fichier rattrapera les lignes manquées).
    // SingleReader = true  : une seule boucle consommatrice, pas de contention.
    // SingleWriter = false : N watchers peuvent publier en parallèle.
    private readonly Channel<string> _fileEventChannel =
        Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    // Fichier de positions persisté sur disque (survit aux redémarrages)
    private readonly string _positionsFilePath;
    private readonly SemaphoreSlim _positionLock = new(1, 1);

    // Gestion du cycle de vie de la boucle consommatrice
    private CancellationTokenSource? _consumerCts;
    private Task? _consumerTask;

    // ── TIMER DE SAUVEGARDE PERIODIQUE (P2.3) ─────────────────────────────────
    // Les ATM équipés de disques eMMC ou CompactFlash ont un nombre limité
    // de cycles d'écriture. Sauvegarder les positions à chaque lot de lignes
    // (potentiellement des centaines de fois par heure) use prématurément les
    // cellules flash. On persiste toutes les 30 secondes au maximum.
    private Timer? _positionSaveTimer;
    private volatile bool _positionsDirty;
    private const int PositionSaveIntervalMs = 30_000; // 30 secondes

    private bool _disposed;

    public event EventHandler<LogEntry>? LogEntryReceived;

    public LogWatcherService(
        IOptions<AgentConfiguration> config,
        ILogDiscoveryService discovery,
        IEncryptionService encryption,
        ILogger<LogWatcherService> logger)
    {
        _config = config.Value;
        _discovery = discovery;
        _encryption = encryption;
        _logger = logger;
        _positionsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AtmLogAgent", "positions.dat");

        // Démarrer le timer de sauvegarde périodique des positions (P2.3)
        _positionSaveTimer = new Timer(
            async _ => await PeriodicallyFlushPositionsAsync(),
            null,
            TimeSpan.FromMilliseconds(PositionSaveIntervalMs),
            TimeSpan.FromMilliseconds(PositionSaveIntervalMs));
    }

    public async Task StartWatchingAsync(string directoryPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(directoryPath))
        {
            _logger.LogWarning("Directory not found, skipping: {Path}", directoryPath);
            return;
        }

        // Charger les positions persistées
        await LoadPositionsAsync(ct);

        // Lire les fichiers existants depuis la dernière position connue
        await CatchUpExistingFilesAsync(directoryPath, ct);

        // Démarrer la boucle consommatrice async (une seule instance globale)
        if (_consumerTask is null || _consumerTask.IsCompleted)
        {
            _consumerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Task.Run isole la boucle du SynchronizationContext appelant
            _consumerTask = Task.Run(
                () => ConsumeFileEventsAsync(_consumerCts.Token),
                CancellationToken.None);
        }

        // ── CORRECTION CRITIQUE ───────────────────────────────────────────────
        // Les lambdas ci-dessous sont SYNCHRONES (signature void, pas async).
        // EnqueueFileEvent est non-bloquant : TryWrite retourne immédiatement.
        // Aucun risque de deadlock, aucun blocage de thread pool.
        var watcher = new FileSystemWatcher(directoryPath)
        {
            Filter                = "*.*",
            IncludeSubdirectories = _config.LogDiscovery.IncludeSubdirectories,
            NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents   = true
        };

        // ── FIX P2.1 — Gérer la rotation des fichiers .jrn à minuit ────────────
        // Les ATM NCR APTRA créent un nouveau fichier YYYYMMDD.jrn à minuit UTC
        // pendant qu'une transaction peut être en cours. L'ancien fichier est
        // renommé (Renamed event) avant la création du nouveau (Created event).
        // Sans ce handler, les lignes écrites dans la dernière minute de la journée
        // et le nouveau fichier créé à minuit ne seraient pas capturés.
        watcher.Changed += (_, e) => EnqueueFileEvent(e.FullPath);
        watcher.Created += (_, e) => EnqueueFileEvent(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            // L'ancien fichier (e.OldFullPath) peut avoir des lignes non lues
            // juste avant la rotation ; le nouveau fichier (e.FullPath) commence à 0.
            EnqueueFileEvent(e.OldFullPath);
            EnqueueFileEvent(e.FullPath);
        };
        watcher.Error += (_, e) => _logger.LogError(e.GetException(),
            "FileSystemWatcher error on {Path}", directoryPath);

        _watchers[directoryPath] = watcher;
        _logger.LogInformation("Watching directory: {Path}", directoryPath);
    }

    /// <summary>
    /// Publie un chemin de fichier dans le Channel depuis un handler synchrone.
    /// TryWrite est non-bloquant, thread-safe, et ne lève jamais d'exception.
    /// </summary>
    private void EnqueueFileEvent(string filePath)
    {
        if (!IsWatchedFilePattern(filePath)) return;

        if (!_fileEventChannel.Writer.TryWrite(filePath))
        {
            // Le channel est plein (1000 events en attente) : on logue et on
            // abandonne cet event. Le prochain Changed rattrapera les lignes.
            _logger.LogWarning(
                "File event channel full — dropped event for {File}",
                Path.GetFileName(filePath));
        }
    }

    /// <summary>
    /// Boucle consommatrice async : lit les chemins depuis le Channel et traite
    /// chaque fichier de manière entièrement asynchrone, sans bloquer de thread.
    /// ReadAllAsync est efficace : attend sans polling quand le channel est vide.
    /// </summary>
    private async Task ConsumeFileEventsAsync(CancellationToken ct)
    {
        _logger.LogDebug("File event consumer loop started");
        try
        {
            await foreach (var filePath in _fileEventChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ProcessFileChangedAsync(filePath, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing file event: {Path}", filePath);
                    // On continue le traitement des autres fichiers en attente
                }
            }
        }
        catch (OperationCanceledException) { /* arrêt normal demandé */ }
        _logger.LogDebug("File event consumer loop stopped");
    }

    public async Task StopAllAsync()
    {
        foreach (var (path, watcher) in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
            _logger.LogInformation("Stopped watching: {Path}", path);
        }
        _watchers.Clear();

        // Signaler la fin de production, puis annuler si la boucle traîne
        _fileEventChannel.Writer.TryComplete();

        if (_consumerCts is not null)
            await _consumerCts.CancelAsync();

        if (_consumerTask is not null)
        {
            try
            {
                await _consumerTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Consumer loop did not stop within 5s — forcing shutdown");
            }
            catch (OperationCanceledException) { /* normal */ }
        }

        await SavePositionsAsync(CancellationToken.None);
    }

    // ──────────────────────────────────────────────
    // Gestion des changements de fichiers
    // ──────────────────────────────────────────────

    private async Task ProcessFileChangedAsync(string filePath, CancellationToken ct)
    {
        try
        {
            await ReadNewLinesAsync(filePath, ct);
        }
        catch (IOException ex) when (ex.HResult == -2147024864) // File locked (ERROR_SHARING_VIOLATION)
        {
            // Fichier verrouillé par l'ATM — réessayer dans 500ms
            await Task.Delay(500, ct);
            await ReadNewLinesAsync(filePath, ct);
        }
    }

    private async Task ReadNewLinesAsync(string filePath, CancellationToken ct)
    {
        using var stream = await OpenFileWithRetryAsync(filePath, ct);
        if (stream is null) return;

        var lastPosition = _filePositions.GetOrAdd(filePath, 0L);
        if (stream.Length <= lastPosition) return;  // Rien de nouveau

        stream.Seek(lastPosition, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var linesRead = 0;

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (string.IsNullOrWhiteSpace(line)) continue;

            var format    = _discovery.DetectFormat(filePath);
            var logDate   = TryExtractTimestamp(line, filePath);
            var remotePath = _discovery.BuildRemotePath(filePath, logDate);
            var checksum  = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(line)));

            var entry = new LogEntry
            {
                AtmId          = _config.Atm.AtmId,
                SourceFilePath = filePath,
                Content        = line,
                Format         = format,
                LogTimestamp   = logDate,
                RemotePath     = remotePath,
                Checksum       = checksum
            };

            LogEntryReceived?.Invoke(this, entry);
            linesRead++;
        }

        _filePositions[filePath] = stream.Position;

        if (linesRead > 0)
        {
            // Marquer les positions comme modifiées : le timer les persistera
            // dans les prochaines 30 secondes (P2.3 : évite l'usure flash).
            _positionsDirty = true;
            _logger.LogDebug("Read {Count} new line(s) from {File}",
                linesRead, Path.GetFileName(filePath));
        }
    }

    /// <summary>
    /// Rattrape les fichiers existants depuis la dernière position connue.
    /// Appelé au démarrage pour ne perdre aucune donnée écrite hors ligne.
    /// </summary>
    private async Task CatchUpExistingFilesAsync(string directoryPath, CancellationToken ct)
    {
        var searchOption = _config.LogDiscovery.IncludeSubdirectories
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        var files = _config.LogDiscovery.FilePatterns
            .SelectMany(p => Directory.EnumerateFiles(directoryPath, p, searchOption))
            .Distinct();

        var count = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            await ReadNewLinesAsync(file, ct);
            count++;
        }

        if (count > 0)
            _logger.LogInformation("Catch-up completed: processed {Count} existing file(s) in {Dir}",
                count, directoryPath);
    }

    // ──────────────────────────────────────────────
    // Persistance des positions de lecture
    // ──────────────────────────────────────────────

    private async Task SavePositionsAsync(CancellationToken ct)
    {
        await _positionLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_positionsFilePath)!);
            var json = System.Text.Json.JsonSerializer.Serialize(
                _filePositions.ToDictionary(kv => kv.Key, kv => kv.Value));
            var encrypted = _encryption.EncryptString(json);
            await File.WriteAllTextAsync(_positionsFilePath, encrypted, ct);
            _positionsDirty = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist file positions");
        }
        finally
        {
            _positionLock.Release();
        }
    }

    /// <summary>
    /// Appelé par le timer toutes les 30 secondes : ne sauvegarde que si
    /// des positions ont réellement été modifiées depuis la dernière sauvegarde.
    /// </summary>
    private async Task PeriodicallyFlushPositionsAsync()
    {
        if (!_positionsDirty) return;
        try
        {
            await SavePositionsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic position flush failed");
        }
    }

    private async Task LoadPositionsAsync(CancellationToken ct)
    {
        if (!File.Exists(_positionsFilePath)) return;
        try
        {
            var encrypted = await File.ReadAllTextAsync(_positionsFilePath, ct);
            var json      = _encryption.DecryptString(encrypted);
            var positions = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, long>>(json);
            if (positions is null) return;

            foreach (var (path, pos) in positions)
                _filePositions[path] = pos;

            _logger.LogInformation("Loaded {Count} file positions from disk", positions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load file positions — starting from end of files");
        }
    }

    // ──────────────────────────────────────────────
    // Utilitaires
    // ──────────────────────────────────────────────

    private bool IsWatchedFilePattern(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return _config.LogDiscovery.FilePatterns
            .Any(p => p.Replace("*", "").Equals(ext, StringComparison.OrdinalIgnoreCase)
                   || p == "*.*");
    }

    private static async Task<FileStream?> OpenFileWithRetryAsync(
        string path, CancellationToken ct, int maxAttempts = 5)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (i < maxAttempts - 1)
            {
                await Task.Delay(200 * (i + 1), ct);
            }
        }
        return null;
    }

    /// <summary>
    /// Extrait le timestamp depuis une ligne de log ATM.
    /// Supporte : "HH:MM:SS ..." et "*NNN*HH:MM:SS ..."
    /// Combine l'heure extraite avec la date du nom de fichier (YYYYMMDD.jrn).
    /// </summary>
    private static DateTime? TryExtractTimestamp(string line, string filePath)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            line, @"(\d{2}):(\d{2}):(\d{2})");
        if (!match.Success) return null;

        if (!int.TryParse(match.Groups[1].Value, out var h)) return null;
        if (!int.TryParse(match.Groups[2].Value, out var m)) return null;
        if (!int.TryParse(match.Groups[3].Value, out var s)) return null;

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        if (fileName.Length == 8 && DateTime.TryParseExact(fileName, "yyyyMMdd",
            null, System.Globalization.DateTimeStyles.None, out var fileDate))
        {
            return fileDate.AddHours(h).AddMinutes(m).AddSeconds(s);
        }

        return DateTime.UtcNow.Date.AddHours(h).AddMinutes(m).AddSeconds(s);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Flush final des positions avant arrêt
        _positionSaveTimer?.Dispose();
        if (_positionsDirty)
        {
            SavePositionsAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        _fileEventChannel.Writer.TryComplete();
        _consumerCts?.Cancel();
        _consumerCts?.Dispose();
        foreach (var (_, watcher) in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        _positionLock.Dispose();
    }
}
