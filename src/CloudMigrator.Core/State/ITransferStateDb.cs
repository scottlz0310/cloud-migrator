using CloudMigrator.Providers.Abstractions;

namespace CloudMigrator.Core.State;

/// <summary>
/// Dropbox 移行の転送状態管理 DB 契約。
/// </summary>
public interface ITransferStateDb : IAsyncDisposable
{
    /// <summary>スキーマを作成する（初回起動時）。</summary>
    Task InitializeAsync(CancellationToken ct);

    /// <summary>
    /// レコードが存在しない場合は null を返す。
    /// null → <see cref="UpsertPendingAsync"/> で INSERT する設計フローと整合。
    /// </summary>
    Task<TransferStatus?> GetStatusAsync(string path, string name, CancellationToken ct);

    /// <summary>
    /// 新規アイテムを pending として UPSERT する。
    /// 既存レコードは retry_count=0 にリセットして pending に戻す。
    /// </summary>
    Task UpsertPendingAsync(StorageItem item, CancellationToken ct);

    /// <summary>status を processing に変更する（アップロード開始前に呼び出す）。</summary>
    Task MarkProcessingAsync(string path, string name, CancellationToken ct);

    /// <summary>status を done に変更する。</summary>
    Task MarkDoneAsync(string path, string name, CancellationToken ct);

    /// <summary>
    /// retry_count を +1 し、status を failed に変更する。
    /// retry_count が <see cref="SqliteTransferStateDb.MaxRetry"/> 以上の場合は permanent_failed に遷移する。
    /// </summary>
    Task MarkFailedAsync(string path, string name, string error, CancellationToken ct);

    /// <summary>チェックポイント値を取得する（例: Dropbox cursor）。存在しない場合は null。</summary>
    Task<string?> GetCheckpointAsync(string key, CancellationToken ct);

    /// <summary>チェックポイント値を保存する（UPSERT）。</summary>
    Task SaveCheckpointAsync(string key, string value, CancellationToken ct);

    /// <summary>
    /// pending / processing / failed レコードをストリーミングで返す。
    /// 大量件数でもメモリ爆発しない設計。クラッシュリカバリ時に使用する。
    /// </summary>
    IAsyncEnumerable<TransferRecord> GetPendingStreamAsync(CancellationToken ct);
}
