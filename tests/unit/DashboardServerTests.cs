using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
    private WebApplication? _app;

    private async Task<HttpClient> CreateClientAsync()
    {
        _app = DashboardServer.BuildApp(_mockDb.Object, wb => wb.UseTestServer(), _mockConfigService.Object);
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

    [Fact]
    public async Task PutConfig_WithPartialBody_PassesOnlySpecifiedFields()
    {
        // 検証対象: PUT /api/config  目的: 一部フィールドのみ送信すると UpdateConfigAsync には null なしで渡される
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
}

