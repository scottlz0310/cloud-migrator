namespace CloudMigrator.Providers.Graph.Auth;

/// <summary>
/// OneDrive Drive ID および SharePoint Site / Drive の発見を行うサービス契約。
/// App-only 認証（Client Credentials フロー）前提。
/// </summary>
public interface IGraphDiscoveryService
{
    /// <summary>
    /// UPN またはユーザー ID から Personal OneDrive の Drive ID を取得する。
    /// App-only 認証では /me/drive が使用できないため UPN 必須。
    /// </summary>
    Task<OneDriveDiscoveryResult> GetOneDriveDriveIdAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string userId,
        CancellationToken ct = default);

    /// <summary>
    /// キーワードで SharePoint サイトを検索する（<c>GET /sites?search={keyword}</c>）。
    /// </summary>
    Task<SharePointSiteSearchResult> SearchSharePointSitesAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string keyword,
        CancellationToken ct = default);

    /// <summary>
    /// Site URL を直接指定してサイトを取得する（<c>GET /sites/{hostname}:/{serverRelativePath}</c>）。
    /// キーワード検索で 0 件の場合のフォールバック用。
    /// </summary>
    Task<SharePointSiteSearchResult> GetSharePointSiteByUrlAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string siteUrl,
        CancellationToken ct = default);

    /// <summary>
    /// 指定サイト内の Drive（Document Library）一覧を取得する（<c>GET /sites/{siteId}/drives</c>）。
    /// </summary>
    Task<SharePointDriveListResult> GetSharePointDrivesAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string siteId,
        CancellationToken ct = default);

    /// <summary>
    /// Drive ID の疎通確認を行う（<c>GET /drives/{driveId}</c>）。
    /// </summary>
    Task<DiscoveryVerifyResult> VerifyDriveAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string driveId,
        CancellationToken ct = default);
}

/// <summary>OneDrive Drive ID 取得結果。</summary>
public sealed record OneDriveDiscoveryResult(
    bool Success,
    string? DriveId = null,
    string? DisplayName = null,
    string? ErrorMessage = null);

/// <summary>SharePoint サイト候補エントリ。</summary>
public sealed record SharePointSiteEntry(
    string SiteId,
    string DisplayName,
    string WebUrl);

/// <summary>SharePoint サイト検索結果。</summary>
public sealed record SharePointSiteSearchResult(
    bool Success,
    IReadOnlyList<SharePointSiteEntry>? Sites = null,
    string? ErrorMessage = null);

/// <summary>SharePoint Document Library（Drive）候補エントリ。</summary>
public sealed record SharePointDriveEntry(
    string DriveId,
    string DisplayName,
    string DriveType);

/// <summary>SharePoint Drive 一覧取得結果。</summary>
public sealed record SharePointDriveListResult(
    bool Success,
    IReadOnlyList<SharePointDriveEntry>? Drives = null,
    string? ErrorMessage = null);

/// <summary>Discovery Verify 結果。</summary>
public sealed record DiscoveryVerifyResult(
    bool Success,
    string? ErrorMessage = null);
