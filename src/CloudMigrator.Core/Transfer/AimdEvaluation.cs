namespace CloudMigrator.Core.Transfer;

/// <summary>
/// AIMD フィードバック評価 1 サイクル分の結果（#162）。
/// 呼び出し側（#163 の制御ループ）は <see cref="NewRate"/> を <c>WeightedTokenBucket.SetRate</c> に、
/// <see cref="Signal"/> を並列数補助制御（§4.3）に適用する。
/// </summary>
/// <param name="Signal">評価サイクルで出力された AIMD 信号。</param>
/// <param name="PreviousRate">評価前のレート（tokens/sec）。ログ・メトリクス用。</param>
/// <param name="NewRate">評価後のレート（tokens/sec、<c>[minRate, maxRate]</c> でクランプ済み）。</param>
/// <param name="BaselineP95Ms">
/// 評価時点のベースライン P95（ms）。ベースライン未確立時は 0。
/// ベースライン比判定・メトリクス出力に使用する。
/// </param>
/// <param name="InCooldown">評価後にクールダウン中であるか。UI・メトリクスで状態表示に使用する。</param>
/// <param name="Snapshot">評価に用いたスライディングウィンドウスナップショット（監査・ログ用）。</param>
/// <param name="EvaluatedAt">評価を実施した時刻（UTC）。</param>
public sealed record AimdEvaluation(
    AimdSignal Signal,
    double PreviousRate,
    double NewRate,
    double BaselineP95Ms,
    bool InCooldown,
    SlidingWindowSnapshot Snapshot,
    DateTimeOffset EvaluatedAt);
