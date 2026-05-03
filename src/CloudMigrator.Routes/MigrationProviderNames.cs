namespace CloudMigrator.Routes;

/// <summary>
/// プロバイダー識別子の定数。<see cref="Core.Configuration.MigratorOptions.DestinationProvider"/> の値と対応する。
/// </summary>
public static class MigrationProviderNames
{
    /// <summary>SharePoint Online プロバイダー識別子。</summary>
    public const string SharePoint = "sharepoint";

    /// <summary>Dropbox プロバイダー識別子。</summary>
    public const string Dropbox = "dropbox";
}
