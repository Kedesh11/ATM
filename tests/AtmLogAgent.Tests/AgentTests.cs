using System.Text;
using System.Text.Json;
using AtmLogAgent.Core.Models;
using AtmLogAgent.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AtmLogAgent.Tests;

// ══════════════════════════════════════════════════════════════
//  Tests — EncryptionService
// ══════════════════════════════════════════════════════════════

public sealed class EncryptionServiceTests : IDisposable
{
    private readonly EncryptionService _sut;
    private readonly string _tempKeyPath;

    public EncryptionServiceTests()
    {
        _tempKeyPath = Path.GetTempFileName();
        File.Delete(_tempKeyPath); // Le service va le créer
        _sut = new EncryptionService(NullLogger<EncryptionService>.Instance, _tempKeyPath);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_ShouldReturnOriginal()
    {
        // Arrange
        var plaintext = Encoding.UTF8.GetBytes("Données sensibles ATM - Transaction 12345");

        // Act
        var encrypted = _sut.Encrypt(plaintext);
        var decrypted = _sut.Decrypt(encrypted);

        // Assert
        decrypted.Should().BeEquivalentTo(plaintext);
        encrypted.Should().NotBeEquivalentTo(plaintext, "les données doivent être chiffrées");
    }

    [Fact]
    public void EncryptString_DecryptString_RoundTrip_ShouldReturnOriginal()
    {
        var original = "TRACK 2 DATA: 531234******5678 | CODE REPONSE: 00 | MONTANT: 30000 XAF";
        var encrypted = _sut.EncryptString(original);
        var decrypted = _sut.DecryptString(encrypted);

        decrypted.Should().Be(original);
        encrypted.Should().NotBe(original);
    }

    [Fact]
    public void Encrypt_TwiceWithSamePlaintext_ShouldProduceDifferentCiphertext()
    {
        // Le nonce GCM aléatoire garantit que deux chiffrements identiques donnent des résultats différents
        var plaintext = Encoding.UTF8.GetBytes("Same message");
        var enc1 = _sut.Encrypt(plaintext);
        var enc2 = _sut.Encrypt(plaintext);

        enc1.Should().NotBeEquivalentTo(enc2, "chaque chiffrement doit utiliser un nonce unique");
    }

    [Fact]
    public void Decrypt_WithTamperedData_ShouldThrowCryptographicException()
    {
        var plaintext = Encoding.UTF8.GetBytes("Data to protect");
        var encrypted = _sut.Encrypt(plaintext);

        // Altérer un octet au milieu du ciphertext
        encrypted[encrypted.Length / 2] ^= 0xFF;

        var act = () => _sut.Decrypt(encrypted);
        act.Should().Throw<System.Security.Cryptography.CryptographicException>(
            "le tag GCM doit détecter toute altération");
    }

    [Fact]
    public async Task ComputeFileChecksum_SameFile_ShouldReturnSameHash()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "20200810.jrn sample content");
            var hash1 = await _sut.ComputeFileChecksumAsync(path);
            var hash2 = await _sut.ComputeFileChecksumAsync(path);
            hash1.Should().Be(hash2);
            hash1.Should().HaveLength(64, "SHA-256 produit 32 octets en hex = 64 caractères");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task VerifyFileIntegrity_AfterModification_ShouldReturnFalse()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "Original content");
            var checksum = await _sut.ComputeFileChecksumAsync(path);

            await File.WriteAllTextAsync(path, "Tampered content");
            var valid = await _sut.VerifyFileIntegrityAsync(path, checksum);

            valid.Should().BeFalse("le fichier a été modifié");
        }
        finally { File.Delete(path); }
    }

    public void Dispose() => _sut.Dispose();
}

// ══════════════════════════════════════════════════════════════
//  Tests — AtmIdentity (construction des chemins distants)
// ══════════════════════════════════════════════════════════════

public sealed class AtmIdentityTests
{
    [Theory]
    [InlineData("BGFI",    "GABON",   "LIBREVILLE", "ATM_001", "BGFI/GABON/LIBREVILLE/ATM_001")]
    [InlineData("BGFI",    "GABON",   "PORT-GENTIL", "ATM_010", "BGFI/GABON/PORT-GENTIL/ATM_010")]
    [InlineData("UBA",     "CAMEROUN","DOUALA",      "ATM_007", "UBA/CAMEROUN/DOUALA/ATM_007")]
    public void GetBasePath_ShouldReturnNormalizedPath(
        string bank, string country, string city, string atmId, string expected)
    {
        var identity = new AtmIdentity
        {
            BankName = bank, Country = country,
            City = city, AtmId = atmId
        };

        identity.GetBasePath().Should().Be(expected);
    }

    [Fact]
    public void GetBasePath_WithSpecialCharacters_ShouldSanitize()
    {
        var identity = new AtmIdentity
        {
            BankName = "BAN QUE/TEST",
            Country = "PAYS:TEST",
            City = "VILLE*TEST",
            AtmId = "ATM|001"
        };

        var path = identity.GetBasePath();
        path.Should().Contain("BAN_QUE-TEST");
        path.Should().Contain("PAYS-TEST");
        path.Should().Contain("VILLETEST");
        path.Should().Contain("ATM001");
    }

    [Fact]
    public void GetBasePath_ShouldBeUppercase()
    {
        var identity = new AtmIdentity
        {
            BankName = "bgfi", Country = "gabon",
            City = "libreville", AtmId = "atm_001"
        };

        identity.GetBasePath().Should().Be("BGFI/GABON/LIBREVILLE/ATM_001");
    }
}

// ══════════════════════════════════════════════════════════════
//  Tests — LogDiscoveryService (format detection & path building)
// ══════════════════════════════════════════════════════════════

public sealed class LogDiscoveryServiceTests
{
    private static AgentConfiguration BuildConfig() => new()
    {
        Atm = new AtmIdentity
        {
            BankName = "BGFI", Country = "GABON",
            City = "LIBREVILLE", AtmId = "ATM_001"
        },
        Transmission = new TransmissionConfig
        {
            Protocol = "SFTP", Host = "test", Port = 22,
            Username = "test"
        },
        Security = new SecurityConfig { LocalEncryptionKeyId = "test" },
        LogDiscovery = new LogDiscoveryConfig(),
        Update = new UpdateConfig { UpdateServerUrl = "https://test" },
        Monitoring = new MonitoringConfig { HeartbeatUrl = "https://test" },
        Retention = new RetentionConfig()
    };

    [Theory]
    [InlineData("journal.jrn",  LogFormat.Proprietary)]
    [InlineData("events.xml",   LogFormat.Xml)]
    [InlineData("data.json",    LogFormat.Json)]
    [InlineData("report.csv",   LogFormat.Csv)]
    [InlineData("unknown.bin",  LogFormat.Unknown)]
    public void DetectFormat_ByExtension_ShouldReturnCorrectFormat(string filename, LogFormat expected)
    {
        var options = Microsoft.Extensions.Options.Options.Create(BuildConfig());
        var sut = new LogDiscoveryService(options, NullLogger<LogDiscoveryService>.Instance);

        var format = sut.DetectFormat(filename);
        format.Should().Be(expected);
    }

    [Fact]
    public void BuildRemotePath_ShouldFollowNormalizedStructure()
    {
        // Structure attendue : BGFI/GABON/LIBREVILLE/ATM_001/YYYY/MM/DD/filename.log
        var options = Microsoft.Extensions.Options.Options.Create(BuildConfig());
        var sut = new LogDiscoveryService(options, NullLogger<LogDiscoveryService>.Instance);

        var logDate = new DateTime(2025, 1, 1, 15, 30, 45, DateTimeKind.Utc);
        var remotePath = sut.BuildRemotePath("/opt/atm/logs/journal.jrn", logDate);

        remotePath.Should().StartWith("BGFI/GABON/LIBREVILLE/ATM_001/2025/01/01/");
        remotePath.Should().EndWith("journal.jrn");
        remotePath.Should().NotContain("\\", "les chemins distants doivent utiliser des slashes Unix");
    }

    [Fact]
    public void BuildRemotePath_WithFilenameContainingSpecialChars_ShouldSanitize()
    {
        var options = Microsoft.Extensions.Options.Options.Create(BuildConfig());
        var sut = new LogDiscoveryService(options, NullLogger<LogDiscoveryService>.Instance);

        // Les caractères invalides dans les noms de fichiers doivent être remplacés
        var remotePath = sut.BuildRemotePath("/logs/my|log<file>.jrn", DateTime.UtcNow);
        remotePath.Should().NotContain("|");
        remotePath.Should().NotContain("<");
        remotePath.Should().NotContain(">");
    }
}

// ══════════════════════════════════════════════════════════════
//  Tests — LocalBufferService (persistance SQLite)
// ══════════════════════════════════════════════════════════════

public sealed class LocalBufferServiceTests : IAsyncDisposable
{
    private readonly LocalBufferService _sut;
    private readonly EncryptionService _encryption;
    private readonly string _tempKeyPath;
    private readonly string _dataDir;

    public LocalBufferServiceTests()
    {
        _tempKeyPath = Path.Combine(Path.GetTempPath(), $"test_key_{Guid.NewGuid():N}");
        _encryption = new EncryptionService(NullLogger<EncryptionService>.Instance, _tempKeyPath);

        // Rediriger la base de données vers un fichier temporaire unique par test
        // (En production, le chemin est dans CommonApplicationData)
        var config = new AgentConfiguration
        {
            Atm = new AtmIdentity
            {
                BankName = "TEST", Country = "TEST",
                City = "TEST", AtmId = "ATM_TEST"
            },
            Transmission = new TransmissionConfig
            {
                Protocol = "SFTP", Host = "test", Port = 22, Username = "test",
                MaxRetryAttempts = 3, RetryDelaySeconds = 1, FullSyncIntervalHours = 24
            },
            Security = new SecurityConfig { LocalEncryptionKeyId = _tempKeyPath },
            LogDiscovery = new LogDiscoveryConfig(),
            Update = new UpdateConfig { UpdateServerUrl = "https://test" },
            Monitoring = new MonitoringConfig { HeartbeatUrl = "https://test" },
            Retention = new RetentionConfig { BufferedDataRetentionDays = 7 }
        };

        var options = Microsoft.Extensions.Options.Options.Create(config);
        _dataDir = Path.Combine(Path.GetTempPath(), $"atm_buffer_{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("ATMAGENT_DATA_DIR", _dataDir);
        _sut = new LocalBufferService(options, _encryption, NullLogger<LocalBufferService>.Instance);
    }

    [Fact]
    public async Task EnqueueAndDequeue_ShouldReturnSameEntry()
    {
        var entry = new LogEntry
        {
            AtmId = "ATM_TEST",
            SourceFilePath = "/logs/20230418.jrn",
            Content = "*523*11:43:25 OPERATOR DOOR OPENED",
            Format = LogFormat.Proprietary,
            RemotePath = "BGFI/GABON/LIBREVILLE/ATM_001/2023/04/18/114325/20230418.jrn",
            Checksum = "abc123"
        };

        await _sut.EnqueueAsync(entry);
        var results = await _sut.DequeuePendingEntriesAsync(10);

        results.Should().ContainSingle();
        results[0].AtmId.Should().Be("ATM_TEST");
        results[0].Content.Should().Be(entry.Content, "le contenu doit survivre au chiffrement/déchiffrement");
        results[0].RemotePath.Should().Be(entry.RemotePath);
    }

    [Fact]
    public async Task MarkEntryCompleted_ShouldRemoveFromPendingQueue()
    {
        var entry = new LogEntry
        {
            AtmId = "ATM_TEST",
            SourceFilePath = "/logs/test.jrn",
            Content = "06:15:00 -> TRANSACTION START",
            Format = LogFormat.Proprietary
        };

        await _sut.EnqueueAsync(entry);
        await _sut.MarkEntryCompletedAsync(entry.Id);

        var pending = await _sut.DequeuePendingEntriesAsync(10);
        pending.Should().BeEmpty("l'entrée marquée comme complète ne doit plus apparaître");
    }

    [Fact]
    public async Task MarkEntryFailed_ShouldKeepEntryRetryableUntilMaxAttempts()
    {
        var entry = new LogEntry
        {
            AtmId = "ATM_TEST",
            SourceFilePath = "/logs/retry.jrn",
            Content = "06:15:00 -> TRANSACTION START",
            Format = LogFormat.Proprietary
        };

        await _sut.EnqueueAsync(entry);
        await _sut.MarkEntryFailedAsync(entry.Id, "network down");

        var retryable = await _sut.DequeuePendingEntriesAsync(10);

        retryable.Should().ContainSingle();
        retryable[0].Id.Should().Be(entry.Id);
        retryable[0].RetryCount.Should().Be(1);
        retryable[0].Status.Should().Be(TransmissionStatus.Pending);
    }

    [Fact]
    public async Task MarkEntryFailed_AfterMaxAttempts_ShouldAbandonEntry()
    {
        var entry = new LogEntry
        {
            AtmId = "ATM_TEST",
            SourceFilePath = "/logs/abandon.jrn",
            Content = "06:15:00 -> TRANSACTION START",
            Format = LogFormat.Proprietary
        };

        await _sut.EnqueueAsync(entry);
        await _sut.MarkEntryFailedAsync(entry.Id, "attempt 1");
        await _sut.MarkEntryFailedAsync(entry.Id, "attempt 2");
        await _sut.MarkEntryFailedAsync(entry.Id, "attempt 3");

        var retryable = await _sut.DequeuePendingEntriesAsync(10);

        retryable.Should().BeEmpty("une entrée qui dépasse MaxRetryAttempts doit sortir de la file retryable");
    }

    [Fact]
    public async Task GetPendingCount_ShouldReflectQueueState()
    {
        for (var i = 0; i < 5; i++)
        {
            await _sut.EnqueueAsync(new LogEntry
            {
                AtmId = "ATM_TEST",
                SourceFilePath = $"/logs/test_{i}.jrn",
                Content = $"Entry {i}",
                Format = LogFormat.PlainText
            });
        }

        var count = await _sut.GetPendingCountAsync();
        count.Should().Be(5);
    }

    [Fact]
    public async Task EnqueueFileSync_ShouldBeRetrievable()
    {
        var record = new FileSyncRecord
        {
            AtmId = "ATM_TEST",
            LocalPath = "/logs/20230418.jrn",
            RemotePath = "BGFI/GABON/LIBREVILLE/ATM_001/2023/04/18/000000/20230418.jrn",
            FileSizeBytes = 4096,
            LocalChecksum = "def456"
        };

        await _sut.EnqueueFileAsync(record);
        var results = await _sut.DequeuePendingFilesAsync(10);

        results.Should().ContainSingle();
        results[0].LocalPath.Should().Be(record.LocalPath);
        results[0].RemotePath.Should().Be(record.RemotePath);
        results[0].LocalChecksum.Should().Be(record.LocalChecksum);
    }

    public async ValueTask DisposeAsync()
    {
        await _sut.DisposeAsync();
        _encryption.Dispose();
        if (File.Exists(_tempKeyPath)) File.Delete(_tempKeyPath);
        Environment.SetEnvironmentVariable("ATMAGENT_DATA_DIR", null);
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }
}

// ══════════════════════════════════════════════════════════════
//  Tests — Configuration de production
// ══════════════════════════════════════════════════════════════

public sealed class ProductionConfigurationTests
{
    [Fact]
    public void ServiceAppSettings_ShouldUseAtmAgentRootSection()
    {
        var path = FindRepositoryFile("src/AtmLogAgent.Service/appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        var root = doc.RootElement;
        root.TryGetProperty("AtmAgent", out var atmAgent).Should().BeTrue(
            "Program.cs bind la configuration depuis la section AtmAgent");

        atmAgent.TryGetProperty("Transmission", out var transmission).Should().BeTrue();
        transmission.GetProperty("Protocol").GetString().Should().Be("SFTP");

        atmAgent.TryGetProperty("Security", out var security).Should().BeTrue();
        security.GetProperty("ValidateServerCertificate").GetBoolean().Should().BeTrue();
        security.GetProperty("ServerCertificatePinning").GetString().Should().NotBeNullOrWhiteSpace();

        atmAgent.TryGetProperty("Update", out var update).Should().BeTrue();
        update.GetProperty("EnableAutoUpdate").GetBoolean().Should().BeFalse(
            "les mises à jour automatiques exigent une clé publique de signature explicitement provisionnée");
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {relativePath} from {AppContext.BaseDirectory}");
    }
}

// ══════════════════════════════════════════════════════════════
//  Tests — Parsing des logs ATM propriétaires
// ══════════════════════════════════════════════════════════════

public sealed class AtmLogParserTests
{
    // Lignes extraites des fichiers .jrn fournis
    [Theory]
    [InlineData("06:00:02 DEVICE CCCardFW STATUS 0 SUPPLY 1",    true,  6, 0, 2)]
    [InlineData("*1000*06:00:02 OPERATOR DOOR OPENED",           true,  6, 0, 2)]
    [InlineData("06:15:00 -> TRANSACTION START",                  true,  6, 15, 0)]
    [InlineData("CODE REPONSE: 00",                               false, 0, 0, 0)]
    [InlineData("CASH TAKEN",                                     false, 0, 0, 0)]
    public void ExtractTimestamp_FromLogLine(string line, bool hasTimestamp, int h, int m, int s)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            line, @"(\d{2}):(\d{2}):(\d{2})");

        match.Success.Should().Be(hasTimestamp);
        if (hasTimestamp)
        {
            int.Parse(match.Groups[1].Value).Should().Be(h);
            int.Parse(match.Groups[2].Value).Should().Be(m);
            int.Parse(match.Groups[3].Value).Should().Be(s);
        }
    }

    [Theory]
    [InlineData("CODE REPONSE: 00", true,  "00")]
    [InlineData("CODE REPONSE: 51", true,  "51")]
    [InlineData("CODE REPONSE: 54", true,  "54")]
    [InlineData("CODE REPONSE: 75", true,  "75")]
    [InlineData("PIN ENTERED",      false, "")]
    public void ExtractResponseCode_FromLogLine(string line, bool found, string expectedCode)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"CODE REPONSE:\s*(\d+)");
        match.Success.Should().Be(found);
        if (found)
            match.Groups[1].Value.Should().Be(expectedCode);
    }

    [Theory]
    [InlineData("TRACK 2 DATA: 531234******5678",  "531234******5678")]
    [InlineData("TRACK 2 DATA: 400000******0001",  "400000******0001")]
    [InlineData("TRACK 2 DATA: 123456******9999",  "123456******9999")]
    public void ExtractMaskedPan_FromTrackData(string line, string expectedMaskedPan)
    {
        // Le PAN doit être masqué (6 premiers + 4 derniers, étoiles au milieu)
        var match = System.Text.RegularExpressions.Regex.Match(line, @"TRACK 2 DATA:\s*(\S+)");
        match.Success.Should().BeTrue();
        var pan = match.Groups[1].Value;
        pan.Should().Be(expectedMaskedPan);
        pan.Should().Contain("*", "le PAN doit être masqué dans les logs");
    }

    [Theory]
    [InlineData("MONTANT:  50000   XAF", true,  50000)]
    [InlineData("AMOUNT 30000 ENTERED",  true,  30000)]
    [InlineData("AMOUNT 20000 ENTERED",  true,  20000)]
    [InlineData("PIN ENTERED",           false, 0)]
    public void ExtractAmount_FromLogLine(string line, bool found, int expectedAmount)
    {
        // Deux formats possibles selon la version du journal
        var match = System.Text.RegularExpressions.Regex.Match(
            line, @"(?:MONTANT|AMOUNT)[:\s]+(\d+)");
        match.Success.Should().Be(found);
        if (found)
            int.Parse(match.Groups[1].Value).Should().Be(expectedAmount);
    }

    [Fact]
    public void FilenameToDate_JrnFormat_ShouldParseCorrectly()
    {
        // Les fichiers .jrn sont nommés YYYYMMDD.jrn
        var filenames = new[]
        {
            ("20200810.jrn", new DateTime(2020, 8, 10)),
            ("20230418.jrn", new DateTime(2023, 4, 18)),
            ("20240512.jrn", new DateTime(2024, 5, 12))
        };

        foreach (var (filename, expected) in filenames)
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
            var parsed = DateTime.TryParseExact(nameWithoutExt, "yyyyMMdd",
                null, System.Globalization.DateTimeStyles.None, out var date);

            parsed.Should().BeTrue();
            date.Should().Be(expected);
        }
    }

    [Theory]
    [InlineData("*1000*06:00:02 OPERATOR DOOR OPENED",    "1000")]
    [InlineData("*1001*06:00:03 COMMUNICATION ONLINE",    "1001")]
    [InlineData("*523*11:43:25 OPERATOR DOOR OPENED",     "523")]
    [InlineData("*900*06:30:10 OPERATOR DOOR OPENED",     "900")]
    public void ExtractEventId_FromAuditLine(string line, string expectedId)
    {
        // Les événements systèmes ATM sont préfixés *NNN*
        var match = System.Text.RegularExpressions.Regex.Match(line, @"^\*(\d+)\*");
        match.Success.Should().BeTrue();
        match.Groups[1].Value.Should().Be(expectedId);
    }
}
