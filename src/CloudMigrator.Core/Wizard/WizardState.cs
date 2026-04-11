using System.Text.Json.Serialization;

namespace CloudMigrator.Core.Wizard;

/// <summary>
/// wizard-state.json の永続化モデル。
/// </summary>
public sealed class WizardState
{
    /// <summary>スキーマバージョン（将来の互換性確認用）。</summary>
    public int SchemaVersion { get; set; } = WizardStateService.CurrentSchemaVersion;

    /// <summary>選択された移行路線。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WizardRoute SelectedRoute { get; set; } = WizardRoute.None;

    /// <summary>Step 0: 移行路線選択。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WizardStepState Step0RouteSelection { get; set; } = WizardStepState.NotStarted;

    /// <summary>Step 1: Azure Entra ID 認証設定（両路線共通）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WizardStepState Step1AzureAuth { get; set; } = WizardStepState.NotStarted;

    /// <summary>Step 2a: OneDrive Drive ID 取得（両路線共通）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WizardStepState Step2aOneDriveDiscovery { get; set; } = WizardStepState.NotStarted;

    /// <summary>Step 3: Dropbox OAuth 連携（OneDrive→Dropbox 路線）。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WizardStepState Step3DropboxOAuth { get; set; } = WizardStepState.NotStarted;

    /// <summary>Step 4: 接続テスト &amp; 完了。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WizardStepState Step4ConnectionTest { get; set; } = WizardStepState.NotStarted;

    /// <summary>ウィザードが完了しているかどうか。</summary>
    public bool IsCompleted { get; set; }

    /// <summary>
    /// <see cref="WizardStepState.InProgress"/> を <see cref="WizardStepState.NotStarted"/> に戻してから
    /// 保存用にクローンする。
    /// </summary>
    public WizardState ToSafeForPersistence()
    {
        static WizardStepState Safe(WizardStepState s) =>
            s == WizardStepState.InProgress ? WizardStepState.NotStarted : s;

        return new WizardState
        {
            // 上位バージョンで読み込んだ場合でも既知バージョンにダウングレードして保存する
            SchemaVersion = WizardStateService.CurrentSchemaVersion,
            SelectedRoute = SelectedRoute,
            Step0RouteSelection = Safe(Step0RouteSelection),
            Step1AzureAuth = Safe(Step1AzureAuth),
            Step2aOneDriveDiscovery = Safe(Step2aOneDriveDiscovery),
            Step3DropboxOAuth = Safe(Step3DropboxOAuth),
            Step4ConnectionTest = Safe(Step4ConnectionTest),
            IsCompleted = IsCompleted,
        };
    }
}
