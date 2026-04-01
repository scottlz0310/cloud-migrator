using System.Net;
using System.Net.Http.Json;
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
    private WebApplication? _app;

    private async Task<HttpClient> CreateClientAsync()
    {
        _app = DashboardServer.BuildApp(_mockDb.Object, wb => wb.UseTestServer());
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
}
