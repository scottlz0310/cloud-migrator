namespace CloudMigrator.Providers.Graph;

/// <summary>
/// GraphStorageProvider に必要な識別子設定。
/// MigratorOptions.Graph からマップして渡す。
/// </summary>
public sealed class GraphStorageOptions
{
    /// <summary>OneDrive ユーザー ID または UPN</summary>
    public string OneDriveUserId { get; init; } = string.Empty;

    /// <summary>SharePoint ドキュメントライブラリ ID</summary>
    public string SharePointDriveId { get; init; } = string.Empty;
}
