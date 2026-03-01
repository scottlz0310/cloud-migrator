using CloudMigrator.Providers.Abstractions;
using CloudMigrator.Providers.Graph;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// GraphStorageProvider のユニットテスト（Phase 2）
/// 検証対象: IStorageProvider 契約の基本動作
/// </summary>
public class GraphStorageProviderTests
{
    private readonly GraphServiceClient _graphClient;
    private readonly Mock<ILogger<GraphStorageProvider>> _mockLogger;

    public GraphStorageProviderTests()
    {
        // GraphServiceClient は IRequestAdapter を介して生成（直接モック不可）
        var mockAdapter = new Mock<IRequestAdapter>();
        mockAdapter.Setup(a => a.SerializationWriterFactory).Returns(new Mock<ISerializationWriterFactory>().Object);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");
        _graphClient = new GraphServiceClient(mockAdapter.Object);
        _mockLogger = new Mock<ILogger<GraphStorageProvider>>();
    }

    private GraphStorageProvider CreateProvider() =>
        new(_graphClient, _mockLogger.Object);

    [Fact]
    public void ProviderId_ShouldReturnGraph()
    {
        // 検証対象: ProviderId  目的: プロバイダー識別子が "graph" であること
        var provider = CreateProvider();
        provider.ProviderId.Should().Be("graph");
    }

    [Fact]
    public async Task ListItemsAsync_ShouldReturnEmptyList_WhenPhase3NotImplemented()
    {
        // 検証対象: ListItemsAsync  目的: Phase 3 実装前はスタブとして空リストを返すこと
        var provider = CreateProvider();

        var result = await provider.ListItemsAsync("/root");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task UploadFileAsync_ShouldThrow_WhenSizeBytesIsNull()
    {
        // 検証対象: UploadFileAsync  目的: SizeBytes が未設定の場合に InvalidOperationException をスローすること
        var provider = CreateProvider();
        var job = new TransferJob
        {
            Source = new StorageItem { Id = "1", Name = "file.txt", Path = "docs", SizeBytes = null },
            DestinationRoot = "/dest"
        };

        var act = async () => await provider.UploadFileAsync(job);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SizeBytes*");
    }

    [Fact]
    public async Task UploadFileAsync_SmallFile_ShouldCompleteWithoutException()
    {
        // 検証対象: UploadFileAsync（小ファイルパス）  目的: 4MB 未満はスタブとして正常完了すること
        var provider = CreateProvider();
        var job = new TransferJob
        {
            Source = new StorageItem { Id = "2", Name = "small.txt", Path = "docs", SizeBytes = 1024 },
            DestinationRoot = "/dest"
        };

        var act = async () => await provider.UploadFileAsync(job);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task UploadFileAsync_LargeFile_ShouldCompleteWithoutException()
    {
        // 検証対象: UploadFileAsync（大ファイルパス）  目的: 4MB 以上はスタブとして正常完了すること
        var provider = CreateProvider();
        var largeFileSizeBytes = 5L * 1024 * 1024; // 5MB
        var job = new TransferJob
        {
            Source = new StorageItem { Id = "3", Name = "large.zip", Path = "docs", SizeBytes = largeFileSizeBytes },
            DestinationRoot = "/dest"
        };

        var act = async () => await provider.UploadFileAsync(job);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureFolderAsync_ShouldCompleteWithoutException()
    {
        // 検証対象: EnsureFolderAsync  目的: Phase 3 実装前はスタブとして正常完了すること
        var provider = CreateProvider();

        var act = async () => await provider.EnsureFolderAsync("/dest/docs");

        await act.Should().NotThrowAsync();
    }
}
