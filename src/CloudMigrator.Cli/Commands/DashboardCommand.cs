using System.CommandLine;
using System.Diagnostics;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Dashboard;
using Microsoft.Extensions.Configuration;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// dashboard サブコマンド - 移行進捗を Web ダッシュボードでリアルタイム表示する。
/// </summary>
internal static class DashboardCommand
{
    public static Command Build()
    {
        var dbOption = new Option<string?>("--db")
        {
            Description = "転送状態 DB のパス（省略時は設定ファイルの値を使用）",
        };
        var portOption = new Option<int>("--port")
        {
            Description = "ダッシュボードのポート番号",
            DefaultValueFactory = _ => 5050,
        };
        var noBrowserOption = new Option<bool>("--no-browser")
        {
            Description = "ブラウザを自動起動しない",
        };

        var cmd = new Command("dashboard", "移行進捗の Web ダッシュボードを起動します");
        cmd.Add(dbOption);
        cmd.Add(portOption);
        cmd.Add(noBrowserOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var config = AppConfiguration.Build();
            var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
                ?? new MigratorOptions();

            // destinationProvider に応じてデフォルト DB を切り替える（SharePoint: SP 用 DB、Dropbox: Dropbox 用 DB）
            var defaultDbPath = options.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase)
                ? options.Paths.DropboxStateDb
                : options.Paths.SharePointStateDb;
            var dbPath = parseResult.GetValue(dbOption) ?? defaultDbPath;
            var port = parseResult.GetValue(portOption);
            var noBrowser = parseResult.GetValue(noBrowserOption);
            var url = $"http://localhost:{port}";

            // DB ファイルが存在する場合は接続して起動。存在しない場合は DB なしモードで起動。
            // DB なしモードでも UI は表示され、転送開始後に自動的に接続される旨をガイドする。
            bool dbExists = !string.IsNullOrEmpty(dbPath) && File.Exists(dbPath);
            string? activeDbPath = dbExists ? dbPath : null;

            Console.WriteLine($"CloudMigrator Studio 起動中: {url}");
            if (dbExists)
                Console.WriteLine($"DB          : {dbPath}");
            else
                Console.WriteLine("DB          : なし — 転送後に自動接続されます");
            Console.WriteLine("終了するには Ctrl+C を押してください。");

            if (!noBrowser)
                TryOpenBrowser(url);

            await DashboardServer.RunAsync(activeDbPath, port, ct).ConfigureAwait(false);
        });

        return cmd;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // UIのない環境（CI、SSH 等）では無視する
        }
    }
}
