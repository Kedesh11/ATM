using System.Runtime.InteropServices;
using AtmLogAgent.Core.Interfaces;
using AtmLogAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AtmLogAgent.Core.Services;

/// <summary>
/// Détecte automatiquement les répertoires de logs ATM selon le fabricant,
/// le modèle et le système d'exploitation sous-jacent.
/// Supporte : Diebold Nixdorf, NCR, Nautilus Hyosung, Wincor, GRG Banking.
/// </summary>
public sealed class LogDiscoveryService : ILogDiscoveryService
{
    private readonly AgentConfiguration _config;
    private readonly ILogger<LogDiscoveryService> _logger;

    // Chemins de logs connus selon le fabricant ATM
    private static readonly Dictionary<string, string[]> KnownAtmLogPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NCR"] = [
            @"C:\Program Files\NCR\APTRA\Logs",
            @"C:\NCR\Logs",
            @"C:\ATM\Logs",
            @"/opt/ncr/logs",
            @"/var/log/ncr"
        ],
        ["DIEBOLD"] = [
            @"C:\Diebold\Logs",
            @"C:\Program Files\Diebold Nixdorf\Logs",
            @"C:\OPTEEVA\Logs",
            @"/opt/diebold/logs"
        ],
        ["WINCOR"] = [
            @"C:\Wincor\Logs",
            @"C:\Program Files\Wincor Nixdorf\Logs",
            @"C:\Program Files\Diebold Nixdorf\ProTopas\Log",
            @"/var/log/wincor"
        ],
        ["HYOSUNG"] = [
            @"C:\Hyosung\Log",
            @"C:\Program Files\Nautilus Hyosung\Logs",
            @"/opt/hyosung/logs"
        ],
        ["GRG"] = [
            @"C:\GRG\Log",
            @"/opt/grg/logs"
        ],
        ["GENERIC"] = [
            @"C:\ATM\Logs",
            @"C:\Logs",
            @"D:\ATM\Logs",
            @"/var/log/atm",
            @"/opt/atm/logs",
            @"/home/atm/logs"
        ]
    };

    public LogDiscoveryService(
        IOptions<AgentConfiguration> config,
        ILogger<LogDiscoveryService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> DiscoverLogPathsAsync(CancellationToken ct = default)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Chemins configurés explicitement
        foreach (var path in _config.LogDiscovery.WatchPaths)
        {
            if (Directory.Exists(path))
            {
                discovered.Add(path);
                _logger.LogInformation("Log path configured: {Path}", path);
            }
        }

        // 2. Découverte automatique si activée
        if (_config.LogDiscovery.AutoDiscoverAtmPaths)
        {
            var autoDiscovered = await AutoDiscoverAsync(ct);
            foreach (var path in autoDiscovered)
                discovered.Add(path);
        }

        // 3. Filtrage des chemins exclus
        var excluded = _config.LogDiscovery.ExcludedPaths
            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = discovered
            .Where(p => !excluded.Any(e => p.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        _logger.LogInformation("Log discovery completed: {Count} path(s) found", result.Count);
        return result;
    }

    public LogFormat DetectFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".xml" => LogFormat.Xml,
            ".json" => LogFormat.Json,
            ".csv" => LogFormat.Csv,
            ".jrn" => LogFormat.Proprietary,   // Format journal ATM propriétaire
            ".log" or ".txt" => DetectTextFormat(filePath),
            _ => LogFormat.Unknown
        };
    }

    /// <summary>
    /// Construit le chemin distant normalisé et déterministe :
    /// bank/country/city/atm_id/YYYY/MM/DD/filename.log
    ///
    /// FIX P2.4 : L'ancienne implémentation utilisait le timestamp de TRAITEMENT
    /// (DateTime.UtcNow ou logDate dynamique) comme segment HHMMSS du chemin.
    /// Problème : deux transmissions du même fichier (ex: sync 24h + retransmission
    /// après failure) généraient des chemins différents, créant des DOUBLONS côté
    /// serveur bancaire.
    ///
    /// Solution : on extrait la date depuis le NOM du fichier (YYYYMMDD.jrn).
    /// Ce chemin est stable et idempotent : re-transmettre le même fichier écrit
    /// TOUJOURS au même emplacement distant. Un fichier .jrn correspond à une
    /// journée entière — il n'y a pas d'ambiguïté temporelle dans le chemin.
    ///
    /// Fallback : si le nom de fichier ne contient pas de date (format non-standard),
    /// on utilise logDate, puis DateTime.UtcNow en dernier recours.
    /// </summary>
    public string BuildRemotePath(string localFilePath, DateTime? logDate = null)
    {
        var basePath = _config.Atm.GetBasePath();
        var fileName = Path.GetFileName(localFilePath);
        var sanitizedFileName = SanitizeFileName(fileName);

        // Priorité 1 : date encodée dans le nom de fichier (YYYYMMDD.jrn)
        // Stable et idempotent : deux uploads du même fichier → même chemin distant.
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        if (nameWithoutExt.Length == 8
            && DateTime.TryParseExact(nameWithoutExt, "yyyyMMdd",
                null, System.Globalization.DateTimeStyles.None, out var fileDate))
        {
            return string.Join("/",
                basePath,
                fileDate.ToString("yyyy"),
                fileDate.ToString("MM"),
                fileDate.ToString("dd"),
                sanitizedFileName);
        }

        // Priorité 2 : date passée en paramètre (ex: LastWriteTime du fichier)
        var date = logDate ?? DateTime.UtcNow;
        return string.Join("/",
            basePath,
            date.ToString("yyyy"),
            date.ToString("MM"),
            date.ToString("dd"),
            sanitizedFileName);
    }

    // ──────────────────────────────────────────────
    // Méthodes privées
    // ──────────────────────────────────────────────

    private async Task<IReadOnlyList<string>> AutoDiscoverAsync(CancellationToken ct)
    {
        var found = new List<string>();
        var manufacturer = _config.Atm.Manufacturer ?? "GENERIC";

        // Chemins propres au fabricant déclaré
        if (KnownAtmLogPaths.TryGetValue(manufacturer, out var specific))
        {
            found.AddRange(specific.Where(Directory.Exists));
        }

        // Toujours inclure les chemins génériques
        if (KnownAtmLogPaths.TryGetValue("GENERIC", out var generic))
        {
            found.AddRange(generic.Where(Directory.Exists));
        }

        // Sur Linux : scanner /var/log pour répertoires contenant "atm" ou "jrn"
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var linuxPaths = await ScanLinuxLogPathsAsync(ct);
            found.AddRange(linuxPaths);
        }

        if (found.Count > 0)
            _logger.LogInformation("Auto-discovered {Count} log directory(s) for manufacturer={Manufacturer}",
                found.Count, manufacturer);
        else
            _logger.LogWarning("No log directories auto-discovered for manufacturer={Manufacturer}", manufacturer);

        return found;
    }

    private static async Task<IReadOnlyList<string>> ScanLinuxLogPathsAsync(CancellationToken ct)
    {
        var result = new List<string>();
        const string baseDir = "/var/log";

        if (!Directory.Exists(baseDir)) return result;

        await Task.Run(() =>
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(baseDir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(dir).ToLowerInvariant();
                    if (name.Contains("atm") || name.Contains("jrn") || name.Contains("ncr") ||
                        name.Contains("diebold") || name.Contains("wincor"))
                    {
                        result.Add(dir);
                    }
                }
            }
            catch (UnauthorizedAccessException) { /* Répertoires protégés ignorés */ }
        }, ct);

        return result;
    }

    private static LogFormat DetectTextFormat(string filePath)
    {
        try
        {
            using var reader = new StreamReader(filePath);
            var firstLine = reader.ReadLine();
            if (firstLine is null) return LogFormat.Unknown;

            if (firstLine.TrimStart().StartsWith('<')) return LogFormat.Xml;
            if (firstLine.TrimStart().StartsWith('{') || firstLine.TrimStart().StartsWith('['))
                return LogFormat.Json;

            // Heuristique format journal ATM : ligne commençant par HH:MM:SS ou *NNN*
            if (System.Text.RegularExpressions.Regex.IsMatch(firstLine, @"^\d{2}:\d{2}:\d{2}") ||
                System.Text.RegularExpressions.Regex.IsMatch(firstLine, @"^\*\d+\*"))
                return LogFormat.Proprietary;

            return LogFormat.PlainText;
        }
        catch
        {
            return LogFormat.Unknown;
        }
    }

    private static string SanitizeFileName(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"[^\w\-. ]", "_");
}
