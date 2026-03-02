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
        using var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger("security-scan");

        logger.LogInformation("セキュリティスキャンを開始します: {Project}", project);

        var (stdout, stderr, exitCode) = await RunDotnetCommandAsync(
            $"list \"{project}\" package --vulnerable --include-transitive",
            ct).ConfigureAwait(false);

        if (exitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            logger.LogWarning("dotnet list package stderr: {Stderr}", stderr);

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
    /// 出力例: "   > PackageName  1.0.0  2.0.0  https://ghsa..."
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
        string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

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

    [GeneratedRegex(@">\s+(\S+)\s+(\S+)\s+(https?://\S+)\s*(\S*)")]
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
