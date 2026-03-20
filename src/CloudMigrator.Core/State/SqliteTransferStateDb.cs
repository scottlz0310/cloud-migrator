using System.Runtime.CompilerServices;
using CloudMigrator.Providers.Abstractions;
using Microsoft.Data.Sqlite;

namespace CloudMigrator.Core.State;

/// <summary>
/// SQLite を使用した <see cref="ITransferStateDb"/> 実装（Dropbox 移行専用）。
/// </summary>
public sealed class SqliteTransferStateDb : ITransferStateDb
{
    /// <summary>失敗回数がこの値以上になると <see cref="TransferStatus.PermanentFailed"/> に遷移する。</summary>
    public const int MaxRetry = 3;

    private readonly SqliteConnection _connection;

    public SqliteTransferStateDb(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await _connection.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS transfer_records (
                path        TEXT NOT NULL,
                name        TEXT NOT NULL,
                source_id   TEXT NOT NULL DEFAULT '',
                size_bytes  INTEGER,
                modified    TEXT,
                status      TEXT NOT NULL DEFAULT 'pending',
                retry_count INTEGER NOT NULL DEFAULT 0,
                error       TEXT,
                updated_at  TEXT NOT NULL,
                PRIMARY KEY (path, name)
            );
            CREATE TABLE IF NOT EXISTS checkpoints (
                key         TEXT PRIMARY KEY,
                value       TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<TransferStatus?> GetStatusAsync(string path, string name, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM transfer_records WHERE path=@path AND name=@name";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@name", name);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : ParseStatus((string)result);
    }

    /// <inheritdoc/>
    public async Task UpsertPendingAsync(StorageItem item, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transfer_records (path, name, source_id, size_bytes, modified, status, retry_count, error, updated_at)
            VALUES (@path, @name, @sourceId, @size, @modified, 'pending', 0, NULL, @updatedAt)
            ON CONFLICT(path, name) DO UPDATE SET
                source_id   = excluded.source_id,
                size_bytes  = excluded.size_bytes,
                modified    = excluded.modified,
                status      = 'pending',
                retry_count = 0,
                error       = NULL,
                updated_at  = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@path", item.Path);
        cmd.Parameters.AddWithValue("@name", item.Name);
        cmd.Parameters.AddWithValue("@sourceId", item.Id);
        cmd.Parameters.AddWithValue("@size", (object?)item.SizeBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@modified", (object?)item.LastModifiedUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task MarkProcessingAsync(string path, string name, CancellationToken ct)
        => UpdateStatusAsync(path, name, "processing", null, ct);

    /// <inheritdoc/>
    public Task MarkDoneAsync(string path, string name, CancellationToken ct)
        => UpdateStatusAsync(path, name, "done", null, ct);

    /// <inheritdoc/>
    public async Task MarkFailedAsync(string path, string name, string error, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE transfer_records
            SET retry_count = retry_count + 1,
                status      = CASE WHEN retry_count + 1 >= @maxRetry THEN 'permanent_failed' ELSE 'failed' END,
                error       = @error,
                updated_at  = @updatedAt
            WHERE path=@path AND name=@name;
            """;
        cmd.Parameters.AddWithValue("@maxRetry", MaxRetry);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@name", name);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string?> GetCheckpointAsync(string key, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM checkpoints WHERE key=@key";
        cmd.Parameters.AddWithValue("@key", key);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : (string)result;
    }

    /// <inheritdoc/>
    public async Task SaveCheckpointAsync(string key, string value, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO checkpoints (key, value, updated_at)
            VALUES (@key, @value, @updatedAt)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TransferRecord> GetPendingStreamAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT path, name, source_id, size_bytes, modified, status, retry_count, error, updated_at
            FROM transfer_records
            WHERE status IN ('pending', 'processing', 'failed')
            ORDER BY rowid;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new TransferRecord
            {
                Path       = reader.GetString(0),
                Name       = reader.GetString(1),
                SourceId   = reader.GetString(2),
                SizeBytes  = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                Modified   = reader.IsDBNull(4) ? null : reader.GetString(4),
                Status     = ParseStatus(reader.GetString(5)),
                RetryCount = reader.GetInt32(6),
                Error      = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdatedAt  = DateTimeOffset.Parse(reader.GetString(8)),
            };
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task UpdateStatusAsync(string path, string name, string status, string? error, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE transfer_records
            SET status=@status, error=@error, updated_at=@updatedAt
            WHERE path=@path AND name=@name;
            """;
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@name", name);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static TransferStatus ParseStatus(string status) => status switch
    {
        "pending"          => TransferStatus.Pending,
        "processing"       => TransferStatus.Processing,
        "done"             => TransferStatus.Done,
        "failed"           => TransferStatus.Failed,
        "permanent_failed" => TransferStatus.PermanentFailed,
        _                  => TransferStatus.Pending,
    };
}
