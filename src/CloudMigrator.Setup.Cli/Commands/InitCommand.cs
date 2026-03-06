using System.CommandLine;
using System.Text.Json;
using CloudMigrator.Core.Configuration;

namespace CloudMigrator.Setup.Cli.Commands;

/// <summary>
/// setup init コマンド。
/// config.json と .env テンプレートを冪等に生成する。
/// </summary>
internal static class InitCommand
{
    public static Command Build()
    {
        var cmd = new Command("init", "設定テンプレート（config.json/.env）を生成します");
        var configPathOpt = new Option<string>("--config-path")
        {
            Description = "config.json の出力先",
            DefaultValueFactory = _ => "configs/config.json",
        };
        var envPathOpt = new Option<string>("--env-path")
        {
            Description = ".env テンプレートの出力先",
            DefaultValueFactory = _ => ".env",
        };
        var forceOpt = new Option<bool>("--force")
        {
            Description = "既存ファイルを上書きします",
        };
        cmd.Add(configPathOpt);
        cmd.Add(envPathOpt);
        cmd.Add(forceOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var configPath = parseResult.GetValue(configPathOpt) ?? "configs/config.json";
            var envPath = parseResult.GetValue(envPathOpt) ?? ".env";
            var force = parseResult.GetValue(forceOpt);
            await RunAsync(configPath, envPath, force, ct).ConfigureAwait(false);
        });

        return cmd;
    }

    internal static async Task RunAsync(
        string configPath,
        string envPath,
        bool force,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var configTemplate = await LoadTemplateAsync(
            relativePath: Path.Combine("configs", "config.json"),
            fallbackContent: BuildDefaultConfigTemplate(),
            ct: ct).ConfigureAwait(false);
        var envTemplate = await LoadTemplateAsync(
            relativePath: "sample.env",
            fallbackContent: DefaultEnvTemplate,
            ct: ct).ConfigureAwait(false);

        var results = new List<InitFileResult>(capacity: 2);
        await WriteTemplateAsync(configPath, configTemplate, force, results, ct).ConfigureAwait(false);
        await WriteTemplateAsync(envPath, envTemplate, force, results, ct).ConfigureAwait(false);

        foreach (var result in results)
        {
            switch (result.Status)
            {
                case InitFileStatus.Written:
                    Console.WriteLine($"[OK]   {result.Path}: 生成しました");
                    break;
                case InitFileStatus.Skipped:
                    Console.WriteLine($"[SKIP] {result.Path}: 既存のため未変更（--force で上書き可）");
                    break;
                case InitFileStatus.Error:
                    Console.WriteLine($"[ERR]  {result.Path}: {result.Message}");
                    break;
            }
        }

        if (results.Any(r => r.Status == InitFileStatus.Error))
            Environment.ExitCode = 1;
    }

    internal static async Task WriteTemplateAsync(
        string outputPath,
        string content,
        bool force,
        IList<InitFileResult> results,
        CancellationToken ct)
    {
        var fullPath = Path.GetFullPath(outputPath);
        if (File.Exists(fullPath) && !force)
        {
            results.Add(new InitFileResult(InitFileStatus.Skipped, fullPath, "既存ファイルを維持"));
            return;
        }

        var directoryPath = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        try
        {
            await File.WriteAllTextAsync(fullPath, content, ct).ConfigureAwait(false);
            results.Add(new InitFileResult(InitFileStatus.Written, fullPath, "生成完了"));
        }
        catch (UnauthorizedAccessException ex)
        {
            results.Add(new InitFileResult(InitFileStatus.Error, fullPath, ex.Message));
        }
        catch (IOException ex)
        {
            results.Add(new InitFileResult(InitFileStatus.Error, fullPath, ex.Message));
        }
    }

    private static async Task<string> LoadTemplateAsync(
        string relativePath,
        string fallbackContent,
        CancellationToken ct)
    {
        var templatePath = ResolveTemplatePath(relativePath);
        if (templatePath is null)
            return fallbackContent;

        return await File.ReadAllTextAsync(templatePath, ct).ConfigureAwait(false);
    }

    internal static string? ResolveTemplatePath(string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);

        var cwdCandidate = Path.GetFullPath(normalizedRelativePath, Environment.CurrentDirectory);
        if (File.Exists(cwdCandidate))
            return cwdCandidate;

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, normalizedRelativePath);
            if (File.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;

            dir = parent.FullName;
        }

        return null;
    }

    private static string BuildDefaultConfigTemplate()
    {
        var model = new { Migrator = new MigratorOptions() };
        return JsonSerializer.Serialize(
            model,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
    }

    private const string DefaultEnvTemplate =
        """
        # Microsoft Graph API 認証情報（必須）
        MIGRATOR__GRAPH__CLIENTID=your-client-id-here
        MIGRATOR__GRAPH__CLIENTSECRET=your-client-secret-here
        MIGRATOR__GRAPH__TENANTID=your-tenant-id-here

        # OneDrive / SharePoint 対象リソース
        MIGRATOR__GRAPH__ONEDRIVEUSERID=user@example.com
        MIGRATOR__GRAPH__SHAREPOINTSITEID=your-site-id-here
        MIGRATOR__GRAPH__SHAREPOINTDRIVEID=your-drive-id-here

        # Dropbox（任意）
        MIGRATOR__DROPBOX__ACCESSTOKEN=your-dropbox-access-token-here
        """;
}

internal enum InitFileStatus
{
    Written,
    Skipped,
    Error,
}

internal sealed record InitFileResult(
    InitFileStatus Status,
    string Path,
    string Message);
