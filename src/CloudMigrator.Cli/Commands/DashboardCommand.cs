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
        cmd.Options.Add(dbOption);
        cmd.Options.Add(portOption);
        cmd.Options.Add(noBrowserOption);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var config = AppConfiguration.Build();
            var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
                ?? new MigratorOptions();

            var dbPath = parseResult.GetValue(dbOption) ?? options.Paths.DropboxStateDb;
            var port = parseResult.GetValue(portOption);
            var noBrowser = parseResult.GetValue(noBrowserOption);
            var url = $"http://localhost:{port}";

            Console.WriteLine($"ダッシュボード起動: {url}");
            Console.WriteLine($"DB          : {dbPath}");
            Console.WriteLine("終了するには Ctrl+C を押してください。");

            if (!noBrowser)
                TryOpenBrowser(url);

            await DashboardServer.RunAsync(dbPath, port, ct).ConfigureAwait(false);
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
