using System.Runtime.CompilerServices;
using CloudMigrator.Providers.Abstractions;
using Microsoft.Data.Sqlite;

namespace CloudMigrator.Core.State;

/// <summary>
/// SQLite を使用した <see cref="ITransferStateDb"/> 実装。
/// Dropbox・SharePoint どちらの移行パイプラインでも共用する。
/// </summary>
public sealed class SqliteTransferStateDb : ITransferStateDb
{
    /// <summary>失敗回数がこの値以上になると <see cref="TransferStatus.PermanentFailed"/> に遷移する。</summary>
    public const int MaxRetry = 3;

    private readonly string _connectionString;

    public SqliteTransferStateDb(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
    }

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // WAL モードで並列読み書きのロック競合を低減する
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=5000;
                """;
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var cmd = conn.CreateCommand();
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
            CREATE TABLE IF NOT EXISTS metrics (
                timestamp   TEXT NOT NULL,
                name        TEXT NOT NULL,
                value       REAL NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_metrics_name_ts
                ON metrics(name, timestamp DESC);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<TransferStatus?> GetStatusAsync(string path, string name, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM transfer_records WHERE path=@path AND name=@name";
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@name", name);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : ParseStatus((string)result);
    }

    /// <inheritdoc/>
    public async Task UpsertPendingAsync(StorageItem item, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
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
    public async Task UpsertPendingIfNotTerminalAsync(StorageItem item, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
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
                updated_at  = excluded.updated_at
            WHERE transfer_records.status NOT IN ('done', 'permanent_failed');
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
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
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
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM checkpoints WHERE key=@key";
        cmd.Parameters.AddWithValue("@key", key);

        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : (string)result;
    }

    /// <inheritdoc/>
    public async Task SaveCheckpointAsync(string key, string value, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
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
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
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
                Path = reader.GetString(0),
                Name = reader.GetString(1),
                SourceId = reader.GetString(2),
                SizeBytes = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                Modified = reader.IsDBNull(4) ? null : reader.GetString(4),
                Status = ParseStatus(reader.GetString(5)),
                RetryCount = reader.GetInt32(6),
                Error = reader.IsDBNull(7) ? null : reader.GetString(7),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(8)),
            };
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public async Task<TransferDbSummary> GetSummaryAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // ステータス別集計と完了バイト数を一括取得
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = """
            SELECT
                SUM(CASE WHEN status='pending'          THEN 1 ELSE 0 END),
                SUM(CASE WHEN status='processing'       THEN 1 ELSE 0 END),
                SUM(CASE WHEN status='done'             THEN 1 ELSE 0 END),
                SUM(CASE WHEN status='failed'           THEN 1 ELSE 0 END),
                SUM(CASE WHEN status='permanent_failed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status='done' THEN COALESCE(size_bytes, 0) ELSE 0 END),
                MIN(updated_at),
                MAX(updated_at),
                SUM(retry_count)
            FROM transfer_records;
            """;

        int pending = 0, processing = 0, done = 0, failed = 0, permanentFailed = 0;
        long doneSizeBytes = 0;
        long totalRetries = 0;
        DateTimeOffset? firstUpdatedAt = null, lastUpdatedAt = null;

        await using (var reader = await countCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                pending = reader.IsDBNull(0) ? 0 : (int)reader.GetInt64(0);
                processing = reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1);
                done = reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2);
                failed = reader.IsDBNull(3) ? 0 : (int)reader.GetInt64(3);
                permanentFailed = reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4);
                doneSizeBytes = reader.IsDBNull(5) ? 0L : reader.GetInt64(5);
                firstUpdatedAt = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6));
                lastUpdatedAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7));
                totalRetries = reader.IsDBNull(8) ? 0L : reader.GetInt64(8);
            }
        }

        // 最近の失敗レコード（最大5件、新しい順）
        await using var failedCmd = conn.CreateCommand();
        failedCmd.CommandText = """
            SELECT path, name, error
            FROM transfer_records
            WHERE status IN ('failed', 'permanent_failed')
            ORDER BY updated_at DESC
            LIMIT 5;
            """;

        var recentFailed = new List<FailedItem>();
        await using (var reader = await failedCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                recentFailed.Add(new FailedItem(
                    Path: reader.GetString(0),
                    Name: reader.GetString(1),
                    Error: reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        // クロール完了フラグ + 確定総数（DropboxMigrationPipeline が Phase B 完了後に書き込む）
        await using var crawlCmd = conn.CreateCommand();
        crawlCmd.CommandText = """
            SELECT key, value FROM checkpoints
            WHERE key IN ('crawl_complete', 'crawl_total', 'pipeline_started_at')
            """;
        // 備考: 旧バージョンで作成された DB や空 DB では crawl_complete チェックポイントが存在しない。
        // その場合でもダッシュボードが常に「クロール中」とならないよう、未記録時の既定値を true とする。
        var crawlComplete = true;
        int? crawlTotal = null;
        DateTimeOffset? pipelineStartedAt = null;
        await using (var reader = await crawlCmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var k = reader.GetString(0);
                var v = reader.GetString(1);
                if (k == "crawl_complete") crawlComplete = v == "true";
                if (k == "crawl_total" && int.TryParse(v, out var n)) crawlTotal = n;
                if (k == "pipeline_started_at" && DateTimeOffset.TryParse(v, out var started)) pipelineStartedAt = started;
            }
        }

        return new TransferDbSummary
        {
            Pending = pending,
            Processing = processing,
            Done = done,
            Failed = failed,
            PermanentFailed = permanentFailed,
            TotalDoneSizeBytes = doneSizeBytes,
            TotalRetries = totalRetries,
            FirstUpdatedAt = firstUpdatedAt,
            LastUpdatedAt = lastUpdatedAt,
            PipelineStartedAt = pipelineStartedAt,
            RecentFailed = recentFailed,
            CrawlComplete = crawlComplete,
            CrawlTotal = crawlTotal,
        };
    }

    private async Task UpdateStatusAsync(string path, string name, string status, string? error, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
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

    /// <inheritdoc/>
    public async Task RecordMetricAsync(string name, double value, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO metrics (timestamp, name, value)
            VALUES (@ts, @name, @value);
            """;
        cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MetricPoint>> GetMetricsAsync(
        string name, int recentMinutes, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp, name, value
            FROM metrics
            WHERE name = @name
              AND timestamp >= @cutoff
            ORDER BY timestamp ASC;
            """;
        cmd.Parameters.AddWithValue("@name", name);
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-recentMinutes).ToString("O");
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        var results = new List<MetricPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new MetricPoint(
                Timestamp: DateTimeOffset.ParseExact(
                    reader.GetString(0), "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind),
                Name: reader.GetString(1),
                Value: reader.GetDouble(2)));
        }
        return results;
    }

    /// <inheritdoc/>
    public async Task InsertDoneIfNotExistsAsync(string path, string name, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO transfer_records (path, name, source_id, status, updated_at)
            VALUES (@path, @name, '', 'done', @updatedAt)
            ON CONFLICT(path, name) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ResetProcessingAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE transfer_records
            SET status='pending', updated_at=@updatedAt
            WHERE status='processing';
            """;
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetDistinctFolderPathsAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT path
            FROM transfer_records
            WHERE path IS NOT NULL AND path != ''
            ORDER BY path;
            """;
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return result;
    }

    internal static TransferStatus ParseStatus(string status) => status switch
    {
        "pending" => TransferStatus.Pending,
        "processing" => TransferStatus.Processing,
        "done" => TransferStatus.Done,
        "failed" => TransferStatus.Failed,
        "permanent_failed" => TransferStatus.PermanentFailed,
        _ => throw new ArgumentOutOfRangeException(
                 nameof(status),
                 status,
                 "未知のステータス値です。DB 破損または実装との不整合の可能性があります。"),
    };
}
