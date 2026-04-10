using System.IO;
using System.Windows;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Setup;
using CloudMigrator.Core.State;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CloudMigrator.Dashboard;

/// <summary>
/// WPF アプリケーションのエントリポイント。
/// DI コンテナを構成し、MainWindow を起動する。
/// </summary>
public partial class App : Application
{
    private IServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // コマンドライン引数: --db-path <path> でDBパスを指定可能
        string? dbPath = null;
        var args = e.Args;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--db-path")
            {
                dbPath = args[i + 1];
                break;
            }
        }

        _services = BuildServiceProvider(dbPath);

        var mainWindow = _services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is IAsyncDisposable ad)
            ad.DisposeAsync().AsTask().GetAwaiter().GetResult();
        else if (_services is IDisposable d)
            d.Dispose();
        base.OnExit(e);
    }

    private static IServiceProvider BuildServiceProvider(string? dbPath)
    {
        var services = new ServiceCollection();

        // ── Observability ────────────────────────────────────────────────────
        var logStreamSink = new LogStreamSink();
        services.AddSingleton(logStreamSink);
        services.AddSingleton<ILogChannel, LogChannelAdapter>();

        // Serilog パイプラインを構築して LogStreamSink に接続する（LogsPage へのリアルタイム配信に必要）
        var logFilePath = AppDataPaths.LogFile("dashboard.log");
        var loggerFactory = LoggingSetup.CreateLoggerFactory(logFilePath, logStreamSink: logStreamSink);
        services.AddSingleton(loggerFactory);
        services.AddLogging();

        // ── Application services ─────────────────────────────────────────────
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<ITransferJobService, TransferJobService>();

        // SetupDoctorService: Core と同じ設定解決順序（環境変数 > config.json > デフォルト値）で資格情報を読み取る
        services.AddSingleton<ISetupDoctorService>(sp =>
        {
            var configuration = AppConfiguration.Build();
            var opts = new DoctorOptions(
                ClientId: configuration["Migrator:Graph:ClientId"] ?? string.Empty,
                TenantId: configuration["Migrator:Graph:TenantId"] ?? string.Empty,
                ClientSecret: AppConfiguration.GetGraphClientSecret(),
                SiteId: configuration["Migrator:Graph:SharePointSiteId"] ?? string.Empty,
                DriveId: configuration["Migrator:Graph:SharePointDriveId"] ?? string.Empty,
                DestinationRoot: configuration["Migrator:DestinationRoot"] ?? string.Empty);
            return new SetupDoctorService(opts, sp.GetRequiredService<System.Net.Http.IHttpClientFactory>());
        });

        // ITransferStateDb: --db-path 引数 > MigratorOptions デフォルトパス > NullObject
        services.AddSingleton<ITransferStateDb>(sp =>
        {
            var resolvedPath = dbPath ?? ResolveDefaultDbPath();
            if (resolvedPath is not null && File.Exists(resolvedPath))
            {
                var db = new SqliteTransferStateDb(resolvedPath);
                try
                {
                    db.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // 初期化失敗: db を確実に破棄してからフォールバック（ファイルハンドルのリーク防止）
                    db.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    MessageBox.Show(
                        $"DB の初期化に失敗しました。DB なしモードで起動します。\n\n{ex.Message}",
                        "警告",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return NullTransferStateDb.Instance;
                }
                return db;
            }
            return NullTransferStateDb.Instance;
        });

        // ── WPF Host サービス ──────────────────────────────────────────────
        services.AddSingleton<INativeDialogService, WpfDialogService>();

        // ── HTTP ─────────────────────────────────────────────────────────────
        services.AddHttpClient();

        // ── BlazorWebView + MudBlazor ────────────────────────────────────
        services.AddWpfBlazorWebView();
        services.AddMudServices();

        // ── WPF ウィンドウ ────────────────────────────────────────────────
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// デフォルトの DB パスを解決する。
    /// 両方の DB が存在する場合はユーザーに選択させる。
    /// </summary>
    private static string? ResolveDefaultDbPath()
    {
        var dropboxDb = AppDataPaths.LogFile("dropbox_transfer_state.db");
        var sharePointDb = AppDataPaths.LogFile("sharepoint_transfer_state.db");
        var hasDropbox = File.Exists(dropboxDb);
        var hasSharePoint = File.Exists(sharePointDb);

        if (hasDropbox && hasSharePoint)
        {
            var result = MessageBox.Show(
                "Dropbox 用 DB と SharePoint 用 DB の両方が見つかりました。\n" +
                "表示する DB を選択してください。\n\n" +
                "はい: Dropbox\nいいえ: SharePoint\nキャンセル: 選択しない",
                "DB の選択",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            return result switch
            {
                MessageBoxResult.Yes => dropboxDb,
                MessageBoxResult.No => sharePointDb,
                _ => null,
            };
        }

        if (hasDropbox) return dropboxDb;
        if (hasSharePoint) return sharePointDb;
        return null;
    }
}
