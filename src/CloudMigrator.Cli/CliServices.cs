using System.Text.Json;
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
    private readonly string? _rateStatePath;

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
        TokenBucketRateLimiter? rateLimiter,
        string? rateStatePath)
    {
        Options = options;
        LoggerFactory = loggerFactory;
        StorageProvider = storageProvider;
        DropboxProvider = dropboxProvider;
        CrawlCache = crawlCache;
        SkipListManager = skipListManager;
        AdaptiveConcurrencyController = adaptiveConcurrencyController;
        RateLimiter = rateLimiter;
        _rateStatePath = rateStatePath;
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
        string? rateStatePath = null;
        if (options.RateLimiter.Enabled)
        {
            var logsDir = Path.GetDirectoryName(options.Paths.SkipList) ?? "logs";
            rateStatePath = Path.Combine(logsDir, "rate_state.json");

            // 前回保存時のレートを初期値として復元（コールドスタート排除）
            double initialRate = options.RateLimiter.InitialRequestsPerSec;
            if (File.Exists(rateStatePath))
            {
                try
                {
                    var jsonText = File.ReadAllText(rateStatePath);
                    using var doc = JsonDocument.Parse(jsonText);
                    if (doc.RootElement.TryGetProperty("rate", out var rateProp)
                        && rateProp.TryGetDouble(out var savedRate)
                        && savedRate >= options.RateLimiter.MinRequestsPerSec
                        && savedRate <= options.RateLimiter.MaxRequestsPerSec)
                    {
                        initialRate = savedRate;
                        loggerFactory.CreateLogger<CliServices>().LogInformation(
                            "前回保存時のレートを復元します: {Rate:F1} file/sec", initialRate);
                    }
                }
                catch (Exception ex)
                {
                    loggerFactory.CreateLogger<CliServices>().LogWarning(
                        ex, "rate_state.json の読み込みに失敗しました。初期値を使用します: {Rate:F1} file/sec", initialRate);
                }
            }

            rateLimiter = new TokenBucketRateLimiter(
                initialRate: initialRate,
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

        // 両方有効の場合は警告（TransferEngine では AdaptiveConcurrency が優先され RateLimiter は無視される）
        if (adaptiveController is not null && rateLimiter is not null)
        {
            loggerFactory.CreateLogger<CliServices>().LogWarning(
                "AdaptiveConcurrency と RateLimiter が同時に有効です。AdaptiveConcurrency が優先されます（RateLimiter は無視されます）。" +
                " 一方のみを有効にしてください。");
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
            rateLimiter,
            rateStatePath);
    }

    public void Dispose()
    {
        // レートリミッター終了前に現在レートを保存（次回起動のコールドスタート排除）
        if (RateLimiter is not null && _rateStatePath is not null)
        {
            // 原子的書き込み（一時ファイル→Move/Replace）でパーシャルライトを防止
            try
            {
                var logsDir = Path.GetDirectoryName(_rateStatePath);
                if (logsDir is not null)
                    Directory.CreateDirectory(logsDir);
                var json = JsonSerializer.Serialize(new
                {
                    rate = RateLimiter.CurrentRate,
                    savedAt = DateTime.UtcNow.ToString("o"),
                });
                var tmpPath = _rateStatePath + ".tmp";
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, _rateStatePath, overwrite: true);
            }
            catch (Exception ex)
            {
                // 保存失敗は終了処理を妨げないが原因追跡のためログを残す
                try { LoggerFactory.CreateLogger<CliServices>().LogWarning(ex, "rate_state.json の保存に失敗しました。"); }
                catch { /* LoggerFactory も破棄済みの場合は握りつぶす */ }
            }
        }
        RateLimiter?.Dispose();
        AdaptiveConcurrencyController?.Dispose();
        DropboxProvider.Dispose();
        LoggerFactory.Dispose();
    }
}
