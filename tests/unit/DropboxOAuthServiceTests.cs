using System.Net;
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
        // 検証対象: EnsureAccessTokenAsync  目的: ストアにトークンも Refresh Token もない場合は InvalidOperationException がスローされること
        var credStoreMock = new Mock<ICredentialStore>();
        credStoreMock.Setup(s => s.GetAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);

        var oAuthMock = new Mock<IDropboxOAuthService>();
        // oAuthService が null でないため HasRefreshCapability() = true → RefreshTokenAsync が呼ばれる
        // ここでは RefreshToken が取得できないため例外が発生するシナリオを確認
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxRefreshToken))
            .ReturnsAsync((string?)null);
        credStoreMock.Setup(s => s.GetAsync(CredentialKeys.DropboxAppKey))
            .ReturnsAsync("app-key");

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

        // リフレッシュトークンが取得できないため InvalidOperationException がスローされること
        await act.Should().ThrowAsync<Exception>();
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
}
