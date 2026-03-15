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
            dropboxToken: string.Empty,
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
            dropboxToken: string.Empty,
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
            dropboxToken: string.Empty,
            skipOnedrive: false,
            skipSharepoint: true);

        errors.Should().NotContain(x => x.Contains("MIGRATOR__GRAPH__SHAREPOINTSITEID"));
        errors.Should().NotContain(x => x.Contains("MIGRATOR__GRAPH__SHAREPOINTDRIVEID"));
    }

    [Fact]
    public void BuildPreflightErrors_ShouldReportDropboxToken_WhenDropboxDestAndTokenMissing()
    {
        // 検証対象: BuildPreflightErrors  目的: destinationProvider=dropbox かつトークン未設定の場合にエラーを返すこと
        var options = new MigratorOptions { DestinationProvider = "dropbox" };

        var errors = VerifyCommand.BuildPreflightErrors(
            options,
            clientSecret: "secret",
            dropboxToken: string.Empty,
            skipOnedrive: true,
            skipSharepoint: true);

        errors.Should().Contain(x => x.Contains("MIGRATOR__DROPBOX__ACCESSTOKEN"));
    }

    [Fact]
    public void BuildPreflightErrors_ShouldNotReportDropboxToken_WhenSharePointDest()
    {
        // 検証対象: BuildPreflightErrors  目的: destinationProvider=sharepoint の場合は Dropbox トークン不足を報告しないこと
        var options = new MigratorOptions { DestinationProvider = "sharepoint" };

        var errors = VerifyCommand.BuildPreflightErrors(
            options,
            clientSecret: "secret",
            dropboxToken: string.Empty,
            skipOnedrive: true,
            skipSharepoint: true);

        errors.Should().NotContain(x => x.Contains("MIGRATOR__DROPBOX__ACCESSTOKEN"));
    }

    [Fact]
    public void BuildPreflightErrors_ShouldNotReportDropboxToken_WhenSkipDropboxIsTrue()
    {
        // 検証対象: BuildPreflightErrors  目的: skipDropbox=true の場合は Dropbox トークン不足を報告しないこと
        var options = new MigratorOptions { DestinationProvider = "dropbox" };

        var errors = VerifyCommand.BuildPreflightErrors(
            options,
            clientSecret: "secret",
            dropboxToken: string.Empty,
            skipOnedrive: true,
            skipSharepoint: true,
            skipDropbox: true);

        errors.Should().NotContain(x => x.Contains("MIGRATOR__DROPBOX__ACCESSTOKEN"));
    }

    [Fact]
    public void TryReadDropboxEmail_ShouldReturnEmail_WhenPresent()
    {
        // 検証対象: TryReadDropboxEmail  目的: Dropbox get_current_account レスポンスから email を抽出できること
        var json = """{"account_id":"dbid:abc","email":"user@example.com","name":{"display_name":"Test User"}}""";

        var email = VerifyCommand.TryReadDropboxEmail(json);

        email.Should().Be("user@example.com");
    }

    [Fact]
    public void TryReadDropboxEmail_ShouldReturnNull_WhenEmailAbsent()
    {
        // 検証対象: TryReadDropboxEmail  目的: email フィールドがない場合は null を返すこと
        var json = """{"account_id":"dbid:abc"}""";

        var email = VerifyCommand.TryReadDropboxEmail(json);

        email.Should().BeNull();
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
