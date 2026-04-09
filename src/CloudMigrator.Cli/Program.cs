using System.CommandLine;
using System.Diagnostics;
using CloudMigrator.Cli.Commands;
using CloudMigrator.Core.Configuration;
using Microsoft.Extensions.Configuration;

// 初回起動時: ./configs/config.json → AppData へ自動移行
// 移行メッセージは JSON ログ（stdout）との混在を避けるため stderr に出力する
var migration = AppConfiguration.MigrateConfigIfNeeded();
if (migration.Migrated)
    Console.Error.WriteLine($"[INFO] 設定ファイルを AppData へ移行しました: {migration.SourcePath} → {migration.DestPath}");
else if (migration.Error is not null)
    Console.Error.WriteLine($"[WARN] 設定ファイルの移行に失敗しました: {migration.Error.Message}");

// AppData ディレクトリ作成: 権限不足や読み取り専用環境でも --help 等が中断しないよう例外を捕捉する
try
{
    AppDataPaths.EnsureDirectoriesExist();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[WARN] AppData ディレクトリの作成に失敗しました: {ex.Message}");
}

// 非 Windows 環境チェック: CloudMigrator は Windows 専用アプリケーション（Windows Credential Manager 等）
if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("[ERROR] CloudMigrator は Windows 専用アプリケーションです。Windows 環境で実行してください。");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var rootCmd = new RootCommand("CloudMigrator - OneDrive から SharePoint へのファイル移行ツール");

// 引数なし起動: CloudMigrator Dashboard (WPF) を起動する
rootCmd.SetAction(async (parseResult, ct) =>
{
    await Task.CompletedTask;
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

    var exePath = FindDashboardExe();
    if (exePath is null)
    {
        Console.Error.WriteLine("エラー: CloudMigrator.Dashboard.exe が見つかりません。インストールを確認してください。");
        return;
    }

    var dbArg = dbExists ? $"--db-path \"{defaultDbPath}\"" : string.Empty;
    Process.Start(new ProcessStartInfo(exePath, dbArg)
    {
        UseShellExecute = true,
    });
});

rootCmd.Add(TransferCommand.Build());
rootCmd.Add(TransferStatusCommand.Build());
rootCmd.Add(RebuildSkipListCommand.Build());
rootCmd.Add(WatchdogCommand.Build());
rootCmd.Add(QualityMetricsCommand.Build());
rootCmd.Add(SecurityScanCommand.Build());
rootCmd.Add(FileCrawlerCommand.Build());
rootCmd.Add(DashboardCommand.Build());
rootCmd.Add(SetupCommand.Build());

return await rootCmd.Parse(args).InvokeAsync(new InvocationConfiguration(), cts.Token);

static string? FindDashboardExe()
{
    var baseDir = AppContext.BaseDirectory;
    var candidate = Path.Combine(baseDir, "CloudMigrator.Dashboard.exe");
    if (File.Exists(candidate))
        return candidate;

    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    var installCandidate = Path.Combine(localAppData, "Programs", "CloudMigrator", "CloudMigrator.Dashboard.exe");
    return File.Exists(installCandidate) ? installCandidate : null;
}
