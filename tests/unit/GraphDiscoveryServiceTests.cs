using CloudMigrator.Providers.Graph.Auth;
using FluentAssertions;

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
}
