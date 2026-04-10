using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Providers.Dropbox.Auth;

/// <summary>
/// Dropbox OAuth 2.0 PKCE フロー実装。
/// リダイレクト URI は <c>http://127.0.0.1:{port}/callback</c> の固定ポート方式を採用。
/// Dropbox App Console は redirect_uri の完全一致を要求するため、ランダムポートは使用不可。
/// ポート競合時は <see cref="CallbackPorts"/> の順でフォールバックする。
/// </summary>
public sealed class DropboxOAuthService : IDropboxOAuthService, IDisposable
{
    // Dropbox App Console に事前登録が必要なポート候補（優先順）
    internal static readonly int[] CallbackPorts = [54321, 54322, 54323, 54324, 54325];

    private const string AuthorizeEndpoint = "https://www.dropbox.com/oauth2/authorize";
    private const string TokenEndpoint = "https://api.dropboxapi.com/oauth2/token";
    private const string CallbackPath = "/callback";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<DropboxOAuthService> _logger;

    public DropboxOAuthService(ILogger<DropboxOAuthService> logger, HttpClient? httpClient = null)
    {
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    /// <inheritdoc/>
    public async Task<DropboxTokenResult> AuthorizeAsync(string appKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appKey))
            throw new ArgumentException("App Key が指定されていません。", nameof(appKey));

        // PKCE パラメータ生成
        var codeVerifier = GenerateCodeVerifier();
        var codeChallenge = GenerateCodeChallenge(codeVerifier);

        // CSRF 対策の state パラメータ
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");

        // ポート候補を順に試してリスナーを起動
        var (listener, port) = StartListener();
        var redirectUri = BuildRedirectUri(port);

        _logger.LogInformation("Dropbox OAuth リスナー起動: {RedirectUri}", redirectUri);

        var authUrl = BuildAuthorizationUrl(appKey, redirectUri, codeChallenge, state);

        // state / code_challenge 等の機微情報はログに出さず、認可エンドポイントのみ記録する
        _logger.LogInformation("ブラウザで Dropbox 認可ページを開いています: {Endpoint}", AuthorizeEndpoint);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(5));

            // コールバック待受
            var code = await WaitForCallbackAsync(listener, state, cts.Token);

            // コードをトークンに交換
            return await ExchangeCodeAsync(appKey, code, codeVerifier, redirectUri, cts.Token);
        }
        finally
        {
            try { listener.Close(); } catch { /* ベストエフォート */ }
        }
    }

    /// <inheritdoc/>
    public async Task<DropboxRefreshResult> RefreshTokenAsync(string appKey, string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appKey))
            throw new ArgumentException("App Key が指定されていません。", nameof(appKey));
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("Refresh Token が指定されていません。", nameof(refreshToken));

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = appKey,
        });

        using var response = await _httpClient.PostAsync(TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError("トークンリフレッシュ失敗: {Status}", response.StatusCode);

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new DropboxOAuthException("Refresh Token が失効しています。再認証が必要です。", isTokenExpired: true);
            }

            throw new DropboxOAuthException($"トークンリフレッシュに失敗しました: {response.StatusCode}");
        }

        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new DropboxOAuthException("トークンレスポンスのデシリアライズに失敗しました。");

        return new DropboxRefreshResult(result.AccessToken, result.ExpiresIn);
    }

    // --- 内部実装 ---

    /// <summary>
    /// ポート候補を順に試して <see cref="HttpListener"/> を起動する。
    /// すべてのポートが使用中の場合は例外をスローする。
    /// </summary>
    internal static (HttpListener Listener, int Port) StartListener()
    {
        foreach (var port in CallbackPorts)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}{CallbackPath}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException)
            {
                // ポート競合 — 次のポートを試す
                listener.Close();
            }
        }

        throw new DropboxOAuthException(
            $"OAuth コールバック用のポートを確保できませんでした。" +
            $"ポート {string.Join(", ", CallbackPorts)} がすべて使用中です。");
    }

    private static string BuildRedirectUri(int port) =>
        $"http://127.0.0.1:{port}{CallbackPath}/";

    private static string BuildAuthorizationUrl(string appKey, string redirectUri, string codeChallenge, string state)
    {
        var query = new StringBuilder("?");
        query.Append("client_id=").Append(Uri.EscapeDataString(appKey));
        query.Append("&response_type=code");
        query.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri));
        query.Append("&code_challenge=").Append(Uri.EscapeDataString(codeChallenge));
        query.Append("&code_challenge_method=S256");
        query.Append("&token_access_type=offline");
        query.Append("&state=").Append(Uri.EscapeDataString(state));
        return AuthorizeEndpoint + query;
    }

    private static async Task<string> WaitForCallbackAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var query = context.Request.QueryString;

        var (code, errorMessage) = ParseCallbackQuery(query, expectedState);
        if (errorMessage is not null)
        {
            SendHtmlResponse(context.Response, 400,
                $"<h1>エラー</h1><p>{System.Net.WebUtility.HtmlEncode(errorMessage)}</p><p>このウィンドウを閉じてください。</p>");
            throw new DropboxOAuthException(errorMessage);
        }

        SendHtmlResponse(context.Response, 200,
            "<h1>認証完了</h1><p>Dropbox との連携が完了しました。このウィンドウを閉じてください。</p>");

        return code!;
    }

    /// <summary>
    /// コールバッククエリを検証し code を返す純関数。
    /// エラー時は <c>(null, errorMessage)</c>、正常時は <c>(code, null)</c> を返す。
    /// </summary>
    internal static (string? Code, string? ErrorMessage) ParseCallbackQuery(
        System.Collections.Specialized.NameValueCollection query,
        string expectedState)
    {
        var error = query["error"];
        if (!string.IsNullOrEmpty(error))
        {
            var desc = query["error_description"] ?? error;
            return (null, $"Dropbox 認可エラー: {desc}");
        }

        var returnedState = query["state"] ?? string.Empty;
        if (!string.Equals(returnedState, expectedState, StringComparison.Ordinal))
            return (null, "state パラメータが一致しません。CSRF 攻撃の可能性があります。");

        var code = query["code"];
        if (string.IsNullOrEmpty(code))
            return (null, "認可コードがコールバックに含まれていません。");

        return (code, null);
    }

    private async Task<DropboxTokenResult> ExchangeCodeAsync(
        string appKey,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["client_id"] = appKey,
        });

        using var response = await _httpClient.PostAsync(TokenEndpoint, form, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError("PKCE トークン交換失敗: {Status}", response.StatusCode);
            throw new DropboxOAuthException($"PKCE トークン交換に失敗しました: {response.StatusCode}");
        }

        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new DropboxOAuthException("トークンレスポンスのデシリアライズに失敗しました。");

        return new DropboxTokenResult(result.AccessToken, result.RefreshToken, result.ExpiresIn);
    }

    private static void SendHtmlResponse(HttpListenerResponse response, int statusCode, string body)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes($"<!DOCTYPE html><html><body>{body}</body></html>");
        response.ContentLength64 = bytes.Length;
        try
        {
            response.OutputStream.Write(bytes);
            response.OutputStream.Flush();
        }
        finally
        {
            response.OutputStream.Close();
        }
    }

    // --- PKCE ユーティリティ ---

    /// <summary>
    /// RFC 7636 準拠の code_verifier を生成する（43〜128 文字、Base64URL エンコード）。
    /// </summary>
    internal static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    /// <summary>
    /// S256 アルゴリズムで code_challenge を生成する。
    /// SHA-256(code_verifier) を Base64URL エンコードした値。
    /// </summary>
    internal static string GenerateCodeChallenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash)
            .Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    // --- JSON デシリアライズ用内部型 ---

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }
}
