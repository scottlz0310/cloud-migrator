namespace CloudMigrator.Core.Transfer;

/// <summary>
/// 転送エンジンの実行結果サマリー。
/// </summary>
public sealed record TransferSummary
{
    /// <summary>転送成功件数</summary>
    public int Success { get; init; }

    /// <summary>転送失敗件数</summary>
    public int Failed { get; init; }

    /// <summary>スキップ件数（skip_list 既登録）</summary>
    public int Skipped { get; init; }

    /// <summary>転送所要時間</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>合計件数（Success + Failed + Skipped）</summary>
    public int Total => Success + Failed + Skipped;
}
