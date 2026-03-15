using CloudMigrator.Core.Configuration;
using CloudMigrator.Setup.Cli.Commands;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// Setup doctor コマンド診断ロジックのユニットテスト。
/// </summary>
public class SetupDoctorCommandTests
{
    [Fact]
    public void BuildChecks_ShouldReportErrors_WhenGraphSettingsAreMissing()
    {
        // 検証対象: BuildChecks  目的: Graph必須項目不足をエラーとして検出できること
        var options = new MigratorOptions
        {
            Paths = new PathOptions
            {
                SkipList = "logs/skip_list.json",
                OneDriveCache = "logs/onedrive.json",
                SharePointCache = "logs/sharepoint.json",
                DropboxCache = "logs/dropbox.json",
                TransferLog = "logs/transfer.log",
                ConfigHash = "logs/config_hash.txt",
            },
        };

        var results = DoctorCommand.BuildChecks(
            options,
            graphClientSecret: string.Empty,
            dropboxAccessToken: string.Empty,
            resolvedConfigPath: null,
            strictDropbox: false);

        var errorNames = results
            .Where(x => x.Status == DoctorCheckStatus.Error)
            .Select(x => x.Name)
            .ToArray();

        errorNames.Should().Contain([
            "graph.clientId",
            "graph.tenantId",
            "graph.oneDriveUserId",
            "graph.sharePointSiteId",
            "graph.sharePointDriveId",
            "graph.clientSecret",
        ]);
    }

    [Fact]
    public void BuildChecks_ShouldTreatDropboxAsError_WhenStrictModeEnabled()
    {
        // 検証対象: BuildChecks  目的: strict-dropbox 時にDropboxトークン不足をエラーとして扱うこと
        var options = new MigratorOptions
        {
            Graph = new GraphProviderOptions
            {
                ClientId = "client-id",
                TenantId = "tenant-id",
                OneDriveUserId = "user@example.com",
                SharePointSiteId = "site-id",
                SharePointDriveId = "drive-id",
            },
        };

        var results = DoctorCommand.BuildChecks(
            options,
            graphClientSecret: "secret",
            dropboxAccessToken: string.Empty,
            resolvedConfigPath: null,
            strictDropbox: true);

        results.Should().ContainSingle(x =>
            x.Name == "dropbox.accessToken" &&
            x.Status == DoctorCheckStatus.Error);
    }

    [Fact]
    public void BuildChecks_ShouldShowConfigPathOk_WhenResolvedPathExists()
    {
        // 検証対象: BuildChecks  目的: resolvedConfigPath が存在するファイルを指す場合 config.path チェックが OK になること
        var tmpFile = Path.GetTempFileName();
        try
        {
            var results = DoctorCommand.BuildChecks(
                new MigratorOptions(),
                graphClientSecret: string.Empty,
                dropboxAccessToken: string.Empty,
                resolvedConfigPath: tmpFile,
                strictDropbox: false);

            results.Should().ContainSingle(x =>
                x.Name == "config.path" &&
                x.Status == DoctorCheckStatus.Ok &&
                x.Message.Contains(tmpFile));
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public void BuildChecks_ShouldShowConfigPathWarning_WhenResolvedPathNotFound()
    {
        // 検証対象: BuildChecks  目的: resolvedConfigPath が存在しないパスを指す場合 config.path チェックが Warning になること
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "config.json");

        var results = DoctorCommand.BuildChecks(
            new MigratorOptions(),
            graphClientSecret: string.Empty,
            dropboxAccessToken: string.Empty,
            resolvedConfigPath: nonExistentPath,
            strictDropbox: false);

        results.Should().ContainSingle(x =>
            x.Name == "config.path" &&
            x.Status == DoctorCheckStatus.Warning);
    }

    [Fact]
    public void BuildChecks_ShouldNotIncludeConfigPath_WhenResolvedPathIsNull()
    {
        // 検証対象: BuildChecks  目的: resolvedConfigPath が null の場合 config.path チェックが結果に含まれないこと
        var results = DoctorCommand.BuildChecks(
            new MigratorOptions(),
            graphClientSecret: string.Empty,
            dropboxAccessToken: string.Empty,
            resolvedConfigPath: null,
            strictDropbox: false);

        results.Should().NotContain(x => x.Name == "config.path");
    }

    [Fact]
    public void BuildChecks_WhenDropboxDest_ShouldWarnNotErrorForSharePointFields()
    {
        // 検証対象: BuildChecks  目的: destinationProvider=dropbox の場合、SP 必須フィールドが Warning になること
        var options = new MigratorOptions
        {
            DestinationProvider = "dropbox",
            Graph = new GraphProviderOptions
            {
                ClientId = "client-id",
                TenantId = "tenant-id",
                OneDriveUserId = "user@example.com",
                // SharePointSiteId / SharePointDriveId は未設定
            },
        };

        var results = DoctorCommand.BuildChecks(
            options,
            graphClientSecret: "secret",
            dropboxAccessToken: "dbx-token",
            resolvedConfigPath: null,
            strictDropbox: false);

        // SP フィールドは Warning にとどまり、Error にならない
        results.Should().NotContain(x =>
            (x.Name == "graph.sharePointSiteId" || x.Name == "graph.sharePointDriveId") &&
            x.Status == DoctorCheckStatus.Error);

        results.Should().Contain(x =>
            x.Name == "graph.sharePointSiteId" &&
            x.Status == DoctorCheckStatus.Warning);
        results.Should().Contain(x =>
            x.Name == "graph.sharePointDriveId" &&
            x.Status == DoctorCheckStatus.Warning);
    }

    [Fact]
    public void BuildChecks_WhenDropboxDest_ShouldErrorForDropboxToken_WhenMissing()
    {
        // 検証対象: BuildChecks  目的: destinationProvider=dropbox でトークン未設定は Error になること
        var options = new MigratorOptions
        {
            DestinationProvider = "dropbox",
            Graph = new GraphProviderOptions
            {
                ClientId = "client-id",
                TenantId = "tenant-id",
                OneDriveUserId = "user@example.com",
            },
        };

        var results = DoctorCommand.BuildChecks(
            options,
            graphClientSecret: "secret",
            dropboxAccessToken: string.Empty,   // トークン未設定
            resolvedConfigPath: null,
            strictDropbox: false);   // --strict-dropbox なしでも Error

        results.Should().ContainSingle(x =>
            x.Name == "dropbox.accessToken" &&
            x.Status == DoctorCheckStatus.Error);
    }
}
