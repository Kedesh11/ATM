using System.Security.Cryptography;
using System.Text;
using AtmLogAgent.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace AtmLogAgent.Core.Services;

/// <summary>
/// Service de chiffrement AES-256-GCM pour les données sensibles.
/// Implémente le chiffrement authentifié (AEAD) garantissant confidentialité ET intégrité.
/// La clé est chargée depuis un fichier protégé par DPAPI (Windows) ou des permissions
/// système restrictives (Linux), jamais stockée en mémoire plus longtemps que nécessaire.
/// </summary>
public sealed class EncryptionService : IEncryptionService, IDisposable
{
    private readonly ILogger<EncryptionService> _logger;
    private readonly byte[] _masterKey;  // 256-bit AES key
    private bool _disposed;

    // Taille du nonce GCM (96 bits — recommandé NIST)
    private const int NonceSizeBytes = 12;
    // Taille du tag d'authentification GCM (128 bits — maximum)
    private const int TagSizeBytes = 16;

    public EncryptionService(ILogger<EncryptionService> logger, string? keyFilePath = null)
    {
        _logger = logger;
        _masterKey = LoadOrCreateKey(keyFilePath);
    }

    /// <summary>
    /// Chiffre avec AES-256-GCM.
    /// Format de sortie : [nonce (12 bytes)] [tag (16 bytes)] [ciphertext]
    /// </summary>
    public byte[] Encrypt(byte[] plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(_masterKey, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Concaténation : nonce + tag + ciphertext
        var result = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, result, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSizeBytes + TagSizeBytes, ciphertext.Length);

        return result;
    }

    /// <summary>
    /// Déchiffre et vérifie l'intégrité (tag GCM).
    /// Lève une exception si les données ont été altérées.
    /// </summary>
    public byte[] Decrypt(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (data.Length < NonceSizeBytes + TagSizeBytes)
            throw new CryptographicException("Invalid encrypted data: too short.");

        var nonce = data[..NonceSizeBytes];
        var tag = data[NonceSizeBytes..(NonceSizeBytes + TagSizeBytes)];
        var ciphertext = data[(NonceSizeBytes + TagSizeBytes)..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_masterKey, TagSizeBytes);
        // AesGcm.Decrypt vérifie le tag et lève AuthenticationTagMismatchException si altéré
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    public string EncryptString(string plaintext)
    {
        var bytes = Encrypt(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToBase64String(bytes);
    }

    public string DecryptString(string ciphertext)
    {
        var bytes = Decrypt(Convert.FromBase64String(ciphertext));
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Calcule le SHA-256 d'un fichier en lecture en continu (efficace pour les gros fichiers).
    /// </summary>
    public async Task<string> ComputeFileChecksumAsync(string filePath, CancellationToken ct = default)
    {
        using var sha256 = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, bufferSize: 81920, useAsync: true);

        var hash = await sha256.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task<bool> VerifyFileIntegrityAsync(string filePath, string expectedChecksum, CancellationToken ct = default)
    {
        try
        {
            var actual = await ComputeFileChecksumAsync(filePath, ct);
            return string.Equals(actual, expectedChecksum, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Integrity check failed for {File}", filePath);
            return false;
        }
    }

    // ──────────────────────────────────────────────
    // Gestion de la clé maître
    // ──────────────────────────────────────────────

    private byte[] LoadOrCreateKey(string? keyFilePath)
    {
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AtmLogAgent", "agent.key");

        var path = keyFilePath ?? defaultPath;

        if (File.Exists(path))
        {
            return LoadKey(path);
        }

        _logger.LogWarning("Encryption key not found — generating a new key at {Path}", path);
        return GenerateAndSaveKey(path);
    }

    private byte[] LoadKey(string path)
    {
        try
        {
            var protectedKey = File.ReadAllBytes(path);
            // Sur Windows : déprotéger avec DPAPI (clé liée à la machine)
            if (OperatingSystem.IsWindows())
            {
                return ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.LocalMachine);
            }
            // Sur Linux : la clé est protégée par des permissions OS (mode 600)
            return protectedKey;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL: Cannot load encryption key from {Path}", path);
            throw new InvalidOperationException("Encryption key could not be loaded.", ex);
        }
    }

    private byte[] GenerateAndSaveKey(string path)
    {
        var key = new byte[32]; // 256 bits
        RandomNumberGenerator.Fill(key);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        byte[] toWrite;
        if (OperatingSystem.IsWindows())
        {
            // DPAPI : lie la clé à la machine locale
            toWrite = ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine);
        }
        else
        {
            toWrite = key;
        }

        File.WriteAllBytes(path, toWrite);
        SetRestrictivePermissions(path);

        _logger.LogInformation("New encryption key generated and saved to {Path}", path);
        return key;
    }

    private static void SetRestrictivePermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // chmod 600 : lecture/écriture uniquement pour le propriétaire (root)
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Effacement sécurisé de la clé en mémoire
        CryptographicOperations.ZeroMemory(_masterKey);
    }
}
