using CloudMigrator.Cli.Commands;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// FileCrawlerCommand のユニットテスト（Phase 7 / FR-18）。
/// </summary>
public class FileCrawlerCommandTests
{
    [Fact]
    public void CompareKeySets_ShouldReturnExpectedDiffCounts()
    {
        // 検証対象: CompareKeySets  目的: 差分件数と一致件数を正しく算出できること
        var left = new[] { "a/file1.txt", "b/file2.txt", "c/file3.txt" };
        var right = new[] { "b/file2.txt", "d/file4.txt" };

        var result = FileCrawlerCommand.CompareKeySets(left, right, top: 10);

        result.LeftCount.Should().Be(3);
        result.RightCount.Should().Be(2);
        result.BothCount.Should().Be(1);
        result.OnlyLeftCount.Should().Be(2);
        result.OnlyRightCount.Should().Be(1);
        result.OnlyLeftSamples.Should().ContainInOrder("a/file1.txt", "c/file3.txt");
        result.OnlyRightSamples.Should().ContainSingle().Which.Should().Be("d/file4.txt");
    }

    [Fact]
    public void ValidateSkipList_ShouldReturnInvalidAndMissingKeys()
    {
        // 検証対象: ValidateSkipList  目的: 不正キーと source 不在キーを検出できること
        var skip = new[] { "docs/report.xlsx", "/invalid-leading", "legacy/missing.txt" };
        var source = new[] { "docs/report.xlsx" };

        var result = FileCrawlerCommand.ValidateSkipList(skip, source, top: 10);

        result.SkipListCount.Should().Be(3);
        result.SourceCount.Should().Be(1);
        result.InvalidCount.Should().Be(1);
        result.MissingCount.Should().Be(2);
        result.InvalidSamples.Should().ContainSingle().Which.Should().Be("/invalid-leading");
        result.MissingSamples.Should().Contain("legacy/missing.txt");
    }

    [Fact]
    public void IsInvalidSkipKey_ShouldReturnFalse_ForPathPlusNameFormat()
    {
        // 検証対象: IsInvalidSkipKey  目的: path+name 形式のキーを有効と判定すること
        var result = FileCrawlerCommand.IsInvalidSkipKey("reports/2026/summary.pdf");

        result.Should().BeFalse();
    }
}
