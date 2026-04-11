using CloudMigrator.Core.Configuration;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// ConfigurationService の Graph 設定読み書きメソッドのユニットテスト。
/// </summary>
[CollectionDefinition(nameof(ConfigurationServiceGraphTests), DisableParallelization = true)]
public sealed class ConfigurationServiceGraphTestsCollection { }

[Collection(nameof(ConfigurationServiceGraphTests))]
public sealed class ConfigurationServiceGraphTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _configFile;
    private readonly ConfigurationService _sut;

    public ConfigurationServiceGraphTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"config_graph_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _configFile = Path.Combine(_tempDir, "config.json");
        // 最小限の config.json を作成
        File.WriteAllText(_configFile, """
            {
              "migrator": {
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

    // ── GetGraphConfigAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetGraphConfigAsync_WhenGraphSectionExists_ReturnsValues()
    {
        // 検証対象: GetGraphConfigAsync  目的: 既存の clientId / tenantId が正しく読み取られること
        File.WriteAllText(_configFile, """
            {
              "migrator": {
                "graph": {
                  "clientId": "test-client-id",
                  "tenantId": "test-tenant-id",
                  "clientSecretExpiry": "2027-01-01T00:00:00Z"
                }
              }
            }
            """);

        var result = await _sut.GetGraphConfigAsync();

        result.ClientId.Should().Be("test-client-id");
        result.TenantId.Should().Be("test-tenant-id");
        result.ClientSecretExpiry.Should().Be("2027-01-01T00:00:00Z");
    }

    [Fact]
    public async Task GetGraphConfigAsync_WhenFileDoesNotExist_ReturnsEmptyDto()
    {
        // 検証対象: GetGraphConfigAsync  目的: ファイル不在時は空の DTO が返されること
        File.Delete(_configFile);

        var result = await _sut.GetGraphConfigAsync();

        result.ClientId.Should().BeEmpty();
        result.TenantId.Should().BeEmpty();
        result.ClientSecretExpiry.Should().BeEmpty();
    }

    [Fact]
    public async Task GetGraphConfigAsync_WhenGraphSectionMissing_ReturnsEmptyDto()
    {
        // 検証対象: GetGraphConfigAsync  目的: graph セクションが存在しない場合は空の DTO が返されること
        File.WriteAllText(_configFile, """{ "migrator": {} }""");

        var result = await _sut.GetGraphConfigAsync();

        result.ClientId.Should().BeEmpty();
    }

    // ── UpdateGraphConfigAsync ─────────────────────────────────────────

    [Fact]
    public async Task UpdateGraphConfigAsync_WhenClientIdProvided_SavesClientId()
    {
        // 検証対象: UpdateGraphConfigAsync  目的: ClientId が config.json へ保存されること
        await _sut.UpdateGraphConfigAsync(new GraphConfigUpdateDto(ClientId: "new-client-id"));

        var result = await _sut.GetGraphConfigAsync();
        result.ClientId.Should().Be("new-client-id");
    }

    [Fact]
    public async Task UpdateGraphConfigAsync_WhenExpiryProvided_SavesExpiry()
    {
        // 検証対象: UpdateGraphConfigAsync  目的: ClientSecretExpiry が config.json へ保存されること
        const string expiry = "2028-06-01T00:00:00Z";
        await _sut.UpdateGraphConfigAsync(new GraphConfigUpdateDto(ClientSecretExpiry: expiry));

        var result = await _sut.GetGraphConfigAsync();
        result.ClientSecretExpiry.Should().Be(expiry);
    }

    [Fact]
    public async Task UpdateGraphConfigAsync_WhenNullFields_DoesNotOverwrite()
    {
        // 検証対象: UpdateGraphConfigAsync  目的: null フィールドは既存値を上書きしないこと
        File.WriteAllText(_configFile, """
            {
              "migrator": {
                "graph": {
                  "clientId": "existing-id",
                  "tenantId": "existing-tenant"
                }
              }
            }
            """);

        // ClientId だけ更新し TenantId は null（上書きしない）
        await _sut.UpdateGraphConfigAsync(new GraphConfigUpdateDto(ClientId: "updated-id"));

        var result = await _sut.GetGraphConfigAsync();
        result.ClientId.Should().Be("updated-id");
        result.TenantId.Should().Be("existing-tenant");
    }

    [Fact]
    public async Task UpdateGraphConfigAsync_WhenAllFieldsProvided_SavesAllFields()
    {
        // 検証対象: UpdateGraphConfigAsync  目的: 全フィールドが同時に保存できること
        const string expiry = "2028-12-31T23:59:59Z";
        await _sut.UpdateGraphConfigAsync(new GraphConfigUpdateDto(
            ClientId: "cid",
            TenantId: "tid",
            ClientSecretExpiry: expiry));

        var result = await _sut.GetGraphConfigAsync();
        result.ClientId.Should().Be("cid");
        result.TenantId.Should().Be("tid");
        result.ClientSecretExpiry.Should().Be(expiry);
    }
}
