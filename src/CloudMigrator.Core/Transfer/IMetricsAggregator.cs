namespace CloudMigrator.Core.Transfer;

/// <summary>
/// 転送イベントを受け取り、時間窓集計 Snapshot を生成する集計コンポーネントの契約。
/// <para>
/// 三層分離原則における Aggregator 層の責務：
/// <list type="bullet">
///   <item>イベントを受け取り、インメモリでリングバッファ集計する</item>
///   <item>任意の時間窓の <see cref="MetricsSnapshot"/> を提供する</item>
///   <item>DB アクセスは一切行わない</item>
/// </list>
/// </para>
/// </summary>
public interface IMetricsAggregator
{
    /// <summary>HTTP リクエスト送信前に呼び出す。RPS カウンターに加算される。</summary>
    void NotifyRequestSent();

    /// <summary>転送成功時に呼び出す。レイテンシが平均レイテンシ計算に使用される。</summary>
    /// <param name="latency">実際の転送レイテンシ。</param>
    void NotifySuccess(TimeSpan latency);

    /// <summary>429/503 を受信した際に呼び出す。429 率カウンターに加算される。</summary>
    /// <param name="retryAfter">サーバーから返された Retry-After 値（null の場合は不明）。</param>
    void NotifyRateLimit(TimeSpan? retryAfter);

    /// <summary>
    /// 指定した時間窓内の集計 Snapshot を返す。
    /// </summary>
    /// <param name="window">集計対象の時間窓。</param>
    MetricsSnapshot GetSnapshot(TimeSpan window);
}
