using System.Net.Http.Json;
using System.Security.Cryptography;
using AtmLogAgent.Core.Interfaces;
using AtmLogAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AtmLogAgent.Core.Services;

/// <summary>
/// Service de mise à jour automatique avec vérification cryptographique.
/// Vérifie la signature RSA de chaque package avant installation.
/// Conserve les N versions précédentes pour rollback automatique.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private readonly AgentConfiguration _config;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<UpdateService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _agentDirectory;
    private readonly string _backupDirectory;

    public string CurrentVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0";

    public UpdateService(
        IOptions<AgentConfiguration> config,
        IEncryptionService encryption,
        ILogger<UpdateService> logger)
    {
        _config = config.Value;
        _encryption = encryption;
        _logger = logger;
        _agentDirectory = AppContext.BaseDirectory;
        _backupDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AtmLogAgent", "Backups");
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.Update.UpdateServerUrl),
            Timeout = TimeSpan.FromMinutes(10)
        };
        _httpClient.DefaultRequestHeaders.Add("X-Agent-Id", _config.Atm.AtmId);
        _httpClient.DefaultRequestHeaders.Add("X-Current-Version", CurrentVersion);
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/check?current={CurrentVersion}", ct);
            if (!response.IsSuccessStatusCode) return null;

            var update = await response.Content.ReadFromJsonAsync<UpdateInfo>(ct);
            if (update is null) return null;

            if (update.Version == CurrentVersion)
            {
                _logger.LogDebug("Agent is up-to-date (v{Version})", CurrentVersion);
                return null;
            }

            _logger.LogInformation("Update available: v{Current} → v{New} (critical={Critical})",
                CurrentVersion, update.Version, update.IsCritical);
            return update;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            return null;
        }
    }

    public async Task<bool> ApplyUpdateAsync(UpdateInfo update, CancellationToken ct = default)
    {
        _logger.LogInformation("Applying update v{Version}", update.Version);

        var tempPath = Path.GetTempFileName();
        try
        {
            // 1. Télécharger le package
            _logger.LogDebug("Downloading update from {Url}", update.DownloadUrl);
            using var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            using (var fs = File.Create(tempPath))
                await response.Content.CopyToAsync(fs, ct);

            // 2. Vérifier l'intégrité (SHA-256)
            var actualChecksum = await _encryption.ComputeFileChecksumAsync(tempPath, ct);
            if (!string.Equals(actualChecksum, update.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Update checksum mismatch! Expected={Expected}, Got={Actual}",
                    update.Checksum, actualChecksum);
                return false;
            }

            // 3. Vérifier la signature RSA (authenticité de l'éditeur)
            if (!string.IsNullOrEmpty(_config.Update.UpdatePublicKeyPath))
            {
                if (!await VerifySignatureAsync(tempPath, update.Signature, ct))
                {
                    _logger.LogCritical("SECURITY: Update signature verification FAILED for v{Version}", update.Version);
                    return false;
                }
            }

            // 4. Sauvegarder la version actuelle
            await BackupCurrentVersionAsync(ct);

            // 5. Installer (extraction du zip vers le répertoire de l'agent)
            _logger.LogInformation("Installing update v{Version}", update.Version);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempPath, _agentDirectory, overwriteFiles: true);

            _logger.LogInformation("Update v{Version} installed successfully", update.Version);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update installation failed — initiating rollback");
            await RollbackAsync(ct);
            return false;
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public async Task<bool> RollbackAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var backups = Directory.EnumerateDirectories(_backupDirectory)
            .OrderByDescending(d => d)
            .Take(1)
            .ToList();

        if (backups.Count == 0)
        {
            _logger.LogError("No backup found for rollback");
            return false;
        }

        var latestBackup = backups[0];
        _logger.LogWarning("Rolling back to backup: {Backup}", latestBackup);

        try
        {
            foreach (var file in Directory.EnumerateFiles(latestBackup, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(latestBackup, file);
                var destPath = Path.Combine(_agentDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                File.Copy(file, destPath, overwrite: true);
            }

            _logger.LogInformation("Rollback completed from {Backup}", latestBackup);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Rollback FAILED — manual intervention required");
            return false;
        }
    }

    private async Task BackupCurrentVersionAsync(CancellationToken ct)
    {
        var backupPath = Path.Combine(_backupDirectory,
            $"{CurrentVersion}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");

        Directory.CreateDirectory(backupPath);

        foreach (var file in Directory.EnumerateFiles(_agentDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(_agentDirectory, file);
            var destPath = Path.Combine(backupPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, overwrite: true);
        }

        // Garder seulement les N dernières sauvegardes
        var allBackups = Directory.EnumerateDirectories(_backupDirectory)
            .OrderByDescending(d => d)
            .Skip(_config.Update.MaxRollbackVersions)
            .ToList();

        foreach (var old in allBackups)
        {
            Directory.Delete(old, recursive: true);
            _logger.LogDebug("Removed old backup: {Path}", old);
        }

        _logger.LogInformation("Current version backed up to {Path}", backupPath);
        await Task.CompletedTask;
    }

    private async Task<bool> VerifySignatureAsync(string filePath, string base64Signature, CancellationToken ct)
    {
        try
        {
            var publicKeyPem = await File.ReadAllTextAsync(_config.Update.UpdatePublicKeyPath!, ct);
            var signature = Convert.FromBase64String(base64Signature);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);

            using var fs = File.OpenRead(filePath);
            return rsa.VerifyData(fs, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signature verification error");
            return false;
        }
    }
}
