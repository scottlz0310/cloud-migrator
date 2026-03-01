using CloudMigrator.Core.Storage;
using CloudMigrator.Providers.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// CrawlCache のユニットテスト（Phase 3 / FR-09）
/// </summary>
public class CrawlCacheTests : IDisposable
{
    private readonly string _testDir;
    private readonly CrawlCache _cache;

    public CrawlCacheTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"crawlcache_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _cache = new CrawlCache(new Mock<ILogger<CrawlCache>>().Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private string TempFile(string name = "cache.json") => Path.Combine(_testDir, name);

    [Fact]
    public async Task LoadAsync_ShouldReturnEmpty_WhenFileNotExists()
    {
        // 検証対象: LoadAsync  目的: ファイル未存在の場合は空リストを返すこと
        var result = await _cache.LoadAsync(TempFile("nonexistent.json"));

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ShouldCreateFile_WithSerializedItems()
    {
        // 検証対象: SaveAsync  目的: アイテムを JSON ファイルに保存すること
        var items = BuildItems(3);
        var path = TempFile();

        await _cache.SaveAsync(path, items);

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task LoadAsync_ShouldReturnItems_WhenFileExists()
    {
        // 検証対象: LoadAsync  目的: 保存済みファイルを正しく読み込めること
        var items = BuildItems(2);
        var path = TempFile();
        await _cache.SaveAsync(path, items);

        var loaded = await _cache.LoadAsync(path);

        loaded.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveAsync_LoadAsync_ShouldRoundTripAllProperties()
    {
        // 検証対象: SaveAsync → LoadAsync  目的: 全プロパティが正しくシリアライズ/デシリアライズされること
        var now = DateTimeOffset.UtcNow;
        var original = new StorageItem
        {
            Id = "abc123",
            Name = "report.xlsx",
            Path = "docs/2024",
            SizeBytes = 1_234_567,
            LastModifiedUtc = now,
            IsFolder = false,
        };
        var path = TempFile();
        await _cache.SaveAsync(path, [original]);

        var loaded = await _cache.LoadAsync(path);

        loaded.Should().HaveCount(1);
        var item = loaded[0];
        item.Id.Should().Be("abc123");
        item.Name.Should().Be("report.xlsx");
        item.Path.Should().Be("docs/2024");
        item.SizeBytes.Should().Be(1_234_567);
        item.LastModifiedUtc.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1));
        item.IsFolder.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_ShouldCreateParentDirectories()
    {
        // 検証対象: SaveAsync  目的: 親ディレクトリが自動作成されること
        var nested = Path.Combine(_testDir, "sub", "deep", "cache.json");

        await _cache.SaveAsync(nested, BuildItems(1));

        File.Exists(nested).Should().BeTrue();
    }

    private static List<StorageItem> BuildItems(int count) =>
        Enumerable.Range(1, count).Select(i => new StorageItem
        {
            Id = $"id-{i}",
            Name = $"file{i}.txt",
            Path = "root",
            SizeBytes = i * 1024L,
        }).ToList();
}
