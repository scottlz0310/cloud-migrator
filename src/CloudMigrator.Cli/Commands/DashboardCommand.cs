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

        cmd.SetAction((parseResult, ct) =>
        {
            var config = AppConfiguration.Build();
            var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
                ?? new MigratorOptions();

            // destinationProvider に応じてデフォルト DB を切り替える
            var defaultDbPath = options.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase)
                ? options.Paths.DropboxStateDb
                : options.Paths.SharePointStateDb;
            var dbPath = parseResult.GetValue(dbOption) ?? defaultDbPath;

            Console.WriteLine("CloudMigrator Dashboard を起動しています...");
            DashboardLauncher.Launch(dbPath);
            return Task.CompletedTask;
        });

        return cmd;
    }

}

/// <summary>
/// CloudMigrator.Dashboard.exe の探索と起動を担う共通ヘルパー。
/// Program.cs と DashboardCommand.cs の両方から使用する。
/// </summary>
internal static class DashboardLauncher
{
    /// <summary>
    /// 設定ファイルから DB パスを自動解決して Dashboard を起動する（引数なし起動用）。
    /// </summary>
    public static void LaunchWithAutoDetect()
    {
        var config = AppConfiguration.Build();
        var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
            ?? new MigratorOptions();
        var defaultDbPath = options.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase)
            ? options.Paths.DropboxStateDb
            : options.Paths.SharePointStateDb;
        bool dbExists = !string.IsNullOrWhiteSpace(defaultDbPath) && File.Exists(defaultDbPath);

        Console.WriteLine("CloudMigrator Dashboard を起動しています...");
        if (dbExists)
            Console.WriteLine($"DB          : {defaultDbPath}");
        else
            Console.WriteLine("DB          : なし — transfer 実行後、DB が作成されたら再起動してください");

        Launch(dbExists ? defaultDbPath : null);
    }

    /// <summary>Dashboard.exe を探して起動する。失敗時はエラーをコンソールに出力する。</summary>
    public static void Launch(string? dbPath = null)
    {
        var exePath = FindDashboardExe();
        if (exePath is null)
        {
            Console.Error.WriteLine("エラー: CloudMigrator.Dashboard.exe が見つかりません。インストールを確認してください。");
            return;
        }

        var psi = new ProcessStartInfo(exePath) { UseShellExecute = true };
        if (!string.IsNullOrEmpty(dbPath))
        {
            // ArgumentList を使って OS に安全にエスケープさせる（引数インジェクション防止）
            psi.ArgumentList.Add("--db-path");
            psi.ArgumentList.Add(dbPath);
        }
        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"エラー: CloudMigrator Dashboard の起動に失敗しました: {ex.Message}");
            Console.Error.WriteLine($"手動で起動してください: {exePath}");
        }
    }

    /// <summary>CloudMigrator.Dashboard.exe のパスを探す。</summary>
    private static string? FindDashboardExe()
    {
        // 1. 同一ディレクトリ（publish / self-contained インストール）
        var candidate = Path.Combine(AppContext.BaseDirectory, "CloudMigrator.Dashboard.exe");
        if (File.Exists(candidate))
            return candidate;

        // 2. インストール済みパス
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var installCandidate = Path.Combine(localAppData, "Programs", "CloudMigrator", "CloudMigrator.Dashboard.exe");
        if (File.Exists(installCandidate))
            return installCandidate;

        // 3. 開発モード: dotnet run 時の sibling プロジェクト bin を探す
        //    AppContext.BaseDirectory = .../CloudMigrator.Cli/bin/{config}/{tfm}/
        //    3 階層上 = .../CloudMigrator.Cli/  → さらに 1 つ上 = .../src/
        var srcDir = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Parent?.Parent?.Parent;
        if (srcDir != null)
        {
            var dashboardBin = Path.Combine(srcDir.FullName, "CloudMigrator.Dashboard", "bin");
            if (Directory.Exists(dashboardBin))
            {
                var devExe = Directory
                    .GetFiles(dashboardBin, "CloudMigrator.Dashboard.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (devExe != null)
                    return devExe;
            }
        }

        return null;
    }
}
