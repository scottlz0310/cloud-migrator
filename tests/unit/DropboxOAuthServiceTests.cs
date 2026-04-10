using System.Collections.Specialized;
using System.Net;
using System.Web;
using CloudMigrator.Core.Credentials;
using CloudMigrator.Providers.Dropbox;
using CloudMigrator.Providers.Dropbox.Auth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// DropboxOAuthService および DropboxStorageProvider (Credential Store パス) のユニットテスト。
/// </summary>
public class DropboxOAuthServiceTests
{
    // ─── DropboxOAuthService.RefreshTokenAsync ────────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_ShouldReturnNewAccessToken_WhenSuccessful()
    {
        // 検証対象: RefreshTokenAsync  目的: 正常系でアクセストークンが返ること
        var handler = new StubTokenHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"new-token","expires_in":14400}""")
        });
        using var httpClient = new HttpClient(handler);
        var service = new DropboxOAuthService(NullLogger<DropboxOAuthService>.Instance, httpClient);

        var result = await service.RefreshTokenAsync("my-app-key", "my-refresh-token");

        result.AccessToken.Should().Be("new-token");
        result.ExpiresIn.Should().Be(14400);
    }

    [Fact]
    public async Task RefreshTokenAsync_ShouldThrow_WhenTokenExpired_On401()
    {
        // 検証対象: RefreshTokenAsync  目的: 401 (Unauthorized) で IsTokenExpired=true の例外がスローされること
        var handler = new StubTokenHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"invalid_grant","error_description":"refresh token is invalid"}""")
        });
        using var httpClient = new HttpClient(handler);
        var service = new DropboxOAuthService(NullLogger<DropboxOAuthService>.Instance, httpClient);

        var act = async () => await service.RefreshTokenAsync("my-app-key", "expired-refresh-token");

        await act.Should().ThrowAsync<DropboxOAuthException>()
            .Where(ex => ex.IsTokenExpired);
    }

    [Fact]
    public async Task RefreshTokenAsync_ShouldThrow_WhenTokenExpired_On403()
    {
        // 検証対象: RefreshTokenAsync  目的: 403 (Forbidden) で IsTokenExpired=true の例外がスローされること
        var handler = new StubTokenHandler(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"error":"token_revoked"}""")
        });
        using var httpClient = new HttpClient(handler);
        var service = new DropboxOAuthService(NullLogger<DropboxOAuthService>.Instance, httpClient);

        var act = async () => await service.RefreshTokenAsync("my-app-key", "revoked-token");

        await act.Should().ThrowAsync<DropboxOAuthException>()
            .Where(ex => ex.IsTokenExpired);
    }

    [Fact]
    public async Task RefreshTokenAsync_ShouldThrow_OnNonAuthFailure()
    {
        // 検証対象: RefreshTokenAsync  目的: 500 等の認証以外の失敗では IsTokenExpired=false の例外がスローされること
        var handler = new StubTokenHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("server error")
        });
        using var httpClient = new HttpClient(handler);
        var service = new DropboxOAuthService(NullLogger<DropboxOAuthService>.Instance, httpClient);

        var act = async () => await service.RefreshTokenAsync("my-app-key", "valid-refresh-token");

        await act.Should().ThrowAsync<DropboxOAuthException>()
            .Where(ex => !ex.IsTokenExpired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task RefreshTokenAsync_ShouldThrow_WhenAppKeyIsNullOrEmpty(string appKey)
    {
        // 検証対象: RefreshTokenAsync  目的: App Key が空の場合は ArgumentException がスローされること
        var service = new DropboxOAuthService(NullLogger<DropboxOAuthService>.Instance);

        var act = async () => await service.RefreshTokenAsync(appKey, "some-refresh-token");

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*App Key*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task RefreshTokenAsync_ShouldThrow_WhenRefreshTokenIsNullOrEmpty(string refreshToken)
    {
        // 検証対象: RefreshTokenAsync  目的: Refresh Token が空の場合は ArgumentException がスローされること
        var service = new DropboxOAuthService(NullLogger<DropboxOAuthService>.Instance);

        var act = async () => await service.RefreshTokenAsync("some-app-key", refreshToken);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Refresh Token*");
    }

    // ─── DropboxOAuthException ────────────────────────────────────────────────

    [Fact]
    public void DropboxOAuthException_ErrorCode_Constructor_SetsIsTokenExpired_ForKnownCodes()
    {
        // 検証対象: DropboxOAuthException  目的: 既知の失効エラーコードで IsTokenExpired=true がセットされること
        foreach (var code in new[] { "token_expired", "token_revoked", "token_not_found", "invalid_grant", "expired_access_token" })
        {
            var ex = new DropboxOAuthException("msg", code);
            ex.IsTokenExpired.Should().BeTrue(because: $"ErrorCode={code}");
            ex.ErrorCode.Should().Be(code);
        }
    }

    [Fact]
    public void DropboxOAuthException_ErrorCode_Constructor_SetsIsTokenExpiredFalse_ForUnknownCode()
    {
        // 検証対象: DropboxOAuthException  目的: 不明なエラーコードでは IsTokenExpired=false であること
        var ex = new DropboxOAuthException("msg", "server_error");
        ex.IsTokenExpired.Should().BeFalse();
        ex.ErrorCode.Should().Be("server_error");
    }

    // ─── DropboxStorageProvider (Credential Store パス) ───────────────────────

    [Fact]
    public async Task CredentialStore_Constructor_ShouldLoadTokenFromStore_OnFirstApiCall()
    {
        // 検証対象: EnsureAccessTokenAsync  目的: Credential Store にトークンがある場合、初回 API 呼び出し前に自動ロードされること
        var credStoreMock = new Mock<ICredentialStore>();
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxAccessToken))
            .ReturnsAsync("stored-access-token");

        var oAuthMock = new Mock<IDropboxOAuthService>();

        var handler = new StubTokenHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"entries":[],"cursor":"cur","has_more":false}""")
        });
        using var httpClient = new HttpClient(handler);

        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            credStoreMock.Object,
            oAuthMock.Object,
            httpClient: httpClient);

        await provider.ListPagedAsync("dropbox", null);

        // ストアから取得が呼ばれること
        credStoreMock.Verify(s => s.GetAsync(CredentialKeys.DropboxAccessToken), Times.AtLeastOnce);
        // OAuthService のリフレッシュは呼ばれないこと
        oAuthMock.Verify(s => s.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CredentialStore_Constructor_ShouldRefreshAndSave_WhenStoredTokenEmpty()
    {
        // 検証対象: RefreshAccessTokenAsync (oAuthService パス)  目的: ストアにトークンがない場合、oAuthService でリフレッシュしてストアに保存すること
        var credStoreMock = new Mock<ICredentialStore>();
        // アクセストークンは空
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxAccessToken))
            .ReturnsAsync((string?)null);
        // App Key と Refresh Token は存在する
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxAppKey))
            .ReturnsAsync("my-app-key");
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxRefreshToken))
            .ReturnsAsync("saved-refresh-token");

        var oAuthMock = new Mock<IDropboxOAuthService>();
        oAuthMock.Setup(s => s.RefreshTokenAsync("my-app-key", "saved-refresh-token", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DropboxRefreshResult("refreshed-access-token", 14400));

        var handler = new StubTokenHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"entries":[],"cursor":"cur","has_more":false}""")
        });
        using var httpClient = new HttpClient(handler);

        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            credStoreMock.Object,
            oAuthMock.Object,
            httpClient: httpClient);

        await provider.ListPagedAsync("dropbox", null);

        // リフレッシュが呼ばれること
        oAuthMock.Verify(s => s.RefreshTokenAsync("my-app-key", "saved-refresh-token", It.IsAny<CancellationToken>()), Times.Once);
        // 新しいトークンがストアに保存されること
        credStoreMock.Verify(s => s.SaveAsync(CredentialKeys.DropboxAccessToken, "refreshed-access-token"), Times.Once);
    }

    [Fact]
    public async Task CredentialStore_Constructor_ShouldDeleteTokensAndRethrow_WhenRefreshTokenExpired()
    {
        // 検証対象: RefreshAccessTokenAsync (oAuthService パス)  目的: リフレッシュトークンも失効時はストアを削除して再認証例外をスローすること
        var credStoreMock = new Mock<ICredentialStore>();
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxAccessToken))
            .ReturnsAsync((string?)null);
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxAppKey))
            .ReturnsAsync("my-app-key");
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxRefreshToken))
            .ReturnsAsync("expired-refresh-token");

        var oAuthMock = new Mock<IDropboxOAuthService>();
        oAuthMock.Setup(s => s.RefreshTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DropboxOAuthException("Refresh Token が失効しています。", isTokenExpired: true));

        using var httpClient = new HttpClient(new StubTokenHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        }));

        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            credStoreMock.Object,
            oAuthMock.Object,
            httpClient: httpClient);

        var act = async () => await provider.ListPagedAsync("dropbox", null);

        await act.Should().ThrowAsync<DropboxOAuthException>()
            .Where(ex => ex.ErrorCode == "token_expired");

        // アクセストークンとリフレッシュトークンがストアから削除されること
        credStoreMock.Verify(s => s.DeleteAsync(CredentialKeys.DropboxAccessToken), Times.Once);
        credStoreMock.Verify(s => s.DeleteAsync(CredentialKeys.DropboxRefreshToken), Times.Once);
    }

    [Fact]
    public async Task CredentialStore_Constructor_ShouldThrow_WhenNoTokenAndNoRefreshCapability()
    {
        // 検証対象: EnsureAccessTokenAsync + RefreshAccessTokenAsync  目的: ストアにトークンも RefreshToken もない場合、ErrorCode="token_not_found" の DropboxOAuthException がスローされること
        var credStoreMock = new Mock<ICredentialStore>();
        // アクセストークンは空
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxAccessToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        // App Key は存在するが RefreshToken はない
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxAppKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync("app-key");
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxRefreshToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var oAuthMock = new Mock<IDropboxOAuthService>();

        using var httpClient = new HttpClient(new StubTokenHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        }));

        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            credStoreMock.Object,
            oAuthMock.Object,
            httpClient: httpClient);

        var act = async () => await provider.ListPagedAsync("dropbox", null);

        // RefreshToken が取得できないため ErrorCode="token_not_found" の DropboxOAuthException がスローされること
        await act.Should().ThrowAsync<DropboxOAuthException>()
            .Where(ex => ex.ErrorCode == "token_not_found");
    }

    // ─── スタブ ───────────────────────────────────────────────────────────────

    private sealed class StubTokenHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubTokenHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }

    // ─── 純関数テスト ─────────────────────────────────────────────────────────

    // ── BuildRedirectUri ── 

    [Theory]
    [InlineData(54321)]
    [InlineData(54325)]
    public void BuildRedirectUri_ShouldHaveTrailingSlash(int port)
    {
        // 検証対象: BuildRedirectUri  目的: HttpListener.Prefix と完全一致するよう末尾スラッシュが付くこと
        var uri = DropboxOAuthServiceTestHelper.BuildRedirectUri(port);

        uri.Should().EndWith("/");
        uri.Should().StartWith($"http://127.0.0.1:{port}/callback");
    }

    [Fact]
    public void BuildRedirectUri_ShouldMatchListenerPrefix()
    {
        // 検証対象: BuildRedirectUri vs StartListener  目的: redirect_uri と HttpListener.Prefix が一致すること
        const int port = 54321;
        var redirectUri = DropboxOAuthServiceTestHelper.BuildRedirectUri(port);
        // HttpListener の Prefix 形式に変換して比較（ホスト名は "localhost" ではなく "127.0.0.1" 固定）
        redirectUri.Should().Be($"http://127.0.0.1:{port}/callback/");
    }

    // ── BuildAuthorizationUrl ──

    [Fact]
    public void BuildAuthorizationUrl_ShouldContainRequiredParameters()
    {
        // 検証対象: BuildAuthorizationUrl  目的: OAuth 2.0 PKCE に必要なパラメータが全て含まれること
        var url = DropboxOAuthServiceTestHelper.BuildAuthorizationUrl(
            "my-app-key",
            "http://127.0.0.1:54321/callback/",
            "challenge123",
            "state456");

        var query = HttpUtility.ParseQueryString(new Uri(url).Query);
        query["client_id"].Should().Be("my-app-key");
        query["response_type"].Should().Be("code");
        query["redirect_uri"].Should().Be("http://127.0.0.1:54321/callback/");
        query["code_challenge"].Should().Be("challenge123");
        query["code_challenge_method"].Should().Be("S256");
        query["token_access_type"].Should().Be("offline");
        query["state"].Should().Be("state456");
    }

    [Fact]
    public void BuildAuthorizationUrl_ShouldNotContainClientSecret()
    {
        // 検証対象: BuildAuthorizationUrl  目的: PKCE Public Client フローでは client_secret が含まれないこと
        var url = DropboxOAuthServiceTestHelper.BuildAuthorizationUrl(
            "app-key", "http://127.0.0.1:54321/callback/", "ch", "st");

        url.Should().NotContain("client_secret");
    }

    // ── ParseCallbackQuery ──

    [Fact]
    public void ParseCallbackQuery_ShouldReturnCode_WhenValidCallback()
    {
        // 検証対象: ParseCallbackQuery  目的: 正常なコールバックで code が返ること
        var query = BuildQuery(("code", "auth-code-abc"), ("state", "my-state"));

        var (code, error) = DropboxOAuthService.ParseCallbackQuery(query, "my-state");

        code.Should().Be("auth-code-abc");
        error.Should().BeNull();
    }

    [Fact]
    public void ParseCallbackQuery_ShouldReturnError_WhenDropboxReturnsError()
    {
        // 検証対象: ParseCallbackQuery  目的: Dropbox がエラーを返した場合にエラーメッセージが返ること
        var query = BuildQuery(("error", "access_denied"), ("error_description", "User denied access"), ("state", "st"));

        var (code, error) = DropboxOAuthService.ParseCallbackQuery(query, "st");

        code.Should().BeNull();
        error.Should().Contain("User denied access");
    }

    [Fact]
    public void ParseCallbackQuery_ShouldReturnError_WhenStateMismatch()
    {
        // 検証対象: ParseCallbackQuery  目的: state 不一致時に CSRF エラーが返ること
        var query = BuildQuery(("code", "abc"), ("state", "evil-state"));

        var (code, error) = DropboxOAuthService.ParseCallbackQuery(query, "expected-state");

        code.Should().BeNull();
        error.Should().Contain("state");
    }

    [Fact]
    public void ParseCallbackQuery_ShouldReturnError_WhenCodeMissing()
    {
        // 検証対象: ParseCallbackQuery  目的: code がない場合にエラーが返ること
        var query = BuildQuery(("state", "st"));  // code なし

        var (code, error) = DropboxOAuthService.ParseCallbackQuery(query, "st");

        code.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    // ── GenerateCodeVerifier / GenerateCodeChallenge ──

    [Fact]
    public void GenerateCodeVerifier_ShouldReturnBase64UrlEncoded_WithExpectedLength()
    {
        // 検証対象: GenerateCodeVerifier  目的: RFC 7636 準拠の文字列（Base64URL, 43文字以上）が返ること
        var verifier = DropboxOAuthService.GenerateCodeVerifier();

        verifier.Should().NotBeNullOrWhiteSpace();
        verifier.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
        verifier.Length.Should().BeGreaterThanOrEqualTo(43);
    }

    [Fact]
    public void GenerateCodeChallenge_ShouldReturnBase64UrlEncoded_SHA256()
    {
        // 検証対象: GenerateCodeChallenge  目的: SHA-256(verifier) を Base64URL エンコードした値が返ること（パディングなし）
        var verifier = DropboxOAuthService.GenerateCodeVerifier();
        var challenge = DropboxOAuthService.GenerateCodeChallenge(verifier);

        challenge.Should().NotBeNullOrWhiteSpace();
        challenge.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
        // SHA-256 は 32 バイト → Base64 で 43 文字（パディング除去後）
        challenge.Length.Should().Be(43);
    }

    [Fact]
    public void GenerateCodeChallenge_ShouldBeDeterministic_ForSameVerifier()
    {
        // 検証対象: GenerateCodeChallenge  目的: 同一 verifier に対して同一 challenge が返ること（冪等性）
        var verifier = DropboxOAuthService.GenerateCodeVerifier();
        var ch1 = DropboxOAuthService.GenerateCodeChallenge(verifier);
        var ch2 = DropboxOAuthService.GenerateCodeChallenge(verifier);

        ch1.Should().Be(ch2);
    }

    // ── ヘルパー ──

    private static NameValueCollection BuildQuery(params (string key, string value)[] pairs)
    {
        var q = HttpUtility.ParseQueryString(string.Empty);
        foreach (var (key, value) in pairs)
            q[key] = value;
        return q;
    }
}

/// <summary>
/// DropboxOAuthService の internal static メソッド テスト用ヘルパー。
/// InternalsVisibleTo によりテストプロジェクトから直接呼び出せる。
/// </summary>
internal static class DropboxOAuthServiceTestHelper
{
    public static string BuildRedirectUri(int port) =>
        DropboxOAuthService.BuildRedirectUri(port);

    public static string BuildAuthorizationUrl(string appKey, string redirectUri, string codeChallenge, string state) =>
        DropboxOAuthService.BuildAuthorizationUrl(appKey, redirectUri, codeChallenge, state);
}
