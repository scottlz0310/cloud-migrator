using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Migration;
using CloudMigrator.Core.State;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: SharePointMigrationPipeline
/// 目的: 4フェーズ構造（リカバリ / クロール / フォルダ先行作成 / 転送）の動作確認
/// </summary>
public class SharePointMigrationPipelineTests
{
    private readonly Mock<IStorageProvider> _mockSource = new(MockBehavior.Loose);
    private readonly Mock<IStorageProvider> _mockDest = new(MockBehavior.Loose);
    private readonly Mock<ITransferStateDb> _mockDb = new(MockBehavior.Loose);
    private readonly MigratorOptions _options = new() { DestinationRoot = "SP-Root", MaxParallelFolderCreations = 2 };

    private SharePointMigrationPipeline CreatePipeline(AdaptiveConcurrencyController? controller = null) =>
        new(_mockSource.Object, _mockDest.Object, _mockDb.Object, _options,
            NullLogger<SharePointMigrationPipeline>.Instance, controller);

    // ── ヘルパー ──────────────────────────────────────────────────────────

#pragma warning disable CS1998
    private static async IAsyncEnumerable<TransferRecord> NoRecords(
        [EnumeratorCancellation] CancellationToken _ = default)
    { yield break; }

    private static async IAsyncEnumerable<TransferRecord> FromRecords(
        IEnumerable<TransferRecord> records,
        [EnumeratorCancellation] CancellationToken _ = default)
    {
        foreach (var r in records) yield return r;
    }
#pragma warning restore CS1998

    private static StoragePage EmptyPage() =>
        new() { Items = [], HasMore = false, Cursor = null };

    private static StoragePage SingleItemPage(StorageItem item, string? cursor = null, bool hasMore = false) =>
        new() { Items = [item], HasMore = hasMore, Cursor = cursor };

    private static StorageItem MakeItem(string path, string name, string id = "od-1", long size = 512) =>
        new() { Id = id, Name = name, Path = path, SizeBytes = size, IsFolder = false };

    private static TransferRecord MakeRecord(string path, string name, TransferStatus status = TransferStatus.Pending, long sizeBytes = 512) =>
        new()
        {
            SourceId = "id-1",
            Path = path,
            Name = name,
            SizeBytes = sizeBytes,
            Status = status,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// 基本的な DB モック設定:
    /// - Phase B スキップ (crawl_complete=true)
    /// - Phase C スキップ (folder_creation_complete=true)
    /// - その他は Task.CompletedTask / null
    /// </summary>
    private void SetupDbWithBothPhasesComplete()
    {
        _mockDb.Setup(db => db.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.ResetProcessingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.ResetPermanentFailedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(NoRecords());
        _mockDb.Setup(db => db.GetCheckpointAsync("pipeline_started_at", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.GetCheckpointAsync(SharePointMigrationPipeline.CrawlCompleteKey, It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockDb.Setup(db => db.GetCheckpointAsync(SharePointMigrationPipeline.FolderCreationCompleteKey, It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockDb.Setup(db => db.GetCheckpointAsync(It.IsNotIn("pipeline_started_at", SharePointMigrationPipeline.CrawlCompleteKey, SharePointMigrationPipeline.FolderCreationCompleteKey), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.SaveCheckpointAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.UpsertPendingAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkProcessingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkDoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkFailedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetSummaryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TransferDbSummary());
        _mockDb.Setup(db => db.RecordMetricAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetDistinctFolderPathsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    private void SetupDestBase()
    {
        _mockDest.Setup(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDest.Setup(d => d.UploadFromLocalAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        // モック先プロバイダーはサーバーサイドコピー非対応としてクライアント経由フォールバックを強制する
        _mockDest.Setup(d => d.ServerSideCopyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new NotSupportedException());
    }

    // ── Phase A: ResetProcessingAsync が呼ばれる ──────────────────────────

    [Fact]
    public async Task RunAsync_AlwaysCallsResetProcessingAsync()
    {
        // 検証対象: Phase A  目的: 起動時に必ず ResetProcessingAsync が呼ばれる
        SetupDbWithBothPhasesComplete();

        await CreatePipeline().RunAsync(CancellationToken.None);

        _mockDb.Verify(db => db.ResetProcessingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Phase B: クロールスキップ ──────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenCrawlCompleteTrue_SkipsPhaseBCrawl()
    {
        // 検証対象: Phase B  目的: crawl_complete="true" のとき ListPagedAsync が呼ばれない
        SetupDbWithBothPhasesComplete();
        // crawl_complete = true はすでにセットアップ済み

        await CreatePipeline().RunAsync(CancellationToken.None);

        _mockSource.Verify(s => s.ListPagedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Phase B: クロール実行 ──────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenCrawlNotComplete_RunsPhaseBAndUpsertsPending()
    {
        // 検証対象: Phase B  目的: crawl_complete=null のとき ListPagedAsync 呼び出し + UpsertPendingAsync
        var item = MakeItem("docs", "report.pdf", "od-abc");

        _mockDb.Setup(db => db.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.ResetProcessingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(NoRecords());
        _mockDb.Setup(db => db.GetCheckpointAsync("pipeline_started_at", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.GetCheckpointAsync(SharePointMigrationPipeline.CrawlCompleteKey, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.GetCheckpointAsync(SharePointMigrationPipeline.SpCursorKey, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.GetCheckpointAsync(SharePointMigrationPipeline.FolderCreationCompleteKey, It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockDb.Setup(db => db.GetCheckpointAsync(It.IsNotIn("pipeline_started_at", SharePointMigrationPipeline.CrawlCompleteKey, SharePointMigrationPipeline.SpCursorKey, SharePointMigrationPipeline.FolderCreationCompleteKey), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.SaveCheckpointAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.UpsertPendingIfNotTerminalAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkProcessingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkDoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkFailedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetSummaryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TransferDbSummary());
        _mockDb.Setup(db => db.RecordMetricAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetDistinctFolderPathsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        _mockSource.Setup(s => s.ListPagedAsync(SharePointMigrationPipeline.SourceRootPath, null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(SingleItemPage(item, cursor: "cur-1"));

        await CreatePipeline().RunAsync(CancellationToken.None);

        _mockSource.Verify(s => s.ListPagedAsync(SharePointMigrationPipeline.SourceRootPath, null, It.IsAny<CancellationToken>()), Times.Once);
        _mockDb.Verify(db => db.UpsertPendingIfNotTerminalAsync(item, It.IsAny<CancellationToken>()), Times.Once);
        // カーソルがチェックポイントに保存される
        _mockDb.Verify(db => db.SaveCheckpointAsync(SharePointMigrationPipeline.SpCursorKey, "cur-1", It.IsAny<CancellationToken>()), Times.Once);
        // Phase B 完了フラグが保存される
        _mockDb.Verify(db => db.SaveCheckpointAsync(SharePointMigrationPipeline.CrawlCompleteKey, "true", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Phase B: フォルダアイテムはスキップ ───────────────────────────────

    [Fact]
    public async Task RunAsync_PhaseBSkipsFolderItems()
    {
        // 検証対象: Phase B  目的: IsFolder=true のアイテムは UpsertPending されない
        var folder = new StorageItem { Id = "f-1", Name = "FolderA", Path = "docs", IsFolder = true };

        _mockDb.Setup(db => db.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.ResetProcessingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(NoRecords());
        _mockDb.Setup(db => db.GetCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.GetCheckpointAsync(SharePointMigrationPipeline.FolderCreationCompleteKey, It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockDb.Setup(db => db.SaveCheckpointAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetSummaryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TransferDbSummary());
        _mockDb.Setup(db => db.RecordMetricAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetDistinctFolderPathsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new StoragePage { Items = [folder], HasMore = false, Cursor = null });

        await CreatePipeline().RunAsync(CancellationToken.None);

        _mockDb.Verify(db => db.UpsertPendingAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Phase C: フォルダ作成スキップ ──────────────────────────────────

    [Fact]
    public async Task RunAsync_WhenFolderCreationCompleteTrue_SkipsPhaseC()
    {
        // 検証対象: Phase C  目的: folder_creation_complete="true" のとき EnsureFolderAsync が呼ばれない
        SetupDbWithBothPhasesComplete();

        await CreatePipeline().RunAsync(CancellationToken.None);

        _mockDest.Verify(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Phase C: フォルダ先行作成（深さ順）───────────────────────────────

    [Fact]
    public async Task RunAsync_PhaseCCreatesAncestorFoldersInDepthOrder()
    {
        // 検証対象: Phase C  目的: DB の DISTINCT paths から祖先フォルダを展開して EnsureFolderAsync が呼ばれる
        // paths: ["docs/projects"] → ancestors: ["docs", "docs/projects"]
        var createdFolders = new ConcurrentBag<string>();

        _mockDb.Setup(db => db.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.ResetProcessingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(NoRecords());
        _mockDb.Setup(db => db.GetCheckpointAsync("pipeline_started_at", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.GetCheckpointAsync(SharePointMigrationPipeline.CrawlCompleteKey, It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockDb.Setup(db => db.GetCheckpointAsync(SharePointMigrationPipeline.FolderCreationCompleteKey, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.GetCheckpointAsync(It.IsNotIn("pipeline_started_at", SharePointMigrationPipeline.CrawlCompleteKey, SharePointMigrationPipeline.FolderCreationCompleteKey), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.SaveCheckpointAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.RecordMetricAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetDistinctFolderPathsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["docs/projects"]);
        _mockDest.Setup(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback<string, CancellationToken>((p, _) => createdFolders.Add(p))
                 .Returns(Task.CompletedTask);

        await CreatePipeline().RunAsync(CancellationToken.None);

        // 祖先フォルダ ("docs", "docs/projects") + DestinationRoot プレフィックス付きで呼ばれる
        createdFolders.Should().BeEquivalentTo(["SP-Root/docs", "SP-Root/docs/projects"]);
        // folder_total チェックポイントが保存される
        _mockDb.Verify(db => db.SaveCheckpointAsync(SharePointMigrationPipeline.FolderTotalKey, "2", It.IsAny<CancellationToken>()), Times.Once);
        // folder_creation_complete チェックポイントが保存される
        _mockDb.Verify(db => db.SaveCheckpointAsync(SharePointMigrationPipeline.FolderCreationCompleteKey, "true", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Phase C: 重複パスの一意化 ─────────────────────────────────────

    [Fact]
    public async Task RunAsync_PhaseCDeduplicatesPaths()
    {
        // 検証対象: Phase C  目的: 同一祖先が複数回 EnsureFolderAsync されない
        var createdFolders = new ConcurrentBag<string>();

        _mockDb.Setup(db => db.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.ResetProcessingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(NoRecords());
        _mockDb.Setup(db => db.GetCheckpointAsync("pipeline_started_at", It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.GetCheckpointAsync(SharePointMigrationPipeline.CrawlCompleteKey, It.IsAny<CancellationToken>())).ReturnsAsync("true");
        _mockDb.Setup(db => db.GetCheckpointAsync(SharePointMigrationPipeline.FolderCreationCompleteKey, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.GetCheckpointAsync(It.IsNotIn("pipeline_started_at", SharePointMigrationPipeline.CrawlCompleteKey, SharePointMigrationPipeline.FolderCreationCompleteKey), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.SaveCheckpointAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.RecordMetricAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        // 2つのファイルが同じ "docs" 祖先を持つ
        _mockDb.Setup(db => db.GetDistinctFolderPathsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["docs/projects", "docs/reports"]);
        _mockDest.Setup(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback<string, CancellationToken>((p, _) => createdFolders.Add(p))
                 .Returns(Task.CompletedTask);

        await CreatePipeline().RunAsync(CancellationToken.None);

        // "docs" は1回だけ作成される（重複除去）
        createdFolders.Count(f => f == "SP-Root/docs").Should().Be(1);
        createdFolders.Should().BeEquivalentTo(["SP-Root/docs", "SP-Root/docs/projects", "SP-Root/docs/reports"]);
    }

    // ── Phase D: 転送成功 ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PhaseDTransferSuccess_CallsMarkDoneAndReturnsSuccess1()
    {
        // 検証対象: Phase D  目的: pending レコードが転送成功 → MarkDoneAsync が呼ばれ success=1 になる
        var record = MakeRecord("docs", "file.pdf");
        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbWithBothPhasesComplete();
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(FromRecords([record]));
            _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(tempFile);
            SetupDestBase();

            var summary = await CreatePipeline().RunAsync(CancellationToken.None);

            summary.Success.Should().Be(1);
            summary.Failed.Should().Be(0);
            _mockDb.Verify(db => db.MarkDoneAsync("docs", "file.pdf", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Phase D: 転送失敗 ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PhaseDTransferFailure_CallsMarkFailedAndReturnsFailed1()
    {
        // 検証対象: Phase D  目的: 転送失敗 → MarkFailedAsync が呼ばれ failed=1 になる
        var record = MakeRecord("docs", "broken.pdf");

        SetupDbWithBothPhasesComplete();
        _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(FromRecords([record]));
        _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new IOException("ダウンロード失敗"));

        SetupDestBase();

        var summary = await CreatePipeline().RunAsync(CancellationToken.None);

        summary.Success.Should().Be(0);
        summary.Failed.Should().Be(1);
        _mockDb.Verify(db => db.MarkFailedAsync("docs", "broken.pdf", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _mockDb.Verify(db => db.MarkDoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Phase D: 100 件転送でスループットメトリクス記録 ──────────────────

    [Fact]
    public async Task RunAsync_100Transfers_RecordsThroughputMetrics()
    {
        // 検証対象: Phase D  目的: 100 件転送ごとに throughput/rate_limit_pct メトリクスが記録される
        var records = Enumerable.Range(0, 100)
            .Select(i => MakeRecord("docs", $"file{i:D3}.txt"))
            .ToList();

        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbWithBothPhasesComplete();
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(FromRecords(records));
            _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(tempFile);
            SetupDestBase();

            var summary = await CreatePipeline().RunAsync(CancellationToken.None);

            summary.Success.Should().Be(100);
            _mockDb.Verify(db => db.RecordMetricAsync("throughput_files_per_min", It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockDb.Verify(db => db.RecordMetricAsync("throughput_bytes_per_sec", It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockDb.Verify(db => db.RecordMetricAsync("rate_limit_pct", It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Phase D: current_parallelism 即時記録（controller あり）──────────

    [Fact]
    public async Task RunAsync_WithAdaptiveController_RecordsCurrentParallelism()
    {
        // 検証対象: Phase D  目的: AdaptiveConcurrencyController が有効なとき並列度変化時に即時記録される
        var record = MakeRecord("docs", "file.pdf");
        var tempFile = Path.GetTempFileName();
        try
        {
            var controller = new AdaptiveConcurrencyController(
                initialDegree: _options.MaxParallelTransfers,
                minDegree: 1,
                maxDegree: _options.MaxParallelTransfers,
                successThreshold: 10,
                NullLogger<AdaptiveConcurrencyController>.Instance);

            SetupDbWithBothPhasesComplete();
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(FromRecords([record]));
            _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(tempFile);
            SetupDestBase();

            var summary = await CreatePipeline(controller).RunAsync(CancellationToken.None);

            summary.Success.Should().Be(1);
            // 並列度変化があった場合（初回: -1 → 実際の値）に記録される
            _mockDb.Verify(db => db.RecordMetricAsync("current_parallelism", It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_PhaseDServerSideCopySuccess_SkipsClientTransfer()
    {
        var record = MakeRecord("docs", "copied.pdf");

        SetupDbWithBothPhasesComplete();
        _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(FromRecords([record]));
        _mockDest.Setup(d => d.ServerSideCopyAsync("id-1", "SP-Root/docs", "copied.pdf", It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        var summary = await CreatePipeline().RunAsync(CancellationToken.None);

        summary.Success.Should().Be(1);
        summary.Failed.Should().Be(0);
        _mockSource.Verify(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockDest.Verify(d => d.UploadFromLocalAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockDb.Verify(db => db.MarkDoneAsync("docs", "copied.pdf", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_PhaseDServerSideCopyFailure_FallsBackToClientTransfer()
    {
        var record = MakeRecord("docs", "fallback.pdf");
        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbWithBothPhasesComplete();
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(FromRecords([record]));
            _mockDest.Setup(d => d.ServerSideCopyAsync("id-1", "SP-Root/docs", "fallback.pdf", It.IsAny<CancellationToken>()))
                     .ThrowsAsync(new InvalidOperationException("copy failed"));
            _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(tempFile);
            _mockDest.Setup(d => d.UploadFromLocalAsync(tempFile, record.SizeBytes!.Value, "SP-Root/docs/fallback.pdf", It.IsAny<CancellationToken>()))
                     .Returns(Task.CompletedTask);

            var summary = await CreatePipeline().RunAsync(CancellationToken.None);

            summary.Success.Should().Be(1);
            summary.Failed.Should().Be(0);
            _mockSource.Verify(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockDest.Verify(d => d.UploadFromLocalAsync(tempFile, record.SizeBytes!.Value, "SP-Root/docs/fallback.pdf", It.IsAny<CancellationToken>()), Times.Once);
            _mockDb.Verify(db => db.MarkDoneAsync("docs", "fallback.pdf", It.IsAny<CancellationToken>()), Times.Once);
            _mockDb.Verify(db => db.MarkFailedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Phase D: 空の DB で success=0, failed=0 ───────────────────────

    [Fact]
    public async Task RunAsync_BothPhasesCompleteEmptyPending_ReturnsZeroCounts()
    {
        // 検証対象: RunAsync  目的: Phase B/C スキップ・pending 0 件なら success=0, failed=0
        SetupDbWithBothPhasesComplete();

        var summary = await CreatePipeline().RunAsync(CancellationToken.None);

        summary.Success.Should().Be(0);
        summary.Failed.Should().Be(0);
    }
}
