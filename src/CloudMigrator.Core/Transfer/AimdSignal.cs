namespace CloudMigrator.Core.Transfer;

/// <summary>
/// AIMD フィードバック制御の判定信号（#162）。
/// <para>
/// スライディングウィンドウ指標を評価した結果として出力され、トークンバケットの
/// レート調整（§6）と並列数補助制御の調整（§4.3、#163 で統合）の両方に使用される。
/// </para>
/// </summary>
public enum AimdSignal
{
    /// <summary>判定対象外（最低サンプル未満・クールダウン中の stable 抑制など）。レート変更なし。</summary>
    Hold,

    /// <summary>429 率が <c>emergencyThreshold</c> を超えた。レートを <c>emergencyDecay</c> 倍に急減速しクールダウンに入る。</summary>
    EmergencyDecrease,

    /// <summary>レイテンシ悪化を検知。レートを <c>slowDecay</c> 倍に緩減速する。</summary>
    SlowDecrease,

    /// <summary>一定期間 429 なし・レイテンシ悪化なし・クールダウン外。レートを <c>addStep</c> だけ緩増加する。</summary>
    Stable,
}
