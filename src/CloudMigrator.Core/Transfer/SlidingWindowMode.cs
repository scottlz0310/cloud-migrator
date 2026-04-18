namespace CloudMigrator.Core.Transfer;

/// <summary>
/// スライディングウィンドウの評価モード（#161）。
/// </summary>
public enum SlidingWindowMode
{
    /// <summary>時間ベース: 直近 N 秒のイベントのみを対象とする（デフォルト）。</summary>
    Time,

    /// <summary>件数ベース: 直近 N 件のイベントのみを対象とする。イベントレートが極端に低い環境向け。</summary>
    Count,
}
