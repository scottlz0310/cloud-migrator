using System.CommandLine;
using System.Diagnostics;
using CloudMigrator.Cli.Commands;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Dashboard;
using Microsoft.Extensions.Configuration;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var rootCmd = new RootCommand("CloudMigrator - OneDrive から SharePoint へのファイル移行ツール");

// トップレベルオプション（引数なし起動で Studio を開く際に使用）
var portOption = new Option<int>("--port")
{
    Description = "Studio のポート番号",
    DefaultValueFactory = _ => 5050,
};
var noBrowserOption = new Option<bool>("--no-browser")
{
    Description = "ブラウザを自動起動しない",
};
rootCmd.Add(portOption);
rootCmd.Add(noBrowserOption);

// 引数なし起動: CloudMigrator Studio を起動する
rootCmd.SetAction(async (parseResult, ct) =>
{
    var port = parseResult.GetValue(portOption);
    var noBrowser = parseResult.GetValue(noBrowserOption);
    var url = $"http://localhost:{port}";

    // 設定から DB パスを取得し、存在する場合のみ接続する
    var config = AppConfiguration.Build();
    var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
        ?? new MigratorOptions();
    var defaultDbPath = options.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase)
        ? options.Paths.DropboxStateDb
        : options.Paths.SharePointStateDb;
    bool dbExists = !string.IsNullOrEmpty(defaultDbPath) && File.Exists(defaultDbPath);
    string? activeDbPath = dbExists ? defaultDbPath : null;

    Console.WriteLine($"CloudMigrator Studio 起動中: {url}");
    if (dbExists)
        Console.WriteLine($"DB          : {defaultDbPath}");
    else
        Console.WriteLine("DB          : なし — 転送後に自動接続されます");
    Console.WriteLine("終了するには Ctrl+C を押してください。");

    if (!noBrowser)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* UIのない環境では無視する */ }
    }

    await DashboardServer.RunAsync(activeDbPath, port, ct).ConfigureAwait(false);
});

rootCmd.Add(TransferCommand.Build());
rootCmd.Add(TransferStatusCommand.Build());
rootCmd.Add(RebuildSkipListCommand.Build());
rootCmd.Add(WatchdogCommand.Build());
rootCmd.Add(QualityMetricsCommand.Build());
rootCmd.Add(SecurityScanCommand.Build());
rootCmd.Add(FileCrawlerCommand.Build());
rootCmd.Add(DashboardCommand.Build());

return await rootCmd.Parse(args).InvokeAsync(new InvocationConfiguration(), cts.Token);

