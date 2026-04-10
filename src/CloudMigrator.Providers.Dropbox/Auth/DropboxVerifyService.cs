using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudMigrator.Core.Credentials;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Providers.Dropbox.Auth;

/// <summary>
/// Dropbox 接続を Credential / Discovery / Preflight の 3 層で検証する <see cref="IDropboxVerifyService"/> 実装。
/// <list type="bullet">
///   <item><description>Credential: <see cref="ICredentialStore"/> でトークンの存在を確認。</description></item>
///   <item><description>Discovery: <c>/files/list_folder</c> でルートフォルダへの API 到達を確認。</description></item>
///   <item><description>Preflight: <c>/files/upload</c> で小ファイルの書き込みと削除を確認。</description></item>
/// </list>
/// </summary>
public sealed class DropboxVerifyService : IDropboxVerifyService
{
    private const string ApiBaseUrl = "https://api.dropboxapi.com/2";
    private const string ContentBaseUrl = "https://content.dropboxapi.com/2";
    private const string PreflightFileName = "/.cloudmigrator-preflight-check.tmp";

    private readonly ICredentialStore _credentialStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DropboxVerifyService> _logger;

    public DropboxVerifyService(
        ICredentialStore credentialStore,
        IHttpClientFactory httpClientFactory,
        ILogger<DropboxVerifyService> logger)
    {
        _credentialStore = credentialStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<DropboxVerifyResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<DropboxVerifyCheck>();

        // ── Credential Verify ────────────────────────────────────────────────
        var (credCheck, accessToken) = await VerifyCredentialAsync(cancellationToken).ConfigureAwait(false);
        checks.Add(credCheck);

        if (!credCheck.IsSuccess)
        {
            checks.Add(new DropboxVerifyCheck(DropboxVerifyLayer.Discovery, false, "クレデンシャル不足のためスキップ"));
            checks.Add(new DropboxVerifyCheck(DropboxVerifyLayer.Preflight, false, "クレデンシャル不足のためスキップ"));
            return new DropboxVerifyResult(false, checks);
        }

        // ── Discovery Verify ────────────────────────────────────────────────
        var discoveryCheck = await VerifyDiscoveryAsync(accessToken!, cancellationToken).ConfigureAwait(false);
        checks.Add(discoveryCheck);

        if (!discoveryCheck.IsSuccess)
        {
            checks.Add(new DropboxVerifyCheck(DropboxVerifyLayer.Preflight, false, "Discovery 失敗のためスキップ"));
            return new DropboxVerifyResult(false, checks);
        }

        // ── Preflight ────────────────────────────────────────────────────────
        var preflightCheck = await VerifyPreflightAsync(accessToken!, cancellationToken).ConfigureAwait(false);
        checks.Add(preflightCheck);

        var isSuccess = checks.All(c => c.IsSuccess);
        return new DropboxVerifyResult(isSuccess, checks);
    }

    // ── 各検証ヘルパー ────────────────────────────────────────────────────

    private async Task<(DropboxVerifyCheck Check, string? AccessToken)> VerifyCredentialAsync(CancellationToken ct)
    {
        try
        {
            var accessToken = await _credentialStore.GetAsync(CredentialKeys.DropboxAccessToken, ct).ConfigureAwait(false);
            var hasAppKey = await _credentialStore.ExistsAsync(CredentialKeys.DropboxAppKey, ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(accessToken))
            {
                return (new DropboxVerifyCheck(DropboxVerifyLayer.Credential, false,
                    "アクセストークンが見つかりません。Dropbox との連携を先に完了してください。"), null);
            }

            if (!hasAppKey)
            {
                return (new DropboxVerifyCheck(DropboxVerifyLayer.Credential, false,
                    "App Key が見つかりません。Dropbox との連携を先に完了してください。"), null);
            }

            return (new DropboxVerifyCheck(DropboxVerifyLayer.Credential, true, "クレデンシャルを確認しました。"), accessToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Credential Verify 中にエラーが発生しました。");
            return (new DropboxVerifyCheck(DropboxVerifyLayer.Credential, false, $"確認中にエラーが発生しました: {ex.Message}"), null);
        }
    }

    private async Task<DropboxVerifyCheck> VerifyDiscoveryAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient("DropboxVerify");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var payload = new { path = "", recursive = false, limit = 1 };
            using var response = await http.PostAsJsonAsync($"{ApiBaseUrl}/files/list_folder", payload, ct)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new DropboxVerifyCheck(DropboxVerifyLayer.Discovery, true, "Dropbox ルートフォルダへの到達を確認しました。");
            }

            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("Dropbox Discovery Verify 失敗: {StatusCode} {Body}", response.StatusCode, errorBody);
            return new DropboxVerifyCheck(DropboxVerifyLayer.Discovery, false,
                $"API 呼び出しに失敗しました ({(int)response.StatusCode}: {response.ReasonPhrase})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery Verify 中にエラーが発生しました。");
            return new DropboxVerifyCheck(DropboxVerifyLayer.Discovery, false, $"確認中にエラーが発生しました: {ex.Message}");
        }
    }

    private async Task<DropboxVerifyCheck> VerifyPreflightAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient("DropboxVerify");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // 小ファイルをアップロード
            var uploadArg = JsonSerializer.Serialize(new
            {
                path = PreflightFileName,
                mode = "overwrite",
                autorename = false,
                mute = true,
            });

            using var content = new StringContent("cloudmigrator-preflight\n", System.Text.Encoding.UTF8, "application/octet-stream");
            using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/upload");
            uploadRequest.Headers.Add("Dropbox-API-Arg", uploadArg);
            uploadRequest.Content = content;

            using var uploadResponse = await http.SendAsync(uploadRequest, ct).ConfigureAwait(false);
            if (!uploadResponse.IsSuccessStatusCode)
            {
                var errorBody = await uploadResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Preflight アップロード失敗: {StatusCode} {Body}", uploadResponse.StatusCode, errorBody);
                return new DropboxVerifyCheck(DropboxVerifyLayer.Preflight, false,
                    $"テストファイルのアップロードに失敗しました ({(int)uploadResponse.StatusCode})");
            }

            // テストファイルを削除
            var deletePayload = new { path = PreflightFileName };
            using var deleteResponse = await http.PostAsJsonAsync($"{ApiBaseUrl}/files/delete_v2", deletePayload, ct)
                .ConfigureAwait(false);

            if (!deleteResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Preflight 削除失敗: {StatusCode}", deleteResponse.StatusCode);
                // 削除失敗はソフトエラーとして扱う（アップロード成功が確認できれば書き込み権限あり）
                return new DropboxVerifyCheck(DropboxVerifyLayer.Preflight, true,
                    "書き込み権限を確認しました。（テストファイルの削除に失敗しました）");
            }

            return new DropboxVerifyCheck(DropboxVerifyLayer.Preflight, true, "読み書き権限を確認しました。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preflight Verify 中にエラーが発生しました。");
            return new DropboxVerifyCheck(DropboxVerifyLayer.Preflight, false, $"確認中にエラーが発生しました: {ex.Message}");
        }
    }
}
