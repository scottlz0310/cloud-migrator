using System.CommandLine;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// security-scan サブコマンド。
/// `dotnet list package --vulnerable --include-transitive` を実行して
/// 脆弱性のある依存パッケージを構造化サマリとして出力する（NFR-07）。
/// 脆弱性が検出された場合は ExitCode=1 を設定する。
/// </summary>
internal static partial class SecurityScanCommand
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Build()
    {
        var cmd = new Command("security-scan", "NuGet パッケージの脆弱性をスキャンして構造化サマリを出力します（NFR-07）");

        var projectOpt = new Option<string>("--project")
        {
            Description = "スキャン対象のプロジェクト/ソリューションファイルパス（デフォルト: CloudMigrator.slnx）",
        };
        var outputOpt = new Option<string?>("--output")
        {
            Description = "スキャン結果 JSON の出力ファイルパス（省略時はコンソール出力のみ）",
        };

        cmd.Add(projectOpt);
        cmd.Add(outputOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var project = parseResult.GetValue(projectOpt) ?? "CloudMigrator.slnx";
            var outputPath = parseResult.GetValue(outputOpt);
            await RunAsync(project, outputPath, ct).ConfigureAwait(false);
        });

        return cmd;
    }

    internal static async Task RunAsync(string project, string? outputPath, CancellationToken ct)
    {
        // JSON を stdout に出力するため、ログは stderr に寄せて混在を避ける
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole(
            options => options.LogToStandardErrorThreshold = LogLevel.Trace));
        var logger = loggerFactory.CreateLogger("security-scan");

        logger.LogInformation("セキュリティスキャンを開始します: {Project}", project);

        // 外部コマンド引数として渡すため、引用符や制御文字を禁止する
        if (project.Contains('"') || project.Any(char.IsControl))
            throw new ArgumentException(
                "プロジェクト/ソリューションパスに使用できない文字が含まれています。--project の値を確認してください。",
                nameof(project));

        var (stdout, stderr, exitCode) = await RunDotnetCommandAsync(project, ct).ConfigureAwait(false);

        if (exitCode != 0)
        {
            logger.LogError(
                "dotnet list package の実行に失敗しました。ExitCode={ExitCode}, stderr={Stderr}",
                exitCode,
                string.IsNullOrWhiteSpace(stderr) ? "<empty>" : stderr);
            Environment.ExitCode = 1;
            return;
        }

        var vulnerabilities = ParseVulnerabilities(stdout);

        var report = new SecurityReport
        {
            ScannedAtUtc = DateTime.UtcNow,
            Project = project,
            VulnerabilityCount = vulnerabilities.Count,
            HasVulnerabilities = vulnerabilities.Count > 0,
            Vulnerabilities = vulnerabilities,
            RawOutput = stdout,
        };

        var json = JsonSerializer.Serialize(report, JsonOpts);
        Console.WriteLine(json);

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outputPath, json, ct).ConfigureAwait(false);
            logger.LogInformation("スキャン結果を出力しました: {Path}", outputPath);
        }

        if (report.HasVulnerabilities)
        {
            logger.LogError(
                "【セキュリティアラート】脆弱性が {Count} 件検出されました。",
                report.VulnerabilityCount);
            Environment.ExitCode = 1;
        }
        else
        {
            logger.LogInformation("脆弱性は検出されませんでした。");
        }
    }

    /// <summary>
    /// dotnet CLI の出力から脆弱パッケージ行をパースする。
    /// 出力例: "   > PackageName  ResolvedVersion  https://ghsa..."
    /// Requested 列（最新要求バージョン）はオプションであり、Resolved バージョンのみを保持する。
    /// </summary>
    internal static List<VulnerablePackage> ParseVulnerabilities(string output)
    {
        var result = new List<VulnerablePackage>();
        string? currentProject = null;

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            // プロジェクト行: "Project 'ProjectName' has the following vulnerable packages"
            var projectMatch = ProjectLineRegex().Match(trimmed);
            if (projectMatch.Success)
            {
                currentProject = projectMatch.Groups[1].Value;
                continue;
            }

            // パッケージ行: "   > PackageName  ResolvedVersion  AdvisoryUrl  Severity"
            var pkgMatch = PackageLineRegex().Match(trimmed);
            if (pkgMatch.Success && currentProject is not null)
            {
                result.Add(new VulnerablePackage
                {
                    Project = currentProject,
                    PackageId = pkgMatch.Groups[1].Value.Trim(),
                    ResolvedVersion = pkgMatch.Groups[2].Value.Trim(),
                    AdvisoryUrl = pkgMatch.Groups[3].Value.Trim(),
                    Severity = pkgMatch.Groups[4].Value.Trim(),
                });
            }
        }

        return result;
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunDotnetCommandAsync(
        string project, CancellationToken ct)
    {
        // ArgumentList を使って要素単位で引数を組み立て（引数インジェクション対策）
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("list");
        psi.ArgumentList.Add(project);
        psi.ArgumentList.Add("package");
        psi.ArgumentList.Add("--vulnerable");
        psi.ArgumentList.Add("--include-transitive");

        using var process = new Process { StartInfo = psi };
        var stdoutSb = new StringBuilder();
        var stderrSb = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutSb.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrSb.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return (stdoutSb.ToString(), stderrSb.ToString(), process.ExitCode);
    }

    [GeneratedRegex(@"Project '(.+?)' has the following vulnerable", RegexOptions.IgnoreCase)]
    private static partial Regex ProjectLineRegex();

    // パッケージ行の形式:
    //   > PackageId [Requested]? Resolved URL [Severity]?
    // Requested 列（最新要求バージョン）はオプション。バージョンは「数字で始まるトークン」としてマッチ。
    // キャプチャグループ: 1=PackageId, 2=Resolved, 3=AdvisoryUrl, 4=Severity(省略可)
    [GeneratedRegex(@">\s+(\S+)\s+(?:\d[\w.\-+]*\s+)?(\d[\w.\-+]*)\s+(https?://\S+)\s*(\S*)")]
    private static partial Regex PackageLineRegex();
}

// ---- モデル ----

internal sealed class SecurityReport
{
    [JsonPropertyName("scannedAtUtc")]
    public DateTime ScannedAtUtc { get; set; }

    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    [JsonPropertyName("hasVulnerabilities")]
    public bool HasVulnerabilities { get; set; }

    [JsonPropertyName("vulnerabilityCount")]
    public int VulnerabilityCount { get; set; }

    [JsonPropertyName("vulnerabilities")]
    public List<VulnerablePackage> Vulnerabilities { get; set; } = [];

    [JsonPropertyName("rawOutput")]
    public string? RawOutput { get; set; }
}

internal sealed class VulnerablePackage
{
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;

    [JsonPropertyName("packageId")]
    public string PackageId { get; set; } = string.Empty;

    [JsonPropertyName("resolvedVersion")]
    public string ResolvedVersion { get; set; } = string.Empty;

    [JsonPropertyName("advisoryUrl")]
    public string AdvisoryUrl { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;
}
