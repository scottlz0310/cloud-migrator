namespace CloudMigrator.Core.State;

/// <summary>
/// SQLite 状態 DB の集計サマリー。<see cref="ITransferStateDb.GetSummaryAsync"/> で取得する。
/// </summary>
public sealed record TransferDbSummary
{
    /// <summary>pending 件数</summary>
    public int Pending { get; init; }

    /// <summary>processing 件数</summary>
    public int Processing { get; init; }

    /// <summary>done 件数</summary>
    public int Done { get; init; }

    /// <summary>failed 件数（リトライ可能）</summary>
    public int Failed { get; init; }

    /// <summary>permanent_failed 件数（リトライ上限超過）</summary>
    public int PermanentFailed { get; init; }

    /// <summary>done レコードの合計バイト数</summary>
    public long TotalDoneSizeBytes { get; init; }

    /// <summary>DB 内の最も古い updated_at</summary>
    public DateTimeOffset? FirstUpdatedAt { get; init; }

    /// <summary>DB 内の最も新しい updated_at</summary>
    public DateTimeOffset? LastUpdatedAt { get; init; }

    /// <summary>最近の失敗レコード（最大5件、新しい順）</summary>
    public IReadOnlyList<FailedItem> RecentFailed { get; init; } = [];

    /// <summary>全レコード数（全ステータス合計）</summary>
    public int Total => Pending + Processing + Done + Failed + PermanentFailed;

    /// <summary>完了率（done / Total × 100）。Total が 0 の場合は 0.0 を返す。</summary>
    public double CompletionRate => Total == 0 ? 0.0 : (double)Done / Total * 100.0;

    /// <summary>
    /// クロール（Phase B）が完了しているかどうか。
    /// <c>true</c> のとき <see cref="CrawlTotal"/> に確定済みの全件数が入る。
    /// <c>false</c> のとき Phase B が進行中であり <see cref="Total"/> はまだ増加する可能性がある。
    /// </summary>
    public bool CrawlComplete { get; init; } = true;

    /// <summary>
    /// Phase B 完了時点でチェックポイントに保存された確定総数。
    /// <see cref="CrawlComplete"/> が <c>true</c> のときのみ有効。null の場合は未記録（前回実行の DB など）。
    /// </summary>
    public int? CrawlTotal { get; init; }
}

/// <summary>失敗したファイルの概要。</summary>
public sealed record FailedItem(string Path, string Name, string? Error);

/// <summary>metrics テーブルの 1 レコード。ダッシュボード向け時系列データ。</summary>
public sealed record MetricPoint(DateTimeOffset Timestamp, string Name, double Value);
