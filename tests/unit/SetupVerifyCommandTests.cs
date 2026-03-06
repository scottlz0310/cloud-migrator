using CloudMigrator.Core.Configuration;
using CloudMigrator.Setup.Cli.Commands;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// Setup verify コマンドの事前検証ロジックを確認するユニットテスト。
/// </summary>
public class SetupVerifyCommandTests
{
    [Fact]
    public void BuildPreflightErrors_ShouldReportGraphRequiredValues()
    {
        // 検証対象: BuildPreflightErrors  目的: Graph必須値が不足した場合にエラーを返すこと
        var options = new MigratorOptions();

        var errors = VerifyCommand.BuildPreflightErrors(
            options,
            clientSecret: string.Empty,
            skipOnedrive: false,
            skipSharepoint: false);

        errors.Should().Contain(x => x.Contains("MIGRATOR__GRAPH__CLIENTID"));
        errors.Should().Contain(x => x.Contains("MIGRATOR__GRAPH__TENANTID"));
        errors.Should().Contain(x => x.Contains("MIGRATOR__GRAPH__CLIENTSECRET"));
        errors.Should().Contain(x => x.Contains("MIGRATOR__GRAPH__ONEDRIVEUSERID"));
        errors.Should().Contain(x => x.Contains("MIGRATOR__GRAPH__SHAREPOINTSITEID"));
        errors.Should().Contain(x => x.Contains("MIGRATOR__GRAPH__SHAREPOINTDRIVEID"));
    }

    [Fact]
    public void BuildPreflightErrors_ShouldNotReportOnedriveUserId_WhenSkipOnedriveIsTrue()
    {
        // 検証対象: BuildPreflightErrors  目的: skipOnedrive=true の場合は OneDrive 識別子不足を報告しないこと
        var options = new MigratorOptions();

        var errors = VerifyCommand.BuildPreflightErrors(
            options,
            clientSecret: string.Empty,
            skipOnedrive: true,
            skipSharepoint: false);

        errors.Should().NotContain(x => x.Contains("MIGRATOR__GRAPH__ONEDRIVEUSERID"));
    }

    [Fact]
    public void BuildPreflightErrors_ShouldNotReportSharepointIds_WhenSkipSharepointIsTrue()
    {
        // 検証対象: BuildPreflightErrors  目的: skipSharepoint=true の場合は SharePoint 識別子不足を報告しないこと
        var options = new MigratorOptions();

        var errors = VerifyCommand.BuildPreflightErrors(
            options,
            clientSecret: string.Empty,
            skipOnedrive: false,
            skipSharepoint: true);

        errors.Should().NotContain(x => x.Contains("MIGRATOR__GRAPH__SHAREPOINTSITEID"));
        errors.Should().NotContain(x => x.Contains("MIGRATOR__GRAPH__SHAREPOINTDRIVEID"));
    }

    [Fact]
    public void TryReadId_ShouldReturnId_WhenTopLevelIdExists()
    {
        // 検証対象: TryReadId  目的: Graphレスポンスのidを抽出できること
        var json = """{"id":"drive-123","name":"Documents"}""";

        var id = VerifyCommand.TryReadId(json);

        id.Should().Be("drive-123");
    }
}
