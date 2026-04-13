using CloudMigrator.Providers.Graph.Auth;
using FluentAssertions;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// GraphDiscoveryService のユニットテスト（入力バリデーション + エラーハンドリング + 正常系）。
/// ネットワーク疎通が不要なケースのみテストする。実際の API 疎通は E2E テストで確認する。
/// </summary>
public sealed class GraphDiscoveryServiceTests
{
    private readonly GraphDiscoveryService _sut = new();

    // ── IRequestAdapter モック ヘルパー ────────────────────────────────

    /// <summary>
    /// IRequestAdapter をモックして単一の SendAsync レスポンスを設定する。
    /// </summary>
    private static GraphServiceClient BuildGraphClientReturning<T>(T? response)
        where T : class, IParsable
    {
        var mockAdapter = new Mock<IRequestAdapter>(MockBehavior.Loose);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");
        mockAdapter
            .Setup(a => a.SendAsync<T>(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<T>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        return new GraphServiceClient(mockAdapter.Object);
    }

    /// <summary>
    /// IRequestAdapter をモックして DriveItemCollectionResponse を返す GraphServiceClient を生成する。
    /// </summary>
    private static GraphServiceClient BuildGraphClientReturningDriveItemCollection(
        DriveItemCollectionResponse? response)
    {
        var mockAdapter = new Mock<IRequestAdapter>(MockBehavior.Loose);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");
        mockAdapter
            .Setup(a => a.SendAsync<DriveItemCollectionResponse>(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<DriveItemCollectionResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        return new GraphServiceClient(mockAdapter.Object);
    }

    // ── GetOneDriveDriveIdAsync 入力バリデーション ─────────────────────

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenUserIdIsEmpty_ReturnsFailure()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: UserId 未入力時に失敗結果が返されること
        var result = await _sut.GetOneDriveDriveIdAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            userId: string.Empty);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.DriveId.Should().BeNull();
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenUserIdIsWhitespace_ReturnsFailure()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: UserId が空白文字のみの場合に失敗結果が返されること
        var result = await _sut.GetOneDriveDriveIdAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            userId: "   ");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // ── SearchSharePointSitesAsync 入力バリデーション ──────────────────

    [Fact]
    public async Task SearchSharePointSitesAsync_WhenKeywordIsEmpty_ReturnsFailure()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: キーワード未入力時に失敗結果が返されること
        var result = await _sut.SearchSharePointSitesAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            keyword: string.Empty);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.Sites.Should().BeNull();
    }

    [Fact]
    public async Task SearchSharePointSitesAsync_WhenKeywordIsWildcard_ReturnsFailure()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: search=* ワイルドカードが拒否されること（全件返し防止）
        var result = await _sut.SearchSharePointSitesAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            keyword: "*");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ワイルドカード");
    }

    [Fact]
    public async Task SearchSharePointSitesAsync_WhenKeywordIsWildcardWithSpaces_ReturnsFailure()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: 前後空白付きワイルドカードも拒否されること
        var result = await _sut.SearchSharePointSitesAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            keyword: "  *  ");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("ワイルドカード");
    }

    // ── GetSharePointSiteByUrlAsync 入力バリデーション ─────────────────

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenUrlIsEmpty_ReturnsFailure()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: URL 未入力時に失敗結果が返されること
        var result = await _sut.GetSharePointSiteByUrlAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            siteUrl: string.Empty);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenUrlIsInvalid_ReturnsFailure()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: 無効な URL 形式の場合に失敗結果が返されること
        var result = await _sut.GetSharePointSiteByUrlAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            siteUrl: "not-a-url");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("URL");
    }

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenUrlIsHttp_ReturnsFailure()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: HTTP URL（非-HTTPS）が拒否されること
        var result = await _sut.GetSharePointSiteByUrlAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            siteUrl: "http://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("HTTPS");
    }

    // ── GetSharePointDrivesAsync 入力バリデーション ────────────────────

    [Fact]
    public async Task GetSharePointDrivesAsync_WhenSiteIdIsEmpty_ReturnsFailure()
    {
        // 検証対象: GetSharePointDrivesAsync  目的: サイト ID 未入力時に失敗結果が返されること
        var result = await _sut.GetSharePointDrivesAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            siteId: string.Empty);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.Drives.Should().BeNull();
    }

    // ── VerifyDriveAsync 入力バリデーション ───────────────────────────

    [Fact]
    public async Task VerifyDriveAsync_WhenDriveIdIsEmpty_ReturnsFailure()
    {
        // 検証対象: VerifyDriveAsync  目的: Drive ID 未入力時に失敗結果が返されること
        var result = await _sut.VerifyDriveAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            driveId: string.Empty);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // ── BuildAdminConsentError ビジネスロジック ─────────────────────────────────

    [Fact]
    public void BuildAdminConsentError_WhenAuthorizationRequestDenied_ReturnsConsentGuide()
    {
        // 検証対象: BuildAdminConsentError  目的: "Authorization_RequestDenied" コード時に管理者同意ガイドが返されること
        var message = GraphDiscoveryService.BuildAdminConsentError("Authorization_RequestDenied");

        message.Should().Contain("管理者の同意");
        message.Should().Contain("Azure Portal");
    }

    [Fact]
    public void BuildAdminConsentError_WhenUnknownErrorCode_ReturnsGenericMessage()
    {
        // 検証対象: BuildAdminConsentError  目的: 未知コード時に汎用エラーメッセージが返されること
        var message = GraphDiscoveryService.BuildAdminConsentError("Authorization_Forbidden");

        message.Should().Contain("アクセスが拒否");
        message.Should().Contain("Authorization_Forbidden");
    }

    [Fact]
    public void BuildAdminConsentError_WhenErrorCodeIsNull_ReturnsGenericMessage()
    {
        // 検証対象: BuildAdminConsentError  目的: errorCode が null でもクラッシュせず汎用メッセージが返されること
        var message = GraphDiscoveryService.BuildAdminConsentError(null);

        message.Should().Contain("アクセスが拒否");
        message.Should().NotBeNullOrEmpty();
    }

    // ── API 応答エラーハンドリング（ClientFactory 注入による例外分岐テスト）──────

    // ClientFactory は try ブロック内で呼ばれるため、ファクトリーから例外を throw するだけで
    // 全 catch ブランチをカバーできる（ネットワーク疎通不要）。

    private static GraphDiscoveryService CreateSutThrowing(Exception ex)
    {
        var sut = new GraphDiscoveryService();
        sut.ClientFactory = (_, _, _) => throw ex;
        return sut;
    }

    private static ODataError MakeODataError(int statusCode, string? code = null, string? message = null) =>
        new() { ResponseStatusCode = statusCode, Error = new MainError { Code = code, Message = message } };

    // ── GetOneDriveDriveIdAsync 例外ハンドリング ──────────────────────

    [Fact]
    public async Task GetOneDriveDriveIdAsync_When401ODataError_ReturnsAuthError()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: 401 ODataError で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(MakeODataError(401))
            .GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenApiException401_ReturnsAuthError()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: 非-OData ApiException 401 で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 401 })
            .GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_When403ODataError_WithAuthorizationRequestDenied_ReturnsAdminConsentGuide()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: 403 + Authorization_RequestDenied で管理者同意ガイドが返されること
        var result = await CreateSutThrowing(MakeODataError(403, "Authorization_RequestDenied"))
            .GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("管理者の同意");
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_When403ODataError_WithUnknownCode_ReturnsForbiddenMessage()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: 403 + 未知コードで汎用拒否メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(403, "Forbidden_Something"))
            .GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("アクセスが拒否");
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_When404ODataError_ReturnsUserNotFound()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: 404 でユーザー未発見メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(404))
            .GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("見つかりませんでした");
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenGenericODataError_ReturnsGraphApiError()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: 汎用 ODataError で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(500, message: "Internal Server Error"))
            .GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenPureApiException403_ReturnsAdminConsentMessage()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: 非-OData ApiException 403 でも管理者同意メッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 403 })
            .GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("アクセスが拒否");
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenGenericApiException_ReturnsGraphApiError()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: 汎用 ApiException で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 500 })
            .GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenNetworkError_ReturnsConnectionError()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: ネットワークエラーで接続エラーメッセージが返されること
        var result = await CreateSutThrowing(new HttpRequestException("Network error"))
            .GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("接続エラー");
    }

    // ── SearchSharePointSitesAsync 例外ハンドリング ────────────────────

    [Fact]
    public async Task SearchSharePointSitesAsync_When401ODataError_ReturnsAuthError()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: 401 ODataError で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(MakeODataError(401))
            .SearchSharePointSitesAsync("c", "t", "s", "contoso");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task SearchSharePointSitesAsync_WhenApiException401_ReturnsAuthError()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: 非-OData ApiException 401 で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 401 })
            .SearchSharePointSitesAsync("c", "t", "s", "contoso");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task SearchSharePointSitesAsync_When403ODataError_ReturnsAdminConsentMessage()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: 403 で管理者同意メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(403, "Authorization_RequestDenied"))
            .SearchSharePointSitesAsync("c", "t", "s", "contoso");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("管理者の同意");
    }

    [Fact]
    public async Task SearchSharePointSitesAsync_WhenGenericODataError_ReturnsGraphApiError()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: 汎用 ODataError で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(500, message: "error"))
            .SearchSharePointSitesAsync("c", "t", "s", "contoso");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task SearchSharePointSitesAsync_WhenPureApiException403_ReturnsAdminConsentMessage()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: 非-OData ApiException 403 でも管理者同意メッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 403 })
            .SearchSharePointSitesAsync("c", "t", "s", "contoso");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("アクセスが拒否");
    }

    [Fact]
    public async Task SearchSharePointSitesAsync_WhenNetworkError_ReturnsConnectionError()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: ネットワークエラーで接続エラーメッセージが返されること
        var result = await CreateSutThrowing(new HttpRequestException("Network error"))
            .SearchSharePointSitesAsync("c", "t", "s", "contoso");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("接続エラー");
    }

    // ── GetSharePointSiteByUrlAsync 例外ハンドリング ───────────────────

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_When401ODataError_ReturnsAuthError()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: 401 ODataError で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(MakeODataError(401))
            .GetSharePointSiteByUrlAsync("c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenApiException401_ReturnsAuthError()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: 非-OData ApiException 401 で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 401 })
            .GetSharePointSiteByUrlAsync("c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_When403ODataError_ReturnsAdminConsentMessage()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: 403 で管理者同意メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(403, "Authorization_RequestDenied"))
            .GetSharePointSiteByUrlAsync("c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("管理者の同意");
    }

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_When404ODataError_ReturnsSiteNotFound()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: 404 でサイト未発見メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(404))
            .GetSharePointSiteByUrlAsync("c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("見つかりませんでした");
    }

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenGenericODataError_ReturnsGraphApiError()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: 汎用 ODataError で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(500, message: "error"))
            .GetSharePointSiteByUrlAsync("c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    // ── GetSharePointDrivesAsync 例外ハンドリング ──────────────────────

    [Fact]
    public async Task GetSharePointDrivesAsync_When401ODataError_ReturnsAuthError()
    {
        // 検証対象: GetSharePointDrivesAsync  目的: 401 ODataError で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(MakeODataError(401))
            .GetSharePointDrivesAsync("c", "t", "s", "site-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task GetSharePointDrivesAsync_WhenApiException401_ReturnsAuthError()
    {
        // 検証対象: GetSharePointDrivesAsync  目的: 非-OData ApiException 401 で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 401 })
            .GetSharePointDrivesAsync("c", "t", "s", "site-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task GetSharePointDrivesAsync_When403ODataError_ReturnsAdminConsentMessage()
    {
        // 検証対象: GetSharePointDrivesAsync  目的: 403 で管理者同意メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(403, "Authorization_RequestDenied"))
            .GetSharePointDrivesAsync("c", "t", "s", "site-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("管理者の同意");
    }

    [Fact]
    public async Task GetSharePointDrivesAsync_WhenGenericODataError_ReturnsGraphApiError()
    {
        // 検証対象: GetSharePointDrivesAsync  目的: 汎用 ODataError で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(500, message: "error"))
            .GetSharePointDrivesAsync("c", "t", "s", "site-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task GetSharePointDrivesAsync_WhenPureApiException_ReturnsGraphApiError()
    {
        // 検証対象: GetSharePointDrivesAsync  目的: 非-OData ApiException で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 500 })
            .GetSharePointDrivesAsync("c", "t", "s", "site-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    // ── VerifyDriveAsync 例外ハンドリング ──────────────────────────────

    [Fact]
    public async Task VerifyDriveAsync_When401ODataError_ReturnsAuthError()
    {
        // 検証対象: VerifyDriveAsync  目的: 401 ODataError で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(MakeODataError(401))
            .VerifyDriveAsync("c", "t", "s", "driveId");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task VerifyDriveAsync_WhenApiException401_ReturnsAuthError()
    {
        // 検証対象: VerifyDriveAsync  目的: 非-OData ApiException 401 で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 401 })
            .VerifyDriveAsync("c", "t", "s", "driveId");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task VerifyDriveAsync_When403ODataError_ReturnsAdminConsentMessage()
    {
        // 検証対象: VerifyDriveAsync  目的: 403 で管理者同意メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(403, "Authorization_RequestDenied"))
            .VerifyDriveAsync("c", "t", "s", "driveId");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("管理者の同意");
    }

    [Fact]
    public async Task VerifyDriveAsync_When404ODataError_ReturnsDriveNotFound()
    {
        // 検証対象: VerifyDriveAsync  目的: 404 で Drive 未発見メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(404))
            .VerifyDriveAsync("c", "t", "s", "driveId");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Drive ID");
    }

    [Fact]
    public async Task VerifyDriveAsync_WhenGenericODataError_ReturnsGraphApiError()
    {
        // 検証対象: VerifyDriveAsync  目的: 汎用 ODataError で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(500, message: "error"))
            .VerifyDriveAsync("c", "t", "s", "driveId");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task VerifyDriveAsync_WhenPureApiException403_ReturnsAdminConsentMessage()
    {
        // 検証対象: VerifyDriveAsync  目的: 非-OData ApiException 403 でも管理者同意メッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 403 })
            .VerifyDriveAsync("c", "t", "s", "driveId");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("アクセスが拒否");
    }

    [Fact]
    public async Task VerifyDriveAsync_WhenGenericApiException_ReturnsGraphApiError()
    {
        // 検証対象: VerifyDriveAsync  目的: 汎用 ApiException で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 500 })
            .VerifyDriveAsync("c", "t", "s", "driveId");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task VerifyDriveAsync_WhenNetworkError_ReturnsConnectionError()
    {
        // 検証対象: VerifyDriveAsync  目的: ネットワークエラーで接続エラーメッセージが返されること
        var result = await CreateSutThrowing(new HttpRequestException("Network error"))
            .VerifyDriveAsync("c", "t", "s", "driveId");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("接続エラー");
    }

    // ── SearchSharePointSitesAsync 残欠落ブランチ ─────────────────────

    [Fact]
    public async Task SearchSharePointSitesAsync_WhenGenericApiException_ReturnsGraphApiError()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: 汎用 ApiException（非 403）で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 500 })
            .SearchSharePointSitesAsync("c", "t", "s", "contoso");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    // ── GetSharePointSiteByUrlAsync 残欠落ブランチ ────────────────────

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenApiException403_ReturnsAdminConsentMessage()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: 非-OData ApiException 403 でも管理者同意メッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 403 })
            .GetSharePointSiteByUrlAsync("c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("アクセスが拒否");
    }

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenGenericApiException_ReturnsGraphApiError()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: 汎用 ApiException で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 500 })
            .GetSharePointSiteByUrlAsync("c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenNetworkError_ReturnsConnectionError()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: ネットワークエラーで接続エラーメッセージが返されること
        var result = await CreateSutThrowing(new HttpRequestException("Network error"))
            .GetSharePointSiteByUrlAsync("c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("接続エラー");
    }

    // ── GetSharePointDrivesAsync 残欠落ブランチ ───────────────────────

    [Fact]
    public async Task GetSharePointDrivesAsync_WhenApiException403_ReturnsAdminConsentMessage()
    {
        // 検証対象: GetSharePointDrivesAsync  目的: 非-OData ApiException 403 でも管理者同意メッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 403 })
            .GetSharePointDrivesAsync("c", "t", "s", "site-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("アクセスが拒否");
    }

    // ── OperationCanceledException rethrow 確認（全メソッド）────────────

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenCanceled_ThrowsOperationCanceledException()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: キャンセル時に OperationCanceledException が握り潰されず再スローされること
        var act = async () => await CreateSutThrowing(new OperationCanceledException())
            .GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SearchSharePointSitesAsync_WhenCanceled_ThrowsOperationCanceledException()
    {
        // 検証対象: SearchSharePointSitesAsync  目的: キャンセル時に OperationCanceledException が握り潰されず再スローされること
        var act = async () => await CreateSutThrowing(new OperationCanceledException())
            .SearchSharePointSitesAsync("c", "t", "s", "contoso");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenCanceled_ThrowsOperationCanceledException()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: キャンセル時に OperationCanceledException が握り潰されず再スローされること
        var act = async () => await CreateSutThrowing(new OperationCanceledException())
            .GetSharePointSiteByUrlAsync("c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetSharePointDrivesAsync_WhenCanceled_ThrowsOperationCanceledException()
    {
        // 検証対象: GetSharePointDrivesAsync  目的: キャンセル時に OperationCanceledException が握り潰されず再スローされること
        var act = async () => await CreateSutThrowing(new OperationCanceledException())
            .GetSharePointDrivesAsync("c", "t", "s", "site-id");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task VerifyDriveAsync_WhenCanceled_ThrowsOperationCanceledException()
    {
        // 検証対象: VerifyDriveAsync  目的: キャンセル時に OperationCanceledException が握り潰されず再スローされること
        var act = async () => await CreateSutThrowing(new OperationCanceledException())
            .VerifyDriveAsync("c", "t", "s", "driveId");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── ListDriveFoldersAsync 入力バリデーション ───────────────────────

    [Fact]
    public async Task ListDriveFoldersAsync_WhenDriveIdIsEmpty_ReturnsFailure()
    {
        // 検証対象: ListDriveFoldersAsync  目的: Drive ID 未入力時に失敗結果が返されること
        var result = await _sut.ListDriveFoldersAsync(
            clientId: "client-id",
            tenantId: "tenant-id",
            clientSecret: "secret",
            driveId: string.Empty);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.Folders.Should().BeNull();
    }

    // ── ListDriveFoldersAsync 例外ハンドリング ─────────────────────────

    [Fact]
    public async Task ListDriveFoldersAsync_When401ODataError_ReturnsAuthError()
    {
        // 検証対象: ListDriveFoldersAsync  目的: 401 ODataError で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(MakeODataError(401))
            .ListDriveFoldersAsync("c", "t", "s", "drive-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task ListDriveFoldersAsync_WhenApiException401_ReturnsAuthError()
    {
        // 検証対象: ListDriveFoldersAsync  目的: 非-OData ApiException 401 で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 401 })
            .ListDriveFoldersAsync("c", "t", "s", "drive-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task ListDriveFoldersAsync_When403ODataError_ReturnsAdminConsentMessage()
    {
        // 検証対象: ListDriveFoldersAsync  目的: 403 で管理者同意メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(403, "Authorization_RequestDenied"))
            .ListDriveFoldersAsync("c", "t", "s", "drive-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("管理者の同意");
    }

    [Fact]
    public async Task ListDriveFoldersAsync_When404ODataError_ReturnsFolderNotFound()
    {
        // 検証対象: ListDriveFoldersAsync  目的: 404 でフォルダ未発見メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(404))
            .ListDriveFoldersAsync("c", "t", "s", "drive-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("見つかりませんでした");
    }

    [Fact]
    public async Task ListDriveFoldersAsync_WhenGenericODataError_ReturnsGraphApiError()
    {
        // 検証対象: ListDriveFoldersAsync  目的: 汎用 ODataError で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(500, message: "error"))
            .ListDriveFoldersAsync("c", "t", "s", "drive-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task ListDriveFoldersAsync_WhenApiException_ReturnsGraphApiError()
    {
        // 検証対象: ListDriveFoldersAsync  目的: ApiException で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 500 })
            .ListDriveFoldersAsync("c", "t", "s", "drive-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task ListDriveFoldersAsync_WhenNetworkError_ReturnsConnectionError()
    {
        // 検証対象: ListDriveFoldersAsync  目的: ネットワークエラーで接続エラーメッセージが返されること
        var result = await CreateSutThrowing(new HttpRequestException("Network error"))
            .ListDriveFoldersAsync("c", "t", "s", "drive-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("接続エラー");
    }

    [Fact]
    public async Task ListDriveFoldersAsync_WhenCanceled_ThrowsOperationCanceledException()
    {
        // 検証対象: ListDriveFoldersAsync  目的: キャンセル時に OperationCanceledException が握り潰されず再スローされること
        var act = async () => await CreateSutThrowing(new OperationCanceledException())
            .ListDriveFoldersAsync("c", "t", "s", "drive-id");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── ListAllSharePointSitesAsync 例外ハンドリング ───────────────────

    [Fact]
    public async Task ListAllSharePointSitesAsync_When401ODataError_ReturnsAuthError()
    {
        // 検証対象: ListAllSharePointSitesAsync  目的: 401 ODataError で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(MakeODataError(401))
            .ListAllSharePointSitesAsync("c", "t", "s");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task ListAllSharePointSitesAsync_WhenApiException401_ReturnsAuthError()
    {
        // 検証対象: ListAllSharePointSitesAsync  目的: 非-OData ApiException 401 で認証エラーガイダンスが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 401 })
            .ListAllSharePointSitesAsync("c", "t", "s");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Azure 認証に失敗しました");
    }

    [Fact]
    public async Task ListAllSharePointSitesAsync_When403ODataError_ReturnsAdminConsentMessage()
    {
        // 検証対象: ListAllSharePointSitesAsync  目的: 403 で管理者同意メッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(403, "Authorization_RequestDenied"))
            .ListAllSharePointSitesAsync("c", "t", "s");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("管理者の同意");
    }

    [Fact]
    public async Task ListAllSharePointSitesAsync_WhenGenericODataError_ReturnsGraphApiError()
    {
        // 検証対象: ListAllSharePointSitesAsync  目的: 汎用 ODataError で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(MakeODataError(500, message: "error"))
            .ListAllSharePointSitesAsync("c", "t", "s");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task ListAllSharePointSitesAsync_WhenApiException403_ReturnsAdminConsentMessage()
    {
        // 検証対象: ListAllSharePointSitesAsync  目的: 非-OData ApiException 403 でも管理者同意メッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 403 })
            .ListAllSharePointSitesAsync("c", "t", "s");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("アクセスが拒否");
    }

    [Fact]
    public async Task ListAllSharePointSitesAsync_WhenGenericApiException_ReturnsGraphApiError()
    {
        // 検証対象: ListAllSharePointSitesAsync  目的: 汎用 ApiException で Graph API エラーメッセージが返されること
        var result = await CreateSutThrowing(new ApiException { ResponseStatusCode = 500 })
            .ListAllSharePointSitesAsync("c", "t", "s");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Graph API エラー");
    }

    [Fact]
    public async Task ListAllSharePointSitesAsync_WhenNetworkError_ReturnsConnectionError()
    {
        // 検証対象: ListAllSharePointSitesAsync  目的: ネットワークエラーで接続エラーメッセージが返されること
        var result = await CreateSutThrowing(new HttpRequestException("Network error"))
            .ListAllSharePointSitesAsync("c", "t", "s");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("接続エラー");
    }

    [Fact]
    public async Task ListAllSharePointSitesAsync_WhenCanceled_ThrowsOperationCanceledException()
    {
        // 検証対象: ListAllSharePointSitesAsync  目的: キャンセル時に OperationCanceledException が握り潰されず再スローされること
        var act = async () => await CreateSutThrowing(new OperationCanceledException())
            .ListAllSharePointSitesAsync("c", "t", "s");

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── GetOneDriveDriveIdAsync 正常系テスト ──────────────────────────

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenDriveIdIsNullInResponse_ReturnsFailure()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: API が Drive を返すが Id が null の場合に失敗結果が返されること
        var sut = new GraphDiscoveryService();
        sut.ClientFactory = (_, _, _) =>
            BuildGraphClientReturning(new Drive { Id = null, Name = "My Drive" });

        var result = await sut.GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Drive が見つかりませんでした");
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenDriveIdExistsInResponse_ReturnsSuccess()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: API が有効な Drive を返した場合に成功結果と Drive ID が返されること
        var sut = new GraphDiscoveryService();
        sut.ClientFactory = (_, _, _) =>
            BuildGraphClientReturning(new Drive { Id = "test-drive-id", Name = "My Drive" });

        var result = await sut.GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeTrue();
        result.DriveId.Should().Be("test-drive-id");
        result.DisplayName.Should().Be("My Drive");
    }

    [Fact]
    public async Task GetOneDriveDriveIdAsync_WhenDriveNameIsNull_UsesUserIdAsDisplayName()
    {
        // 検証対象: GetOneDriveDriveIdAsync  目的: Drive.Name が null の場合に userId が DisplayName として使われること
        var sut = new GraphDiscoveryService();
        sut.ClientFactory = (_, _, _) =>
            BuildGraphClientReturning(new Drive { Id = "drive-id", Name = null });

        var result = await sut.GetOneDriveDriveIdAsync("c", "t", "s", "user@contoso.com");

        result.Success.Should().BeTrue();
        result.DisplayName.Should().Be("user@contoso.com");
    }

    // ── GetSharePointSiteByUrlAsync 正常系テスト ──────────────────────

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenSiteIdIsNullInResponse_ReturnsFailure()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: API が Site を返すが Id が null の場合に失敗結果が返されること
        var sut = new GraphDiscoveryService();
        sut.ClientFactory = (_, _, _) =>
            BuildGraphClientReturning<Site>(new Site { Id = null, DisplayName = "My Site" });

        var result = await sut.GetSharePointSiteByUrlAsync(
            "c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("見つかりませんでした");
    }

    [Fact]
    public async Task GetSharePointSiteByUrlAsync_WhenSiteExists_ReturnsSuccess()
    {
        // 検証対象: GetSharePointSiteByUrlAsync  目的: API が有効な Site を返した場合に成功結果が返されること
        var sut = new GraphDiscoveryService();
        sut.ClientFactory = (_, _, _) =>
            BuildGraphClientReturning<Site>(new Site
            {
                Id = "site-id",
                DisplayName = "My Team",
                WebUrl = "https://contoso.sharepoint.com/sites/MyTeam"
            });

        var result = await sut.GetSharePointSiteByUrlAsync(
            "c", "t", "s", "https://contoso.sharepoint.com/sites/MyTeam");

        result.Success.Should().BeTrue();
        result.Sites.Should().ContainSingle(s => s.SiteId == "site-id");
    }

    // ── VerifyDriveAsync 正常系テスト ─────────────────────────────────

    [Fact]
    public async Task VerifyDriveAsync_WhenDriveExists_ReturnsSuccess()
    {
        // 検証対象: VerifyDriveAsync  目的: API が有効な Drive を返した場合に成功結果が返されること
        var sut = new GraphDiscoveryService();
        sut.ClientFactory = (_, _, _) =>
            BuildGraphClientReturning(new Drive { Id = "drive-id" });

        var result = await sut.VerifyDriveAsync("c", "t", "s", "drive-id");

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyDriveAsync_WhenDriveIdIsNullInResponse_ReturnsFailure()
    {
        // 検証対象: VerifyDriveAsync  目的: API が Drive を返すが Id が null の場合に失敗結果が返されること
        var sut = new GraphDiscoveryService();
        sut.ClientFactory = (_, _, _) =>
            BuildGraphClientReturning(new Drive { Id = null });

        var result = await sut.VerifyDriveAsync("c", "t", "s", "drive-id");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("見つかりませんでした");
    }

    // ── GetSharePointDrivesAsync 正常系テスト ─────────────────────────

    [Fact]
    public async Task GetSharePointDrivesAsync_WhenDrivesExist_ReturnsSuccess()
    {
        // 検証対象: GetSharePointDrivesAsync  目的: API がドライブリストを返した場合に成功結果とドライブ一覧が返されること
        var sut = new GraphDiscoveryService();
        var drivesResponse = new DriveCollectionResponse
        {
            Value =
            [
                new Drive { Id = "drive-1", Name = "Documents", DriveType = "documentLibrary" },
                new Drive { Id = "drive-2", Name = "Site Assets", DriveType = "documentLibrary" }
            ]
        };
        var mockAdapter = new Mock<IRequestAdapter>(MockBehavior.Loose);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");
        mockAdapter
            .Setup(a => a.SendAsync<DriveCollectionResponse>(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<DriveCollectionResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(drivesResponse);
        sut.ClientFactory = (_, _, _) => new GraphServiceClient(mockAdapter.Object);

        var result = await sut.GetSharePointDrivesAsync("c", "t", "s", "site-id");

        result.Success.Should().BeTrue();
        result.Drives.Should().HaveCount(2);
        result.Drives![0].DriveId.Should().Be("drive-1");
    }

    [Fact]
    public async Task GetSharePointDrivesAsync_WhenResponseIsEmpty_ReturnsSuccessWithEmptyList()
    {
        // 検証対象: GetSharePointDrivesAsync  目的: API が空リストを返した場合に成功結果と空リストが返されること
        var sut = new GraphDiscoveryService();
        var drivesResponse = new DriveCollectionResponse { Value = [] };
        var mockAdapter = new Mock<IRequestAdapter>(MockBehavior.Loose);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");
        mockAdapter
            .Setup(a => a.SendAsync<DriveCollectionResponse>(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<DriveCollectionResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(drivesResponse);
        sut.ClientFactory = (_, _, _) => new GraphServiceClient(mockAdapter.Object);

        var result = await sut.GetSharePointDrivesAsync("c", "t", "s", "site-id");

        result.Success.Should().BeTrue();
        result.Drives.Should().BeEmpty();
    }

    // ── ListDriveFoldersAsync 正常系テスト ────────────────────────────

    [Fact]
    public async Task ListDriveFoldersAsync_WhenFolderIdIsNull_UsesRootChildren()
    {
        // 検証対象: ListDriveFoldersAsync  目的: folderId=null のとき root の子アイテムが取得されること
        var sut = new GraphDiscoveryService();
        var items = new DriveItemCollectionResponse
        {
            Value =
            [
                new DriveItem { Id = "folder-1", Name = "Documents", Folder = new Folder() },
                new DriveItem { Id = "file-1", Name = "readme.txt", Folder = null } // フォルダでないのでフィルター除外
            ]
        };
        sut.ClientFactory = (_, _, _) => BuildGraphClientReturningDriveItemCollection(items);

        var result = await sut.ListDriveFoldersAsync("c", "t", "s", "drive-id", folderId: null);

        result.Success.Should().BeTrue();
        result.Folders.Should().ContainSingle(f => f.FolderId == "folder-1");
    }

    [Fact]
    public async Task ListDriveFoldersAsync_WhenFolderIdIsSpecified_UsesItemChildren()
    {
        // 検証対象: ListDriveFoldersAsync  目的: folderId が指定されたとき、そのアイテムの子が取得されること
        var sut = new GraphDiscoveryService();
        var items = new DriveItemCollectionResponse
        {
            Value =
            [
                new DriveItem { Id = "sub-folder-1", Name = "SubFolder", Folder = new Folder() }
            ]
        };
        sut.ClientFactory = (_, _, _) => BuildGraphClientReturningDriveItemCollection(items);

        var result = await sut.ListDriveFoldersAsync("c", "t", "s", "drive-id", folderId: "parent-folder-id");

        result.Success.Should().BeTrue();
        result.Folders.Should().ContainSingle(f => f.FolderId == "sub-folder-1");
    }
}

