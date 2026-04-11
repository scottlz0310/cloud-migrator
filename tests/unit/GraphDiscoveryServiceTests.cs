using CloudMigrator.Providers.Graph.Auth;
using FluentAssertions;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// GraphDiscoveryService のユニットテスト（入力バリデーション + エラーハンドリング）。
/// ネットワーク疎通が不要なケースのみテストする。実際の API 疎通は E2E テストで確認する。
/// </summary>
public sealed class GraphDiscoveryServiceTests
{
    private readonly GraphDiscoveryService _sut = new();

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
        var message = GraphDiscoveryService.BuildAdminConsentError(403, "Authorization_RequestDenied");

        message.Should().Contain("管理者の同意");
        message.Should().Contain("Azure Portal");
    }

    [Fact]
    public void BuildAdminConsentError_WhenUnknownErrorCode_ReturnsGenericMessage()
    {
        // 検証対象: BuildAdminConsentError  目的: 未知コード時に汎用エラーメッセージが返されること
        var message = GraphDiscoveryService.BuildAdminConsentError(403, "Authorization_Forbidden");

        message.Should().Contain("アクセスが拒否");
        message.Should().Contain("Authorization_Forbidden");
    }

    [Fact]
    public void BuildAdminConsentError_WhenErrorCodeIsNull_ReturnsGenericMessage()
    {
        // 検証対象: BuildAdminConsentError  目的: errorCode が null でもクラッシュせず汎用メッセージが返されること
        var message = GraphDiscoveryService.BuildAdminConsentError(403, null);

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
}
