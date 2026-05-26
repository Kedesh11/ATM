using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AtmLogAgent.Core.Interfaces;
using AtmLogAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AtmLogAgent.Core.Services;

/// <summary>
/// Résout de façon autonome l'identité complète de l'ATM (BankName, Country,
/// City, AtmId) sans aucune saisie humaine, en combinant :
///   1. Numéro de série hardware (DMI/BIOS) → AtmId
///   2. Géolocalisation IP (ip-api.com) → Country + City
///   3. Fichier de provisionnement bancaire → BankName
///
/// La seule intervention humaine requise est la configuration SFTP
/// (Host, Port, Username, PrivateKeyPath) et optionnellement le fichier
/// de provisionnement pour le nom de banque.
/// </summary>
public sealed class AtmIdentityResolverService : IAtmIdentityResolver
{
    private readonly AgentConfiguration _config;
    private readonly ILogger<AtmIdentityResolverService> _logger;

    // Chemins des fichiers de provisionnement bancaire (déposés par l'IT de la banque)
    private static readonly string[] ProvisioningFilePaths =
    [
        // Linux : fichier système protégé
        "/etc/atm-agent/provisioning.conf",
        // Linux : alternative dans le répertoire de données
        "/var/lib/atm-agent/provisioning.conf",
        // Windows : répertoire commun de l'application
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AtmLogAgent", "provisioning.conf"),
        // Windows : répertoire du programme
        Path.Combine(AppContext.BaseDirectory, "provisioning.conf")
    ];

    /// <summary>
    /// Valeurs lues depuis provisioning.conf — toutes les clés disponibles.
    /// Source de vérité primaire pour BankName, Country, City.
    /// </summary>
    private sealed record ProvisioningData(
        string? BankName,
        string? Country,
        string? City,
        string? BankCode,
        string? Region);

    // Cache en mémoire : la résolution ne s'effectue qu'une fois
    private AtmIdentityResolution? _cachedResolution;

    public AtmIdentityResolverService(
        IOptions<AgentConfiguration> config,
        ILogger<AtmIdentityResolverService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    public async Task<AtmIdentityResolution> ResolveAsync(CancellationToken ct = default)
    {
        // Retourner le cache si déjà résolu
        if (_cachedResolution is not null) return _cachedResolution;

        _logger.LogInformation("Resolving ATM identity autonomously...");

        // ── 1. Résolution de l'AtmId ────────────────────────────────────────
        var (atmId, atmIdSource) = ResolveAtmId();

        // ── 2. Lecture du fichier de provisionnement (source primaire) ──────
        // Le fichier provisioning.conf est déposé une seule fois par le technicien
        // bancaire et contient les valeurs fiables pour BankName, Country, City.
        // Il est TOUJOURS prioritaire sur toute autre source (géolocalisation,
        // config, hostname SFTP) car c'est la seule source opérationnellement certaine.
        var provisioning = ReadProvisioningFile();

        // ── 3. Résolution de la localisation (Country + City) ───────────────
        var (country, city, locationSource) = await ResolveLocationAsync(provisioning, ct);

        // ── 4. Résolution du nom de banque ──────────────────────────────────
        var (bankName, bankSource) = ResolveBankName(provisioning);

        // ── 5. Manufacturer / Model depuis la config si renseignés ─────────
        var manufacturer = _config.Atm.Manufacturer;
        var model        = _config.Atm.Model;

        _cachedResolution = new AtmIdentityResolution
        {
            AtmId          = atmId,
            BankName       = bankName,
            Country        = country,
            City           = city,
            Manufacturer   = manufacturer,
            Model          = model,
            AtmIdSource    = atmIdSource,
            LocationSource = locationSource,
            BankSource     = bankSource
        };

        _logger.LogInformation(
            "ATM identity resolved — AtmId={AtmId} ({AtmIdSrc}) | " +
            "Bank={Bank} ({BankSrc}) | {Country}/{City} ({LocSrc})",
            atmId, atmIdSource, bankName, bankSource, country, city, locationSource);

        if (!_cachedResolution.IsFullyResolved)
        {
            _logger.LogWarning(
                "ATM identity not fully resolved. Verify provisioning.conf " +
                "or network connectivity for geolocation.");
        }

        return _cachedResolution;
    }

    // ──────────────────────────────────────────────────────────────
    // AtmId : numéro de série hardware → MAC → hostname
    // ──────────────────────────────────────────────────────────────

    private (string atmId, string source) ResolveAtmId()
    {
        // Priorité 1 : numéro de série DMI/BIOS (identifiant machine physique unique)
        var serial = ReadHardwareSerial();
        if (!string.IsNullOrWhiteSpace(serial))
        {
            _logger.LogDebug("AtmId from hardware serial: {Serial}", serial);
            return (NormalizeAtmId(serial), "hardware_serial");
        }

        // Priorité 2 : adresse MAC de l'interface réseau principale (stable)
        var mac = ReadPrimaryMacAddress();
        if (!string.IsNullOrWhiteSpace(mac))
        {
            _logger.LogDebug("AtmId from MAC address: {Mac}", mac);
            return (NormalizeAtmId("ATM-" + mac), "mac_address");
        }

        // Fallback : nom d'hôte machine
        var hostname = Dns.GetHostName();
        _logger.LogDebug("AtmId from hostname: {Hostname}", hostname);
        return (NormalizeAtmId(hostname), "hostname");
    }

    /// <summary>
    /// Lit le numéro de série du BIOS/chassis via les interfaces système.
    /// Linux : /sys/class/dmi/id/product_serial (ou board_serial)
    /// Windows : clé de registre HKLM\HARDWARE\DESCRIPTION\System\BIOS
    /// </summary>
    private string? ReadHardwareSerial()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Les ATM Linux exposent le serial via sysfs (kernel 2.6.x+)
                foreach (var sysfsPath in new[]
                {
                    "/sys/class/dmi/id/product_serial",
                    "/sys/class/dmi/id/board_serial",
                    "/sys/class/dmi/id/chassis_serial"
                })
                {
                    if (!File.Exists(sysfsPath)) continue;
                    var val = File.ReadAllText(sysfsPath).Trim();
                    if (!string.IsNullOrWhiteSpace(val)
                        && val != "None"
                        && val != "Not Specified"
                        && val != "Unknown"
                        && val.Length > 3)
                        return val;
                }
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Windows : registre BIOS
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                var serial = key?.GetValue("SystemSerialNumber") as string;
                if (!string.IsNullOrWhiteSpace(serial)
                    && serial != "None"
                    && serial != "To Be Filled By O.E.M.")
                    return serial;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hardware serial not accessible — falling back to MAC address");
        }

        return null;
    }

    /// <summary>
    /// Retourne l'adresse MAC de l'interface réseau principale (non-loopback,
    /// connectée, préférence Ethernet puis Wi-Fi).
    /// Format : sans séparateurs, en majuscules. Ex : "001A2B3C4D5E"
    /// </summary>
    private string? ReadPrimaryMacAddress()
    {
        try
        {
            var iface = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                         && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel
                         && n.GetPhysicalAddress().ToString().Length == 12)
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                .FirstOrDefault();

            return iface?.GetPhysicalAddress().ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAC address not readable");
            return null;
        }
    }

    /// <summary>
    /// Normalise un identifiant brut en format ATM_XXXXXX :
    /// - Majuscules
    /// - Caractères non alphanumériques remplacés par des tirets
    /// - Préfixé par ATM_ si non déjà préfixé
    /// </summary>
    private static string NormalizeAtmId(string raw)
    {
        var cleaned = Regex.Replace(raw.ToUpperInvariant().Trim(), @"[^A-Z0-9]", "-")
                           .Trim('-');
        // Éviter les identifiants trop longs (max 20 chars)
        if (cleaned.Length > 20) cleaned = cleaned[..20].TrimEnd('-');
        return cleaned.StartsWith("ATM") ? cleaned : $"ATM-{cleaned}";
    }

    // ──────────────────────────────────────────────────────────────
    // Localisation : géolocalisation IP → fuseau horaire → config
    // ──────────────────────────────────────────────────────────────

    private async Task<(string country, string city, string source)> ResolveLocationAsync(
        ProvisioningData? provisioning, CancellationToken ct)
    {
        // Priorité 1 : valeurs explicites dans la config (override absolu)
        if (!string.IsNullOrWhiteSpace(_config.Atm.Country)
            && _config.Atm.Country != "AUTO"
            && !string.IsNullOrWhiteSpace(_config.Atm.City)
            && _config.Atm.City != "AUTO")
        {
            return (_config.Atm.Country, _config.Atm.City, "config");
        }

        // Priorité 2 : fichier de provisionnement — SOURCE PRIMAIRE FIABLE
        // Un ATM derrière le proxy/NAT bancaire géolocalisera dans le pays du datacenter
        // (ex: Paris) et non dans la ville physique de l'ATM (ex: Libreville).
        // Le provisioning.conf est la SEULE source 100% fiable pour la localisation.
        if (provisioning is not null
            && !string.IsNullOrWhiteSpace(provisioning.Country)
            && !string.IsNullOrWhiteSpace(provisioning.City))
        {
            _logger.LogInformation(
                "Location from provisioning file: {Country}/{City}",
                provisioning.Country, provisioning.City);
            return (provisioning.Country.ToUpperInvariant(),
                    provisioning.City.ToUpperInvariant(),
                    "provisioning_file");
        }

        // Priorité 3 : géolocalisation IP via ip-api.com (fallback seulement)
        // ATTENTION : utilise HTTPS pour éviter les attaques MITM.
        // Fiable uniquement si l'ATM a une IP publique non-NATée côté banque.
        try
        {
            var geo = await QueryIpGeolocationAsync(ct);
            if (geo is not null)
                return (geo.Value.country, geo.Value.city, "ip_geolocation");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "IP geolocation failed — falling back to timezone-based location");
        }

        // Priorité 4 : fuseau horaire système → pays approximatif
        var (tzCountry, tzCity) = GetLocationFromTimezone();
        if (!string.IsNullOrWhiteSpace(tzCountry))
        {
            _logger.LogDebug("Location from system timezone: {Country}/{City}", tzCountry, tzCity);
            return (tzCountry, tzCity, "timezone");
        }

        _logger.LogWarning("Location could not be determined — add Country and City to provisioning.conf");
        return ("UNKNOWN", "UNKNOWN", "unresolved");
    }

    /// <summary>
    /// Interroge l'API ip-api.com pour obtenir pays et ville à partir de l'IP publique.
    /// Utilise HTTPS pour éviter les attaques MITM sur les réseaux bancaires.
    /// Note : ip-api.com requiert un abonnement pour HTTPS ; utiliser ipapi.co en alternatif.
    /// Réponse typique : { "country":"Gabon", "regionName":"Estuaire", "city":"Libreville" }
    /// </summary>
    private async Task<(string country, string city)?> QueryIpGeolocationAsync(CancellationToken ct)
    {
        // Timeout court : l'ATM ne doit pas bloquer au démarrage
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // HTTPS au lieu de HTTP : évite les attaques MITM sur réseaux bancaires
        // ip-api.com pro (HTTPS) ou ipapi.co comme alternative gratuite HTTPS
        var url = "https://ipapi.co/json/";
        var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // ipapi.co retourne un champ "error" si l'IP est privée/non routable
        if (root.TryGetProperty("error", out var errorProp) && errorProp.GetBoolean())
        {
            _logger.LogWarning("ipapi.co returned error: {Reason}",
                root.TryGetProperty("reason", out var r) ? r.GetString() : "unknown");
            return null;
        }

        var country = root.TryGetProperty("country_name", out var cn) ? cn.GetString() ?? "" : "";
        var city    = root.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
        var ip      = root.TryGetProperty("ip", out var ipProp) ? ipProp.GetString() : "?";

        if (string.IsNullOrWhiteSpace(country) || string.IsNullOrWhiteSpace(city))
        {
            _logger.LogWarning("ipapi.co returned incomplete location data");
            return null;
        }

        _logger.LogInformation(
            "Geolocation resolved: IP={Ip} Country={Country} City={City}",
            ip, country, city);

        return (country.ToUpperInvariant(), city.ToUpperInvariant());
    }

    /// <summary>
    /// Dérive la localisation depuis le fuseau horaire système.
    /// Fiable pour les pays à fuseau unique (Gabon = Africa/Libreville, etc.).
    /// </summary>
    private static (string country, string city) GetLocationFromTimezone()
    {
        // Mapping fuseau → (pays, ville principale) pour les pays d'Afrique Centrale
        // où les ATM BGFI sont déployés, extensible à d'autres régions
        var tzMap = new Dictionary<string, (string country, string city)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Africa/Libreville"]   = ("GABON",   "LIBREVILLE"),
            ["Africa/Douala"]       = ("CAMEROUN", "DOUALA"),
            ["Africa/Bangui"]       = ("RCA",      "BANGUI"),
            ["Africa/Brazzaville"]  = ("CONGO",    "BRAZZAVILLE"),
            ["Africa/Kinshasa"]     = ("RDC",      "KINSHASA"),
            ["Africa/Malabo"]       = ("GUINEE-EQUATORIALE", "MALABO"),
            ["Africa/Lagos"]        = ("NIGERIA",  "LAGOS"),
            ["Africa/Abidjan"]      = ("COTE-DIVOIRE", "ABIDJAN"),
            ["Africa/Dakar"]        = ("SENEGAL",  "DAKAR"),
            ["Africa/Nairobi"]      = ("KENYA",    "NAIROBI"),
            ["Africa/Johannesburg"] = ("AFRIQUE-DU-SUD", "JOHANNESBURG"),
            ["Europe/Paris"]        = ("FRANCE",   "PARIS"),
            ["America/New_York"]    = ("USA",      "NEW-YORK"),
        };

        try
        {
            var tz = TimeZoneInfo.Local.Id;
            if (tzMap.TryGetValue(tz, out var loc)) return loc;

            // IANA → Windows mapping partiel
            foreach (var (key, val) in tzMap)
            {
                if (tz.Contains(key.Split('/').Last(), StringComparison.OrdinalIgnoreCase))
                    return val;
            }
        }
        catch { /* timezone non lisible */ }

        return (string.Empty, string.Empty);
    }

    // ──────────────────────────────────────────────────────────────
    // BankName : fichier de provisionnement → hostname SFTP → config
    // ──────────────────────────────────────────────────────────────

    private (string bankName, string source) ResolveBankName(ProvisioningData? provisioning)
    {
        // Priorité 1 : valeur explicite dans la config (non-AUTO)
        if (!string.IsNullOrWhiteSpace(_config.Atm.BankName)
            && _config.Atm.BankName != "AUTO")
        {
            return (_config.Atm.BankName.ToUpperInvariant(), "config");
        }

        // Priorité 2 : fichier de provisionnement bancaire (source primaire fiable)
        if (provisioning is not null && !string.IsNullOrWhiteSpace(provisioning.BankName))
        {
            _logger.LogInformation("BankName from provisioning file: {Bank}", provisioning.BankName);
            return (provisioning.BankName, "provisioning_file");
        }

        // Priorité 3 : dériver du hostname SFTP configuré
        var bankFromSftp = DeriveBankFromSftpHost(_config.Transmission.Host);
        if (!string.IsNullOrWhiteSpace(bankFromSftp))
        {
            _logger.LogInformation("BankName derived from SFTP host: {Host} → {Bank}",
                _config.Transmission.Host, bankFromSftp);
            return (bankFromSftp, "sftp_hostname");
        }

        _logger.LogWarning(
            "BankName could not be resolved. Create provisioning.conf with 'BankName=YOUR_BANK'");

        return ("UNKNOWN_BANK", "unresolved");
    }

    /// <summary>
    /// Lit le fichier de provisionnement bancaire (format INI minimal).
    /// Ce fichier est le SEUL artefact déposé manuellement par le technicien
    /// lors de l'installation initiale de l'ATM.
    ///
    /// Clés supportées (toutes optionnelles sauf BankName) :
    ///   BankName=BGFI            ← Nom de la banque (obligatoire)
    ///   BankCode=BGFI-GBN        ← Code interne banque (optionnel)
    ///   Country=GABON            ← Pays physique de l'ATM (recommandé)
    ///   City=LIBREVILLE          ← Ville physique de l'ATM (recommandé)
    ///   Region=ESTUAIRE          ← Région (optionnel, informatif)
    ///
    /// Country et City dans ce fichier sont PRIORITAIRES sur la géolocalisation IP
    /// car un ATM derrière le NAT/proxy bancaire géolocalise dans le pays du datacenter.
    /// </summary>
    private ProvisioningData? ReadProvisioningFile()
    {
        foreach (var path in ProvisioningFilePaths)
        {
            if (!File.Exists(path)) continue;

            try
            {
                var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var lines = File.ReadAllLines(path, Encoding.UTF8);

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith('#') || trimmed.StartsWith(';')) continue;

                    var sep = trimmed.IndexOf('=');
                    if (sep < 0) continue;

                    var key = trimmed[..sep].Trim();
                    var val = trimmed[(sep + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
                        data[key] = val;
                }

                if (data.Count == 0) continue;

                _logger.LogDebug("Provisioning file found: {Path} ({Count} keys)", path, data.Count);

                // Lecture souple des variantes de noms de clés
                var bankName = data.GetValueOrDefault("BankName")
                    ?? data.GetValueOrDefault("Bank_Name")
                    ?? data.GetValueOrDefault("Bank");

                var country = data.GetValueOrDefault("Country")
                    ?? data.GetValueOrDefault("Pays");

                var city = data.GetValueOrDefault("City")
                    ?? data.GetValueOrDefault("Ville");

                return new ProvisioningData(
                    BankName: bankName?.ToUpperInvariant(),
                    Country:  country?.ToUpperInvariant(),
                    City:     city?.ToUpperInvariant(),
                    BankCode: data.GetValueOrDefault("BankCode") ?? data.GetValueOrDefault("Bank_Code"),
                    Region:   data.GetValueOrDefault("Region"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read provisioning file: {Path}", path);
            }
        }

        return null;
    }

    /// <summary>
    /// Extrait le nom de banque depuis le hostname SFTP.
    /// Heuristique : premier segment du hostname avant le premier point,
    /// filtré des termes génériques (sftp, atm, ftp, server, host, api).
    /// Ex: "sftp.bgfi-bank.com" → "BGFI" | "atm-agent.ecobank.net" → "ECOBANK"
    /// </summary>
    private static string? DeriveBankFromSftpHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        var genericTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "sftp", "ftp", "atm", "server", "host", "api", "prod", "dev", "test", "backup" };

        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            // Ignorer les termes génériques et les parties trop courtes
            if (part.Length < 3 || genericTerms.Contains(part)) continue;

            // Nettoyer les tirets internes : "bgfi-bank" → "BGFI"
            var cleaned = part.Split('-').First().ToUpperInvariant();
            if (cleaned.Length >= 2 && Regex.IsMatch(cleaned, @"^[A-Z]+$"))
                return cleaned;
        }

        return null;
    }
}
