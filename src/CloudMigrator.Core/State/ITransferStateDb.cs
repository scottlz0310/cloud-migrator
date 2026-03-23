using CloudMigrator.Providers.Abstractions;

namespace CloudMigrator.Core.State;

/// <summary>
/// 転送状態管理 DB 契約。Dropbox・SharePoint どちらの移行パイプラインでも使用する。
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

    /// <summary>
    /// SharePoint Phase B クロール用: 新規アイテムを pending として UPSERT する。
    /// done / permanent_failed の既存レコードは上書きしない（1 クエリで N+1 を回避）。
    /// </summary>
    Task UpsertPendingIfNotTerminalAsync(StorageItem item, CancellationToken ct);

    /// <summary>
    /// 起動時クラッシュリカバリ: processing 状態のレコードを pending に戻す。
    /// 前回実行中にクラッシュしたアイテムを再キューイング可能な状態に戻す。
    /// </summary>
    Task ResetProcessingAsync(CancellationToken ct);

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

    /// <summary>
    /// ステータス別件数・完了バイト数などの集計サマリーを取得する。
    /// DB が空の場合はすべてゼロのサマリーを返す。
    /// </summary>
    Task<TransferDbSummary> GetSummaryAsync(CancellationToken ct);

    /// <summary>
    /// メトリクス値を記録する（metrics テーブルへ INSERT）。
    /// パイプラインが定期的に呼び出してダッシュボード向けに時系列データを蓄積する。
    /// </summary>
    Task RecordMetricAsync(string name, double value, CancellationToken ct);

    /// <summary>
    /// 直近 <paramref name="recentMinutes"/> 分以内の指定メトリクスを時系列で取得する。
    /// メトリクステーブルが空の場合は空リストを返す。
    /// </summary>
    Task<IReadOnlyList<MetricPoint>> GetMetricsAsync(string name, int recentMinutes, CancellationToken ct);

    /// <summary>
    /// skip_list マイグレーション用: path/name を done ステータスで INSERT する。
    /// レコードが既存の場合は何もしない（ON CONFLICT DO NOTHING）。
    /// </summary>
    Task InsertDoneIfNotExistsAsync(string path, string name, CancellationToken ct);

    /// <summary>
    /// DB 内の全ファイルレコードの path カラムの DISTINCT 一覧を返す。
    /// SharePoint 版 Phase C（フォルダ先行作成）でフォルダ階層を導出するために使用する。
    /// </summary>
    Task<IReadOnlyList<string>> GetDistinctFolderPathsAsync(CancellationToken ct);
}
