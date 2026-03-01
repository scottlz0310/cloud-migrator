using CloudMigrator.Core.Configuration;
using FluentAssertions;

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

    public void Dispose() => Directory.Delete(_testDir, recursive: true);

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

    [Fact]
    public void ComputeHash_SameOptions_ReturnsSameHash()
    {
        var opts = CreateOptions();
        var h1 = ConfigHashChecker.ComputeHash(opts);
        var h2 = ConfigHashChecker.ComputeHash(opts);
        h1.Should().Be(h2);
    }

    [Fact]
    public void ComputeHash_DifferentClientId_ReturnsDifferentHash()
    {
        var h1 = ConfigHashChecker.ComputeHash(CreateOptions(clientId: "clientA"));
        var h2 = ConfigHashChecker.ComputeHash(CreateOptions(clientId: "clientB"));
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void ComputeHash_DifferentDestinationRoot_ReturnsDifferentHash()
    {
        var h1 = ConfigHashChecker.ComputeHash(CreateOptions(destinationRoot: ""));
        var h2 = ConfigHashChecker.ComputeHash(CreateOptions(destinationRoot: "Migration/2026"));
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void ComputeHash_ReturnsLowercaseHex()
    {
        var hash = ConfigHashChecker.ComputeHash(CreateOptions());
        hash.Should().MatchRegex("^[0-9a-f]+$");
        hash.Should().HaveLength(64); // SHA-256 = 32 bytes = 64 hex chars
    }

    [Fact]
    public async Task HasChangedAsync_NoFile_ReturnsTrue()
    {
        var result = await ConfigHashChecker.HasChangedAsync(_hashFile, "abc123");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasChangedAsync_SameHash_ReturnsFalse()
    {
        var hash = ConfigHashChecker.ComputeHash(CreateOptions());
        await ConfigHashChecker.SaveHashAsync(_hashFile, hash);

        var result = await ConfigHashChecker.HasChangedAsync(_hashFile, hash);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasChangedAsync_DifferentHash_ReturnsTrue()
    {
        await ConfigHashChecker.SaveHashAsync(_hashFile, "oldhash");

        var result = await ConfigHashChecker.HasChangedAsync(_hashFile, "newhash");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasChangedAsync_StoredHashWithTrailingWhitespace_ReturnsFalse()
    {
        var hash = "abcdef1234";
        await File.WriteAllTextAsync(_hashFile, hash + "\n");

        var result = await ConfigHashChecker.HasChangedAsync(_hashFile, hash);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task SaveHashAsync_CreatesDirectoryAndFile()
    {
        var deepPath = Path.Combine(_testDir, "sub", "dir", "config_hash.txt");
        await ConfigHashChecker.SaveHashAsync(deepPath, "testhash");

        File.Exists(deepPath).Should().BeTrue();
        (await File.ReadAllTextAsync(deepPath)).Should().Be("testhash");
    }
}
