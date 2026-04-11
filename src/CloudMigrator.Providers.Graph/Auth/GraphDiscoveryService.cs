using CloudMigrator.Providers.Graph.Http;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;

namespace CloudMigrator.Providers.Graph.Auth;

/// <summary>
/// Graph SDK を使用して OneDrive / SharePoint のリソースを発見する
/// <see cref="IGraphDiscoveryService"/> 実装。
/// 各メソッドで GraphServiceClient を都度生成するため、認証情報がメモリに残らない。
/// </summary>
public sealed class GraphDiscoveryService : IGraphDiscoveryService
{
    /// <summary>
    /// テスト時に差し替え可能な GraphServiceClient ファクトリー。
    /// 引数は (clientId, tenantId, clientSecret)。
    /// </summary>
    internal Func<string, string, string, GraphServiceClient> ClientFactory { get; set; } = CreateClient;
    /// <inheritdoc/>
    public async Task<OneDriveDiscoveryResult> GetOneDriveDriveIdAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string userId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return new OneDriveDiscoveryResult(false, ErrorMessage: "UPN またはユーザー ID を入力してください。");

        try
        {
            var client = ClientFactory(clientId, tenantId, clientSecret);
            var drive = await client.Users[userId].Drive
                .GetAsync(cancellationToken: ct)
                .ConfigureAwait(false);

            if (drive?.Id is null)
                return new OneDriveDiscoveryResult(false, ErrorMessage: "Drive が見つかりませんでした。UPN を確認してください。");

            return new OneDriveDiscoveryResult(
                Success: true,
                DriveId: drive.Id,
                DisplayName: drive.Name ?? userId);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 403)
        {
            return new OneDriveDiscoveryResult(false,
                ErrorMessage: BuildAdminConsentError(ex.ResponseStatusCode, ex.Error?.Code));
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return new OneDriveDiscoveryResult(false,
                ErrorMessage: $"ユーザー '{userId}' が見つかりませんでした。UPN またはユーザー ID を確認してください。");
        }
        catch (ODataError ex)
        {
            return new OneDriveDiscoveryResult(false,
                ErrorMessage: $"Graph API エラー ({ex.ResponseStatusCode}): {ex.Error?.Message}");
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 403)
        {
            return new OneDriveDiscoveryResult(false,
                ErrorMessage: BuildAdminConsentError(ex.ResponseStatusCode, null));
        }
        catch (ApiException ex)
        {
            return new OneDriveDiscoveryResult(false,
                ErrorMessage: $"Graph API エラー ({ex.ResponseStatusCode})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OneDriveDiscoveryResult(false,
                ErrorMessage: $"接続エラー: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<SharePointSiteSearchResult> SearchSharePointSitesAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string keyword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return new SharePointSiteSearchResult(false, ErrorMessage: "検索キーワードを入力してください。");

        // search=* は意図的に禁止（全件返しによる負荷を防ぐ）
        if (keyword.Trim() == "*")
            return new SharePointSiteSearchResult(false, ErrorMessage: "ワイルドカード検索（*）は使用できません。キーワードを入力してください。");

        try
        {
            var client = ClientFactory(clientId, tenantId, clientSecret);
            var sitesResponse = await client.Sites
                .GetAsync(r => r.QueryParameters.Search = keyword, ct)
                .ConfigureAwait(false);

            var sites = sitesResponse?.Value?
                .Where(s => s.Id is not null)
                .Select(s => new SharePointSiteEntry(
                    SiteId: s.Id!,
                    DisplayName: s.DisplayName ?? s.Name ?? s.Id!,
                    WebUrl: s.WebUrl ?? string.Empty))
                .ToList() ?? [];

            return new SharePointSiteSearchResult(Success: true, Sites: sites);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 403)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: BuildAdminConsentError(ex.ResponseStatusCode, ex.Error?.Code));
        }
        catch (ODataError ex)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: $"Graph API エラー ({ex.ResponseStatusCode}): {ex.Error?.Message}");
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 403)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: BuildAdminConsentError(ex.ResponseStatusCode, null));
        }
        catch (ApiException ex)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: $"Graph API エラー ({ex.ResponseStatusCode})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: $"接続エラー: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<SharePointSiteSearchResult> GetSharePointSiteByUrlAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string siteUrl,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteUrl))
            return new SharePointSiteSearchResult(false, ErrorMessage: "サイト URL を入力してください。");

        try
        {
            // https://{hostname}/sites/{path} → GET /sites/{hostname}:/sites/{path}
            if (!Uri.TryCreate(siteUrl.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                return new SharePointSiteSearchResult(false,
                    ErrorMessage: "有効な HTTPS URL を入力してください（例: https://contoso.sharepoint.com/sites/MyTeam）。");
            }

            var hostname = uri.Host;
            var serverRelativePath = uri.AbsolutePath.TrimStart('/');

            var client = ClientFactory(clientId, tenantId, clientSecret);
            var site = await client.Sites[$"{hostname}:/{serverRelativePath}"]
                .GetAsync(cancellationToken: ct)
                .ConfigureAwait(false);

            if (site?.Id is null)
                return new SharePointSiteSearchResult(false, ErrorMessage: "サイトが見つかりませんでした。URL を確認してください。");

            var entry = new SharePointSiteEntry(
                SiteId: site.Id,
                DisplayName: site.DisplayName ?? site.Name ?? site.Id,
                WebUrl: site.WebUrl ?? siteUrl);

            return new SharePointSiteSearchResult(Success: true, Sites: [entry]);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 403)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: BuildAdminConsentError(ex.ResponseStatusCode, ex.Error?.Code));
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: "指定された URL のサイトが見つかりませんでした。URL を確認してください。");
        }
        catch (ODataError ex)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: $"Graph API エラー ({ex.ResponseStatusCode}): {ex.Error?.Message}");
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 403)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: BuildAdminConsentError(ex.ResponseStatusCode, null));
        }
        catch (ApiException ex)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: $"Graph API エラー ({ex.ResponseStatusCode})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SharePointSiteSearchResult(false,
                ErrorMessage: $"接続エラー: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<SharePointDriveListResult> GetSharePointDrivesAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string siteId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteId))
            return new SharePointDriveListResult(false, ErrorMessage: "サイト ID が指定されていません。");

        try
        {
            var client = ClientFactory(clientId, tenantId, clientSecret);
            var drivesResponse = await client.Sites[siteId].Drives
                .GetAsync(cancellationToken: ct)
                .ConfigureAwait(false);

            var drives = drivesResponse?.Value?
                .Where(d => d.Id is not null)
                .Select(d => new SharePointDriveEntry(
                    DriveId: d.Id!,
                    DisplayName: d.Name ?? d.Id!,
                    DriveType: d.DriveType ?? string.Empty))
                .ToList() ?? [];

            return new SharePointDriveListResult(Success: true, Drives: drives);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 403)
        {
            return new SharePointDriveListResult(false,
                ErrorMessage: BuildAdminConsentError(ex.ResponseStatusCode, ex.Error?.Code));
        }
        catch (ODataError ex)
        {
            return new SharePointDriveListResult(false,
                ErrorMessage: $"Graph API エラー ({ex.ResponseStatusCode}): {ex.Error?.Message}");
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 403)
        {
            return new SharePointDriveListResult(false,
                ErrorMessage: BuildAdminConsentError(ex.ResponseStatusCode, null));
        }
        catch (ApiException ex)
        {
            return new SharePointDriveListResult(false,
                ErrorMessage: $"Graph API エラー ({ex.ResponseStatusCode})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SharePointDriveListResult(false,
                ErrorMessage: $"接続エラー: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<DiscoveryVerifyResult> VerifyDriveAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        string driveId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(driveId))
            return new DiscoveryVerifyResult(false, "Drive ID が指定されていません。");

        try
        {
            var client = ClientFactory(clientId, tenantId, clientSecret);
            var drive = await client.Drives[driveId]
                .GetAsync(cancellationToken: ct)
                .ConfigureAwait(false);

            return drive?.Id is not null
                ? new DiscoveryVerifyResult(true)
                : new DiscoveryVerifyResult(false, "Drive が見つかりませんでした。");
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 403)
        {
            return new DiscoveryVerifyResult(false, BuildAdminConsentError(ex.ResponseStatusCode, ex.Error?.Code));
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return new DiscoveryVerifyResult(false, "指定された Drive ID が見つかりませんでした。");
        }
        catch (ODataError ex)
        {
            return new DiscoveryVerifyResult(false,
                $"Graph API エラー ({ex.ResponseStatusCode}): {ex.Error?.Message}");
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 403)
        {
            return new DiscoveryVerifyResult(false, BuildAdminConsentError(ex.ResponseStatusCode, null));
        }
        catch (ApiException ex)
        {
            return new DiscoveryVerifyResult(false,
                $"Graph API エラー ({ex.ResponseStatusCode})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DiscoveryVerifyResult(false, $"接続エラー: {ex.Message}");
        }
    }

    // ── プライベートヘルパー ────────────────────────────────────────────────

    private static GraphServiceClient CreateClient(string clientId, string tenantId, string clientSecret)
    {
        var authenticator = new GraphAuthenticator(clientId, tenantId, clientSecret);
        return Http.GraphClientFactory.Create(authenticator);
    }

    /// <summary>
    /// 403 レスポンスを受けた際の管理者同意エラーメッセージを生成する。
    /// </summary>
    /// <param name="statusCode">HTTP ステータスコード（403）。</param>
    /// <param name="errorCode">OData エラーコード（例: "Authorization_RequestDenied"）。null 可。</param>
    internal static string BuildAdminConsentError(int statusCode, string? errorCode)
    {
        return errorCode switch
        {
            "Authorization_RequestDenied" =>
                "管理者の同意が付与されていません。\n" +
                "Azure Portal の「API のアクセス許可」→「管理者の同意を与えます」で同意を付与するか、" +
                "Step 1 の「管理者同意 URL を生成」ボタンで生成した URL をテナント管理者に送付してください。",
            _ =>
                string.IsNullOrEmpty(errorCode)
                    ? "アクセスが拒否されました。管理者の同意が必要な場合があります。"
                    : $"アクセスが拒否されました ({errorCode})。管理者の同意が必要な場合があります。",
        };
    }
}
