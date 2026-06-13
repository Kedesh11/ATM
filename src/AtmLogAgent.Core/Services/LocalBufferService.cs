using AtmLogAgent.Core.Interfaces;
using AtmLogAgent.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AtmLogAgent.Core.Services;

/// <summary>
/// Tampon local persistant basé sur SQLite.
/// Garantit la durabilité des logs en cas de coupure réseau ou de crash de l'agent.
/// Toutes les données sensibles sont chiffrées avant d'être écrites.
/// La base SQLite est en mode WAL (Write-Ahead Logging) pour la performance et la sécurité.
/// </summary>
public sealed class LocalBufferService : IBufferService, IAsyncDisposable
{
    private readonly AgentConfiguration _config;
    private readonly IEncryptionService _encryption;
    private readonly ILogger<LocalBufferService> _logger;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _initialized;

    public LocalBufferService(
        IOptions<AgentConfiguration> config,
        IEncryptionService encryption,
        ILogger<LocalBufferService> logger)
    {
        _config = config.Value;
        _encryption = encryption;
        _logger = logger;
        var baseDir = Environment.GetEnvironmentVariable("ATMAGENT_DATA_DIR") 
            ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            
        _dbPath = Path.Combine(baseDir, "AtmLogAgent", "buffer.db");
    }

    // ──────────────────────────────────────────────
    // Initialisation
    // ──────────────────────────────────────────────

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _dbLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await CreateSchemaAsync(conn, ct);
            _initialized = true;
            _logger.LogInformation("Local buffer initialized at {Path}", _dbPath);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    // ──────────────────────────────────────────────
    // Entrées de log (temps réel)
    // ──────────────────────────────────────────────

    public async Task EnqueueAsync(LogEntry entry, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // Chiffrer le contenu du log avant stockage local
        var encryptedContent = _encryption.EncryptString(entry.Content);

        await ExecuteAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO log_entries
                    (id, atm_id, source_file_path, content_encrypted, format,
                     captured_utc, log_timestamp_utc, remote_path, checksum, status, retry_count)
                VALUES
                    (@id, @atm, @src, @content, @fmt,
                     @cap, @ts, @rmt, @chk, @status, 0)
                """;
            cmd.Parameters.AddWithValue("@id", entry.Id.ToString());
            cmd.Parameters.AddWithValue("@atm", entry.AtmId);
            cmd.Parameters.AddWithValue("@src", entry.SourceFilePath);
            cmd.Parameters.AddWithValue("@content", encryptedContent);
            cmd.Parameters.AddWithValue("@fmt", (int)entry.Format);
            cmd.Parameters.AddWithValue("@cap", entry.CapturedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@ts", (object?)entry.LogTimestamp?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@rmt", (object?)entry.RemotePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@chk", (object?)entry.Checksum ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@status", (int)TransmissionStatus.Pending);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task EnqueueFileAsync(FileSyncRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await ExecuteAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO file_sync_records
                    (id, atm_id, local_path, remote_path, file_size_bytes,
                     local_checksum, scheduled_utc, status, compressed, retry_count)
                VALUES
                    (@id, @atm, @local, @remote, @size,
                     @chk, @sched, @status, @comp, 0)
                """;
            cmd.Parameters.AddWithValue("@id", record.Id.ToString());
            cmd.Parameters.AddWithValue("@atm", record.AtmId);
            cmd.Parameters.AddWithValue("@local", record.LocalPath);
            cmd.Parameters.AddWithValue("@remote", record.RemotePath);
            cmd.Parameters.AddWithValue("@size", record.FileSizeBytes);
            cmd.Parameters.AddWithValue("@chk", (object?)record.LocalChecksum ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@sched", record.ScheduledUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@status", (int)TransmissionStatus.Pending);
            cmd.Parameters.AddWithValue("@comp", record.Compressed ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task<IReadOnlyList<LogEntry>> DequeuePendingEntriesAsync(int maxCount, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var results = new List<LogEntry>();

        await ExecuteAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, atm_id, source_file_path, content_encrypted, format,
                       captured_utc, log_timestamp_utc, remote_path, checksum, retry_count, status
                FROM log_entries
                WHERE status IN (@pending, @failed)
                  AND retry_count < @maxRetry
                ORDER BY captured_utc ASC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@pending", (int)TransmissionStatus.Pending);
            cmd.Parameters.AddWithValue("@failed", (int)TransmissionStatus.Failed);
            cmd.Parameters.AddWithValue("@maxRetry", _config.Transmission.MaxRetryAttempts);
            cmd.Parameters.AddWithValue("@limit", maxCount);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var encryptedContent = reader.GetString(3);
                var decryptedContent = _encryption.DecryptString(encryptedContent);

                results.Add(new LogEntry
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    AtmId = reader.GetString(1),
                    SourceFilePath = reader.GetString(2),
                    Content = decryptedContent,
                    Format = (LogFormat)reader.GetInt32(4),
                    CapturedUtc = DateTime.Parse(reader.GetString(5)),
                    LogTimestamp = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
                    RemotePath = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Checksum = reader.IsDBNull(8) ? null : reader.GetString(8),
                    RetryCount = reader.GetInt32(9),
                    Status = (TransmissionStatus)reader.GetInt32(10)
                });
            }
        }, ct);

        return results;
    }

    public async Task<IReadOnlyList<FileSyncRecord>> DequeuePendingFilesAsync(int maxCount, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var results = new List<FileSyncRecord>();

        await ExecuteAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, atm_id, local_path, remote_path, file_size_bytes,
                       local_checksum, scheduled_utc, compressed, retry_count
                FROM file_sync_records
                WHERE status = @pending
                  AND retry_count < @maxRetry
                ORDER BY scheduled_utc ASC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@pending", (int)TransmissionStatus.Pending);
            cmd.Parameters.AddWithValue("@maxRetry", _config.Transmission.MaxRetryAttempts);
            cmd.Parameters.AddWithValue("@limit", maxCount);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new FileSyncRecord
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    AtmId = reader.GetString(1),
                    LocalPath = reader.GetString(2),
                    RemotePath = reader.GetString(3),
                    FileSizeBytes = reader.GetInt64(4),
                    LocalChecksum = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ScheduledUtc = DateTime.Parse(reader.GetString(6)),
                    Compressed = reader.GetInt32(7) == 1,
                    RetryCount = reader.GetInt32(8)
                });
            }
        }, ct);

        return results;
    }

    public async Task MarkEntryCompletedAsync(Guid entryId, CancellationToken ct = default)
        => await UpdateEntryStatusAsync(entryId, TransmissionStatus.Completed, null, ct);

    public async Task MarkEntryFailedAsync(Guid entryId, string errorMessage, CancellationToken ct = default)
        => await UpdateEntryStatusAsync(entryId, TransmissionStatus.Failed, errorMessage, ct);

    public async Task MarkFileCompletedAsync(Guid recordId, string remoteChecksum, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await ExecuteAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE file_sync_records
                SET status = @done, remote_checksum = @chk, completed_utc = @now
                WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@done", (int)TransmissionStatus.Completed);
            cmd.Parameters.AddWithValue("@chk", remoteChecksum);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@id", recordId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    public async Task<long> GetBufferSizeBytesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var fi = new FileInfo(_dbPath);
        return fi.Exists ? fi.Length : 0L;
    }

    public async Task<long> GetPendingCountAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        long count = 0;
        await ExecuteAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*)
                FROM log_entries
                WHERE status IN (@pending, @failed)
                  AND retry_count < @maxRetry
                """;
            cmd.Parameters.AddWithValue("@pending", (int)TransmissionStatus.Pending);
            cmd.Parameters.AddWithValue("@failed", (int)TransmissionStatus.Failed);
            cmd.Parameters.AddWithValue("@maxRetry", _config.Transmission.MaxRetryAttempts);
            count = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }, ct);
        return count;
    }

    public async Task PurgeExpiredDataAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var cutoff = DateTime.UtcNow.AddDays(-_config.Retention.BufferedDataRetentionDays).ToString("O");

        await ExecuteAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM log_entries
                WHERE status = @done AND captured_utc < @cutoff;
                DELETE FROM file_sync_records
                WHERE status = @done AND scheduled_utc < @cutoff;
                """;
            cmd.Parameters.AddWithValue("@done", (int)TransmissionStatus.Completed);
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0)
                _logger.LogInformation("Purged {Count} expired buffer record(s)", deleted);
        }, ct);

        // VACUUM pour récupérer l'espace disque
        await ExecuteAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM";
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    // ──────────────────────────────────────────────
    // Méthodes privées
    // ──────────────────────────────────────────────

    private async Task UpdateEntryStatusAsync(Guid id, TransmissionStatus status, string? error, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await ExecuteAsync(async (conn, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = status == TransmissionStatus.Failed
                ? """
                  UPDATE log_entries
                  SET retry_count = retry_count + 1,
                      status = CASE
                          WHEN retry_count + 1 >= @maxRetry THEN @abandoned
                          ELSE @pending
                      END,
                      error_message = @err
                  WHERE id = @id
                  """
                : "UPDATE log_entries SET status = @s WHERE id = @id";
            cmd.Parameters.AddWithValue("@s", (int)status);
            cmd.Parameters.AddWithValue("@id", id.ToString());
            if (status == TransmissionStatus.Failed)
            {
                cmd.Parameters.AddWithValue("@err", (object?)error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@maxRetry", _config.Transmission.MaxRetryAttempts);
                cmd.Parameters.AddWithValue("@abandoned", (int)TransmissionStatus.Abandoned);
                cmd.Parameters.AddWithValue("@pending", (int)TransmissionStatus.Pending);
            }
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
    }

    private async Task ExecuteAsync(Func<SqliteConnection, CancellationToken, Task> action, CancellationToken ct)
    {
        await _dbLock.WaitAsync(ct);
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync(ct);
            await action(conn, ct);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    private SqliteConnection CreateConnection()
    {
        // Mode WAL = meilleure concurrence lecture/écriture, récupération plus robuste
        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
        return new SqliteConnection(connStr);
    }

    private static async Task CreateSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS log_entries (
                id                  TEXT PRIMARY KEY,
                atm_id              TEXT NOT NULL,
                source_file_path    TEXT NOT NULL,
                content_encrypted   TEXT NOT NULL,
                format              INTEGER NOT NULL DEFAULT 0,
                captured_utc        TEXT NOT NULL,
                log_timestamp_utc   TEXT,
                remote_path         TEXT,
                checksum            TEXT,
                status              INTEGER NOT NULL DEFAULT 0,
                retry_count         INTEGER NOT NULL DEFAULT 0,
                error_message       TEXT,
                created_at          TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS file_sync_records (
                id                  TEXT PRIMARY KEY,
                atm_id              TEXT NOT NULL,
                local_path          TEXT NOT NULL,
                remote_path         TEXT NOT NULL,
                file_size_bytes     INTEGER NOT NULL DEFAULT 0,
                local_checksum      TEXT,
                remote_checksum     TEXT,
                scheduled_utc       TEXT NOT NULL,
                completed_utc       TEXT,
                status              INTEGER NOT NULL DEFAULT 0,
                compressed          INTEGER NOT NULL DEFAULT 0,
                retry_count         INTEGER NOT NULL DEFAULT 0,
                created_at          TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE INDEX IF NOT EXISTS idx_entries_status ON log_entries(status, captured_utc);
            CREATE INDEX IF NOT EXISTS idx_files_status ON file_sync_records(status, scheduled_utc);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _dbLock.Dispose();
        await Task.CompletedTask;
    }
}
