using CloudMigrator.Cli.Commands;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// SecurityScanCommand のユニットテスト（Phase 6 / NFR-07）
/// </summary>
public class SecurityScanCommandTests
{
    [Fact]
    public void ParseVulnerabilities_EmptyOutput_ReturnsEmpty()
    {
        // 検証対象: ParseVulnerabilities  目的: 空出力の場合は空リストを返すこと
        var result = SecurityScanCommand.ParseVulnerabilities(string.Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseVulnerabilities_NoVulnerabilities_ReturnsEmpty()
    {
        // 検証対象: ParseVulnerabilities  目的: 脆弱性なしメッセージの場合は空リストを返すこと
        const string output = """
            The following sources were used:
               https://api.nuget.org/v3/index.json

            Project `CloudMigrator.Cli` has no vulnerable packages given the current sources.
            Project `CloudMigrator.Core` has no vulnerable packages given the current sources.
            """;

        var result = SecurityScanCommand.ParseVulnerabilities(output);

        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseVulnerabilities_WithVulnerablePackage_ReturnsEntry()
    {
        // 検証対象: ParseVulnerabilities  目的: 脆弱パッケージ行を正しくパースしてモデルに変換すること
        const string output = """
            Project 'CloudMigrator.Cli' has the following vulnerable packages
               [net10.0]:
               > Newtonsoft.Json  12.0.1  https://github.com/advisories/GHSA-5crp-9r3c-p9vr  High
            """;

        var result = SecurityScanCommand.ParseVulnerabilities(output);

        result.Should().HaveCount(1);
        result[0].Project.Should().Be("CloudMigrator.Cli");
        result[0].PackageId.Should().Be("Newtonsoft.Json");
        result[0].ResolvedVersion.Should().Be("12.0.1");
        result[0].AdvisoryUrl.Should().Contain("GHSA-5crp-9r3c-p9vr");
        result[0].Severity.Should().Be("High");
    }

    [Fact]
    public void ParseVulnerabilities_MultipleProjects_AssignsCorrectProject()
    {
        // 検証対象: ParseVulnerabilities  目的: 複数プロジェクトの脆弱パッケージを各プロジェクト名と紐付けること
        const string output = """
            Project 'ProjectA' has the following vulnerable packages
               [net10.0]:
               > PackageA  1.0.0  https://github.com/advisories/GHSA-aaaa  Medium
            Project 'ProjectB' has the following vulnerable packages
               [net10.0]:
               > PackageB  2.0.0  https://github.com/advisories/GHSA-bbbb  Low
            """;

        var result = SecurityScanCommand.ParseVulnerabilities(output);

        result.Should().HaveCount(2);
        result[0].Project.Should().Be("ProjectA");
        result[0].PackageId.Should().Be("PackageA");
        result[1].Project.Should().Be("ProjectB");
        result[1].PackageId.Should().Be("PackageB");
    }
}
