using System.IO;
using System.Windows;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Credentials;
using CloudMigrator.Core.Setup;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Core.Wizard;
using CloudMigrator.Dashboard.Runners;
using CloudMigrator.Observability;
using CloudMigrator.Providers.Dropbox.Auth;
using CloudMigrator.Providers.Graph;
using CloudMigrator.Providers.Graph.Auth;
using CloudMigrator.Routes;
using CloudMigrator.Routes.Descriptors;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        // 実効設定ファクトリを DI 登録（呼ぶたびに AppConfiguration.Build() で最新設定を取得）
        // singleton ではなくファクトリにすることで、設定変更後も UI が古い値を参照し続けない
        // ジョブ実行側（MigrationWork）も都度 AppConfiguration.Build() するため動作が一致する
        services.AddSingleton<Func<MigratorOptions>>(_ =>
            () => AppConfiguration.Build()
                .GetSection(MigratorOptions.SectionName)
                .Get<MigratorOptions>() ?? new MigratorOptions());

        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<ITransferStateDbAccessor>(sp =>
            new TransferStateDbAccessor(
                sp.GetRequiredService<Func<MigratorOptions>>(),
                dbPath,
                sp.GetRequiredService<ILogger<TransferStateDbAccessor>>()));
        services.AddSingleton<ITransferJobService>(sp =>
        {
            var loggerFactory2 = sp.GetRequiredService<ILoggerFactory>();
            var stateDbAccessor = sp.GetRequiredService<ITransferStateDbAccessor>();
            var runnerRegistry = sp.GetRequiredService<MigrationPipelineRunnerRegistry>();

            async Task MigrationWork(CancellationToken ct)
            {
                var configuration = AppConfiguration.Build();
                var opts = configuration.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>() ?? new MigratorOptions();

                // UI とジョブが同じ route-aware な DB 解決を使うことで、Dropbox 実行時も
                // Dashboard が書き込み先と同じ state DB を参照できるようにする。
                var effectiveStateDb = await stateDbAccessor.GetForOptionsAsync(opts, ct).ConfigureAwait(false);

                // 設定変更検知（FR-10）: CLI の TransferCommand と同等のハッシュ確認を行う
                var configHash = ConfigHashChecker.ComputeHash(opts);
                bool hashChanged = await ConfigHashChecker.HasChangedAsync(opts.Paths.ConfigHash, configHash, ct)
                    .ConfigureAwait(false);
                if (hashChanged)
                {
                    var hashLogger = loggerFactory2.CreateLogger("MigrationWork");
                    hashLogger.LogWarning("設定変更を検知しました。キャッシュと skip_list をクリアします。");
                    ConfigHashChecker.ClearAll(opts.Paths, hashLogger);
                    await ConfigHashChecker.SaveHashAsync(opts.Paths.ConfigHash, configHash, ct).ConfigureAwait(false);
                    await effectiveStateDb.ResetAllAsync(ct).ConfigureAwait(false);
                }

                // プロバイダー名からランナーを解決してパイプラインを実行する
                // 新プロバイダー追加は Runner + DI 登録のみで対応可能（App.xaml.cs の変更不要）
                var runner = runnerRegistry.Resolve(opts.DestinationProvider);
                await runner.RunAsync(opts, effectiveStateDb, ct).ConfigureAwait(false);
            }

            return new TransferJobService(
                loggerFactory2.CreateLogger<TransferJobService>(),
                MigrationWork);
        });

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

        // ── WPF Host サービス ──────────────────────────────────────────────
        services.AddSingleton<INativeDialogService, WpfDialogService>();

        // ── Wizard ────────────────────────────────────────────────────────────
        services.AddSingleton<IWizardStateService, WizardStateService>();
        services.AddSingleton<ICredentialStore>(_ => CredentialStoreFactory.Create());
        services.AddSingleton<IDropboxOAuthService, DropboxOAuthService>();
        services.AddSingleton<IDropboxVerifyService, DropboxVerifyService>();
        services.AddSingleton<IDropboxFolderService, DropboxFolderService>();
        services.AddSingleton<IAzureAuthVerifyService, AzureAuthVerifyService>();
        services.AddSingleton<IGraphDiscoveryService, GraphDiscoveryService>();
        services.AddSingleton<ISharePointVerifyService, SharePointVerifyService>();

        // ── 移行ルート descriptor ──
        services.AddSingleton<IMigrationRouteDescriptor, SharePointRouteDescriptor>();
        services.AddSingleton<IMigrationRouteDescriptor, DropboxRouteDescriptor>();
        services.AddSingleton<MigrationRouteRegistry>();

        // ── 移行パイプライン Runner（#209: isDropbox 分岐を runner 委譲に置換）──
        services.AddSingleton<IMigrationPipelineRunner, SharePointPipelineRunner>();
        services.AddSingleton<IMigrationPipelineRunner, DropboxPipelineRunner>();
        services.AddSingleton<MigrationPipelineRunnerRegistry>();

        // ── HTTP ─────────────────────────────────────────────────────────────
        services.AddHttpClient();

        // ── BlazorWebView + MudBlazor ────────────────────────────────────
        services.AddWpfBlazorWebView();
        services.AddMudServices();

        // ── WPF ウィンドウ ────────────────────────────────────────────────
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

}
