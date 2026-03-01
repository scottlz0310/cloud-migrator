using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Storage;
using CloudMigrator.Observability;
using CloudMigrator.Providers.Graph;
using CloudMigrator.Providers.Graph.Auth;
using CloudMigrator.Providers.Graph.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Cli;

/// <summary>
/// CLI コマンドハンドラーが共通で使用するサービス群の組み立てヘルパー。
/// </summary>
internal sealed class CliServices : IDisposable
{
    public MigratorOptions Options { get; }
    public ILoggerFactory LoggerFactory { get; }
    public GraphStorageProvider StorageProvider { get; }
    public CrawlCache CrawlCache { get; }
    public SkipListManager SkipListManager { get; }

    private CliServices(
        MigratorOptions options,
        ILoggerFactory loggerFactory,
        GraphStorageProvider storageProvider,
        CrawlCache crawlCache,
        SkipListManager skipListManager)
    {
        Options = options;
        LoggerFactory = loggerFactory;
        StorageProvider = storageProvider;
        CrawlCache = crawlCache;
        SkipListManager = skipListManager;
    }

    public static CliServices Build(string? configPath = null)
    {
        var config = AppConfiguration.Build(configPath);
        var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
            ?? new MigratorOptions();

        var loggerFactory = LoggingSetup.CreateLoggerFactory(options.Paths.TransferLog);

        var clientSecret = AppConfiguration.GetGraphClientSecret();
        var auth = new GraphAuthenticator(
            options.Graph.ClientId,
            options.Graph.TenantId,
            clientSecret);

        var graphClient = GraphClientFactory.Create(
            auth,
            timeoutSec: options.TimeoutSec,
            maxRetry: options.RetryCount);

        var storageOptions = new GraphStorageOptions
        {
            OneDriveUserId = options.Graph.OneDriveUserId,
            SharePointDriveId = options.Graph.SharePointDriveId,
        };

        var sessionStorePath = Path.Combine(
            Path.GetDirectoryName(options.Paths.SkipList) ?? "logs",
            "upload_sessions.json");
        var sessionStore = new UploadSessionStore(sessionStorePath);

        var spLogger = loggerFactory.CreateLogger<GraphStorageProvider>();
        var storageProvider = new GraphStorageProvider(
            graphClient,
            spLogger,
            storageOptions,
            largeFileThresholdMb: options.LargeFileThresholdMb,
            chunkSizeMb: options.ChunkSizeMb,
            sessionStore: sessionStore);

        var crawlCache = new CrawlCache(loggerFactory.CreateLogger<CrawlCache>());
        var skipListManager = new SkipListManager(
            options.Paths.SkipList,
            loggerFactory.CreateLogger<SkipListManager>());

        return new CliServices(options, loggerFactory, storageProvider, crawlCache, skipListManager);
    }

    public void Dispose() => LoggerFactory.Dispose();
}
