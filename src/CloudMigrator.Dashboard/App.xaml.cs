using System.IO;
using System.Windows;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.State;
using CloudMigrator.Observability;
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

        // ── Application services ─────────────────────────────────────────────
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<ITransferJobService, TransferJobService>();

        // SetupDoctorService: 環境変数から資格情報を読み取る
        services.AddSingleton<ISetupDoctorService>(sp =>
        {
            var opts = new DoctorOptions(
                ClientId: Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTID") ?? string.Empty,
                TenantId: Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__TENANTID") ?? string.Empty,
                ClientSecret: Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__CLIENTSECRET") ?? string.Empty,
                SiteId: Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__SITEID") ?? string.Empty,
                DriveId: Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__DRIVEID") ?? string.Empty,
                DestinationRoot: Environment.GetEnvironmentVariable("MIGRATOR__GRAPH__DESTINATIONROOT") ?? string.Empty);
            return new SetupDoctorService(opts, sp.GetRequiredService<System.Net.Http.IHttpClientFactory>());
        });

        // ITransferStateDb: --db-path 引数 > MigratorOptions デフォルトパス > NullObject
        services.AddSingleton<ITransferStateDb>(sp =>
        {
            var resolvedPath = dbPath ?? ResolveDefaultDbPath();
            if (resolvedPath is not null && File.Exists(resolvedPath))
            {
                var db = new SqliteTransferStateDb(resolvedPath);
                db.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
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
    /// Dropbox DB → SharePoint DB の順で存在確認し、最初に見つかったものを返す。
    /// </summary>
    private static string? ResolveDefaultDbPath()
    {
        var candidates = new[]
        {
            AppDataPaths.LogFile("dropbox_transfer_state.db"),
            AppDataPaths.LogFile("sharepoint_transfer_state.db"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
