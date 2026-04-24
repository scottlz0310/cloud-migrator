using CloudMigrator.Providers.Abstractions;

namespace CloudMigrator.Core.State;

/// <summary>
/// DB なし起動時に使用する Null Object パターン実装。
/// すべての読み込みメソッドは空/デフォルト値を返し、書き込みは無視する。
/// </summary>
public sealed class NullTransferStateDb : ITransferStateDb
{
    public static readonly NullTransferStateDb Instance = new();

    private NullTransferStateDb() { }

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<TransferStatus?> GetStatusAsync(string path, string name, CancellationToken ct) => Task.FromResult<TransferStatus?>(null);
    public Task UpsertPendingAsync(StorageItem item, CancellationToken ct) => Task.CompletedTask;
    public Task UpsertPendingIfNotTerminalAsync(StorageItem item, CancellationToken ct) => Task.CompletedTask;
    public Task ResetProcessingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<int> ResetPermanentFailedAsync(CancellationToken ct) => Task.FromResult(0);
    public Task MarkProcessingAsync(string path, string name, CancellationToken ct) => Task.CompletedTask;
    public Task MarkDoneAsync(string path, string name, CancellationToken ct) => Task.CompletedTask;
    public Task MarkFailedAsync(string path, string name, string error, CancellationToken ct) => Task.CompletedTask;
    public Task<string?> GetCheckpointAsync(string key, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> GetLatestProcessingNameAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    public Task SaveCheckpointAsync(string key, string value, CancellationToken ct) => Task.CompletedTask;
    public IAsyncEnumerable<TransferRecord> GetPendingStreamAsync(CancellationToken ct) => AsyncEnumerable.Empty<TransferRecord>();
    public Task<TransferDbSummary> GetSummaryAsync(CancellationToken ct) => Task.FromResult(new TransferDbSummary());
    public Task RecordMetricAsync(string name, double value, CancellationToken ct) => Task.CompletedTask;
    public Task RecordMetricsBatchAsync(IEnumerable<(string Name, double Value, DateTimeOffset Timestamp)> snapshots, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<MetricPoint>> GetMetricsAsync(string name, int recentMinutes, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetricPoint>>([]);
    public Task<IReadOnlyDictionary<string, double>> GetLatestMetricsAsync(IEnumerable<string> names, int recentMinutes, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());
    public Task ResetAllAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<bool> InsertPendingIfNewAsync(StorageItem item, CancellationToken ct) => Task.FromResult(false);
    public Task InsertDoneIfNotExistsAsync(string path, string name, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<string>> GetDistinctFolderPathsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
