namespace CloudMigrator.Core.Transfer;

/// <summary>
/// スライディングウィンドウ指標の集計スナップショット（#161）。
/// AIMD フィードバック制御（#162）の入力として使用する。
/// </summary>
/// <param name="SampleCount">ウィンドウ内のリクエスト件数（<c>NotifyRequestSent</c> の呼び出し回数）。</param>
/// <param name="HasMinSamples">
/// <see cref="SampleCount"/> が <c>minSamples</c> 以上か。
/// <c>false</c> の場合、統計値が不安定なため呼び出し側は判断をスキップすべき。
/// </param>
/// <param name="Rate429">
/// 429 / 503 発生率（<c>rateLimitCount / sampleCount</c>）。
/// <see cref="SampleCount"/> が 0 の場合は 0。
/// </param>
/// <param name="SuccessRate">
/// 成功率（<c>successCount / sampleCount</c>）。
/// <see cref="SampleCount"/> が 0 の場合は 0。
/// </param>
/// <param name="AvgLatencyMs">成功リクエストの平均レイテンシ（ms）。成功が 0 件なら 0。</param>
/// <param name="P95LatencyMs">
/// 成功リクエストの P95 レイテンシ（ms）。成功が 0 件なら 0。
/// AIMD のベースライン比悪化検知（§6.1）に使用する。
/// </param>
/// <param name="Timestamp">スナップショットを取得した時刻（UTC）。</param>
public sealed record SlidingWindowSnapshot(
    int SampleCount,
    bool HasMinSamples,
    double Rate429,
    double SuccessRate,
    double AvgLatencyMs,
    double P95LatencyMs,
    DateTimeOffset Timestamp);
