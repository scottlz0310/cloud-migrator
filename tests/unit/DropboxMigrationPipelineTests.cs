using System.Runtime.CompilerServices;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Migration;
using CloudMigrator.Core.State;
using CloudMigrator.Providers.Abstractions;
using FluentAssertions;
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

    // ── 共通セットアップ ──────────────────────────────────────────────────

    private void SetupDbBase()
    {
        _mockDb.Setup(db => db.InitializeAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.GetPendingStreamAsync(It.IsAny<CancellationToken>())).Returns(NoRecords());
        _mockDb.Setup(db => db.GetCheckpointAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _mockDb.Setup(db => db.UpsertPendingAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkProcessingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkDoneAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.MarkFailedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDb.Setup(db => db.SaveCheckpointAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
    }

    private void SetupDestBase()
    {
        _mockDest.Setup(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _mockDest.Setup(d => d.UploadFromLocalAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
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
        // 検証対象: Phase B クロール  目的: status=null の新規アイテムが転送され success=1 になる
        var item = MakeItem("docs", "report.pdf", "od-abc");
        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbBase();
            _mockDb.Setup(db => db.GetStatusAsync("docs", "report.pdf", It.IsAny<CancellationToken>()))
                   .ReturnsAsync((TransferStatus?)null);

            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(SingleItemPage(item));
            _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(tempFile);

            SetupDestBase();

            var summary = await CreatePipeline().RunAsync(CancellationToken.None);

            summary.Success.Should().Be(1);
            summary.Failed.Should().Be(0);
            _mockDb.Verify(db => db.UpsertPendingAsync(item, It.IsAny<CancellationToken>()), Times.Once);
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
        // 検証対象: Phase B  目的: done/permanent_failed のアイテムは転送されない
        var item = MakeItem("docs", "done.txt");

        SetupDbBase();
        _mockDb.Setup(db => db.GetStatusAsync("docs", "done.txt", It.IsAny<CancellationToken>()))
               .ReturnsAsync(status);

        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(SingleItemPage(item));

        var summary = await CreatePipeline().RunAsync(CancellationToken.None);

        summary.Success.Should().Be(0);
        summary.Failed.Should().Be(0);
        _mockDb.Verify(db => db.UpsertPendingAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockSource.Verify(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Phase B: pending/processing/failed はスキップ（Phase A でキュー済み）──

    [Theory]
    [InlineData(TransferStatus.Pending)]
    [InlineData(TransferStatus.Processing)]
    [InlineData(TransferStatus.Failed)]
    public async Task RunAsync_ItemAlreadyQueuedStatus_SkipsUpsertInPhaseB(TransferStatus status)
    {
        // 検証対象: Phase B  目的: Phase A キュー済みステータスのアイテムは Phase B では追加されない
        var item = MakeItem("docs", "queued.txt");

        SetupDbBase();
        _mockDb.Setup(db => db.GetStatusAsync("docs", "queued.txt", It.IsAny<CancellationToken>()))
               .ReturnsAsync(status);

        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(SingleItemPage(item));

        await CreatePipeline().RunAsync(CancellationToken.None);

        _mockDb.Verify(db => db.UpsertPendingAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()), Times.Never);
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
            _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(tempFile);

            SetupDestBase();

            var summary = await CreatePipeline().RunAsync(CancellationToken.None);

            summary.Success.Should().Be(1);
            // SourceId が StorageItem.Id として DownloadToTempAsync に渡されることを検証
            _mockSource.Verify(s => s.DownloadToTempAsync(
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
        // 検証対象: ConsumeAsync エラーハンドラ  目的: DownloadToTempAsync の例外で MarkFailedAsync が呼ばれる
        var item = MakeItem("data", "bad.bin", "od-bad");

        SetupDbBase();
        _mockDb.Setup(db => db.GetStatusAsync("data", "bad.bin", It.IsAny<CancellationToken>()))
               .ReturnsAsync((TransferStatus?)null);

        _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(SingleItemPage(item));
        _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
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
        // 検証対象: ConsumeAsync エラーハンドラ  目的: UploadFromLocalAsync の例外で MarkFailedAsync が呼ばれる
        var item = MakeItem("data", "upload-fail.bin", "od-uf");
        var tempFile = Path.GetTempFileName();
        try
        {
            SetupDbBase();
            _mockDb.Setup(db => db.GetStatusAsync("data", "upload-fail.bin", It.IsAny<CancellationToken>()))
                   .ReturnsAsync((TransferStatus?)null);

            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(SingleItemPage(item));
            _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(tempFile);

            _mockDest.Setup(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            _mockDest.Setup(d => d.UploadFromLocalAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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
        _mockDb.Verify(db => db.GetStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockSource.Verify(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()), Times.Never);
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
            _mockDb.Setup(db => db.GetStatusAsync("docs", "report.pdf", It.IsAny<CancellationToken>()))
                   .ReturnsAsync((TransferStatus?)null);
            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(SingleItemPage(item));
            _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(tempFile);
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
            _mockDb.Setup(db => db.GetStatusAsync("docs", "report.pdf", It.IsAny<CancellationToken>()))
                   .ReturnsAsync((TransferStatus?)null);
            _mockSource.Setup(s => s.ListPagedAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                       .ReturnsAsync(SingleItemPage(item));
            _mockSource.Setup(s => s.DownloadToTempAsync(It.IsAny<StorageItem>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(tempFile);
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
}
