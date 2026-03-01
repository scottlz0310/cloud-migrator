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

    // ─── フォルダ先行作成 ────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_PreCreatesFolders_BeforeFileTransfer()
    {
        var folder = MakeFolder("docs", "sub");
        var file = MakeFile("docs/sub", "file.txt");
        var callOrder = new List<string>();

        _mockDest.Setup(d => d.EnsureFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .Callback<string, CancellationToken>((p, _) => callOrder.Add($"folder:{p}"))
                 .Returns(Task.CompletedTask);
        _mockDest.Setup(d => d.UploadFileAsync(It.IsAny<TransferJob>(), It.IsAny<CancellationToken>()))
                 .Callback<TransferJob, CancellationToken>((j, _) => callOrder.Add($"file:{j.Source.SkipKey}"))
                 .Returns(Task.CompletedTask);

        var engine = CreateEngine();
        await engine.RunAsync([folder, file], "dest/root");

        // フォルダ作成がファイル転送より前に呼ばれていること
        callOrder.Should().HaveCount(2);
        callOrder[0].Should().StartWith("folder:");
        callOrder[1].Should().StartWith("file:");
    }

    // ─── 複数ファイルの部分成功 ──────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_WithMixedResults_CountsCorrectly()
    {
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
}
