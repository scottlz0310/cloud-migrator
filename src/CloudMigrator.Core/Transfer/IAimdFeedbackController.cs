namespace CloudMigrator.Core.Transfer;

/// <summary>
/// AIMD フィードバック制御の契約（#162）。
/// <para>
/// スライディングウィンドウ指標（<see cref="SlidingWindowSnapshot"/>）を入力に、
/// トークンバケットの補充レートを動的調整する。本インターフェースはレート計算ロジックを
/// 純粋化したもので、<c>WeightedTokenBucket.SetRate</c> への反映や制御ループの駆動は
/// 呼び出し側（#163 で実装予定）の責務とする。
/// </para>
/// </summary>
public interface IAimdFeedbackController
{
    /// <summary>現在の補充レート（tokens/sec、<c>[minRate, maxRate]</c> 範囲）。</summary>
    double CurrentRate { get; }

    /// <summary>クールダウン期間中か。<c>EmergencyDecrease</c> 発動後 <c>CooldownSec</c> 秒間 true。</summary>
    bool InCooldown { get; }

    /// <summary>現在のベースライン P95（ms）。起動直後で未確立なら 0。</summary>
    double BaselineP95Ms { get; }

    /// <summary>
    /// 1 サイクル分のフィードバック評価を実行する。
    /// 呼び出しは制御ループ（§7、#163 で実装）から周期的に行う。
    /// </summary>
    /// <param name="snapshot">スライディングウィンドウの現在指標。</param>
    /// <returns>評価結果（信号・新レート・クールダウン状態など）。</returns>
    AimdEvaluation Evaluate(SlidingWindowSnapshot snapshot);
}
