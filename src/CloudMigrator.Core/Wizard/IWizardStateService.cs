namespace CloudMigrator.Core.Wizard;

/// <summary>
/// wizard-state.json の読み書きサービス抽象。
/// </summary>
public interface IWizardStateService
{
    /// <summary>
    /// 現在のウィザード状態を返す。
    /// ファイルが存在しない場合は初期状態を返す。
    /// </summary>
    Task<WizardState> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// ウィザード状態を永続化する。
    /// <see cref="WizardStepState.InProgress"/> は <see cref="WizardStepState.NotStarted"/> に戻してから保存する。
    /// </summary>
    Task SaveAsync(WizardState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// ウィザード状態をすべてリセットする（「セットアップをやり直す」用）。
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 初回起動かどうかを判定する。
    /// wizard-state.json が不在の場合に true を返す。
    /// </summary>
    Task<bool> IsFirstRunAsync(CancellationToken cancellationToken = default);
}
