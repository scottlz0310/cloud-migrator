namespace CloudMigrator.Core.Wizard;

/// <summary>
/// ウィザード各ステップの状態。
/// </summary>
public enum WizardStepState
{
    /// <summary>未着手。</summary>
    NotStarted,

    /// <summary>進行中（wizard-state.json には書き出さない。保存時は <see cref="NotStarted"/> に戻す）。</summary>
    InProgress,

    /// <summary>検証済み（全レイヤーの Verify が成功した状態）。</summary>
    Verified,

    /// <summary>検証失敗。</summary>
    Failed,

    /// <summary>スキップ済み。</summary>
    Skipped,
}
