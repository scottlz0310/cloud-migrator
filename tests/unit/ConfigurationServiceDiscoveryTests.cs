using CloudMigrator.Core.Configuration;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// ConfigurationService の Discovery 設定読み書きメソッドのユニットテスト。
/// </summary>
[CollectionDefinition(nameof(ConfigurationServiceDiscoveryTests), DisableParallelization = true)]
public sealed class ConfigurationServiceDiscoveryTestsCollection { }

[Collection(nameof(ConfigurationServiceDiscoveryTests))]
public sealed class ConfigurationServiceDiscoveryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFile;
    private readonly ConfigurationService _sut;

    public ConfigurationServiceDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"config_discovery_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configFile = Path.Combine(_tempDir, "config.json");
        File.WriteAllText(_configFile, """
            {
              "migrator": {
                "destinationProvider": "sharepoint",
                "graph": {
                  "clientId": "",
                  "tenantId": ""
                }
              }
            }
            """);
        _sut = new ConfigurationService(_configFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── GetDiscoveryConfigAsync ────────────────────────────────────────

    [Fact]
    public async Task GetDiscoveryConfigAsync_WhenDiscoveryFieldsAreMissing_ReturnsDefaults()
    {
        // 検証対象: GetDiscoveryConfigAsync  目的: Discovery 関連フィールドが未設定の場合にデフォルト値が返されること
        var result = await _sut.GetDiscoveryConfigAsync();

        result.OneDriveUserId.Should().BeEmpty();
        result.OneDriveDriveId.Should().BeEmpty();
        result.SharePointSiteId.Should().BeEmpty();
        result.SharePointDriveId.Should().BeEmpty();
        result.DestinationProvider.Should().Be("sharepoint");
    }

    [Fact]
    public async Task GetDiscoveryConfigAsync_WhenDiscoveryFieldsExist_ReturnsValues()
    {
        // 検証対象: GetDiscoveryConfigAsync  目的: 保存済みの Discovery 値が正しく読み取られること
        File.WriteAllText(_configFile, """
            {
              "migrator": {
                "destinationProvider": "dropbox",
                "migrationRoute": "OneDriveToDropbox",
                "graph": {
                  "oneDriveUserId": "user@contoso.com",
                  "oneDriveDriveId": "b!test-drive-id",
                  "sharePointSiteId": "contoso.sharepoint.com,site-id",
                  "sharePointDriveId": "b!test-sp-drive-id"
                }
              }
            }
            """);

        var result = await _sut.GetDiscoveryConfigAsync();

        result.OneDriveUserId.Should().Be("user@contoso.com");
        result.OneDriveDriveId.Should().Be("b!test-drive-id");
        result.SharePointSiteId.Should().Be("contoso.sharepoint.com,site-id");
        result.SharePointDriveId.Should().Be("b!test-sp-drive-id");
        result.MigrationRoute.Should().Be("OneDriveToDropbox");
        result.DestinationProvider.Should().Be("dropbox");
    }

    [Fact]
    public async Task GetDiscoveryConfigAsync_WhenFileNotExists_ReturnsDefaults()
    {
        // 検証対象: GetDiscoveryConfigAsync  目的: config.json が存在しない場合にデフォルト DTO が返されること
        File.Delete(_configFile);

        var result = await _sut.GetDiscoveryConfigAsync();

        result.OneDriveUserId.Should().BeEmpty();
        result.DestinationProvider.Should().Be("sharepoint");
    }

    // ── UpdateDiscoveryConfigAsync ─────────────────────────────────────

    [Fact]
    public async Task UpdateDiscoveryConfigAsync_WhenOneDriveUserIdIsSet_PersistsValue()
    {
        // 検証対象: UpdateDiscoveryConfigAsync  目的: OneDriveUserId が config.json に保存されること
        await _sut.UpdateDiscoveryConfigAsync(new DiscoveryConfigUpdateDto(
            OneDriveUserId: "test@contoso.com"));

        var result = await _sut.GetDiscoveryConfigAsync();
        result.OneDriveUserId.Should().Be("test@contoso.com");
    }

    [Fact]
    public async Task UpdateDiscoveryConfigAsync_WhenOneDriveDriveIdIsSet_PersistsValue()
    {
        // 検証対象: UpdateDiscoveryConfigAsync  目的: OneDriveDriveId が config.json に保存されること
        await _sut.UpdateDiscoveryConfigAsync(new DiscoveryConfigUpdateDto(
            OneDriveDriveId: "b!test-drive-id-12345"));

        var result = await _sut.GetDiscoveryConfigAsync();
        result.OneDriveDriveId.Should().Be("b!test-drive-id-12345");
    }

    [Fact]
    public async Task UpdateDiscoveryConfigAsync_NullFieldsAreNotOverwritten()
    {
        // 検証対象: UpdateDiscoveryConfigAsync  目的: null フィールドは既存値を上書きしないこと
        await _sut.UpdateDiscoveryConfigAsync(new DiscoveryConfigUpdateDto(
            OneDriveUserId: "user@contoso.com",
            OneDriveDriveId: "drive-id-1"));

        // UserId のみ更新（DriveId は null = 変更しない）
        await _sut.UpdateDiscoveryConfigAsync(new DiscoveryConfigUpdateDto(
            OneDriveUserId: "updated@contoso.com"));

        var result = await _sut.GetDiscoveryConfigAsync();
        result.OneDriveUserId.Should().Be("updated@contoso.com");
        result.OneDriveDriveId.Should().Be("drive-id-1"); // 変更されていないこと
    }

    [Fact]
    public async Task UpdateDiscoveryConfigAsync_WhenSharePointFieldsSet_PersistsValues()
    {
        // 検証対象: UpdateDiscoveryConfigAsync  目的: SharePoint の SiteId / DriveId が保存されること
        await _sut.UpdateDiscoveryConfigAsync(new DiscoveryConfigUpdateDto(
            SharePointSiteId: "contoso.sharepoint.com,abc,def",
            SharePointDriveId: "b!sp-drive",
            MigrationRoute: "OneDriveToSharePoint",
            DestinationProvider: "sharepoint"));

        var result = await _sut.GetDiscoveryConfigAsync();
        result.SharePointSiteId.Should().Be("contoso.sharepoint.com,abc,def");
        result.SharePointDriveId.Should().Be("b!sp-drive");
        result.MigrationRoute.Should().Be("OneDriveToSharePoint");
        result.DestinationProvider.Should().Be("sharepoint");
    }
}
