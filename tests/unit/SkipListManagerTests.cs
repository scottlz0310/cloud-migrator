using CloudMigrator.Core.Storage;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// SkipListManager のユニットテスト（Phase 3 / FR-07/FR-08）
/// </summary>
public class SkipListManagerTests : IDisposable
{
    private readonly string _testDir;

    public SkipListManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"skiplist_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private SkipListManager CreateManager(string fileName = "skip_list.json") =>
        new(Path.Combine(_testDir, fileName), new Mock<ILogger<SkipListManager>>().Object);

    [Fact]
    public async Task LoadAsync_ShouldReturnEmpty_WhenFileNotExists()
    {
        // 検証対象: LoadAsync  目的: ファイル未存在の場合は空セットを返すこと
        var manager = CreateManager();

        var result = await manager.LoadAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task AddAsync_ShouldCreateFile_WithKey()
    {
        // 検証対象: AddAsync  目的: 新規ファイルへキーを追加できること
        var manager = CreateManager();

        await manager.AddAsync("docs/report.xlsx");

        var keys = await manager.LoadAsync();
        keys.Should().Contain("docs/report.xlsx");
    }

    [Fact]
    public async Task AddAsync_ShouldBeIdempotent_WhenAddingSameKeyTwice()
    {
        // 検証対象: AddAsync  目的: 同一キーを複数回追加しても重複しないこと（FR-07）
        var manager = CreateManager();
        await manager.AddAsync("docs/file.txt");
        await manager.AddAsync("docs/file.txt");

        var keys = await manager.LoadAsync();
        keys.Should().HaveCount(1);
    }

    [Fact]
    public async Task ContainsAsync_ShouldReturnTrue_WhenKeyExists()
    {
        // 検証対象: ContainsAsync  目的: 追加済みキーは Contains が true を返すこと
        var manager = CreateManager();
        await manager.AddAsync("reports/2024/q1.docx");

        var result = await manager.ContainsAsync("reports/2024/q1.docx");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task ContainsAsync_ShouldReturnFalse_WhenKeyNotExists()
    {
        // 検証対象: ContainsAsync  目的: 未追加キーは Contains が false を返すこと
        var manager = CreateManager();

        var result = await manager.ContainsAsync("nonexistent/file.txt");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task AddAsync_ShouldPersistMultipleKeys()
    {
        // 検証対象: AddAsync  目的: 複数キーが正しく永続化されること（FR-08）
        var manager = CreateManager();
        await manager.AddAsync("a/file1.txt");
        await manager.AddAsync("b/file2.txt");
        await manager.AddAsync("c/file3.txt");

        var keys = await manager.LoadAsync();
        keys.Should().HaveCount(3)
            .And.Contain("a/file1.txt")
            .And.Contain("b/file2.txt")
            .And.Contain("c/file3.txt");
    }

    [Fact]
    public async Task AddAsync_ShouldCreateParentDirectories()
    {
        // 検証対象: AddAsync  目的: 親ディレクトリが存在しなくても自動作成されること
        var deepPath = Path.Combine(_testDir, "logs", "sub", "skip_list.json");
        var manager = new SkipListManager(deepPath, new Mock<ILogger<SkipListManager>>().Object);

        await manager.AddAsync("any/file.txt");

        File.Exists(deepPath).Should().BeTrue();
    }
}
