using System.IO;
using System.Windows;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Credentials;
using CloudMigrator.Core.Migration;
using CloudMigrator.Core.Setup;
using CloudMigrator.Core.State;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Core.Wizard;
using CloudMigrator.Observability;
using CloudMigrator.Providers.Dropbox.Auth;
using CloudMigrator.Providers.Graph;
using CloudMigrator.Providers.Graph.Auth;
using CloudMigrator.Providers.Graph.Http;
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
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<ITransferJobService>(sp =>
        {
            var loggerFactory2 = sp.GetRequiredService<ILoggerFactory>();
            var stateDb = sp.GetRequiredService<ITransferStateDb>();

            async Task MigrationWork(CancellationToken ct)
            {
                var configuration = AppConfiguration.Build();
                var opts = configuration.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>() ?? new MigratorOptions();

                ICredentialStore credentialStore = CredentialStoreFactory.Create();
                var clientSecret = await credentialStore.GetAsync(CredentialKeys.AzureClientSecret).ConfigureAwait(false)
                    ?? AppConfiguration.GetGraphClientSecret();

                var auth = new GraphAuthenticator(opts.Graph.ClientId, opts.Graph.TenantId, clientSecret);

                // AdaptiveConcurrencyController の初期化（config.json の AdaptiveConcurrency.sharepoint.Enabled = true で有効）
                // Dashboard では UseRateControl=false 時のみ ACC を使用する（RateControlledTransferController の UI は未実装）
                var adaptiveOpts = opts.GetAdaptiveConcurrency("sharepoint");
                AdaptiveConcurrencyController? accMain = null;        // Dispose + onRateLimit 用に保持
                AdaptiveConcurrencyController? accFolder = null;      // Dispose + onRateLimit 用に保持
                ITransferRateController? concurrencyController = null;
                ITransferRateController? folderCreationController = null;
                Action<TimeSpan?>? onRateLimit = null;
                Action<ITransferRateController?>? activateController = null;
                if (adaptiveOpts.Enabled && !opts.RateControl.UseRateControl)
                {
                    var initialDegree = adaptiveOpts.InitialDegree > 0
                        ? Math.Min(adaptiveOpts.InitialDegree, opts.MaxParallelTransfers)
                        : opts.MaxParallelTransfers;
                    accMain = new AdaptiveConcurrencyController(
                        initialDegree: initialDegree,
                        minDegree: adaptiveOpts.MinDegree,
                        maxDegree: opts.MaxParallelTransfers,
                        increaseIntervalSec: adaptiveOpts.IncreaseIntervalSec,
                        logger: loggerFactory2.CreateLogger<AdaptiveConcurrencyController>(),
                        increaseStep: adaptiveOpts.IncreaseStep,
                        decreaseTriggerCount: adaptiveOpts.DecreaseTriggerCount,
                        decreaseMultiplier: adaptiveOpts.DecreaseMultiplier);
                    concurrencyController = new AdaptiveConcurrencyControllerAdapter(accMain);

                    // Phase C（フォルダ先行作成）専用コントローラー（maxDegree = MaxParallelFolderCreations）
                    // 転送用コントローラーとは独立させ、Phase C の 429 が Phase D の並列度に影響しないようにする
                    var maxFolderCreationDegree = Math.Max(1, opts.MaxParallelFolderCreations);
                    var folderInitialDegree = adaptiveOpts.InitialDegree > 0
                        ? Math.Min(adaptiveOpts.InitialDegree, maxFolderCreationDegree)
                        : maxFolderCreationDegree;
                    accFolder = new AdaptiveConcurrencyController(
                        initialDegree: folderInitialDegree,
                        minDegree: Math.Min(adaptiveOpts.MinDegree, maxFolderCreationDegree),
                        maxDegree: maxFolderCreationDegree,
                        increaseIntervalSec: adaptiveOpts.IncreaseIntervalSec,
                        logger: loggerFactory2.CreateLogger<AdaptiveConcurrencyController>(),
                        increaseStep: adaptiveOpts.IncreaseStep,
                        decreaseTriggerCount: adaptiveOpts.DecreaseTriggerCount,
                        decreaseMultiplier: adaptiveOpts.DecreaseMultiplier);
                    folderCreationController = new AdaptiveConcurrencyControllerAdapter(accFolder);

                    // onRateLimit はプロキシ経由にしてフェーズに応じて通知先を切り替える
                    AdaptiveConcurrencyController? activeCtrl = accMain;
                    onRateLimit = retryAfter => Volatile.Read(ref activeCtrl)?.NotifyRateLimit(retryAfter);
                    // 参照一致で folderCreationController（Phase C 用）か concurrencyController（Phase D 用）かを判別する
                    activateController = ctrl =>
                        Volatile.Write(ref activeCtrl, ReferenceEquals(ctrl, folderCreationController) ? accFolder : accMain);
                }

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
                    // stateDb は DI シングルトンであり、以下のパイプラインにも同一インスタンスを渡す。
                    // つまり「パイプラインが使う DB と同じ DB をリセットする」ため、誤ったDBへの操作は発生しない。
                    await stateDb.ResetAllAsync(ct).ConfigureAwait(false);
                }

                try
                {
                    var graphClient = GraphClientFactory.Create(
                        auth,
                        timeoutSec: opts.TimeoutSec,
                        maxRetry: opts.RetryCount,
                        onRateLimit: onRateLimit ?? (_ => { }),
                        rateLimitLogger: loggerFactory2.CreateLogger<GraphStorageProvider>());

                    var storageOptions = new GraphStorageOptions
                    {
                        OneDriveUserId = opts.Graph.OneDriveUserId,
                        SharePointDriveId = opts.Graph.SharePointDriveId,
                        OneDriveSourceFolder = opts.Graph.OneDriveSourceFolder,
                    };

                    var sessionStore = new UploadSessionStore(Path.Combine(AppDataPaths.LogsDirectory, "upload_sessions.json"));

                    var storageProvider = new GraphStorageProvider(
                        graphClient,
                        loggerFactory2.CreateLogger<GraphStorageProvider>(),
                        storageOptions,
                        largeFileThresholdMb: opts.LargeFileThresholdMb,
                        chunkSizeMb: opts.ChunkSizeMb,
                        sessionStore: sessionStore);

                    var pipeline = new SharePointMigrationPipeline(
                        storageProvider,
                        storageProvider,
                        stateDb,
                        opts,
                        loggerFactory2.CreateLogger<SharePointMigrationPipeline>(),
                        concurrencyController,
                        folderCreationController,
                        activateController);

                    await pipeline.RunAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    // AdaptiveConcurrencyControllerAdapter は内部 ACC を所有しないため ACC を直接 Dispose する
                    accMain?.Dispose();
                    accFolder?.Dispose();
                }
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

        // ITransferStateDb: --db-path 引数 > 選択済み DB > SharePoint デフォルト（初回実行時は DB を新規作成）
        services.AddSingleton<ITransferStateDb>(sp =>
        {
            var resolvedPath = dbPath ?? ResolveDefaultDbPath();
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
        });

        // ── WPF Host サービス ──────────────────────────────────────────────
        services.AddSingleton<INativeDialogService, WpfDialogService>();

        // ── Wizard ────────────────────────────────────────────────────────────
        services.AddSingleton<IWizardStateService, WizardStateService>();
        services.AddSingleton<ICredentialStore>(_ => CredentialStoreFactory.Create());
        services.AddSingleton<IDropboxOAuthService, DropboxOAuthService>();
        services.AddSingleton<IDropboxVerifyService, DropboxVerifyService>();
        services.AddSingleton<IAzureAuthVerifyService, AzureAuthVerifyService>();
        services.AddSingleton<IGraphDiscoveryService, GraphDiscoveryService>();
        services.AddSingleton<ISharePointVerifyService, SharePointVerifyService>();

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
    private static string ResolveDefaultDbPath()
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
                "はい: Dropbox\nいいえ: SharePoint",
                "DB の選択",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            return result == MessageBoxResult.Yes ? dropboxDb : sharePointDb;
        }

        if (hasDropbox) return dropboxDb;
        // SharePoint DB（既存または新規作成）をデフォルトとして返す
        return sharePointDb;
    }
}
