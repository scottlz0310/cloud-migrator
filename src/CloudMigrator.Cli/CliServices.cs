using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Storage;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Observability;
using CloudMigrator.Providers.Dropbox;
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
    public DropboxStorageProvider DropboxProvider { get; }
    public CrawlCache CrawlCache { get; }
    public SkipListManager SkipListManager { get; }

    /// <summary>
    /// 動的並列度コントローラー。<see cref="MigratorOptions.AdaptiveConcurrency"/> が無効な場合は null。
    /// </summary>
    public AdaptiveConcurrencyController? AdaptiveConcurrencyController { get; }

    /// <summary>
    /// Token Bucket レートリミッター。<see cref="RateLimiterOptions.Enabled"/> が false の場合は null。
    /// </summary>
    public TokenBucketRateLimiter? RateLimiter { get; }

    private CliServices(
        MigratorOptions options,
        ILoggerFactory loggerFactory,
        GraphStorageProvider storageProvider,
        DropboxStorageProvider dropboxProvider,
        CrawlCache crawlCache,
        SkipListManager skipListManager,
        AdaptiveConcurrencyController? adaptiveConcurrencyController,
        TokenBucketRateLimiter? rateLimiter)
    {
        Options = options;
        LoggerFactory = loggerFactory;
        StorageProvider = storageProvider;
        DropboxProvider = dropboxProvider;
        CrawlCache = crawlCache;
        SkipListManager = skipListManager;
        AdaptiveConcurrencyController = adaptiveConcurrencyController;
        RateLimiter = rateLimiter;
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

        // 動的並列度制御コントローラーを生成（設定で有効な場合）
        AdaptiveConcurrencyController? adaptiveController = null;
        Action<TimeSpan?>? onRateLimit = null;
        if (options.AdaptiveConcurrency.Enabled)
        {
            adaptiveController = new AdaptiveConcurrencyController(
                initialDegree: options.MaxParallelTransfers,
                minDegree: options.AdaptiveConcurrency.MinDegree,
                maxDegree: options.MaxParallelTransfers,
                successThreshold: options.AdaptiveConcurrency.SuccessThresholdToIncrease,
                logger: loggerFactory.CreateLogger<AdaptiveConcurrencyController>());
            onRateLimit = adaptiveController.NotifyRateLimit;
        }

        // Token Bucket レートリミッターを生成（設定で有効な場合）
        TokenBucketRateLimiter? rateLimiter = null;
        if (options.RateLimiter.Enabled)
        {
            rateLimiter = new TokenBucketRateLimiter(
                initialRate: options.RateLimiter.InitialRequestsPerSec,
                minRate: options.RateLimiter.MinRequestsPerSec,
                maxRate: options.RateLimiter.MaxRequestsPerSec,
                burstCapacity: options.RateLimiter.BurstCapacity,
                increaseStep: options.RateLimiter.IncreaseStep,
                decreaseFactor: options.RateLimiter.DecreaseFactor,
                logger: loggerFactory.CreateLogger<TokenBucketRateLimiter>(),
                increaseIntervalSec: options.RateLimiter.IncreaseIntervalSec);
            // onRateLimit チェーン: 既存ハンドラーに加えて TokenBucketRateLimiter も通知
            var prev = onRateLimit;
            onRateLimit = retryAfter => { prev?.Invoke(retryAfter); rateLimiter.NotifyRateLimit(retryAfter); };
        }

        var graphClient = GraphClientFactory.Create(
            auth,
            timeoutSec: options.TimeoutSec,
            maxRetry: options.RetryCount,
            onRateLimit: onRateLimit);

        var storageOptions = new GraphStorageOptions
        {
            OneDriveUserId = options.Graph.OneDriveUserId,
            SharePointDriveId = options.Graph.SharePointDriveId,
            OneDriveSourceFolder = options.Graph.OneDriveSourceFolder,
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

        var dropboxOptions = new DropboxStorageOptions
        {
            RootPath = options.Dropbox.RootPath,
            SimpleUploadLimitMb = options.Dropbox.SimpleUploadLimitMb,
            UploadChunkSizeMb = options.Dropbox.UploadChunkSizeMb,
        };
        var dropboxHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSec)),
        };
        var dropboxProvider = new DropboxStorageProvider(
            loggerFactory.CreateLogger<DropboxStorageProvider>(),
            AppConfiguration.GetDropboxAccessToken(),
            dropboxOptions,
            dropboxHttpClient,
            options.RetryCount,
            disposeHttpClient: true);

        var crawlCache = new CrawlCache(loggerFactory.CreateLogger<CrawlCache>());
        var skipListManager = new SkipListManager(
            options.Paths.SkipList,
            loggerFactory.CreateLogger<SkipListManager>());

        return new CliServices(
            options,
            loggerFactory,
            storageProvider,
            dropboxProvider,
            crawlCache,
            skipListManager,
            adaptiveController,
            rateLimiter);
    }

    public void Dispose()
    {
        RateLimiter?.Dispose();
        AdaptiveConcurrencyController?.Dispose();
        DropboxProvider.Dispose();
        LoggerFactory.Dispose();
    }
}
