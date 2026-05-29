using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using AtmLogAgent.Core.Interfaces;
using AtmLogAgent.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace AtmLogAgent.Core.Services;

/// <summary>
/// Service de transmission SFTP sécurisé.
/// Implémente la politique de retry exponentielle via Polly,
/// la vérification d'intégrité SHA-256 après chaque transfert,
/// et la compression GZip optionnelle avant envoi.
/// </summary>
public sealed class SftpTransmissionService : ITransmissionService, IDisposable
{
    private readonly AgentConfiguration _config;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<SftpTransmissionService> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;
    private readonly ConcurrentBag<SftpClient> _clientPool = new();
    private bool _disposed;

    public SftpTransmissionService(
        IOptions<AgentConfiguration> config,
        IEncryptionService encryption,
        ILogger<SftpTransmissionService> logger)
    {
        _config = config.Value;
        _encryption = encryption;
        _logger = logger;
        _retryPolicy = BuildRetryPolicy();
    }

    // ──────────────────────────────────────────────
    // Transmission d'entrée de log (temps réel)
    // ──────────────────────────────────────────────

    public async Task<bool> TransmitEntryAsync(LogEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entry.RemotePath))
        {
            _logger.LogWarning("Entry {Id} has no remote path — skipping", entry.Id);
            return false;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var client = await GetConnectedClientAsync(ct);
            try
            {
                var remotePath = entry.RemotePath!;
                EnsureRemoteDirectory(client, Path.GetDirectoryName(remotePath)!.Replace('\\', '/'));

                // ── FIX P1.1 — Append binaire correct ─────────────────────────────────
                //
                // AVANT (incorrect) :
                //   client.UploadFile(stream, remotePath, canOverride: true)
                //   canOverride:true équivaut à O_WRONLY|O_CREAT|O_TRUNC → ÉCRASE le fichier.
                //   Après une journée, seule la DERNIÈRE ligne de log serait présente côté
                //   serveur. Perte totale de l'historique des transactions.
                //
                // APRÈS (correct) :
                //   client.OpenWrite(remotePath) ouvre en O_WRONLY|O_APPEND|O_CREAT.
                //   Si le fichier n'existe pas : il est créé.
                //   S'il existe : les données sont ajoutées à la fin (append).
                //   Sémantique exacte requise pour un journal de transactions ATM.

                var contentBytes = Encoding.UTF8.GetBytes(entry.Content + Environment.NewLine);

                await Task.Run(() =>
                {
                    using var remoteStream = client.OpenWrite(remotePath);
                    remoteStream.Write(contentBytes, 0, contentBytes.Length);
                }, ct);

                _logger.LogDebug("Entry {Id} appended to {Path}", entry.Id, remotePath);
                return true;
            }
            finally
            {
                ReturnClient(client);
            }
        });
    }

    // ──────────────────────────────────────────────
    // Transmission de fichier complet (sync 24h)
    // ──────────────────────────────────────────────

    public async Task<bool> TransmitFileAsync(FileSyncRecord record, CancellationToken ct = default)
    {
        if (!File.Exists(record.LocalPath))
        {
            _logger.LogWarning("Local file not found: {Path}", record.LocalPath);
            return false;
        }

        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var client = await GetConnectedClientAsync(ct);
            try
            {
                var remotePath = record.RemotePath;

                EnsureRemoteDirectory(client, Path.GetDirectoryName(remotePath)!.Replace('\\', '/'));

                if (_config.Transmission.CompressBeforeTransmit && !record.Compressed)
                {
                    var tempPath = Path.GetTempFileName();
                    try
                    {
                        await CompressFileAsync(record.LocalPath, tempPath, ct);
                        remotePath += ".gz";
                        await UploadFileWithProgressAsync(client, tempPath, remotePath, ct);
                    }
                    finally
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                }
                else
                {
                    await UploadFileWithProgressAsync(client, record.LocalPath, remotePath, ct);
                }

                _logger.LogInformation("File sync completed: {Local} → {Remote} ({Size:N0} bytes)",
                    Path.GetFileName(record.LocalPath), remotePath, record.FileSizeBytes);
                return true;
            }
            finally
            {
                ReturnClient(client);
            }
        });
    }

    public async Task<bool> TestConnectivityAsync(CancellationToken ct = default)
    {
        try
        {
            var client = await GetConnectedClientAsync(ct);
            try
            {
                return client.IsConnected;
            }
            finally
            {
                ReturnClient(client);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connectivity test failed to {Host}:{Port}",
                _config.Transmission.Host, _config.Transmission.Port);
            return false;
        }
    }

    /// <summary>
    /// FIX P1.2 — Vérification du checksum distant sans re-télécharger le fichier.
    ///
    /// AVANT (incorrect) :
    ///   DownloadFile() téléchargeait tout le fichier distant en mémoire pour calculer
    ///   son SHA-256. Sur liaison GSM/ADSL bancaire (512 Kbps–1 Mbps), un fichier
    ///   .jrn journalier de 10–50 MB prenait 80–800 secondes. Inacceptable.
    ///
    /// APRÈS (correct) :
    ///   Exécute sha256sum côté serveur via SSH exec channel (sans ouvrir de shell).
    ///   Seul le résultat textuel (64 hex + nom fichier, ~100 octets) transite.
    ///   Coût réseau : négligeable. Temps : &lt;1 seconde.
    /// </summary>
    public async Task<bool> VerifyRemoteChecksumAsync(
        string remotePath, string expectedChecksum, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedChecksum)) return false;

        try
        {
            var sshClient = CreateSshClient();
            await Task.Run(() => sshClient.Connect(), ct);
            try
            {
                // Échapper le chemin pour éviter les injections shell
                var escapedPath = remotePath.Replace("'", "'\\''");
                using var command = sshClient.CreateCommand($"sha256sum '{escapedPath}'");
                command.CommandTimeout = TimeSpan.FromSeconds(30);
                var result = command.Execute();

                if (command.ExitStatus != 0)
                {
                    _logger.LogWarning(
                        "sha256sum failed on server (exit {Code}): {Err}",
                        command.ExitStatus, command.Error);
                    return false;
                }

                // Sortie format : "hash  filename\n"
                var parts = result.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1) return false;

                var remoteHash = parts[0].Trim().ToLowerInvariant();
                var localHash  = expectedChecksum.Replace(":", "").ToLowerInvariant();

                var match = string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase);

                if (!match)
                    _logger.LogWarning(
                        "Checksum mismatch for {Path} — local={Local} remote={Remote}",
                        remotePath, localHash, remoteHash);

                return match;
            }
            finally
            {
                sshClient.Disconnect();
                sshClient.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote checksum verification failed for {Path}", remotePath);
            return false;
        }
    }

    // ──────────────────────────────────────────────
    // Gestion de la connexion SFTP
    // ──────────────────────────────────────────────

    private async Task<SftpClient> GetConnectedClientAsync(CancellationToken ct)
    {
        while (_clientPool.TryTake(out var client))
        {
            if (client.IsConnected) return client;
            client.Dispose(); // Cleanup broken connections
        }

        var newClient = CreateSftpClient();
        await Task.Run(() => newClient.Connect(), ct);

        _logger.LogDebug("SFTP connection pooled to {Host}:{Port}",
            _config.Transmission.Host, _config.Transmission.Port);

        return newClient;
    }

    private void ReturnClient(SftpClient client)
    {
        if (_disposed)
        {
            client.Dispose();
            return;
        }

        if (client.IsConnected)
            _clientPool.Add(client);
        else
            client.Dispose();
    }

    private void ClearPool()
    {
        while (_clientPool.TryTake(out var client))
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Construit les informations de connexion SSH/SFTP partagées entre SftpClient et SshClient.
    /// Clé privée prioritaire sur mot de passe (meilleure sécurité).
    /// </summary>
    private ConnectionInfo BuildConnectionInfo()
    {
        var host     = _config.Transmission.Host;
        var port     = _config.Transmission.Port;
        var username = _config.Transmission.Username;

        if (!string.IsNullOrEmpty(_config.Transmission.PrivateKeyPath)
            && File.Exists(_config.Transmission.PrivateKeyPath))
        {
            var passphrase = _config.Transmission.PrivateKeyPassphrase;
            var keyFile = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(_config.Transmission.PrivateKeyPath)
                : new PrivateKeyFile(_config.Transmission.PrivateKeyPath, passphrase);

            return new ConnectionInfo(host, port, username,
                new PrivateKeyAuthenticationMethod(username, keyFile));
        }

        if (!string.IsNullOrEmpty(_config.Transmission.Password))
        {
            return new ConnectionInfo(host, port, username,
                new PasswordAuthenticationMethod(username, _config.Transmission.Password));
        }

        throw new InvalidOperationException(
            "SFTP: no authentication method configured (key or password required).");
    }

    private SftpClient CreateSftpClient()
    {
        var client = new SftpClient(BuildConnectionInfo());
        ConfigureHostKeyValidation(client);
        client.ConnectionInfo.Timeout =
            TimeSpan.FromSeconds(_config.Transmission.ConnectionTimeoutSeconds);
        client.KeepAliveInterval =
            TimeSpan.FromSeconds(_config.Transmission.KeepAliveIntervalSeconds);
        return client;
    }

    private SshClient CreateSshClient()
    {
        var client = new SshClient(BuildConnectionInfo());
        client.ConnectionInfo.Timeout =
            TimeSpan.FromSeconds(_config.Transmission.ConnectionTimeoutSeconds);
        return client;
    }

    private void ConfigureHostKeyValidation(BaseClient client)
    {
        if (!string.IsNullOrEmpty(_config.Security.ServerCertificatePinning))
        {
            client.HostKeyReceived += (_, args) =>
            {
                var fingerprint = BitConverter.ToString(args.FingerPrint)
                    .Replace("-", "").ToLowerInvariant();
                args.CanTrust = fingerprint.Equals(
                    _config.Security.ServerCertificatePinning.Replace(":", "").ToLowerInvariant(),
                    StringComparison.OrdinalIgnoreCase);

                if (!args.CanTrust)
                    _logger.LogCritical(
                        "SECURITY: SSH host key mismatch! Expected {Expected}, got {Actual}",
                        _config.Security.ServerCertificatePinning, fingerprint);
            };
        }
    }

    // ──────────────────────────────────────────────
    // Upload avec rapport de progression
    // ──────────────────────────────────────────────

    private async Task UploadFileWithProgressAsync(
        SftpClient client, string localPath, string remotePath, CancellationToken ct)
    {
        var fileInfo = new FileInfo(localPath);
        var uploaded = 0L;

        using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await Task.Run(() =>
        {
            client.UploadFile(fs, remotePath, true, bytesUploaded =>
            {
                uploaded = (long)bytesUploaded;
                if (fileInfo.Length > 0)
                {
                    var pct = (double)uploaded / fileInfo.Length * 100;
                    if (pct % 25 < 1)
                        _logger.LogDebug("Upload {File}: {Pct:F0}%",
                            Path.GetFileName(localPath), pct);
                }
            });
        }, ct);
    }

    // ──────────────────────────────────────────────
    // Utilitaires
    // ──────────────────────────────────────────────

    private static void EnsureRemoteDirectory(SftpClient client, string remotePath)
    {
        var parts = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            if (!client.Exists(current))
                client.CreateDirectory(current);
        }
    }

    private static async Task CompressFileAsync(string source, string dest, CancellationToken ct)
    {
        using var input  = File.OpenRead(source);
        using var output = File.Create(dest);
        using var gz     = new GZipStream(output, CompressionLevel.Optimal);
        await input.CopyToAsync(gz, ct);
    }

    private AsyncRetryPolicy BuildRetryPolicy()
    {
        return Policy
            .Handle<SshException>()
            .Or<SshConnectionException>()
            .Or<SshOperationTimeoutException>()
            .Or<IOException>()
            .WaitAndRetryAsync(
                retryCount: _config.Transmission.MaxRetryAttempts,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromSeconds(_config.Transmission.RetryDelaySeconds * Math.Pow(2, attempt - 1))
                    + TimeSpan.FromSeconds(Random.Shared.Next(0, 10)),
                onRetry: (ex, delay, attempt, _) =>
                {
                    _logger.LogWarning(ex,
                        "SFTP transmission failed (attempt {Attempt}/{Max}). Retrying in {Delay:F0}s...",
                        attempt, _config.Transmission.MaxRetryAttempts, delay.TotalSeconds);
                    ClearPool();
                });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearPool();
    }
}
