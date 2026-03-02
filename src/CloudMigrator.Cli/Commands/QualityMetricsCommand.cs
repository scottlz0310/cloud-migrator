using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// quality-metrics サブコマンド。
/// テスト結果（.trx）とカバレッジレポート（Cobertura XML）を集計し、
/// JSON/コンソール形式で品質メトリクスを出力する（NFR-04/NFR-05）。
/// 閾値未達の場合は ExitCode=1 で警告する（NFR-05）。
/// </summary>
internal static class QualityMetricsCommand
{
    // NFR-05 の閾値
    internal const double CoverageThreshold = 60.0;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Build()
    {
        var cmd = new Command("quality-metrics", "テスト結果とカバレッジを集計して品質メトリクスを出力します（NFR-04/NFR-05）");

        var trxDirOpt = new Option<string>("--trx-dir")
        {
            Description = ".trx ファイルを検索するディレクトリ（デフォルト: カレントディレクトリ以下）",
        };
        var coverageOpt = new Option<string?>("--coverage-xml")
        {
            Description = "Cobertura カバレッジ XML ファイルパス",
        };
        var outputOpt = new Option<string?>("--output")
        {
            Description = "メトリクス JSON の出力ファイルパス（省略時はコンソール出力のみ）",
        };

        cmd.Add(trxDirOpt);
        cmd.Add(coverageOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var trxDir = parseResult.GetValue(trxDirOpt) ?? ".";
            var coverageXml = parseResult.GetValue(coverageOpt);
            var outputPath = parseResult.GetValue(outputOpt);
            await RunAsync(trxDir, coverageXml, outputPath, ct).ConfigureAwait(false);
        });

        return cmd;
    }

    internal static async Task RunAsync(
        string trxDir,
        string? coverageXmlPath,
        string? outputPath,
        CancellationToken ct)
    {
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger("quality-metrics");

        // .trx 解析
        var testMetrics = ParseTrxFiles(trxDir, logger);

        // カバレッジ解析
        double? lineCoverage = null;
        if (!string.IsNullOrWhiteSpace(coverageXmlPath) && File.Exists(coverageXmlPath))
            lineCoverage = ParseCoberturaLineCoverage(coverageXmlPath, logger);

        var report = new QualityReport
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Tests = testMetrics,
            LineCoveragePercent = lineCoverage,
            Thresholds = new ThresholdStatus
            {
                CoveragePass = lineCoverage is null || lineCoverage >= CoverageThreshold,
                TestsPass = testMetrics.Failed == 0,
            },
        };

        var json = JsonSerializer.Serialize(report, JsonOpts);
        Console.WriteLine(json);

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outputPath, json, ct).ConfigureAwait(false);
            logger.LogInformation("品質メトリクスを出力しました: {Path}", outputPath);
        }

        // NFR-05: 閾値アラート
        bool alertTriggered = false;
        if (!report.Thresholds.TestsPass)
        {
            logger.LogError("【品質アラート】失敗テストあり: {Failed} 件", testMetrics.Failed);
            alertTriggered = true;
        }
        if (!report.Thresholds.CoveragePass)
        {
            logger.LogError(
                "【品質アラート】カバレッジ不足: {Coverage:F1}% < {Threshold}%",
                lineCoverage, CoverageThreshold);
            alertTriggered = true;
        }

        if (alertTriggered)
            Environment.ExitCode = 1;
    }

    /// <summary>指定ディレクトリ以下の .trx ファイルを再帰的に解析してテスト集計を返す。</summary>
    internal static TestMetrics ParseTrxFiles(string dir, ILogger logger)
    {
        var trxFiles = Directory.GetFiles(dir, "*.trx", SearchOption.AllDirectories);
        int passed = 0, failed = 0, skipped = 0;

        foreach (var file in trxFiles)
        {
            try
            {
                var doc = XDocument.Load(file);
                XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                var counters = doc.Descendants(ns + "Counters").FirstOrDefault();
                if (counters is null) continue;

                passed += int.TryParse(counters.Attribute("passed")?.Value, out var p) ? p : 0;
                failed += int.TryParse(counters.Attribute("failed")?.Value, out var f) ? f : 0;
                skipped += int.TryParse(counters.Attribute("notExecuted")?.Value, out var s) ? s : 0;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, ".trx 解析に失敗しました: {File}", file);
            }
        }

        return new TestMetrics
        {
            TrxFileCount = trxFiles.Length,
            Passed = passed,
            Failed = failed,
            Skipped = skipped,
            Total = passed + failed + skipped,
        };
    }

    /// <summary>Cobertura XML からライン カバレッジ率（0〜100）を取得する。</summary>
    internal static double? ParseCoberturaLineCoverage(string xmlPath, ILogger logger)
    {
        try
        {
            var doc = XDocument.Load(xmlPath);
            var rateStr = doc.Root?.Attribute("line-rate")?.Value;
            if (double.TryParse(rateStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                return Math.Round(rate * 100.0, 2);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cobertura XML 解析に失敗しました: {Path}", xmlPath);
        }
        return null;
    }
}

// ---- モデル ----

internal sealed class QualityReport
{
    [JsonPropertyName("generatedAtUtc")]
    public DateTime GeneratedAtUtc { get; set; }

    [JsonPropertyName("tests")]
    public TestMetrics Tests { get; set; } = new();

    [JsonPropertyName("lineCoveragePercent")]
    public double? LineCoveragePercent { get; set; }

    [JsonPropertyName("thresholds")]
    public ThresholdStatus Thresholds { get; set; } = new();
}

internal sealed class TestMetrics
{
    [JsonPropertyName("trxFileCount")]
    public int TrxFileCount { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("passed")]
    public int Passed { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; set; }
}

internal sealed class ThresholdStatus
{
    [JsonPropertyName("coveragePass")]
    public bool CoveragePass { get; set; }

    [JsonPropertyName("testsPass")]
    public bool TestsPass { get; set; }

    [JsonPropertyName("overallPass")]
    public bool OverallPass => CoveragePass && TestsPass;
}
