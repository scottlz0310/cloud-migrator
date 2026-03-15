using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Storage;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// TransferEngine のユニットテスト（Phase 4）
/// </summary>
public sealed class TransferEngineTests : IDisposable
{
    private readonly string _skipListPath;
    private readonly SkipListManager _skipList;
    private readonly Mock<IStorageProvider> _mockDest;
    private readonly Mock<ILogger<TransferEngine>> _mockLogger;
    private readonly MigratorOptions _options;

    public TransferEngineTests()
    {
        _skipListPath = Path.GetTempFileName();
        _skipList = new SkipListManager(_skipListPath, Mock.Of<ILogger<SkipListManager>>());
        _mockDest = new Mock<IStorageProvider>();
        _mockLogger = new Mock<ILogger<TransferEngine>>();
        _options = new MigratorOptions { MaxParallelTransfers = 1 };
    }

    public void Dispose() => File.Delete(_skipListPath);

    private TransferEngine CreateEngine() =>
        new(_mockDest.Object, _skipList, _options, _mockLogger.Object);

    private static StorageItem MakeFile(string path, string name) =>
        new() { Id = Guid.NewGuid().ToString(), Name = name, Path = path, SizeBytes = 100 };

    private static StorageItem MakeFolder(string path, string name) =>
        new() { Id = Guid.NewGuid().ToString(), Name = name, Path = path, IsFolder = true };

    // ─── 空リスト ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithEmptySourceList_ReturnsZeroSummary()
    {
        // 検証対象: RunAsync  目的: ソースが空の場合はすべてゼロのサマリーを返すこと
        var engine = CreateEngine();

        var summary = await engine.RunAsync([], "dest/root");

        summary.Success.Should().Be(0);
        summary.Failed.Should().Be(0);
        summary.Skipped.Should().Be(0);
        summary.Total.Should().Be(0);
        _mockDest.Verify(d => d.UploadFileAsync(It.IsAny<TransferJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── スキップリスト ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SkipsAlreadyTransferredItems_BySkipList()
    {
        // 検証対象: RunAsync  目的: skip_list 登録済みアイテムはアップロードせずスキップすること
        var file = MakeFile("docs", "report.pdf");
        await _skipList.AddAsync(file.SkipKey);

        var engine = CreateEngine();
        var summary = await engine.RunAsync([file], "dest/root");

        summary.Skipped.Should().Be(1);
        summary.Success.Should().Be(0);
        _mockDest.Verify(d => d.UploadFileAsync(It.IsAny<TransferJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── 転送成功 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_OnSuccessfulTransfer_IncrementsSuccessAndUpdatesSkipList()
    {
        // 検証対象: RunAsync  目的: 転送成功時に Success をインクリメントし、skip_list に登録すること
        var file = MakeFile("docs", "report.pdf");
        _mockDest.Setup(d => d.UploadFileAsync(It.IsAny<TransferJob>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        var engine = CreateEngine();
        var summary = await engine.RunAsync([file], "dest/root");

        summary.Success.Should().Be(1);
        summary.Failed.Should().Be(0);

        // skip_list に追加されているか確認
        var contains = await _skipList.ContainsAsync(file.SkipKey);
        contains.Should().BeTrue();
    }

    // ─── 転送失敗 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_OnFailedTransfer_IncrementsFailedAndDoesNotUpdateSkipList()
    {
        // 検証対象: RunAsync  目的: 転送失敗時に Failed をインクリメントし、skip_list には登録しないこと
        var file = MakeFile("docs", "broken.pdf");
        _mockDest.Setup(d => d.UploadFileAsync(It.IsAny<TransferJob>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new IOException("network error"));

        var engine = CreateEngine();
        var summary = await engine.RunAsync([file], "dest/root");

        summary.Failed.Should().Be(1);
        summary.Success.Should().Be(0);

        var contains = await _skipList.ContainsAsync(file.SkipKey);
        contains.Should().BeFalse();
    }

    // ─── フォルダ階層の先行作成（Path から導出）────────────────────────────

    [Fact]
    public async Task RunAsync_PreCreatesFolderHierarchy_DerivedFromItemPaths()
    {
        // 検証対象: RunAsync フォルダ先行作成  目的: アイテムの Path セグメントからフォルダ階層を導出し、ファイル転送前に EnsureFolderAsync を呼ぶこと
        // ファイルの Path = "docs/sub" → "dest/root/docs" と "dest/root/docs/sub" が先行作成されること
        var file = MakeFile("docs/sub", "file.txt");
        var callOrder = new List<string>();

        _mockDest.Setup(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback<string, CancellationToken>((p, _) => callOrder.Add($"folder:{p}"))
                 .Returns(Task.CompletedTask);
        _mockDest.Setup(d => d.UploadFileAsync(It.IsAny<TransferJob>(), It.IsAny<CancellationToken>()))
                 .Callback<TransferJob, CancellationToken>((j, _) => callOrder.Add($"file:{j.Source.SkipKey}"))
                 .Returns(Task.CompletedTask);

        var engine = CreateEngine();
        await engine.RunAsync([file], "dest/root");

        var folderCalls = callOrder.Where(x => x.StartsWith("folder:")).ToList();
        var fileCalls = callOrder.Where(x => x.StartsWith("file:")).ToList();

        // フォルダがファイルより前に呼ばれること
        folderCalls.Should().NotBeEmpty();
        fileCalls.Should().HaveCount(1);
        callOrder.IndexOf(folderCalls.Last()).Should().BeLessThan(callOrder.IndexOf(fileCalls.First()));

        // 必要なフォルダ階層が両方作成されること
        folderCalls.Should().Contain("folder:dest/root/docs");
        folderCalls.Should().Contain("folder:dest/root/docs/sub");
    }

    // ─── 複数ファイルの部分成功 ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithMixedResults_CountsCorrectly()
    {
        // 検証対象: RunAsync  目的: 成功・失敗・スキップが混在する場合、各カウントが正確に集計されること
        var file1 = MakeFile("docs", "ok.pdf");
        var file2 = MakeFile("docs", "fail.pdf");
        var file3 = MakeFile("docs", "skip.pdf");
        await _skipList.AddAsync(file3.SkipKey);

        _mockDest.Setup(d => d.UploadFileAsync(It.Is<TransferJob>(j => j.Source.Name == "ok.pdf"), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        _mockDest.Setup(d => d.UploadFileAsync(It.Is<TransferJob>(j => j.Source.Name == "fail.pdf"), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new HttpRequestException("server error"));

        var engine = CreateEngine();
        var summary = await engine.RunAsync([file1, file2, file3], "dest/root");

        summary.Success.Should().Be(1);
        summary.Failed.Should().Be(1);
        summary.Skipped.Should().Be(1);
        summary.Total.Should().Be(3);
    }

    // ─── Token Bucket レートリミッターモード ────────────────────────────────

    private static TokenBucketRateLimiter CreateHighSpeedRateLimiter() =>
        new(initialRate: 1000.0, minRate: 1.0, maxRate: 1000.0, burstCapacity: 100,
            increaseStep: 1.0, decreaseFactor: 0.5,
            logger: Mock.Of<ILogger<TokenBucketRateLimiter>>(),
            increaseIntervalSec: 5.0);

    [Fact]
    public async Task RunAsync_WithRateLimiter_TransfersFileSuccessfully()
    {
        // 検証対象: RunAsync (TokenBucket モード)  目的: RateLimiter 指定時に転送が成功し Success がインクリメントされること
        var file = MakeFile("docs", "report.pdf");
        _mockDest.Setup(d => d.UploadFileAsync(It.IsAny<TransferJob>(), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        using var rateLimiter = CreateHighSpeedRateLimiter();
        var engine = new TransferEngine(_mockDest.Object, _skipList, _options, _mockLogger.Object,
            rateLimiter: rateLimiter);
        var summary = await engine.RunAsync([file], "dest/root");

        summary.Success.Should().Be(1);
        summary.Failed.Should().Be(0);
        var contains = await _skipList.ContainsAsync(file.SkipKey);
        contains.Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WithRateLimiter_FailedTransfer_CountedCorrectly()
    {
        // 検証対象: RunAsync (TokenBucket モード) done カウンター  目的: 失敗時も Failed がインクリメントされ skip_list に登録されないこと
        var file = MakeFile("docs", "broken.pdf");
        _mockDest.Setup(d => d.UploadFileAsync(It.IsAny<TransferJob>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new IOException("network error"));

        using var rateLimiter = CreateHighSpeedRateLimiter();
        var engine = new TransferEngine(_mockDest.Object, _skipList, _options, _mockLogger.Object,
            rateLimiter: rateLimiter);
        var summary = await engine.RunAsync([file], "dest/root");

        summary.Failed.Should().Be(1);
        summary.Success.Should().Be(0);
        var contains = await _skipList.ContainsAsync(file.SkipKey);
        contains.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_WithRateLimiter_MixedResults_CountedCorrectly()
    {
        // 検証対象: RunAsync (TokenBucket モード) 混在  目的: 成功・失敗が混在する場合、各カウントが正確に集計されること
        var fileOk = MakeFile("docs", "ok.pdf");
        var fileFail = MakeFile("docs", "fail.pdf");
        _mockDest.Setup(d => d.UploadFileAsync(It.Is<TransferJob>(j => j.Source.Name == "ok.pdf"), It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);
        _mockDest.Setup(d => d.UploadFileAsync(It.Is<TransferJob>(j => j.Source.Name == "fail.pdf"), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new HttpRequestException("server error"));

        using var rateLimiter = CreateHighSpeedRateLimiter();
        var engine = new TransferEngine(_mockDest.Object, _skipList, _options, _mockLogger.Object,
            rateLimiter: rateLimiter);
        var summary = await engine.RunAsync([fileOk, fileFail], "dest/root");

        summary.Success.Should().Be(1);
        summary.Failed.Should().Be(1);
    }

    // ─── DestinationRoot 先行作成・深さ順序・区切り文字混在 ──────────────────────

    [Fact]
    public async Task RunAsync_EnsuresDestRootBeforeSubfolders()
    {
        // 検証対象: DestinationRoot 先行作成  目的: 子フォルダより前に destRoot 自体が EnsureFolderAsync されること
        // Moq の Callback/Returns/Invocations が Ubuntu CI で最初の呼び出しを捕捉しない問題を避けるため
        // 具象テストダブル FakeStorageProvider で呼び出し順序を直接記録する。
        var file = MakeFile("sub", "file.txt");
        var fake = new FakeStorageProvider();
        var engine = new TransferEngine(fake, _skipList, _options, _mockLogger.Object);

        await engine.RunAsync([file], "dest/root");

        // "dest/root" が "dest/root/sub" より前に呼ばれること
        fake.EnsureCalls.Should().ContainInOrder("dest/root", "dest/root/sub");
    }

    [Fact]
    public async Task RunAsync_MultiSegmentDestRoot_EnsuredBeforeSubfolders()
    {
        // 検証対象: 複数セグメント DestinationRoot  目的: "Migration/OneDrive" のような多段パスも子フォルダより先に作成されること
        var file = MakeFile("docs", "file.txt");
        var fake = new FakeStorageProvider();
        var engine = new TransferEngine(fake, _skipList, _options, _mockLogger.Object);

        await engine.RunAsync([file], "Migration/OneDrive");

        // "Migration/OneDrive" が "Migration/OneDrive/docs" より前に呼ばれること
        fake.EnsureCalls.Should().ContainInOrder("Migration/OneDrive", "Migration/OneDrive/docs");
    }

    [Fact]
    public async Task RunAsync_FolderDepth_ParentAlwaysBeforeChild()
    {
        // 検証対象: 深さ別グループ順序  目的: 深いフォルダより浅いフォルダが必ず先に EnsureFolderAsync されること
        var file = MakeFile("a/b/c", "file.txt");
        var fake = new FakeStorageProvider();
        var engine = new TransferEngine(fake, _skipList, _options, _mockLogger.Object);

        await engine.RunAsync([file], "dest/root");

        // destRoot 除いたフォルダ呼び出しの順序: a → a/b → a/b/c
        var subfolderCalls = fake.EnsureCalls.Where(p => p != "dest/root").ToList();
        subfolderCalls.Should().ContainInOrder(
            "dest/root/a",
            "dest/root/a/b",
            "dest/root/a/b/c");
    }

    [Fact]
    public async Task RunAsync_DestRootWithBackslash_CompletesWithoutException()
    {
        // 検証対象: 深さ計算 (バックスラッシュ混在)  目的: DestinationRoot に \ が含まれても例外なく処理完了すること
        var file = MakeFile("sub", "file.txt");

        _mockDest.Setup(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockDest.Setup(d => d.UploadFileAsync(It.IsAny<TransferJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var engine = CreateEngine();
        var act = async () => await engine.RunAsync([file], @"dest\root");
        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Moq の Callback/Returns/Invocations が Ubuntu 24.04 CI で特定の呼び出しを捕捉しない問題を避けるため、
    /// 具象テストダブルで EnsureFolderAsync の呼び出し順序を直接記録する。
    /// </summary>
    private sealed class FakeStorageProvider : IStorageProvider
    {
        public string ProviderId => "fake";

        /// <summary>EnsureFolderAsync が呼ばれた順に folderPath を記録する。</summary>
        public List<string> EnsureCalls { get; } = [];

        public Task<IReadOnlyList<StorageItem>> ListItemsAsync(
            string rootPath, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StorageItem>>([]);

        public Task UploadFileAsync(
            TransferJob job, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task EnsureFolderAsync(
            string folderPath, CancellationToken cancellationToken = default)
        {
            EnsureCalls.Add(folderPath);
            return Task.CompletedTask;
        }
    }
}
