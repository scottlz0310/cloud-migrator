using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudMigrator.Core.Migration;
using CloudMigrator.Core.State;
using CloudMigrator.Dashboard;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: DashboardServer API エンドポイント
/// 目的: 各エンドポイントが ITransferStateDb を正しく呼び出し、
///       適切な HTTP レスポンスを返すことを確認する
/// </summary>
public sealed class DashboardServerTests : IAsyncDisposable
{
    private readonly Mock<ITransferStateDb> _mockDb = new(MockBehavior.Loose);
    private readonly Mock<IConfigurationService> _mockConfigService = new(MockBehavior.Loose);
    private readonly Mock<ITransferJobService> _mockJobService = new(MockBehavior.Loose);
    private WebApplication? _app;

    private async Task<HttpClient> CreateClientAsync()
    {
        _app = DashboardServer.BuildApp(
            _mockDb.Object,
            wb => wb.UseTestServer(),
            _mockConfigService.Object,
            _mockJobService.Object);
        await _app.StartAsync();
        return _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    // ── /api/status ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatus_ReturnsOkWithSummary()
    {
        // 検証対象: GET /api/status  目的: GetSummaryAsync を呼び出し 200 OK とサマリーを返す
        var summary = new TransferDbSummary { Done = 42, Pending = 3 };
        _mockDb.Setup(d => d.GetSummaryAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(summary);
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TransferDbSummary>();
        body!.Done.Should().Be(42);
        body.Pending.Should().Be(3);
    }

    // ── /api/metrics ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetMetrics_DefaultsToRateLimitPct()
    {
        // 検証対象: GET /api/metrics (name省略)  目的: デフォルト name=rate_limit_pct・minutes=60 で取得する
        _mockDb.Setup(d => d.GetMetricsAsync("rate_limit_pct", 60, It.IsAny<CancellationToken>()))
               .ReturnsAsync((IReadOnlyList<MetricPoint>)new List<MetricPoint>());
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _mockDb.Verify(d => d.GetMetricsAsync("rate_limit_pct", 60, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetMetrics_CustomNameAndMinutes_ForwardsToDb()
    {
        // 検証対象: GET /api/metrics?name=throughput_files_per_min&minutes=30
        // 目的: 指定パラメータを GetMetricsAsync にそのまま渡す
        _mockDb.Setup(d => d.GetMetricsAsync("throughput_files_per_min", 30, It.IsAny<CancellationToken>()))
               .ReturnsAsync((IReadOnlyList<MetricPoint>)new List<MetricPoint>());
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/metrics?name=throughput_files_per_min&minutes=30");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _mockDb.Verify(d => d.GetMetricsAsync("throughput_files_per_min", 30, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── /api/phase ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetPhase_ReturnsCrawling_WhenNoCrawlCompleteCheckpoint()
    {
        // 検証対象: GET /api/phase  目的: crawl_complete チェックポイントなし → phase="crawling"
        _mockDb.Setup(d => d.GetCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((string?)null);
        _mockDb.Setup(d => d.GetMetricsAsync("sp_folder_done", 120, It.IsAny<CancellationToken>()))
               .ReturnsAsync((IReadOnlyList<MetricPoint>)new List<MetricPoint>());
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/phase");
        var json = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.Should().Contain("\"phase\":\"crawling\"");
    }

    [Fact]
    public async Task GetPhase_ReturnsFolderCreation_WhenCrawlCompleteButNotFolderCreation()
    {
        // 検証対象: GET /api/phase
        // 目的: crawl_complete=true かつ folder_creation_complete=null → phase="folder_creation"
        _mockDb.Setup(d => d.GetCheckpointAsync(SharePointMigrationPipeline.CrawlCompleteKey, It.IsAny<CancellationToken>()))
               .ReturnsAsync("true");
        _mockDb.Setup(d => d.GetCheckpointAsync(SharePointMigrationPipeline.FolderCreationCompleteKey, It.IsAny<CancellationToken>()))
               .ReturnsAsync((string?)null);
        _mockDb.Setup(d => d.GetCheckpointAsync(SharePointMigrationPipeline.FolderTotalKey, It.IsAny<CancellationToken>()))
               .ReturnsAsync((string?)null);
        _mockDb.Setup(d => d.GetMetricsAsync("sp_folder_done", 120, It.IsAny<CancellationToken>()))
               .ReturnsAsync((IReadOnlyList<MetricPoint>)new List<MetricPoint>());
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/phase");
        var json = await response.Content.ReadAsStringAsync();

        json.Should().Contain("\"phase\":\"folder_creation\"");
    }

    [Fact]
    public async Task GetPhase_ReturnsTransferring_WhenBothCheckpointsTrue()
    {
        // 検証対象: GET /api/phase
        // 目的: crawl_complete=true かつ folder_creation_complete=true → phase="transferring"、folderTotal を含む
        _mockDb.Setup(d => d.GetCheckpointAsync(SharePointMigrationPipeline.CrawlCompleteKey, It.IsAny<CancellationToken>()))
               .ReturnsAsync("true");
        _mockDb.Setup(d => d.GetCheckpointAsync(SharePointMigrationPipeline.FolderCreationCompleteKey, It.IsAny<CancellationToken>()))
               .ReturnsAsync("true");
        _mockDb.Setup(d => d.GetCheckpointAsync(SharePointMigrationPipeline.FolderTotalKey, It.IsAny<CancellationToken>()))
               .ReturnsAsync("100");
        _mockDb.Setup(d => d.GetMetricsAsync("sp_folder_done", 120, It.IsAny<CancellationToken>()))
               .ReturnsAsync((IReadOnlyList<MetricPoint>)new List<MetricPoint>());
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/phase");
        var json = await response.Content.ReadAsStringAsync();

        json.Should().Contain("\"phase\":\"transferring\"");
        json.Should().Contain("\"folderTotal\":100");
    }

    // ── /api/errors ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetErrors_ReturnsRecentFailedItems()
    {
        // 検証対象: GET /api/errors  目的: TransferDbSummary.RecentFailed をそのまま返す
        var summary = new TransferDbSummary
        {
            RecentFailed = [new FailedItem("docs/sub", "report.xlsx", "Timeout")]
        };
        _mockDb.Setup(d => d.GetSummaryAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(summary);
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/errors");
        var json = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.Should().Contain("report.xlsx");
        json.Should().Contain("Timeout");
    }

    // ── / ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetIndex_ReturnsHtmlDashboard()
    {
        // 検証対象: GET /  目的: ContentType=text/html, CloudMigrator ダッシュボード HTML を返す
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("CloudMigrator");
    }

    // ── /api/config GET ──────────────────────────────────────────────────

    [Fact]
    public async Task GetConfig_ReturnsOkWithConfigDto()
    {
        // 検証対象: GET /api/config  目的: IConfigurationService から ConfigDto を取得して 200 OK を返す
        var dto = new ConfigDto(
            MaxParallelTransfers: 20,
            MaxParallelFolderCreations: 4,
            ChunkSizeMb: 5,
            LargeFileThresholdMb: 4,
            RetryCount: 3,
            TimeoutSec: 300,
            DestinationRoot: "Documents/テスト",
            DestinationProvider: "sharepoint");
        _mockConfigService
            .Setup(s => s.GetConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"maxParallelTransfers\":20");
        body.Should().Contain("\"destinationRoot\":\"Documents/\u30c6\u30b9\u30c8\"");
    }

    // ── /api/config PUT ──────────────────────────────────────────────────

    [Fact]
    public async Task PutConfig_WithValidBody_Returns200()
    {
        // 検証対象: PUT /api/config  目的: 正常な JSON を送ると UpdateConfigAsync を呼び出して 200 を返す
        _mockConfigService
            .Setup(s => s.UpdateConfigAsync(It.IsAny<ConfigUpdateDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var client = await CreateClientAsync();

        var json = JsonSerializer.Serialize(new { maxParallelTransfers = 10, chunkSizeMb = 8 });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/api/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _mockConfigService.Verify(
            s => s.UpdateConfigAsync(It.Is<ConfigUpdateDto>(d => d.MaxParallelTransfers == 10 && d.ChunkSizeMb == 8),
                                     It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PutConfig_WithSecretKey_Returns400()
    {
        // 検証対象: PUT /api/config  目的: clientSecret を含む body は 400 BadRequest
        var client = await CreateClientAsync();

        var json = JsonSerializer.Serialize(new { maxParallelTransfers = 5, clientSecret = "leakme" });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/api/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _mockConfigService.Verify(s => s.UpdateConfigAsync(It.IsAny<ConfigUpdateDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0, 5, 3)]    // maxParallelTransfers=0 → 下限違反
    [InlineData(101, 5, 3)]  // maxParallelTransfers=101 → 上限違反
    [InlineData(1, 0, 3)]    // chunkSizeMb=0 → 下限違反
    [InlineData(1, 101, 3)]  // chunkSizeMb=101 → 上限違反
    [InlineData(1, 5, -1)]   // retryCount=-1 → 下限違反
    [InlineData(1, 5, 21)]   // retryCount=21 → 上限違反
    public async Task PutConfig_WithOutOfRangeValues_Returns400(int maxParallel, int chunkSize, int retryCount)
    {
        // 検証対象: PUT /api/config  目的: バリデーション範囲外の値は 400 BadRequest
        var client = await CreateClientAsync();

        var json = JsonSerializer.Serialize(new
        {
            maxParallelTransfers = maxParallel,
            chunkSizeMb = chunkSize,
            retryCount
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/api/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]   // maxParallelFolderCreations=0 → 下限違反
    [InlineData(33)]  // maxParallelFolderCreations=33 → 上限違反
    public async Task PutConfig_WithOutOfRangeMaxParallelFolderCreations_Returns400(int maxParallelFolderCreations)
    {
        // 検証対象: PUT /api/config  目的: maxParallelFolderCreations の範囲外値は 400 BadRequest
        var client = await CreateClientAsync();

        var json = JsonSerializer.Serialize(new { maxParallelFolderCreations });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/api/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PutConfig_WithPartialBody_PassesOnlySpecifiedFields()
    {
        // 検証対象: PUT /api/config  目的: 一部フィールドのみ送信すると、未指定フィールドは null のまま ConfigUpdateDto 経由で UpdateConfigAsync に渡される
        _mockConfigService
            .Setup(s => s.UpdateConfigAsync(It.IsAny<ConfigUpdateDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var client = await CreateClientAsync();

        // retryCount だけ送信: maxParallelTransfers は null として ConfigUpdateDto に入る
        var json = JsonSerializer.Serialize(new { retryCount = 5 });
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PutAsync("/api/config", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _mockConfigService.Verify(
            s => s.UpdateConfigAsync(
                It.Is<ConfigUpdateDto>(d => d.RetryCount == 5 && d.MaxParallelTransfers == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── /api/transfer/start POST ──────────────────────────────────────────

    [Fact]
    public async Task PostTransferStart_Returns202_WhenNoJobRunning()
    {
        // 検証対象: POST /api/transfer/start  目的: ジョブがない場合 202 Accepted + jobId を返す
        var job = new TransferJobInfo("test-guid-001", JobStatus.Pending, DateTimeOffset.UtcNow, null, null);
        _mockJobService
            .Setup(s => s.TryStartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        var client = await CreateClientAsync();

        var response = await client.PostAsync("/api/transfer/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("test-guid-001");
        json.Should().Contain("Pending");
    }

    [Fact]
    public async Task PostTransferStart_Returns409_WhenJobAlreadyRunning()
    {
        // 検証対象: POST /api/transfer/start  目的: ジョブ実行中の場合 409 Conflict を返す
        _mockJobService
            .Setup(s => s.TryStartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((TransferJobInfo?)null);
        var client = await CreateClientAsync();

        var response = await client.PostAsync("/api/transfer/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── /api/transfer/{id} GET ───────────────────────────────────────────

    [Fact]
    public async Task GetTransfer_Returns200_WithJobInfo()
    {
        // 検証対象: GET /api/transfer/{id}  目的: 存在する jobId を指定すると 200 OK とジョブ情報を返す
        var job = new TransferJobInfo("abc-def-123", JobStatus.Running, DateTimeOffset.UtcNow, null, null);
        _mockJobService
            .Setup(s => s.GetJob("abc-def-123"))
            .Returns(job);
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/transfer/abc-def-123");
        var json = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.Should().Contain("abc-def-123");
        json.Should().Contain("Running");
    }

    [Fact]
    public async Task GetTransfer_Returns404_WhenJobNotFound()
    {
        // 検証対象: GET /api/transfer/{id}  目的: 存在しない jobId は 404 NotFound を返す
        _mockJobService
            .Setup(s => s.GetJob(It.IsAny<string>()))
            .Returns((TransferJobInfo?)null);
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/transfer/nonexistent-id");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── /api/logs/stream GET (SSE) ───────────────────────────────────────

    [Fact]
    public async Task GetLogsStream_Returns200_AndCallsStreamAsync()
    {
        // 検証対象: GET /api/logs/stream  目的: ILogStreamService.StreamAsync が呼ばれ 200 OK を返す
        var mockLogSvc = new Mock<ILogStreamService>(MockBehavior.Strict);
        mockLogSvc
            .Setup(s => s.StreamAsync(It.IsAny<Microsoft.AspNetCore.Http.HttpContext>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _app = DashboardServer.BuildApp(
            _mockDb.Object,
            wb => wb.UseTestServer(),
            _mockConfigService.Object,
            _mockJobService.Object,
            mockLogSvc.Object);
        await _app.StartAsync();
        var client = _app.GetTestClient();

        var response = await client.GetAsync("/api/logs/stream");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        mockLogSvc.Verify(
            s => s.StreamAsync(It.IsAny<Microsoft.AspNetCore.Http.HttpContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── POST /api/setup/doctor ────────────────────────────────────────────

    [Fact]
    public async Task PostDoctor_ReturnsOkWithDoctorResult()
    {
        // 検証対象: POST /api/setup/doctor  目的: ISetupDoctorService.RunAsync を呼び出し 200 OK と結果を返す
        var expectedResult = new DoctorResult(
            OverallStatus.Healthy,
            [
                new DoctorCheck("Graph 認証", DoctorStatus.Pass, null),
                new DoctorCheck("SharePoint サイト", DoctorStatus.Pass, "sites/abc,xyz"),
                new DoctorCheck("ドキュメントライブラリ", DoctorStatus.Pass, null),
            ]);
        var mockDoctorSvc = new Mock<ISetupDoctorService>(MockBehavior.Strict);
        mockDoctorSvc
            .Setup(s => s.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);
        _app = DashboardServer.BuildApp(
            _mockDb.Object,
            wb => wb.UseTestServer(),
            _mockConfigService.Object,
            _mockJobService.Object,
            doctorService: mockDoctorSvc.Object);
        await _app.StartAsync();
        var client = _app.GetTestClient();

        var response = await client.PostAsync("/api/setup/doctor", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        mockDoctorSvc.Verify(s => s.RunAsync(It.IsAny<CancellationToken>()), Times.Once);
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var result = await response.Content.ReadFromJsonAsync<DoctorResult>(opts);
        result!.OverallStatus.Should().Be(OverallStatus.Healthy);
        result.Checks.Should().HaveCount(3);
        result.Checks[0].Name.Should().Be("Graph 認証");
        result.Checks.Should().AllSatisfy(c => c.Status.Should().Be(DoctorStatus.Pass));
    }

    [Fact]
    public async Task PostDoctor_WhenUnhealthy_ReturnsOkWithUnhealthyStatus()
    {
        // 検証対象: POST /api/setup/doctor（Unhealthy）
        // 目的: 認証失敗時に 200 OK かつ overallStatus=Unhealthy を返すことを確認する
        var failResult = new DoctorResult(
            OverallStatus.Unhealthy,
            [
                new DoctorCheck("Graph 認証", DoctorStatus.Fail, "トークン取得失敗 (HTTP 401)"),
                new DoctorCheck("SharePoint サイト", DoctorStatus.Fail, "認証失敗のためスキップ"),
                new DoctorCheck("ドキュメントライブラリ", DoctorStatus.Fail, "認証失敗のためスキップ"),
            ]);
        var mockDoctorSvc = new Mock<ISetupDoctorService>(MockBehavior.Strict);
        mockDoctorSvc
            .Setup(s => s.RunAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(failResult);
        _app = DashboardServer.BuildApp(
            _mockDb.Object,
            wb => wb.UseTestServer(),
            _mockConfigService.Object,
            _mockJobService.Object,
            doctorService: mockDoctorSvc.Object);
        await _app.StartAsync();
        var client = _app.GetTestClient();

        var response = await client.PostAsync("/api/setup/doctor", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };
        var result = await response.Content.ReadFromJsonAsync<DoctorResult>(opts);
        result!.OverallStatus.Should().Be(OverallStatus.Unhealthy);
        result.Checks.Should().AllSatisfy(c => c.Status.Should().Be(DoctorStatus.Fail));
    }

    // ── /api/db-status ────────────────────────────────────────────────────

    [Fact]
    public async Task GetDbStatus_WithDb_ReturnsConnectedTrue()
    {
        // 検証対象: GET /api/db-status  目的: 実 DB 使用時に connected=true・ requiresRestart=false を返す
        var summary = new TransferDbSummary();
        _mockDb.Setup(d => d.GetSummaryAsync(It.IsAny<CancellationToken>()))
               .ReturnsAsync(summary);
        var client = await CreateClientAsync();

        var response = await client.GetAsync("/api/db-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"connected\":true");
        json.Should().Contain("\"requiresRestart\":false");
    }

    [Fact]
    public async Task GetDbStatus_WithoutDb_ReturnsConnectedFalse_RequiresRestartFalse()
    {
        // 検証対象: GET /api/db-status  目的: NullTransferStateDb かつ DB ファイルなしの場合
        // connected=false・ requiresRestart=false (ファイルがまだ存在しない)を返す
        _app = DashboardServer.BuildApp(
            NullTransferStateDb.Instance,
            wb => wb.UseTestServer(),
            _mockConfigService.Object,
            _mockJobService.Object,
            dbPath: "/nonexistent/path/state.db");
        await _app.StartAsync();
        var client = _app.GetTestClient();

        var response = await client.GetAsync("/api/db-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"connected\":false");
        json.Should().Contain("\"requiresRestart\":false");
    }

    [Fact]
    public async Task GetStatus_WithoutDb_ReturnsEmptySummary()
    {
        // 検証対象: GET /api/status（DB なしモード）
        // 目的: NullTransferStateDb 使用時に全ゼロのサマリーを 200 OK で返す
        _app = DashboardServer.BuildApp(
            NullTransferStateDb.Instance,
            wb => wb.UseTestServer(),
            _mockConfigService.Object,
            _mockJobService.Object);
        await _app.StartAsync();
        var client = _app.GetTestClient();

        var response = await client.GetAsync("/api/status");
        var body = await response.Content.ReadFromJsonAsync<TransferDbSummary>();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!.Total.Should().Be(0);
        body.Done.Should().Be(0);
    }

    [Fact]
    public async Task GetMetrics_WithoutDb_ReturnsEmptyList()
    {
        // 検証対象: GET /api/metrics（DB なしモード）
        // 目的: NullTransferStateDb 使用時に空リストを 200 OK で返す
        _app = DashboardServer.BuildApp(
            NullTransferStateDb.Instance,
            wb => wb.UseTestServer(),
            _mockConfigService.Object,
            _mockJobService.Object);
        await _app.StartAsync();
        var client = _app.GetTestClient();

        var response = await client.GetAsync("/api/metrics");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Be("[]");
    }
}

