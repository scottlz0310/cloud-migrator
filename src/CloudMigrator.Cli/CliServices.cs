using System.Text.Json;
using System.Net;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Credentials;
using CloudMigrator.Core.Storage;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Observability;
using CloudMigrator.Providers.Abstractions;
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

    // プロファイル名 → コントローラーの辞書
    private readonly Dictionary<string, AdaptiveConcurrencyController> _controllers;
    // onRateLimit ラムダが参照するプロキシ（アクティブなコントローラーへの間接参照）
    private readonly ControllerProxy _controllerProxy;

    /// <summary>
    /// 指定プロバイダーのコントローラーを取得する。
    /// <paramref name="providerName"/> に一致するプロファイルが存在しない場合は "default" プロファイルへフォールバックし、
    /// それも存在しない場合のみ null を返す。
    /// </summary>
    public AdaptiveConcurrencyController? GetController(string providerName) =>
        _controllers.TryGetValue(providerName, out var c) ? c :
        _controllers.TryGetValue("default", out var def) ? def : null;

    /// <summary>
    /// 指定プロバイダーのコントローラーをアクティブにする。
    /// onRateLimit ラムダがこのコントローラーに通知を転送するようになる。
    /// </summary>
    public void ActivateController(string providerName) =>
        _controllerProxy.Active = GetController(providerName);

    /// <summary>
    /// Token Bucket レートリミッター。<see cref="RateLimiterOptions.Enabled"/> が false の場合は null。
    /// </summary>
    public TokenBucketRateLimiter? RateLimiter { get; }

    /// <summary>
    /// 設定 <see cref="MigratorOptions.DestinationProvider"/> に従って転送先プロバイダーを返す。
    /// "dropbox" の場合は <see cref="DropboxProvider"/>、それ以外は <see cref="StorageProvider"/>（SharePoint）。
    /// </summary>
    public IStorageProvider DestinationProvider =>
        Options.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase)
            ? DropboxProvider
            : StorageProvider;

    /// <summary>
    /// クロスプロバイダー転送（OneDrive→Dropbox）時のソースプロバイダー。
    /// 転送先が Dropbox の場合は <see cref="StorageProvider"/>（GraphStorageProvider）を返す。
    /// 単一プロバイダーモード（OneDrive→SharePoint）の場合は null。
    /// </summary>
    public IStorageProvider? CrossProviderSource =>
        Options.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase)
            ? StorageProvider
            : null;

    private CliServices(
        MigratorOptions options,
        ILoggerFactory loggerFactory,
        GraphStorageProvider storageProvider,
        DropboxStorageProvider dropboxProvider,
        CrawlCache crawlCache,
        SkipListManager skipListManager,
        Dictionary<string, AdaptiveConcurrencyController> controllers,
        ControllerProxy controllerProxy,
        TokenBucketRateLimiter? rateLimiter,
        string? rateStatePath)
    {
        Options = options;
        LoggerFactory = loggerFactory;
        StorageProvider = storageProvider;
        DropboxProvider = dropboxProvider;
        CrawlCache = crawlCache;
        SkipListManager = skipListManager;
        _controllers = controllers;
        _controllerProxy = controllerProxy;
        RateLimiter = rateLimiter;
        _rateStatePath = rateStatePath;
    }

    public static CliServices Build(string? configPath = null)
    {
        var config = AppConfiguration.Build(configPath);
        var options = config.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
            ?? new MigratorOptions();

        var loggerFactory = LoggingSetup.CreateLoggerFactory(options.Paths.TransferLog);

        // 認証情報ストア: Windows では Credential Manager を優先し、環境変数をフォールバックとして使用する
        ICredentialStore credentialStore = CreateCredentialStore();

        var clientSecret = credentialStore.GetAsync(CredentialKeys.AzureClientSecret)
            .GetAwaiter().GetResult()
            ?? AppConfiguration.GetGraphClientSecret();
        var auth = new GraphAuthenticator(
            options.Graph.ClientId,
            options.Graph.TenantId,
            clientSecret);

        // プロファイル別に動的並列度制御コントローラーを生成
        var controllers = new Dictionary<string, AdaptiveConcurrencyController>(StringComparer.OrdinalIgnoreCase);
        var controllerProxy = new ControllerProxy();
        Action<TimeSpan?>? onRateLimit = null;

        foreach (var (profileName, profile) in options.AdaptiveConcurrency)
        {
            if (!profile.Enabled) continue;
            var useDropboxAdaptiveMode = options.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase);
            // InitialDegree > 0: 設定値を使用（スロースタート）
            // InitialDegree == 0 かつ Dropbox: min(2, max)（旧来のスロースタート）
            // InitialDegree == 0 かつ SharePoint: max（スロースタートなし）
            var initialDegree = profile.InitialDegree > 0
                ? Math.Min(profile.InitialDegree, options.MaxParallelTransfers)
                : useDropboxAdaptiveMode ? Math.Min(2, options.MaxParallelTransfers) : options.MaxParallelTransfers;
            controllers[profileName] = new AdaptiveConcurrencyController(
                initialDegree: initialDegree,
                minDegree: profile.MinDegree,
                maxDegree: options.MaxParallelTransfers,
                increaseIntervalSec: profile.IncreaseIntervalSec,
                logger: loggerFactory.CreateLogger<AdaptiveConcurrencyController>(),
                increaseStep: profile.IncreaseStep,
                decreaseStep: profile.DecreaseStep,
                decreaseTriggerCount: profile.DecreaseTriggerCount,
                halveOnRateLimit: useDropboxAdaptiveMode);
        }

        if (controllers.Count > 0)
            onRateLimit = retryAfter => controllerProxy.Active?.NotifyRateLimit(retryAfter);

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

        // 両方有効の場合は警告（AdaptiveConcurrency が優先され RateLimiter は無視される）
        if (controllers.Count > 0 && rateLimiter is not null)
        {
            loggerFactory.CreateLogger<CliServices>().LogWarning(
                "AdaptiveConcurrency と RateLimiter が同時に有効です。AdaptiveConcurrency が優先されます（RateLimiter は無視されます）。" +
                " 一方のみを有効にしてください。");
        }

        var copyCapture = new CopyLocationCaptureHandler();
        var graphClient = GraphClientFactory.Create(
            auth,
            timeoutSec: options.TimeoutSec,
            maxRetry: options.RetryCount,
            onRateLimit: onRateLimit,
            copyLocationCapture: copyCapture,
            rateLimitLogger: loggerFactory.CreateLogger<CliServices>());

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
            sessionStore: sessionStore,
            copyLocationCapture: copyCapture,
            serverSideCopy: options.ServerSideCopy);

        var dropboxOptions = new DropboxStorageOptions
        {
            RootPath = options.Dropbox.RootPath,
            SimpleUploadLimitMb = options.Dropbox.SimpleUploadLimitMb,
            UploadChunkSizeMb = options.Dropbox.UploadChunkSizeMb,
        };
        var dropboxHandler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 100,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var dropboxHttpClient = new HttpClient(dropboxHandler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSec)),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        var dropboxAccessToken = credentialStore.GetAsync(CredentialKeys.DropboxAccessToken)
            .GetAwaiter().GetResult()
            ?? AppConfiguration.GetDropboxAccessToken();
        var dropboxRefreshToken = credentialStore.GetAsync(CredentialKeys.DropboxRefreshToken)
            .GetAwaiter().GetResult()
            ?? AppConfiguration.GetDropboxRefreshToken();
        var dropboxAppKey = credentialStore.GetAsync(CredentialKeys.DropboxAppKey)
            .GetAwaiter().GetResult()
            ?? AppConfiguration.GetDropboxClientId();

        var dropboxProvider = new DropboxStorageProvider(
            loggerFactory.CreateLogger<DropboxStorageProvider>(),
            dropboxAccessToken,
            dropboxOptions,
            dropboxHttpClient,
            options.RetryCount,
            disposeHttpClient: true,
            refreshToken: dropboxRefreshToken,
            clientId: dropboxAppKey,
            clientSecret: AppConfiguration.GetDropboxClientSecret(),
            onRateLimit: onRateLimit);

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
            controllers,
            controllerProxy,
            rateLimiter,
            rateStatePath);
    }

    /// <summary>
    /// プラットフォームに応じた <see cref="ICredentialStore"/> を生成する。
    /// <see cref="CredentialStoreFactory.Create"/> に委譲する。
    /// </summary>
    internal static ICredentialStore CreateCredentialStore()
        => CredentialStoreFactory.Create();

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
        foreach (var c in _controllers.Values) c.Dispose();
        DropboxProvider.Dispose();
        LoggerFactory.Dispose();
    }

    /// <summary>onRateLimit ラムダからアクティブなコントローラーへの間接参照。</summary>
    private sealed class ControllerProxy
    {
        internal volatile AdaptiveConcurrencyController? Active;
    }
}
