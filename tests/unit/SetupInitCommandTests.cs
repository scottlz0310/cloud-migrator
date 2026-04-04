using CloudMigrator.Setup.Cli.Commands;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// Setup init コマンドのファイル生成ロジックを検証するユニットテスト。
/// </summary>
public sealed class SetupInitCommandTests : IDisposable
{
    private readonly string _tempDir;

    public SetupInitCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"setup_init_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task WriteTemplateAsync_ShouldSkipExistingFile_WhenForceIsFalse()
    {
        // 検証対象: WriteTemplateAsync  目的: 既存ファイルをforceなしで上書きしないこと
        var filePath = Path.Combine(_tempDir, "sample.env");
        await File.WriteAllTextAsync(filePath, "old-value");
        var results = new List<InitFileResult>();

        await InitCommand.WriteTemplateAsync(
            filePath,
            content: "new-value",
            force: false,
            results,
            CancellationToken.None);

        results.Should().ContainSingle(x => x.Status == InitFileStatus.Skipped);
        (await File.ReadAllTextAsync(filePath)).Should().Be("old-value");
    }

    [Fact]
    public async Task WriteTemplateAsync_ShouldOverwriteExistingFile_WhenForceIsTrue()
    {
        // 検証対象: WriteTemplateAsync  目的: force指定時は既存ファイルを上書きすること
        var filePath = Path.Combine(_tempDir, "config.json");
        await File.WriteAllTextAsync(filePath, "old-config");
        var results = new List<InitFileResult>();

        await InitCommand.WriteTemplateAsync(
            filePath,
            content: "new-config",
            force: true,
            results,
            CancellationToken.None);

        results.Should().ContainSingle(x => x.Status == InitFileStatus.Written);
        (await File.ReadAllTextAsync(filePath)).Should().Be("new-config");
    }

    [Fact]
    public void ApplyGraphValuesToConfigTemplate_ShouldSetGraphIdentifiers()
    {
        // 検証対象: ApplyGraphValuesToConfigTemplate  目的: 指定したGraph識別子をconfigテンプレートへ反映できること
        var template =
            """
            {
              "migrator": {
                "graph": {
                  "oneDriveUserId": "",
                  "sharePointSiteId": "",
                  "sharePointDriveId": ""
                }
              }
            }
            """;

        var updated = InitCommand.ApplyGraphValuesToConfigTemplate(
            template,
            oneDriveUserId: "user@contoso.com",
            sharePointSiteId: "site-id",
            sharePointDriveId: "drive-id");

        updated.Should().Contain("\"oneDriveUserId\": \"user@contoso.com\"");
        updated.Should().Contain("\"sharePointSiteId\": \"site-id\"");
        updated.Should().Contain("\"sharePointDriveId\": \"drive-id\"");
    }

    [Fact]
    public void ApplyGraphValuesToConfigTemplate_ShouldSetSourceFolderAndDestinationRoot()
    {
        // 検証対象: ApplyGraphValuesToConfigTemplate  目的: oneDriveSourceFolder と destinationRoot を config.json に反映できること
        var template = InitCommand.BuildDefaultConfigTemplate();

        var updated = InitCommand.ApplyGraphValuesToConfigTemplate(
            template,
            oneDriveUserId: null,
            sharePointSiteId: null,
            sharePointDriveId: null,
            oneDriveSourceFolder: "Documents/Projects",
            destinationRoot: "移行データ/OneDrive");

        updated.Should().Contain("\"oneDriveSourceFolder\": \"Documents/Projects\"");
        updated.Should().Contain("\"destinationRoot\": \"移行データ/OneDrive\"");
    }

    [Fact]
    public void ApplyGraphValuesToConfigTemplate_ShouldReturnUnchanged_WhenAllParamsNull()
    {
        // 検証対象: ApplyGraphValuesToConfigTemplate  目的: 全パラメータ null 時はテンプレートをそのまま返すこと
        var template = InitCommand.BuildDefaultConfigTemplate();

        var updated = InitCommand.ApplyGraphValuesToConfigTemplate(
            template,
            oneDriveUserId: null,
            sharePointSiteId: null,
            sharePointDriveId: null,
            oneDriveSourceFolder: null,
            destinationRoot: null);

        updated.Should().Be(template);
    }

    [Fact]
    public void ApplyGraphValuesToConfigTemplate_ShouldClearSourceFolder_WhenEmptyString()
    {
        // 検証対象: ApplyGraphValuesToConfigTemplate  目的: "" を渡すと oneDriveSourceFolder が空文字でクリアされること（bootstrap で "-" 入力した場合）
        var template = InitCommand.ApplyGraphValuesToConfigTemplate(
            InitCommand.BuildDefaultConfigTemplate(),
            oneDriveUserId: null,
            sharePointSiteId: null,
            sharePointDriveId: null,
            oneDriveSourceFolder: "Documents/Projects",
            destinationRoot: null);

        var cleared = InitCommand.ApplyGraphValuesToConfigTemplate(
            template,
            oneDriveUserId: null,
            sharePointSiteId: null,
            sharePointDriveId: null,
            oneDriveSourceFolder: string.Empty,
            destinationRoot: null);

        cleared.Should().Contain("\"oneDriveSourceFolder\": \"\"");
    }

    [Fact]
    public void ApplyGraphValuesToConfigTemplate_ShouldClearDestinationRoot_WhenEmptyString()
    {
        // 検証対象: ApplyGraphValuesToConfigTemplate  目的: "" を渡すと destinationRoot が空文字でクリアされること（bootstrap で "-" 入力した場合）
        var template = InitCommand.ApplyGraphValuesToConfigTemplate(
            InitCommand.BuildDefaultConfigTemplate(),
            oneDriveUserId: null,
            sharePointSiteId: null,
            sharePointDriveId: null,
            oneDriveSourceFolder: null,
            destinationRoot: "移行データ/OneDrive");

        var cleared = InitCommand.ApplyGraphValuesToConfigTemplate(
            template,
            oneDriveUserId: null,
            sharePointSiteId: null,
            sharePointDriveId: null,
            oneDriveSourceFolder: null,
            destinationRoot: string.Empty);

        cleared.Should().Contain("\"destinationRoot\": \"\"");
    }

    [Fact]
    public void ApplyGraphValuesToConfigTemplate_ShouldTrimWhitespace_FromFolderPaths()
    {
        // 検証対象: ApplyGraphValuesToConfigTemplate  目的: 前後に空白を含むパスが Trim されて保存されること
        var template = InitCommand.BuildDefaultConfigTemplate();

        var updated = InitCommand.ApplyGraphValuesToConfigTemplate(
            template,
            oneDriveUserId: null,
            sharePointSiteId: null,
            sharePointDriveId: null,
            oneDriveSourceFolder: "  Documents/Projects  ",
            destinationRoot: "  移行データ/OneDrive  ");

        updated.Should().Contain("\"oneDriveSourceFolder\": \"Documents/Projects\"");
        updated.Should().Contain("\"destinationRoot\": \"移行データ/OneDrive\"");
    }

    [Fact]
    public void ApplyGraphValuesToEnvTemplate_ShouldUpsertVariables()
    {
        // 検証対象: ApplyGraphValuesToEnvTemplate  目的: Graph関連キーを.envテンプレートに上書き反映できること
        var envTemplate =
            """
            MIGRATOR__GRAPH__CLIENTID=old-client
            MIGRATOR__GRAPH__TENANTID=old-tenant
            MIGRATOR__GRAPH__ONEDRIVEUSERID=old-user
            MIGRATOR__GRAPH__SHAREPOINTSITEID=old-site
            MIGRATOR__GRAPH__SHAREPOINTDRIVEID=old-drive
            """;

        var updated = InitCommand.ApplyGraphValuesToEnvTemplate(
            envTemplate,
            oneDriveUserId: "user@contoso.com",
            sharePointSiteId: "site-id",
            sharePointDriveId: "drive-id",
            graphClientId: "client-id",
            graphTenantId: "tenant-id");

        updated.Should().Contain("MIGRATOR__GRAPH__CLIENTID=client-id");
        updated.Should().Contain("MIGRATOR__GRAPH__TENANTID=tenant-id");
        updated.Should().Contain("MIGRATOR__GRAPH__ONEDRIVEUSERID=user@contoso.com");
        updated.Should().Contain("MIGRATOR__GRAPH__SHAREPOINTSITEID=site-id");
        updated.Should().Contain("MIGRATOR__GRAPH__SHAREPOINTDRIVEID=drive-id");
    }

    [Fact]
    public void ApplyGraphValuesToEnvTemplate_ShouldAppendNewKeys_WhenKeysNotPresent()
    {
        // 検証対象: ApplyGraphValuesToEnvTemplate (UpsertEnvVariable 末尾追記分岐)  目的: .envにGraph関連キーが存在しない場合に末尾へ追記できること
        var envTemplate = "SOME_OTHER_KEY=existing-value\n";

        var updated = InitCommand.ApplyGraphValuesToEnvTemplate(
            envTemplate,
            oneDriveUserId: "user@contoso.com",
            sharePointSiteId: "new-site-id",
            sharePointDriveId: "new-drive-id",
            graphClientId: "new-client-id",
            graphTenantId: "new-tenant-id");

        // 元のキーが保持されること
        updated.Should().Contain("SOME_OTHER_KEY=existing-value");
        // 新規キーが末尾に追記されること
        updated.Should().Contain("MIGRATOR__GRAPH__CLIENTID=new-client-id");
        updated.Should().Contain("MIGRATOR__GRAPH__TENANTID=new-tenant-id");
        updated.Should().Contain("MIGRATOR__GRAPH__ONEDRIVEUSERID=user@contoso.com");
        updated.Should().Contain("MIGRATOR__GRAPH__SHAREPOINTSITEID=new-site-id");
        updated.Should().Contain("MIGRATOR__GRAPH__SHAREPOINTDRIVEID=new-drive-id");
    }

    [Fact]
    public void ParseSharePointSiteUrl_ShouldExtractHostAndPath()
    {
        // 検証対象: ParseSharePointSiteUrl  目的: SharePoint URLからホスト名とサイトパスを抽出できること
        var address = InitCommand.ParseSharePointSiteUrl("https://contoso.sharepoint.com/sites/migration/");

        address.HostName.Should().Be("contoso.sharepoint.com");
        address.SitePath.Should().Be("/sites/migration");
    }

    [Fact]
    public void FindDriveIdByName_ShouldReturnDriveId_WhenDriveNameMatches()
    {
        // 検証対象: FindDriveIdByName  目的: Graph drives応答から指定ライブラリ名のidを取得できること
        var json = """{"value":[{"id":"drive-1","name":"Documents"},{"id":"drive-2","name":"Archive"}]}""";

        var driveId = InitCommand.FindDriveIdByName(json, "Documents");

        driveId.Should().Be("drive-1");
    }

    [Fact]
    public void ApplyPerformanceValuesToConfigTemplate_ShouldReturnUnchanged_WhenBothParamsAreNull()
    {
        // 検証対象: ApplyPerformanceValuesToConfigTemplate  目的: 両パラメーターが null の場合はテンプレートをそのまま返すこと
        var original = InitCommand.BuildDefaultConfigTemplate();

        var result = InitCommand.ApplyPerformanceValuesToConfigTemplate(original, null, null, null);

        result.Should().Be(original);
    }

    [Fact]
    public void ApplyPerformanceValuesToConfigTemplate_ShouldSetMaxParallelTransfers()
    {
        // 検証対象: ApplyPerformanceValuesToConfigTemplate  目的: maxParallelTransfers が config.json に反映されること
        var template = InitCommand.BuildDefaultConfigTemplate();

        var result = InitCommand.ApplyPerformanceValuesToConfigTemplate(template, maxParallelTransfers: 8, null, null);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        doc.RootElement
            .GetProperty("migrator")
            .GetProperty("maxParallelTransfers")
            .GetInt32()
            .Should().Be(8);
    }

    [Fact]
    public void ApplyPerformanceValuesToConfigTemplate_ShouldEnableAdaptiveConcurrency()
    {
        // 検証対象: ApplyPerformanceValuesToConfigTemplate  目的: adaptiveConcurrency.enabled が true に設定されること
        var template = InitCommand.BuildDefaultConfigTemplate();

        var result = InitCommand.ApplyPerformanceValuesToConfigTemplate(template, null, null, adaptiveConcurrencyEnabled: true);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        doc.RootElement
            .GetProperty("migrator")
            .GetProperty("adaptiveConcurrency")
            .GetProperty("default")
            .GetProperty("enabled")
            .GetBoolean()
            .Should().BeTrue();
    }

    [Fact]
    public void ApplyPerformanceValuesToConfigTemplate_ShouldApplyBothValuesSimultaneously()
    {
        // 検証対象: ApplyPerformanceValuesToConfigTemplate  目的: maxParallelTransfers と adaptiveConcurrency.enabled を同時に設定できること
        var template = InitCommand.BuildDefaultConfigTemplate();

        var result = InitCommand.ApplyPerformanceValuesToConfigTemplate(template, maxParallelTransfers: 6, null, adaptiveConcurrencyEnabled: true);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var migrator = doc.RootElement.GetProperty("migrator");
        migrator.GetProperty("maxParallelTransfers").GetInt32().Should().Be(6);
        migrator.GetProperty("adaptiveConcurrency").GetProperty("default").GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    // ===== ApplyDropboxValuesToConfigTemplate =====

    [Fact]
    public void ApplyDropboxValuesToConfigTemplate_ShouldSetDestinationProviderAndRootPath()
    {
        // 検証対象: ApplyDropboxValuesToConfigTemplate  目的: destinationProvider="dropbox" と rootPath が config.json に反映されること
        var template = InitCommand.BuildDefaultConfigTemplate();

        var result = InitCommand.ApplyDropboxValuesToConfigTemplate(template, "/移行データ");

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var migrator = doc.RootElement.GetProperty("migrator");
        migrator.GetProperty("destinationProvider").GetString().Should().Be("dropbox");
        migrator.GetProperty("dropbox").GetProperty("rootPath").GetString().Should().Be("/移行データ");
    }

    [Fact]
    public void ApplyDropboxValuesToConfigTemplate_ShouldSetDestinationProvider_WhenRootPathIsEmpty()
    {
        // 検証対象: ApplyDropboxValuesToConfigTemplate  目的: rootPath が空文字の場合も destinationProvider が反映されること
        var template = InitCommand.BuildDefaultConfigTemplate();

        var result = InitCommand.ApplyDropboxValuesToConfigTemplate(template, string.Empty);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var migrator = doc.RootElement.GetProperty("migrator");
        migrator.GetProperty("destinationProvider").GetString().Should().Be("dropbox");
        migrator.GetProperty("dropbox").GetProperty("rootPath").GetString().Should().BeEmpty();
    }

    [Fact]
    public void ApplyDropboxValuesToConfigTemplate_ShouldTrimRootPath()
    {
        // 検証対象: ApplyDropboxValuesToConfigTemplate  目的: rootPath の前後空白が Trim されること
        var template = InitCommand.BuildDefaultConfigTemplate();

        var result = InitCommand.ApplyDropboxValuesToConfigTemplate(template, "  /移行データ  ");

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var migrator = doc.RootElement.GetProperty("migrator");
        migrator.GetProperty("dropbox").GetProperty("rootPath").GetString().Should().Be("/移行データ");
    }

    [Fact]
    public void ApplySharePointDestinationToConfigTemplate_ShouldOverwriteDestinationProviderToSharePoint()
    {
        // 検証対象: ApplySharePointDestinationToConfigTemplate
        // 目的: 既存の destinationProvider を sharepoint に上書きし、他の設定値は維持すること
        var template = InitCommand.ApplyDropboxValuesToConfigTemplate(
            InitCommand.BuildDefaultConfigTemplate(),
            "/移行データ");

        var result = InitCommand.ApplySharePointDestinationToConfigTemplate(template);

        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var migrator = doc.RootElement.GetProperty("migrator");
        migrator.GetProperty("destinationProvider").GetString().Should().Be("sharepoint");
        migrator.GetProperty("dropbox").GetProperty("rootPath").GetString().Should().Be("/移行データ");
    }
}
