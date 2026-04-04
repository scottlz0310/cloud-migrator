using System.Net;
using System.Text;
using System.Text.Json;
using CloudMigrator.Dashboard;
using FluentAssertions;
using Moq;
using Moq.Protected;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: SetupDoctorService
/// 目的: Graph 認証・SharePoint サイト・ドキュメントライブラリの各チェックが
///       HTTP レスポンスに応じて正しいステータスを返すことを確認する
/// </summary>
public sealed class SetupDoctorServiceTests
{
    // ── ヘルパー ─────────────────────────────────────────────────────────

    private static DoctorOptions ValidOptions(string destinationRoot = "") => new(
        ClientId: "test-client-id",
        TenantId: "test-tenant-id",
        ClientSecret: "test-secret",
        SiteId: "test-site-id",
        DriveId: "test-drive-id",
        DestinationRoot: destinationRoot);

    private static HttpClient BuildHttpClient(params (string UrlContains, HttpStatusCode StatusCode, string Body)[] responses)
    {
        var handler = new Mock<HttpMessageHandler>();
        foreach (var (urlContains, statusCode, body) in responses)
        {
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.ToString().Contains(urlContains)),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
        }

        return new HttpClient(handler.Object);
    }

    private static string TokenJson(string token = "dummy-token") =>
        JsonSerializer.Serialize(new { access_token = token });

    private static string SiteJson(string id = "site-abc-123") =>
        JsonSerializer.Serialize(new { id });

    // ── Graph 認証チェック ─────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_AllChecksPass_ReturnsHealthy()
    {
        // 検証対象: RunAsync  目的: 3 チェックすべて成功時に Healthy を返す
        var http = BuildHttpClient(
            ("oauth2/v2.0/token", HttpStatusCode.OK, TokenJson()),
            ("/sites/", HttpStatusCode.OK, SiteJson()),
            ("/drives/", HttpStatusCode.OK, "{}"));

        var svc = new SetupDoctorService(ValidOptions(), http);

        var result = await svc.RunAsync();

        result.OverallStatus.Should().Be(OverallStatus.Healthy);
        result.Checks.Should().HaveCount(3);
        result.Checks.Should().AllSatisfy(c => c.Status.Should().Be(DoctorStatus.Pass));
    }

    [Fact]
    public async Task RunAsync_AuthFails_ReturnsUnhealthyAndSkipsOtherChecks()
    {
        // 検証対象: RunAsync（認証失敗）  目的: 認証失敗時に後続チェックをスキップして Unhealthy を返す
        var http = BuildHttpClient(
            ("oauth2/v2.0/token", HttpStatusCode.Unauthorized, "{}"));

        var svc = new SetupDoctorService(ValidOptions(), http);

        var result = await svc.RunAsync();

        result.OverallStatus.Should().Be(OverallStatus.Unhealthy);
        result.Checks[0].Status.Should().Be(DoctorStatus.Fail);
        result.Checks[0].Name.Should().Be("Graph 認証");
        result.Checks[1].Status.Should().Be(DoctorStatus.Fail);
        result.Checks[1].Detail.Should().Contain("スキップ");
        result.Checks[2].Status.Should().Be(DoctorStatus.Fail);
        result.Checks[2].Detail.Should().Contain("スキップ");
    }

    [Fact]
    public async Task RunAsync_EmptyCredentials_ReturnsFailWithMessage()
    {
        // 検証対象: RunAsync（資格情報未設定）  目的: ClientId が空の場合 Fail を返し後続をスキップする
        var opts = new DoctorOptions(
            ClientId: "",
            TenantId: "tenant",
            ClientSecret: "secret",
            SiteId: "site",
            DriveId: "drive",
            DestinationRoot: "");
        var svc = new SetupDoctorService(opts, new HttpClient());

        var result = await svc.RunAsync();

        result.OverallStatus.Should().Be(OverallStatus.Unhealthy);
        result.Checks[0].Status.Should().Be(DoctorStatus.Fail);
        result.Checks[0].Detail.Should().Contain("設定されていません");
    }

    // ── SharePoint サイトチェック ────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SiteFails_ReturnsUnhealthy()
    {
        // 検証対象: CheckSiteAsync  目的: サイト取得が 404 の場合 Unhealthy を返す
        var http = BuildHttpClient(
            ("oauth2/v2.0/token", HttpStatusCode.OK, TokenJson()),
            ("/sites/", HttpStatusCode.NotFound, "{}"),
            ("/drives/", HttpStatusCode.OK, "{}"));

        var svc = new SetupDoctorService(ValidOptions(), http);

        var result = await svc.RunAsync();

        result.OverallStatus.Should().Be(OverallStatus.Unhealthy);
        result.Checks[1].Status.Should().Be(DoctorStatus.Fail);
        result.Checks[1].Name.Should().Be("SharePoint サイト");
    }

    [Fact]
    public async Task RunAsync_EmptySiteId_ReturnsSiteFail()
    {
        // 検証対象: CheckSiteAsync（SiteId 未設定）  目的: SiteId が空の場合 Fail を返す
        var opts = ValidOptions() with { SiteId = "" };
        // SiteIdが空なのでサイトは確認しないが driveIdは設定済み
        var http = BuildHttpClient(
            ("oauth2/v2.0/token", HttpStatusCode.OK, TokenJson()),
            ("/drives/", HttpStatusCode.OK, "{}"));
        var svc = new SetupDoctorService(opts, http);

        var result = await svc.RunAsync();

        result.Checks[1].Status.Should().Be(DoctorStatus.Fail);
        result.Checks[1].Detail.Should().Contain("SharePointSiteId");
    }

    // ── ドキュメントライブラリチェック ────────────────────────────────────

    [Fact]
    public async Task RunAsync_DriveNotFound_ReturnsDestinationRootError()
    {
        // 検証対象: CheckDriveAsync（DestinationRoot が見つからない）
        // 目的: destinationRoot が見つからない 404 の場合に詳細メッセージを返す
        var http = BuildHttpClient(
            ("oauth2/v2.0/token", HttpStatusCode.OK, TokenJson()),
            ("/sites/", HttpStatusCode.OK, SiteJson()),
            ("/drives/", HttpStatusCode.NotFound, "{}"));

        var svc = new SetupDoctorService(ValidOptions("Migration/2026"), http);

        var result = await svc.RunAsync();

        result.OverallStatus.Should().Be(OverallStatus.Unhealthy);
        result.Checks[2].Status.Should().Be(DoctorStatus.Fail);
        result.Checks[2].Detail.Should().Contain("destinationRoot が見つかりません");
        result.Checks[2].Detail.Should().Contain("Migration/2026");
    }

    [Fact]
    public async Task RunAsync_EmptyDriveId_ReturnsDriveFail()
    {
        // 検証対象: CheckDriveAsync（DriveId 未設定）  目的: DriveId が空の場合 Fail を返す
        var opts = ValidOptions() with { DriveId = "" };
        var http = BuildHttpClient(
            ("oauth2/v2.0/token", HttpStatusCode.OK, TokenJson()),
            ("/sites/", HttpStatusCode.OK, SiteJson()));
        var svc = new SetupDoctorService(opts, http);

        var result = await svc.RunAsync();

        result.Checks[2].Status.Should().Be(DoctorStatus.Fail);
        result.Checks[2].Detail.Should().Contain("SharePointDriveId");
    }

    [Fact]
    public async Task RunAsync_AuthPassSitePassDriveFail_ReturnsUnhealthy()
    {
        // 検証対象: BuildResult（一部 Fail）  目的: 一部 Fail があれば Unhealthy になる
        var http = BuildHttpClient(
            ("oauth2/v2.0/token", HttpStatusCode.OK, TokenJson()),
            ("/sites/", HttpStatusCode.OK, SiteJson()),
            ("/drives/", HttpStatusCode.Forbidden, "{}"));

        var svc = new SetupDoctorService(ValidOptions(), http);

        var result = await svc.RunAsync();

        result.OverallStatus.Should().Be(OverallStatus.Unhealthy);
        result.Checks[0].Status.Should().Be(DoctorStatus.Pass);
        result.Checks[1].Status.Should().Be(DoctorStatus.Pass);
        result.Checks[2].Status.Should().Be(DoctorStatus.Fail);
    }

    [Fact]
    public async Task RunAsync_AccessTokenNullOrEmpty_ReturnsAuthFail()
    {
        // 検証対象: CheckAuthAsync（access_token null）  目的: 200 OK でも access_token が空の場合は Fail を返す
        var http = BuildHttpClient(
            ("oauth2/v2.0/token", HttpStatusCode.OK, "{\"access_token\":\"\"}"));

        var svc = new SetupDoctorService(ValidOptions(), http);

        var result = await svc.RunAsync();

        result.OverallStatus.Should().Be(OverallStatus.Unhealthy);
        result.Checks[0].Status.Should().Be(DoctorStatus.Fail);
        result.Checks[0].Detail.Should().Contain("access_token");
        result.Checks[1].Detail.Should().Contain("スキップ");
        result.Checks[2].Detail.Should().Contain("スキップ");
    }

    [Fact]
    public async Task RunAsync_DestinationRootWithDoubleSlash_NormalizesPath()
    {
        // 検証対象: CheckDriveAsync（DestinationRoot 正規化）  目的: // を含むパスが正規化され 404 にならずに Pass する
        var http = BuildHttpClient(
            ("oauth2/v2.0/token", HttpStatusCode.OK, TokenJson()),
            ("/sites/", HttpStatusCode.OK, SiteJson()),
            ("/drives/", HttpStatusCode.OK, "{}"));

        var opts = ValidOptions(destinationRoot: "Migration//2026");
        var svc = new SetupDoctorService(opts, http);

        var result = await svc.RunAsync();

        result.OverallStatus.Should().Be(OverallStatus.Healthy);
        result.Checks.Should().AllSatisfy(c => c.Status.Should().Be(DoctorStatus.Pass));
    }
}
