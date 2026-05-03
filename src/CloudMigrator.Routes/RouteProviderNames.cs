namespace CloudMigrator.Routes;

/// <summary>
/// ルートプロバイダー識別子の定数。<see cref="Core.Configuration.MigratorOptions.DestinationProvider"/> の値と対応する。
/// ストレージプロバイダー固有の <c>ProviderId</c>（例: "graph"）とは別の識別子空間である点に注意。
/// </summary>
public static class RouteProviderNames
{
    /// <summary>SharePoint Online プロバイダー識別子。</summary>
    public const string SharePoint = "sharepoint";

    /// <summary>Dropbox プロバイダー識別子。</summary>
    public const string Dropbox = "dropbox";
}
