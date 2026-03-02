using CloudMigrator.Cli.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// QualityMetricsCommand のユニットテスト（Phase 6 / NFR-04/NFR-05）
/// </summary>
public class QualityMetricsCommandTests : IDisposable
{
    private readonly string _testDir;

    public QualityMetricsCommandTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"qualitymetrics_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, recursive: true);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_testDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ParseTrxFiles_NoFiles_ReturnsAllZero()
    {
        // 検証対象: ParseTrxFiles  目的: .trx ファイルが存在しない場合は全項目 0 を返すこと
        var result = QualityMetricsCommand.ParseTrxFiles(_testDir, NullLogger.Instance);

        result.TrxFileCount.Should().Be(0);
        result.Total.Should().Be(0);
        result.Passed.Should().Be(0);
        result.Failed.Should().Be(0);
    }

    [Fact]
    public void ParseTrxFiles_ValidTrx_ReturnsCorrectCounts()
    {
        // 検証対象: ParseTrxFiles  目的: 有効な .trx から passed/failed/notExecuted を正しく集計すること
        const string trxContent = """
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="10" executed="9" passed="7" failed="1" error="0"
                          timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0"
                          notRunnable="0" notExecuted="1" disconnected="0" warning="0"
                          completed="0" inProgress="0" pending="0" />
              </ResultSummary>
            </TestRun>
            """;
        WriteFile("results.trx", trxContent);

        var result = QualityMetricsCommand.ParseTrxFiles(_testDir, NullLogger.Instance);

        result.TrxFileCount.Should().Be(1);
        result.Passed.Should().Be(7);
        result.Failed.Should().Be(1);
        result.Skipped.Should().Be(1);
        result.Total.Should().Be(9);
    }

    [Fact]
    public void ParseTrxFiles_MultipleTrx_SumsCounts()
    {
        // 検証対象: ParseTrxFiles  目的: 複数の .trx ファイルのカウントを合算すること
        const string trx1 = """
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="5" executed="5" passed="5" failed="0" error="0"
                          timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0"
                          notRunnable="0" notExecuted="0" disconnected="0" warning="0"
                          completed="0" inProgress="0" pending="0" />
              </ResultSummary>
            </TestRun>
            """;
        const string trx2 = """
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="3" executed="3" passed="2" failed="1" error="0"
                          timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0"
                          notRunnable="0" notExecuted="0" disconnected="0" warning="0"
                          completed="0" inProgress="0" pending="0" />
              </ResultSummary>
            </TestRun>
            """;
        WriteFile("unit.trx", trx1);
        WriteFile("integration.trx", trx2);

        var result = QualityMetricsCommand.ParseTrxFiles(_testDir, NullLogger.Instance);

        result.TrxFileCount.Should().Be(2);
        result.Passed.Should().Be(7);
        result.Failed.Should().Be(1);
        result.Total.Should().Be(8);
    }

    [Fact]
    public void ParseCoberturaLineCoverage_ValidXml_ReturnsRate()
    {
        // 検証対象: ParseCoberturaLineCoverage  目的: Cobertura XML の line-rate を 0〜100 スケールに変換すること
        var xmlPath = WriteFile("coverage.xml", """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0.753" branch-rate="0.5" version="1.9" timestamp="1234567890">
              <packages />
            </coverage>
            """);

        var result = QualityMetricsCommand.ParseCoberturaLineCoverage(xmlPath, NullLogger.Instance);

        result.Should().BeApproximately(75.3, precision: 0.01);
    }

    [Fact]
    public void ParseCoberturaLineCoverage_FileNotExist_ReturnsNull()
    {
        // 検証対象: ParseCoberturaLineCoverage  目的: 存在しないファイルを渡した場合 null を返すこと
        var result = QualityMetricsCommand.ParseCoberturaLineCoverage(
            Path.Combine(_testDir, "nonexistent.xml"), NullLogger.Instance);

        result.Should().BeNull();
    }

    [Fact]
    public void ParseCoberturaLineCoverage_InvalidXml_ReturnsNull()
    {
        // 検証対象: ParseCoberturaLineCoverage  目的: 破損 XML でも例外なく null を返すこと（例外スワロー確認）
        var xmlPath = WriteFile("broken.xml", "NOT XML CONTENT {{}}");

        var result = QualityMetricsCommand.ParseCoberturaLineCoverage(xmlPath, NullLogger.Instance);

        result.Should().BeNull();
    }

    [Fact]
    public void CoverageThreshold_IsSetTo60()
    {
        // 検証対象: CoverageThreshold  目的: NFR-05 に規定された閾値が 60% であること
        QualityMetricsCommand.CoverageThreshold.Should().Be(60.0);
    }
}
