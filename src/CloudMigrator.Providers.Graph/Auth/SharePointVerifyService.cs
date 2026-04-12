using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Credentials;
using CloudMigrator.Providers.Graph.Http;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Providers.Graph.Auth;

/// <summary>
/// SharePoint 接続を Credential / Discovery / Preflight の 3 層で検証する
/// <see cref="ISharePointVerifyService"/> 実装。
/// <list type="bullet">
///   <item><description>Credential: Azure クレデンシャルの存在確認 + App-only 認証疎通確認。</description></item>
///   <item><description>Discovery: <c>GET /drives/{driveId}</c> で SharePoint Drive への到達確認。</description></item>
///   <item><description>Preflight: テストファイルの書き込み・削除による読み書き権限確認。</description></item>
/// </list>
/// </summary>
public sealed class SharePointVerifyService : ISharePointVerifyService
{
    private static readonly string PreflightFileName =
        $".cloudmigrator-preflight-check-{Guid.NewGuid():N}.tmp";
    private const string PreflightContent = "cloudmigrator-preflight\n";

    private readonly ICredentialStore _credentialStore;
    private readonly IConfigurationService _configurationService;
    private readonly IAzureAuthVerifyService _azureAuthVerifyService;
    private readonly IGraphDiscoveryService _discoveryService;
    private readonly ILogger<SharePointVerifyService> _logger;

    public SharePointVerifyService(
        ICredentialStore credentialStore,
        IConfigurationService configurationService,
        IAzureAuthVerifyService azureAuthVerifyService,
        IGraphDiscoveryService discoveryService,
        ILogger<SharePointVerifyService> logger)
    {
        _credentialStore = credentialStore;
        _configurationService = configurationService;
        _azureAuthVerifyService = azureAuthVerifyService;
        _discoveryService = discoveryService;
        _logger = logger;
    }

    /// <summary>テスト時に差し替え可能な GraphServiceClient ファクトリー。</summary>
    internal Func<string, string, string, GraphServiceClient> ClientFactory { get; set; } =
        (clientId, tenantId, clientSecret) =>
        {
            var authenticator = new GraphAuthenticator(clientId, tenantId, clientSecret);
            return Http.GraphClientFactory.Create(authenticator);
        };

    /// <inheritdoc/>
    public async Task<SharePointVerifyResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<SharePointVerifyCheck>();

        // ── Credential Verify ────────────────────────────────────────────────
        var (credCheck, clientId, tenantId, clientSecret) =
            await VerifyCredentialAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(credCheck);

        if (!credCheck.IsSuccess)
        {
            checks.Add(new SharePointVerifyCheck(SharePointVerifyLayer.Discovery, false, "クレデンシャル不足のためスキップ"));
            checks.Add(new SharePointVerifyCheck(SharePointVerifyLayer.Preflight, false, "クレデンシャル不足のためスキップ"));
            return new SharePointVerifyResult(false, checks);
        }

        // ── Discovery Verify ────────────────────────────────────────────────
        var (discoveryCheck, driveId) =
            await VerifyDiscoveryAsync(clientId!, tenantId!, clientSecret!, cancellationToken)
                .ConfigureAwait(false);
        checks.Add(discoveryCheck);

        if (!discoveryCheck.IsSuccess)
        {
            checks.Add(new SharePointVerifyCheck(SharePointVerifyLayer.Preflight, false, "Discovery 失敗のためスキップ"));
            return new SharePointVerifyResult(false, checks);
        }

        // ── Preflight ────────────────────────────────────────────────────────
        var preflightCheck = await VerifyPreflightAsync(clientId!, tenantId!, clientSecret!, driveId!, cancellationToken)
            .ConfigureAwait(false);
        checks.Add(preflightCheck);

        var isSuccess = checks.All(c => c.IsSuccess);
        return new SharePointVerifyResult(isSuccess, checks);
    }

    // ── 各検証ヘルパー ────────────────────────────────────────────────────

    private async Task<(SharePointVerifyCheck Check, string? ClientId, string? TenantId, string? ClientSecret)>
        VerifyCredentialAsync(CancellationToken ct)
    {
        try
        {
            // ClientId / TenantId は config.json から取得（secret 以外は Credential Manager には保存しない）
            var graphConfig = await _configurationService.GetGraphConfigAsync(ct).ConfigureAwait(false);
            var clientId = graphConfig.ClientId;
            var tenantId = graphConfig.TenantId;
            var clientSecret = await _credentialStore.GetAsync(CredentialKeys.AzureClientSecret, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(tenantId))
            {
                return (new SharePointVerifyCheck(SharePointVerifyLayer.Credential, false,
                    "Azure 認証情報（ClientId / TenantId）が保存されていません。Step 1 に戻って再設定してください。"),
                    null, null, null);
            }

            if (string.IsNullOrWhiteSpace(clientSecret))
            {
                return (new SharePointVerifyCheck(SharePointVerifyLayer.Credential, false,
                    "クライアントシークレットが見つかりません。Step 1 に戻って再設定してください。"),
                    null, null, null);
            }

            // App-only 認証の疎通確認
            var authResult = await _azureAuthVerifyService
                .VerifyAsync(clientId, tenantId, clientSecret, ct)
                .ConfigureAwait(false);

            if (!authResult.IsSuccess)
            {
                return (new SharePointVerifyCheck(SharePointVerifyLayer.Credential, false,
                    authResult.ErrorMessage ?? "App-only 認証に失敗しました。"),
                    null, null, null);
            }

            return (new SharePointVerifyCheck(SharePointVerifyLayer.Credential, true, "クレデンシャルと認証を確認しました。"),
                clientId, tenantId, clientSecret);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Credential Verify 中にエラーが発生しました。");
            return (new SharePointVerifyCheck(SharePointVerifyLayer.Credential, false,
                $"確認中にエラーが発生しました: {ex.Message}"),
                null, null, null);
        }
    }

    private async Task<(SharePointVerifyCheck Check, string? DriveId)> VerifyDiscoveryAsync(
        string clientId, string tenantId, string clientSecret, CancellationToken ct)
    {
        try
        {
            var discoveryConfig = await _configurationService.GetDiscoveryConfigAsync(ct).ConfigureAwait(false);
            var driveId = discoveryConfig.SharePointDriveId;

            if (string.IsNullOrWhiteSpace(driveId))
            {
                return (new SharePointVerifyCheck(SharePointVerifyLayer.Discovery, false,
                    "SharePoint Drive ID が保存されていません。Step 2b に戻って再設定してください。"),
                    null);
            }

            var verifyResult = await _discoveryService
                .VerifyDriveAsync(clientId, tenantId, clientSecret, driveId, ct)
                .ConfigureAwait(false);

            if (!verifyResult.Success)
            {
                return (new SharePointVerifyCheck(SharePointVerifyLayer.Discovery, false,
                    verifyResult.ErrorMessage ?? "SharePoint Drive への到達確認に失敗しました。"),
                    null);
            }

            return (new SharePointVerifyCheck(SharePointVerifyLayer.Discovery, true,
                "SharePoint Drive への到達を確認しました。"), driveId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery Verify 中にエラーが発生しました。");
            return (new SharePointVerifyCheck(SharePointVerifyLayer.Discovery, false,
                $"確認中にエラーが発生しました: {ex.Message}"),
                null);
        }
    }

    private async Task<SharePointVerifyCheck> VerifyPreflightAsync(
        string clientId, string tenantId, string clientSecret, string driveId, CancellationToken ct)
    {
        try
        {
            var graphClient = ClientFactory(clientId, tenantId, clientSecret);

            // テストファイルをアップロード
            var contentBytes = System.Text.Encoding.UTF8.GetBytes(PreflightContent);
            using var contentStream = new MemoryStream(contentBytes);

            var uploadedItem = await graphClient.Drives[driveId]
                .Root
                .ItemWithPath(PreflightFileName)
                .Content
                .PutAsync(contentStream, cancellationToken: ct)
                .ConfigureAwait(false);

            if (uploadedItem?.Id is null)
            {
                return new SharePointVerifyCheck(SharePointVerifyLayer.Preflight, false,
                    "テストファイルのアップロードに失敗しました。書き込み権限を確認してください。");
            }

            // テストファイルを削除
            try
            {
                await graphClient.Drives[driveId]
                    .Items[uploadedItem.Id]
                    .DeleteAsync(cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            catch (Exception deleteEx)
            {
                _logger.LogWarning(deleteEx, "Preflight テストファイルの削除に失敗しました。driveId={DriveId}", driveId);
                // 削除失敗はソフトエラー（アップロード成功で書き込み権限確認済み）
                return new SharePointVerifyCheck(SharePointVerifyLayer.Preflight, true,
                    "書き込み権限を確認しました。（テストファイルの削除に失敗しました）");
            }

            return new SharePointVerifyCheck(SharePointVerifyLayer.Preflight, true, "読み書き権限を確認しました。");
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 403)
        {
            return new SharePointVerifyCheck(SharePointVerifyLayer.Preflight, false,
                "書き込み権限がありません。Azure アプリの権限設定を確認してください。");
        }
        catch (ODataError ex)
        {
            _logger.LogError(ex, "Preflight Verify 中に Graph API エラーが発生しました。driveId={DriveId}", driveId);
            return new SharePointVerifyCheck(SharePointVerifyLayer.Preflight, false,
                $"Graph API エラー ({ex.ResponseStatusCode}): {ex.Error?.Message}");
        }
        catch (ApiException ex)
        {
            _logger.LogError(ex, "Preflight Verify 中に API エラーが発生しました。driveId={DriveId}", driveId);
            return new SharePointVerifyCheck(SharePointVerifyLayer.Preflight, false,
                $"API エラー ({ex.ResponseStatusCode})");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preflight Verify 中にエラーが発生しました。driveId={DriveId}", driveId);
            return new SharePointVerifyCheck(SharePointVerifyLayer.Preflight, false,
                $"確認中にエラーが発生しました: {ex.Message}");
        }
    }
}
