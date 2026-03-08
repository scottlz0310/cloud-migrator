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
}
