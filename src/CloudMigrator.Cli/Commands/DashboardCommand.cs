using System.CommandLine;
using System.Diagnostics;
using CloudMigrator.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// dashboard サブコマンド - 移行進捗を Blazor Hybrid WPF ダッシュボードで表示する。
/// </summary>
internal static class DashboardCommand
{
    public static Command Build()
    {
        var dbOption = new Option<string?>("--db")
        {
            Description = "転送状態 DB のパス（省略時は設定ファイルの値を使用）",
        };

        var cmd = new Command("dashboard", "移行進捗の Blazor Hybrid ダッシュボードを起動します");
        cmd.Add(dbOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            await Task.CompletedTask;
            var config = AppConfiguration.Build();
            var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
                ?? new MigratorOptions();

            // destinationProvider に応じてデフォルト DB を切り替える
            var defaultDbPath = options.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase)
                ? options.Paths.DropboxStateDb
                : options.Paths.SharePointStateDb;
            var dbPath = parseResult.GetValue(dbOption) ?? defaultDbPath;

            Console.WriteLine("CloudMigrator Dashboard を起動しています...");

            // CloudMigrator.Dashboard.exe を独立プロセスとして起動する
            var exePath = FindDashboardExe();
            if (exePath is null)
            {
                Console.Error.WriteLine("エラー: CloudMigrator.Dashboard.exe が見つかりません。インストールを確認してください。");
                return;
            }

            var args = string.IsNullOrEmpty(dbPath) ? string.Empty : $"--db-path \"{dbPath}\"";
            Process.Start(new ProcessStartInfo(exePath, args)
            {
                UseShellExecute = true,
            });
        });

        return cmd;
    }

    /// <summary>
    /// CloudMigrator.Dashboard.exe を探す。
    /// 現在の実行ファイルと同じディレクトリを優先する。
    /// </summary>
    private static string? FindDashboardExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "CloudMigrator.Dashboard.exe");
        if (File.Exists(candidate))
            return candidate;

        // インストーラーが Programs フォルダに配置する場合のパス
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installCandidate = Path.Combine(localAppData, "Programs", "CloudMigrator", "CloudMigrator.Dashboard.exe");
        if (File.Exists(installCandidate))
            return installCandidate;

        return null;
    }
}
