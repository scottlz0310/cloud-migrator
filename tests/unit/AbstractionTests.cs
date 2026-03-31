using CloudMigrator.Providers.Abstractions;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// StorageItem のユニットテスト（Phase 1 フォローアップ）
/// 検証対象: SkipKey プロパティ（FR-07）
/// </summary>
public class StorageItemTests
{
    [Fact]
    public void SkipKey_ShouldReturnPathSlashName_WhenPathIsNotEmpty()
    {
        // 検証対象: SkipKey  目的: パスがある場合は "path/name" 形式になること
        var item = new StorageItem { Id = "1", Name = "file.txt", Path = "folder/sub" };
        item.SkipKey.Should().Be("folder/sub/file.txt");
    }

    [Fact]
    public void SkipKey_ShouldReturnNameOnly_WhenPathIsEmpty()
    {
        // 検証対象: SkipKey  目的: ルート直下（Path=""）の場合は名前のみになること（先頭スラッシュなし）
        var item = new StorageItem { Id = "2", Name = "root-file.txt", Path = "" };
        item.SkipKey.Should().Be("root-file.txt");
        item.SkipKey.Should().NotStartWith("/");
    }

    [Fact]
    public void SkipKey_ShouldNotIncludeSizeOrTime()
    {
        // 検証対象: SkipKey  目的: サイズや更新日時がキーに含まれないこと（FR-07 の設計原則）
        var item1 = new StorageItem { Id = "3", Name = "data.csv", Path = "exports", SizeBytes = 100, LastModifiedUtc = DateTimeOffset.UtcNow };
        var item2 = new StorageItem { Id = "4", Name = "data.csv", Path = "exports", SizeBytes = 999, LastModifiedUtc = DateTimeOffset.UtcNow.AddDays(-1) };

        item1.SkipKey.Should().Be(item2.SkipKey);
    }
}

/// <summary>
/// TransferJob のユニットテスト
/// 検証対象: DestinationPath / DestinationFullPath プロパティ
/// </summary>
public class TransferJobTests
{
    [Fact]
    public void DestinationPath_ShouldNotHaveDoubleSlashes()
    {
        // 検証対象: DestinationPath  目的: DestinationRoot に末尾スラッシュがあっても二重スラッシュにならないこと
        var job = new TransferJob
        {
            Source = new StorageItem { Id = "1", Name = "file.txt", Path = "docs" },
            DestinationRoot = "/dest/"
        };
        job.DestinationPath.Should().Be("/dest/docs");
        job.DestinationPath.Should().NotContain("//");
    }

    [Fact]
    public void DestinationFullPath_ShouldIncludeFileName()
    {
        // 検証対象: DestinationFullPath  目的: DestinationPath にファイル名が付加されること
        var job = new TransferJob
        {
            Source = new StorageItem { Id = "2", Name = "report.pdf", Path = "reports/2026" },
            DestinationRoot = "/archive"
        };
        job.DestinationFullPath.Should().Be("/archive/reports/2026/report.pdf");
    }

    [Fact]
    public void DestinationPath_ShouldHandleRootLevelItem()
    {
        // 検証対象: DestinationPath  目的: Source.Path が空（ルート直下）の場合に DestinationRoot だけになること
        var job = new TransferJob
        {
            Source = new StorageItem { Id = "3", Name = "readme.md", Path = "" },
            DestinationRoot = "/target"
        };
        job.DestinationPath.Should().Be("/target");
        job.DestinationFullPath.Should().Be("/target/readme.md");
    }
}

public class StorageProviderDefaultMethodTests
{
    [Fact]
    public async Task ServerSideCopyAsync_DefaultImplementation_ShouldThrowNotSupported()
    {
        IStorageProvider provider = new StubStorageProvider();

        var act = async () => await provider.ServerSideCopyAsync("src-1", "docs", "file.txt");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*stub*");
    }

    private sealed class StubStorageProvider : IStorageProvider
    {
        public string ProviderId => "stub";

        public Task<IReadOnlyList<StorageItem>> ListItemsAsync(string rootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StorageItem>>([]);

        public Task<string> DownloadToTempAsync(StorageItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(Path.GetTempFileName());

        public Task UploadFromLocalAsync(string localFilePath, long fileSizeBytes, string destinationFullPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task UploadFileAsync(TransferJob job, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task EnsureFolderAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
