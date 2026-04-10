namespace CloudMigrator.Core.Wizard;

/// <summary>
/// ウィザードで選択する移行路線。
/// </summary>
public enum WizardRoute
{
    /// <summary>未選択。</summary>
    None,

    /// <summary>OneDrive → Dropbox 路線（v0.4.0 で対応）。</summary>
    OneDriveToDropbox,

    /// <summary>OneDrive → SharePoint 路線（v0.5.0 で対応予定）。</summary>
    OneDriveToSharePoint,
}
