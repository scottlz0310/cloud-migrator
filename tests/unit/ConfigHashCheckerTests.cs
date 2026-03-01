using CloudMigrator.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// ConfigHashChecker のユニットテスト（Phase 5 / FR-10）
/// </summary>
public class ConfigHashCheckerTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _hashFile;

    public ConfigHashCheckerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"hashchecker_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _hashFile = Path.Combine(_testDir, "config_hash.txt");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private static MigratorOptions CreateOptions(
        string clientId = "client1",
        string tenantId = "tenant1",
        string oneDriveUserId = "user1",
        string sharePointSiteId = "site1",
        string sharePointDriveId = "drive1",
        string destinationRoot = "") => new()
    {
        Graph = new GraphProviderOptions
        {
            ClientId = clientId,
            TenantId = tenantId,
            OneDriveUserId = oneDriveUserId,
            SharePointSiteId = sharePointSiteId,
            SharePointDriveId = sharePointDriveId,
        },
        DestinationRoot = destinationRoot,
    };

    private PathOptions CreatePaths(string? skipList = null, string? oneDriveCache = null, string? spCache = null) => new()
    {
        SkipList = skipList ?? Path.Combine(_testDir, "skip_list.json"),
        OneDriveCache = oneDriveCache ?? Path.Combine(_testDir, "onedrive.json"),
        SharePointCache = spCache ?? Path.Combine(_testDir, "sp.json"),
        ConfigHash = _hashFile,
        TransferLog = Path.Combine(_testDir, "transfer.log"),
    };

    [Fact]
    public void ComputeHash_SameOptions_ReturnsSameHash()
    {
        // 検証対象: ComputeHash  目的: 同一設定は常に同じハッシュを返すこと
        var opts = CreateOptions();
        var h1 = ConfigHashChecker.ComputeHash(opts);
        var h2 = ConfigHashChecker.ComputeHash(opts);
        h1.Should().Be(h2);
    }

    [Fact]
    public void ComputeHash_DifferentClientId_ReturnsDifferentHash()
    {
        // 検証対象: ComputeHash  目的: ClientId が異なる場合はハッシュが変わること
        var h1 = ConfigHashChecker.ComputeHash(CreateOptions(clientId: "clientA"));
        var h2 = ConfigHashChecker.ComputeHash(CreateOptions(clientId: "clientB"));
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void ComputeHash_DifferentDestinationRoot_ReturnsDifferentHash()
    {
        // 検証対象: ComputeHash  目的: DestinationRoot の違いがハッシュに反映されること
        var h1 = ConfigHashChecker.ComputeHash(CreateOptions(destinationRoot: ""));
        var h2 = ConfigHashChecker.ComputeHash(CreateOptions(destinationRoot: "Migration/2026"));
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void ComputeHash_DestinationRootNormalization_ProducesSameHash()
    {
        // 検証対象: ComputeHash  目的: バックスラッシュ・末尾スラッシュ・空白の表記揺れでハッシュが変わらないこと
        var h1 = ConfigHashChecker.ComputeHash(CreateOptions(destinationRoot: "Migration/2026"));
        var h2 = ConfigHashChecker.ComputeHash(CreateOptions(destinationRoot: "Migration\\2026"));
        var h3 = ConfigHashChecker.ComputeHash(CreateOptions(destinationRoot: "Migration/2026/"));
        var h4 = ConfigHashChecker.ComputeHash(CreateOptions(destinationRoot: " Migration/2026 "));
        h1.Should().Be(h2);
        h1.Should().Be(h3);
        h1.Should().Be(h4);
    }

    [Fact]
    public void ComputeHash_ReturnsLowercaseHex()
    {
        // 検証対象: ComputeHash  目的: 出力が小文字 16 進数 64 文字（SHA-256）であること
        var hash = ConfigHashChecker.ComputeHash(CreateOptions());
        hash.Should().MatchRegex("^[0-9a-f]+$");
        hash.Should().HaveLength(64); // SHA-256 = 32 bytes = 64 hex chars
    }

    [Fact]
    public async Task HasChangedAsync_NoFile_ReturnsTrue()
    {
        // 検証対象: HasChangedAsync  目的: ハッシュファイルが存在しない場合は変更ありと判定すること
        var result = await ConfigHashChecker.HasChangedAsync(_hashFile, "abc123");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasChangedAsync_SameHash_ReturnsFalse()
    {
        // 検証対象: HasChangedAsync  目的: 保存済みハッシュと一致する場合は変更なしと判定すること
        var hash = ConfigHashChecker.ComputeHash(CreateOptions());
        await ConfigHashChecker.SaveHashAsync(_hashFile, hash);

        var result = await ConfigHashChecker.HasChangedAsync(_hashFile, hash);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasChangedAsync_DifferentHash_ReturnsTrue()
    {
        // 検証対象: HasChangedAsync  目的: ハッシュが異なる場合は変更ありと判定すること
        await ConfigHashChecker.SaveHashAsync(_hashFile, "oldhash");

        var result = await ConfigHashChecker.HasChangedAsync(_hashFile, "newhash");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasChangedAsync_StoredHashWithTrailingWhitespace_ReturnsFalse()
    {
        // 検証対象: HasChangedAsync  目的: ファイル末尾の改行・空白を無視してハッシュ比較すること
        var hash = "abcdef1234";
        await File.WriteAllTextAsync(_hashFile, hash + "\n");

        var result = await ConfigHashChecker.HasChangedAsync(_hashFile, hash);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SaveHashAsync_CreatesDirectoryAndFile()
    {
        // 検証対象: SaveHashAsync  目的: 中間ディレクトリを作成してハッシュを保存すること
        var deepPath = Path.Combine(_testDir, "sub", "dir", "config_hash.txt");
        await ConfigHashChecker.SaveHashAsync(deepPath, "testhash");

        File.Exists(deepPath).Should().BeTrue();
        (await File.ReadAllTextAsync(deepPath)).Should().Be("testhash");
    }

    [Fact]
    public void ClearAll_DeletesAllFiles_WhenAllExist()
    {
        // 検証対象: ClearAll  目的: OneDrive キャッシュ・SP キャッシュ・skip_list の 3 ファイルを削除すること
        var paths = CreatePaths();
        File.WriteAllText(paths.OneDriveCache, "[]");
        File.WriteAllText(paths.SharePointCache, "[]");
        File.WriteAllText(paths.SkipList, "[]");

        ConfigHashChecker.ClearAll(paths, NullLogger.Instance);

        File.Exists(paths.OneDriveCache).Should().BeFalse();
        File.Exists(paths.SharePointCache).Should().BeFalse();
        File.Exists(paths.SkipList).Should().BeFalse();
    }

    [Fact]
    public void ClearAll_DoesNotThrow_WhenFilesDoNotExist()
    {
        // 検証対象: ClearAll  目的: 対象ファイルが存在しなくても例外が発生しないこと（例外スワロー確認）
        var paths = CreatePaths();

        var act = () => ConfigHashChecker.ClearAll(paths, NullLogger.Instance);

        act.Should().NotThrow();
    }

    [Fact]
    public void ClearSkipList_DeletesOnlySkipList_WhenExists()
    {
        // 検証対象: ClearSkipList  目的: skip_list のみを削除し、キャッシュファイルには手を付けないこと
        var paths = CreatePaths();
        File.WriteAllText(paths.OneDriveCache, "[]");
        File.WriteAllText(paths.SharePointCache, "[]");
        File.WriteAllText(paths.SkipList, "[]");

        ConfigHashChecker.ClearSkipList(paths, NullLogger.Instance);

        File.Exists(paths.SkipList).Should().BeFalse();
        File.Exists(paths.OneDriveCache).Should().BeTrue();
        File.Exists(paths.SharePointCache).Should().BeTrue();
    }

    [Fact]
    public void ClearSkipList_DoesNotThrow_WhenFileDoesNotExist()
    {
        // 検証対象: ClearSkipList  目的: skip_list が存在しなくても例外が発生しないこと（例外スワロー確認）
        var paths = CreatePaths();

        var act = () => ConfigHashChecker.ClearSkipList(paths, NullLogger.Instance);

        act.Should().NotThrow();
    }
}
