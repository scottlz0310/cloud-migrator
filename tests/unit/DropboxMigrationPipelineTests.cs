using System.Runtime.CompilerServices;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Migration;
using CloudMigrator.Core.State;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: DropboxMigrationPipeline  目的: Producer/Consumer パイプラインの各フェーズの動作確認
/// </summary>
public class DropboxMigrationPipelineTests
{
    private readonly Mock<IStorageProvider> _mockSource = new(MockBehavior.Loose);
    private readonly Mock<IStorageProvider> _mockDest = new(MockBehavior.Loose);
    private readonly Mock<ITransferStateDb> _mockDb = new(MockBehavior.Loose);
    private readonly MigratorOptions _options = new() { DestinationRoot = "/dropbox-root" };

    private DropboxMigrationPipeline CreatePipeline() =>
        new(_mockSource.Object, _mockDest.Object, _mockDb.Object, _options,
            NullLogger<DropboxMigrationPipeline>.Instance);

    // ── ヘルパー: 空 / 指定レコードを返す IAsyncEnumerable ──────────────────

#pragma warning disable CS1998 // 非同期メソッドに await 演算子がない
    private static async IAsyncEnumerable<TransferRecord> NoRecords(
        [EnumeratorCancellation] CancellationToken _ = default)
    {
        yield break;
    }

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

    private static TransferRecord RecordFrom(StorageItem item) =>
        new TransferRecord
        {
            SourceId = item.Id,
            Path = item.Path,
            Name = item.Name,
            SizeBytes = item.SizeBytes,
            Status = TransferStatus.Pending,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    // ── 共通セットアップ ──────────────────────────────────────────────────

    private void SetupDbBase()
    {
        _mockDb.Setup(db => db.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.ResetProcessingAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.ResetPermanentFailedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(NoRecords());
        _mockDb.Setup(db => db.GetCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.GetSummaryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new TransferDbSummary());
        // Phase B: InsertPendingIfNewAsync はデフォルト false（既存アイテム扱い）
        _mockDb.Setup(db => db.InsertPendingIfNewAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _mockDb.Setup(db => db.MarkProcessingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkDoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkFailedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.SaveCheckpointAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private void SetupDestBase()
    {
        _mockDest.Setup(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDest.Setup(d => d.UploadFromStreamAsync(It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    // ── RunAsync: 空の DB / 空の source ──────────────────────────────────

    [Fact]
    public async Task RunAsync_EmptyDbAndSource_ReturnsZeroCounts()
    {
        // 検証対象: RunAsync  目的: DB も source も空のとき success=0, failed=0 を返す
        SetupDbBase();
        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(EmptyPage());

        var summary = await CreatePipeline().RunAsync(CancellationToken.None);

        summary.Success.Should().Be(0);
        summary.Failed.Should().Be(0);
    }

    // ── Phase B: 新規アイテムの転送 ───────────────────────────────────────

    [Fact]
    public async Task RunAsync_NewItemFromPhaseB_TransfersSuccessfullyAndReturnsSuccess1()
    {
        // 検証対象: Phase B クロール  目的: 新規アイテムが InsertPendingIfNewAsync で登録され、Phase D で転送されて success=1 になる
        var item = MakeItem("docs", "report.pdf", "od-abc");
        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbBase();
            // Phase B: 新規アイテム → InsertPendingIfNewAsync が true を返す
            _mockDb.Setup(db => db.InsertPendingIfNewAsync(item, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
            // Phase D: GetPendingStreamAsync が Phase B で登録されたアイテムを返す
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>()))
                   .Returns(FromRecords([RecordFrom(item)]));

            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(SingleItemPage(item));
            _mockSource.Setup(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .Returns(() => Task.FromResult<Stream>(File.OpenRead(tempFile)));

            SetupDestBase();

            var summary = await CreatePipeline().RunAsync(CancellationToken.None);

            summary.Success.Should().Be(1);
            summary.Failed.Should().Be(0);
            _mockDb.Verify(db => db.InsertPendingIfNewAsync(item, It.IsAny<CancellationToken>()), Times.Once);
            _mockDb.Verify(db => db.MarkDoneAsync("docs", "report.pdf", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            // DropboxMigrationPipeline が File.Delete を行うため残存しない場合もある
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Phase B: done / permanent_failed のスキップ ───────────────────────

    [Theory]
    [InlineData(TransferStatus.Done)]
    [InlineData(TransferStatus.PermanentFailed)]
    public async Task RunAsync_ItemWithTerminalStatus_SkipsTransfer(TransferStatus status)
    {
        // 検証対象: Phase B  目的: 既存アイテムは InsertPendingIfNewAsync が false を返し転送されない
        // InsertPendingIfNewAsync の ON CONFLICT DO NOTHING により、ステータス問わず既存行はスキップされる
        _ = status; // InsertPendingIfNewAsync はステータスを区別しない（DB 側で DO NOTHING）
        var item = MakeItem("docs", "done.txt");

        SetupDbBase(); // InsertPendingIfNewAsync はデフォルト false（既存アイテム）

        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(SingleItemPage(item));

        var summary = await CreatePipeline().RunAsync(CancellationToken.None);

        summary.Success.Should().Be(0);
        summary.Failed.Should().Be(0);
        _mockDb.Verify(db => db.InsertPendingIfNewAsync(item, It.IsAny<CancellationToken>()), Times.Once);
        _mockSource.Verify(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Phase B: pending/processing/failed はスキップ（Phase D の GetPendingStreamAsync でキューイングされる）──

    [Theory]
    [InlineData(TransferStatus.Pending)]
    [InlineData(TransferStatus.Processing)]
    [InlineData(TransferStatus.Failed)]
    public async Task RunAsync_ItemAlreadyQueuedStatus_SkipsUpsertInPhaseB(TransferStatus status)
    {
        // 検証対象: Phase B  目的: 既存アイテムは InsertPendingIfNewAsync が false を返し再 INSERT されない
        // pending/processing/failed → Phase D の GetPendingStreamAsync でキューイングされる
        _ = status; // InsertPendingIfNewAsync はステータスを区別しない（DO NOTHING で一律スキップ）
        var item = MakeItem("docs", "queued.txt");

        SetupDbBase(); // InsertPendingIfNewAsync はデフォルト false（既存アイテム）

        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(SingleItemPage(item));

        await CreatePipeline().RunAsync(CancellationToken.None);

        // InsertPendingIfNewAsync は呼ばれるが false（既存）を返すため新規登録は行われない
        _mockDb.Verify(db => db.InsertPendingIfNewAsync(item, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Phase A: クラッシュリカバリ ───────────────────────────────────────

    [Fact]
    public async Task RunAsync_PendingRecordFromPhaseA_RecoveredAndTransferred()
    {
        // 検証対象: Phase A リカバリ  目的: DB の pending レコードが転送され、SourceId が使用される
        var record = new TransferRecord
        {
            SourceId = "od-recovery-id",
            Path = "docs",
            Name = "recover.txt",
            SizeBytes = 256,
            Status = TransferStatus.Pending,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbBase();
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>()))
                   .Returns(FromRecords([record]));

            // Phase B は空ページ返却
            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(EmptyPage());
            _mockSource.Setup(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .Returns(() => Task.FromResult<Stream>(File.OpenRead(tempFile)));

            SetupDestBase();

            var summary = await CreatePipeline().RunAsync(CancellationToken.None);

            summary.Success.Should().Be(1);
            // SourceId が StorageItem.Id として DownloadStreamAsync に渡されることを検証
            _mockSource.Verify(s => s.DownloadStreamAsync(
                It.Is<StorageItem>(i => i.Id == "od-recovery-id"),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Consumerエラー処理: MarkFailedAsync が呼ばれる ────────────────────

    [Fact]
    public async Task RunAsync_DownloadThrows_CallsMarkFailedAndReturnsFailed1()
    {
        // 検証対象: ConsumeAsync エラーハンドラ  目的: DownloadStreamAsync の例外で MarkFailedAsync が呼ばれる
        var item = MakeItem("data", "bad.bin", "od-bad");

        SetupDbBase();
        _mockDb.Setup(db => db.InsertPendingIfNewAsync(item, It.IsAny<CancellationToken>()))
               .ReturnsAsync(true);
        _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>()))
               .Returns(FromRecords([RecordFrom(item)]));

        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(SingleItemPage(item));
        _mockSource.Setup(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new IOException("ダウンロードネットワークエラー"));

        SetupDestBase();

        var summary = await CreatePipeline().RunAsync(CancellationToken.None);

        summary.Success.Should().Be(0);
        summary.Failed.Should().Be(1);
        _mockDb.Verify(db => db.MarkFailedAsync("data", "bad.bin",
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_UploadThrows_CallsMarkFailedAndReturnsFailed1()
    {
        // 検証対象: ConsumeAsync エラーハンドラ  目的: UploadFromStreamAsync の例外で MarkFailedAsync が呼ばれる
        var item = MakeItem("data", "upload-fail.bin", "od-uf");
        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbBase();
            _mockDb.Setup(db => db.InsertPendingIfNewAsync(item, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>()))
                   .Returns(FromRecords([RecordFrom(item)]));

            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(SingleItemPage(item));
            _mockSource.Setup(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .Returns(() => Task.FromResult<Stream>(File.OpenRead(tempFile)));

            _mockDest.Setup(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _mockDest.Setup(d => d.UploadFromStreamAsync(It.IsAny<Stream>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(new HttpRequestException("500 サーバーエラー"));

            var summary = await CreatePipeline().RunAsync(CancellationToken.None);

            summary.Success.Should().Be(0);
            summary.Failed.Should().Be(1);
            _mockDb.Verify(db => db.MarkFailedAsync("data", "upload-fail.bin",
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Phase B: 複数ページのチェックポイント保存 ────────────────────────

    [Fact]
    public async Task RunAsync_MultiPage_SavesCheckpointAfterFirstPage()
    {
        // 検証対象: Phase B ページネーション  目的: cursor 付きページ後に SaveCheckpointAsync が呼ばれる
        SetupDbBase();

        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new StoragePage { Items = [], HasMore = true, Cursor = "cur1" });
        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), "cur1", It.IsAny<CancellationToken>()))
                   .ReturnsAsync(EmptyPage());

        await CreatePipeline().RunAsync(CancellationToken.None);

        // カーソル "cur1" がチェックポイントとして保存されている
        _mockDb.Verify(db => db.SaveCheckpointAsync(
            DropboxMigrationPipeline.CursorKey, "cur1", It.IsAny<CancellationToken>()), Times.Once);
        // ページ 2（cursor="cur1"）が呼ばれている
        _mockSource.Verify(s => s.ListPagedAsync(
            DropboxMigrationPipeline.SourceRootPath, "cur1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── フォルダアイテムはスキップされる ─────────────────────────────────

    [Fact]
    public async Task RunAsync_FolderItem_IsSkippedInPhaseB()
    {
        // 検証対象: Phase B フィルタリング  目的: IsFolder=true のアイテムは転送されない
        var folder = new StorageItem { Id = "folder-1", Name = "MyFolder", Path = "root", IsFolder = true };

        SetupDbBase();
        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(SingleItemPage(folder));

        var summary = await CreatePipeline().RunAsync(CancellationToken.None);

        summary.Success.Should().Be(0);
        _mockDb.Verify(db => db.InsertPendingIfNewAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockSource.Verify(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── EnsureFolder Feature Flag ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_EnableEnsureFolderFalse_DoesNotCallEnsureFolder()
    {
        // 検証対象: Feature Flag  目的: EnableEnsureFolder=false（デフォルト）のとき EnsureFolderAsync が呼ばれない
        var item = MakeItem("docs", "report.pdf", "od-ff-off");
        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbBase();
            _mockDb.Setup(db => db.InsertPendingIfNewAsync(item, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>()))
                   .Returns(FromRecords([RecordFrom(item)]));
            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(SingleItemPage(item));
            _mockSource.Setup(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .Returns(() => Task.FromResult<Stream>(File.OpenRead(tempFile)));
            SetupDestBase();

            // EnableEnsureFolder はデフォルト false
            var summary = await CreatePipeline().RunAsync(CancellationToken.None);

            summary.Success.Should().Be(1);
            _mockDest.Verify(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_EnableEnsureFolderTrue_CallsEnsureFolder()
    {
        // 検証対象: Feature Flag  目的: EnableEnsureFolder=true のとき EnsureFolderAsync が 1 回呼ばれる
        var item = MakeItem("docs", "report.pdf", "od-ff-on");
        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbBase();
            _mockDb.Setup(db => db.InsertPendingIfNewAsync(item, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>()))
                   .Returns(FromRecords([RecordFrom(item)]));
            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(SingleItemPage(item));
            _mockSource.Setup(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .Returns(() => Task.FromResult<Stream>(File.OpenRead(tempFile)));
            SetupDestBase();

            var options = new MigratorOptions
            {
                DestinationRoot = "/dropbox-root",
                Dropbox = new CloudMigrator.Core.Configuration.DropboxProviderOptions { EnableEnsureFolder = true },
            };
            var pipeline = new DropboxMigrationPipeline(
                _mockSource.Object, _mockDest.Object, _mockDb.Object, options,
                NullLogger<DropboxMigrationPipeline>.Instance);

            var summary = await pipeline.RunAsync(CancellationToken.None);

            summary.Success.Should().Be(1);
            _mockDest.Verify(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── Phase B: throughput メトリクス記録 ──────────────────────────────────

    [Fact]
    public async Task ProduceAsync_CrawlComplete_SavesCrawlTotalThenCrawlComplete()
    {
        // 検証対象: Phase B 完了後のチェックポイント保存順序
        // 目的: crawl_total → crawl_complete の順に SaveCheckpointAsync が呼ばれることを確認する
        var item = MakeItem("docs", "a.txt");
        var tempFile = Path.GetTempFileName();
        var callOrder = new List<string>();
        try
        {
            SetupDbBase();
            _mockDb.Setup(db => db.InsertPendingIfNewAsync(item, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
            // crawl_total / crawl_complete 保存時の呼び出し順を記録
            _mockDb.Setup(db => db.SaveCheckpointAsync(
                DropboxMigrationPipeline.CrawlTotalKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback((string k, string _, CancellationToken __) => callOrder.Add(k))
                .Returns(Task.CompletedTask);
            _mockDb.Setup(db => db.SaveCheckpointAsync(
                DropboxMigrationPipeline.CrawlCompleteKey, "true", It.IsAny<CancellationToken>()))
                .Callback((string k, string _, CancellationToken __) => callOrder.Add(k + "=true"))
                .Returns(Task.CompletedTask);
            // GetSummaryAsync が Total=1 を返すよう設定
            _mockDb.Setup(db => db.GetSummaryAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new TransferDbSummary { Pending = 0, Done = 1 });
            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(SingleItemPage(item));
            _mockSource.Setup(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .Returns(() => Task.FromResult<Stream>(File.OpenRead(tempFile)));
            SetupDestBase();

            await CreatePipeline().RunAsync(CancellationToken.None);

            // crawl_total が crawl_complete より先に保存されること
            callOrder.Should().ContainInOrder(DropboxMigrationPipeline.CrawlTotalKey, "crawl_complete=true");
            // crawl_total に "1" が保存されること
            _mockDb.Verify(
                db => db.SaveCheckpointAsync(DropboxMigrationPipeline.CrawlTotalKey, "1", It.IsAny<CancellationToken>()),
                Times.Once);
            // crawl_complete に "false"（開始時リセット）と "true"（完了時）が両方保存されること
            _mockDb.Verify(
                db => db.SaveCheckpointAsync(DropboxMigrationPipeline.CrawlCompleteKey, "false", It.IsAny<CancellationToken>()),
                Times.Once);
            _mockDb.Verify(
                db => db.SaveCheckpointAsync(DropboxMigrationPipeline.CrawlCompleteKey, "true", It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ── current_parallelism 即時記録（変化検出） ─────────────────────────────

    [Fact]
    public async Task RunAsync_WithAdaptiveController_RecordsCurrentParallelismOnFirstTransfer()
    {
        // 検証対象: current_parallelism 即時記録
        // 目的: _lastRecordedParallelism=-1（初期値）から CurrentDegree=4 への「変化」が検出され、
        //       RecordMetricAsync("current_parallelism", 4, ...) が呼ばれること
        var item = MakeItem("docs", "a.txt");
        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbBase();
            _mockDb.Setup(db => db.InsertPendingIfNewAsync(item, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>()))
                   .Returns(FromRecords([RecordFrom(item)]));
            _mockDb.Setup(db => db.RecordMetricAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);
            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(SingleItemPage(item));
            _mockSource.Setup(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .Returns(() => Task.FromResult<Stream>(File.OpenRead(tempFile)));
            SetupDestBase();

            using var controller = new AdaptiveConcurrencyController(
                initialDegree: 4, minDegree: 1, maxDegree: 4, increaseIntervalSec: 30,
                Mock.Of<ILogger<AdaptiveConcurrencyController>>());

            var pipeline = new DropboxMigrationPipeline(
                _mockSource.Object, _mockDest.Object, _mockDb.Object, _options,
                NullLogger<DropboxMigrationPipeline>.Instance,
                concurrencyController: controller);

            await pipeline.RunAsync(CancellationToken.None);

            // _lastRecordedParallelism=-1 → CurrentDegree=4 への変化で即時記録される
            _mockDb.Verify(
                db => db.RecordMetricAsync(
                    "current_parallelism",
                    4.0,
                    It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task RunAsync_100Transfers_RecordsThroughputBytesPerSec()
    {
        // 検証対象: throughput メトリクス記録  目的: 100 件転送完了で throughput_bytes_per_sec が RecordMetricAsync に記録される
        var tempFile = Path.GetTempFileName();
        var items = Enumerable.Range(1, 100)
            .Select(i => MakeItem("docs", $"file{i:D3}.bin", $"od-{i}", size: 1024))
            .ToList();
        try
        {
            SetupDbBase();
            _mockDb.Setup(db => db.InsertPendingIfNewAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(true);
            // Phase D: GetPendingStreamAsync が 100 件の全アイテムを返す
            _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>()))
                   .Returns(FromRecords(items.Select(RecordFrom)));
            _mockDb.Setup(db => db.RecordMetricAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                   .Returns(Task.CompletedTask);
            var page = new StoragePage { Items = items, HasMore = false, Cursor = null };
            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(page);
            _mockSource.Setup(s => s.DownloadStreamAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .Returns(() => Task.FromResult<Stream>(File.OpenRead(tempFile)));
            SetupDestBase();

            var summary = await CreatePipeline().RunAsync(CancellationToken.None);

            summary.Success.Should().Be(100);
            // 100 回目の転送完了で throughput_bytes_per_sec が記録される
            _mockDb.Verify(
                db => db.RecordMetricAsync(
                    "throughput_bytes_per_sec",
                    It.IsAny<double>(),
                    It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
