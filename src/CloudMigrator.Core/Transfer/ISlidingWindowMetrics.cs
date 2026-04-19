namespace CloudMigrator.Core.Transfer;

/// <summary>
/// v0.6.0 AIMD フィードバック制御向けのスライディングウィンドウ指標収集契約（#161）。
/// <para>
/// 転送経路から直接イベントを受け取り、指定ウィンドウ内の集計値
/// （429 率・成功率・平均/P95 レイテンシ）を <see cref="GetSnapshot"/> で返す。
/// 時間ベース / 件数ベースの 2 モードに対応し、最低件数保証（<c>minSamples</c>）で
/// 統計が不安定な初期段階の誤判定を防ぐ。
/// </para>
/// <para>
/// 既存 <see cref="IMetricsAggregator"/>（v0.5.0・1 秒バケット）とは並行して存在する。
/// v0.6.0 AIMD ループは本インターフェースを参照し、v0.5.0 ダッシュボード系は引き続き
/// <see cref="IMetricsAggregator"/> を使用する。#163 で統合方針を最終決定する。
/// </para>
/// </summary>
public interface ISlidingWindowMetrics
{
    /// <summary>HTTP リクエスト送信前に呼び出す。サンプル件数のカウンターとなる。</summary>
    void NotifyRequestSent();

    /// <summary>転送成功時に呼び出す。レイテンシは平均 / P95 算出に使用する。</summary>
    /// <param name="latency">実測レイテンシ。</param>
    /// <param name="bytes">
    /// 転送バイト数（#159 ウィンドウスループット集計用）。
    /// バイト数を持たない呼び出し（フォルダ作成等）では 0 を渡す。
    /// </param>
    void NotifySuccess(TimeSpan latency, long bytes = 0);

    /// <summary>429 / 503（Retry-After 含む）を受信した際に呼び出す。</summary>
    /// <param name="retryAfter">サーバーから返された Retry-After（情報用、集計自体には未使用）。</param>
    void NotifyRateLimit(TimeSpan? retryAfter);

    /// <summary>
    /// 現時点のスライディングウィンドウ集計値を返す。
    /// 時間モードでは窓外のイベントが、件数モードでは超過イベントが、
    /// 本メソッド呼び出し時点で evict される（遅延評価）。
    /// </summary>
    SlidingWindowSnapshot GetSnapshot();
}
