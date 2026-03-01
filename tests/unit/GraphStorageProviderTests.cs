using CloudMigrator.Providers.Abstractions;
using CloudMigrator.Providers.Graph;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
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
    public async Task ListItemsAsync_ShouldReturnEmpty_WhenRootPathUnknown()
    {
        // 検証対象: ListItemsAsync  目的: 不明な rootPath は空リストを返すこと
        var provider = CreateProvider();

        var result = await provider.ListItemsAsync("/root");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListItemsAsync_ShouldReturnEmpty_WhenOneDriveUserIdIsEmpty()
    {
        // 検証対象: ListItemsAsync("onedrive")  目的: OneDriveUserId 未設定時は空リストを返すこと
        var provider = CreateProvider(); // options 省略 → OneDriveUserId = ""

        var result = await provider.ListItemsAsync("onedrive");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListItemsAsync_ShouldReturnEmpty_WhenSharePointDriveIdIsEmpty()
    {
        // 検証対象: ListItemsAsync("sharepoint")  目的: SharePointDriveId 未設定時は空リストを返すこと
        var provider = CreateProvider(); // options 省略 → SharePointDriveId = ""

        var result = await provider.ListItemsAsync("sharepoint");

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

    // ── Phase 3 クロール挙動テスト ─────────────────────────────────────

    private static (Mock<IRequestAdapter> adapter, GraphServiceClient client) CreateMockClient(
        DriveItemCollectionResponse itemsResponse)
    {
        var mockAdapter = new Mock<IRequestAdapter>();
        mockAdapter.Setup(a => a.SerializationWriterFactory)
            .Returns(new Mock<ISerializationWriterFactory>().Object);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");

        // ドライブ取得 (Users[userId].Drive.GetAsync → Drive 型)
        mockAdapter.Setup(a => a.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<Drive>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Drive { Id = "drive123" });

        // アイテム一覧取得: 1回目は指定応答、以降はnull（フォルダ再帰を抑制）
        mockAdapter.SetupSequence(a => a.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<DriveItemCollectionResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(itemsResponse)
            .ReturnsAsync((DriveItemCollectionResponse?)null)
            .ReturnsAsync((DriveItemCollectionResponse?)null)
            .ReturnsAsync((DriveItemCollectionResponse?)null);

        return (mockAdapter, new GraphServiceClient(mockAdapter.Object));
    }

    [Fact]
    public async Task ListItemsAsync_ShouldReturnOnlyFiles_WhenFolderAndFilePresent()
    {
        // 検証対象: ListItemsAsync  目的: フォルダは result に含まれずファイルのみ返すこと
        var response = new DriveItemCollectionResponse
        {
            Value = [
                new() { Id = "folder1", Name = "Documents", Folder = new Folder() },
                new() { Id = "file1",   Name = "report.xlsx", Size = 1024 }
            ]
        };
        var (_, client) = CreateMockClient(response);
        var options = new GraphStorageOptions { OneDriveUserId = "user123" };
        var provider = new GraphStorageProvider(client, _mockLogger.Object, options);

        var result = await provider.ListItemsAsync("onedrive");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("report.xlsx");
        result[0].IsFolder.Should().BeFalse();
    }

    [Fact]
    public async Task ListItemsAsync_ShouldSkipItem_WhenNameIsNull()
    {
        // 検証対象: ListItemsAsync  目的: Name が null のアイテムはスキップされること
        var response = new DriveItemCollectionResponse
        {
            Value = [
                new() { Id = "bad",   Name = (string?)null, Size = 512 },
                new() { Id = "file2", Name = "valid.txt",   Size = 256 }
            ]
        };
        var (_, client) = CreateMockClient(response);
        var options = new GraphStorageOptions { OneDriveUserId = "user123" };
        var provider = new GraphStorageProvider(client, _mockLogger.Object, options);

        var result = await provider.ListItemsAsync("onedrive");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("valid.txt");
    }

    [Fact]
    public void DriveItemToStorageItem_ShouldSetSizeBytesToNull_ForFolderItems()
    {
        // DriveItemToStorageItem を直接呼び出し、フォルダアイテムの SizeBytes が null になることを検証
        var folderItem = new DriveItem
        {
            Id = "folder1",
            Name = "Archive",
            Folder = new Folder(),
            Size = 999, // フォルダでも Size が設定されている場合でも null になること
        };

        var result = GraphStorageProvider.DriveItemToStorageItem(folderItem, "/root");

        result.Should().NotBeNull();
        result!.SizeBytes.Should().BeNull("フォルダアイテムは意味のあるサイズを持たないため null であること");
        result.IsFolder.Should().BeTrue();
    }

    [Fact]
    public void DriveItemToStorageItem_ShouldSetSizeBytes_ForFileItems()
    {
        // ファイルアイテムでは Size がそのまま SizeBytes に設定されることを確認
        var fileItem = new DriveItem
        {
            Id = "file1",
            Name = "report.pdf",
            Size = 12345,
        };

        var result = GraphStorageProvider.DriveItemToStorageItem(fileItem, "/docs");

        result.Should().NotBeNull();
        result!.SizeBytes.Should().Be(12345);
        result.IsFolder.Should().BeFalse();
    }
}
