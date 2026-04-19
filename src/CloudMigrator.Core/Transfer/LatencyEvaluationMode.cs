namespace CloudMigrator.Core.Transfer;

/// <summary>
/// AIMD フィードバック制御のレイテンシ悪化判定モード（#162）。
/// <para>
/// 設計書§6.1 で 3 方式を想定している。デフォルトは <see cref="Baseline"/>（長期 EMA 比較）。
/// v0.6.0 では急激な変化は主に <c>429_rate &gt; emergencyThreshold</c> で捕捉する方針のため、
/// レイテンシ判定は <c>slow_decrease</c> 用の補助信号として誤検知を抑えたい場面では
/// <see cref="Baseline"/> が適する。環境によって応答性が欲しい場合は <see cref="Recent"/>・
/// <see cref="Both"/> に切替可能とする。
/// </para>
/// </summary>
public enum LatencyEvaluationMode
{
    /// <summary>
    /// レイテンシ判定を無効化（デフォルト）。
    /// 429/503 レートのみで制御し、レイテンシ悪化による <c>SlowDecrease</c> は発動しない。
    /// </summary>
    None,

    /// <summary>
    /// ベースライン比判定。
    /// 過去の安定期 P95 を EMA で追跡し、<c>current_p95 &gt; baseline * (1 + latencyRiseRatio)</c>
    /// で悪化と判定する。起動直後は <c>baselineSamples</c> 件の成功サンプルが蓄積されるまで判定しない。
    /// </summary>
    Baseline,

    /// <summary>
    /// 直近比判定。
    /// 直近 <c>trendWindowSec</c> 秒の窓に含まれる履歴エントリ（サイクルごとのスナップショット P95）
    /// の平均値と、その前の同時間窓の P95 平均値を比較し <c>latencyRiseRatio</c> 以上の悪化で発動する。
    /// ベースライン学習を必要としないため起動直後から効くが、徐々に進行する悪化を取りこぼす可能性がある。
    /// </summary>
    Recent,

    /// <summary>
    /// 併用（OR 条件）。<see cref="Baseline"/> と <see cref="Recent"/> のどちらかが発動すれば悪化と判定する。
    /// 両方の強みを活かせるが誤検知率が上がる傾向がある。
    /// </summary>
    Both,
}
