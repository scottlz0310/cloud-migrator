using System.IO;
using System.Net.Http;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Credentials;
using CloudMigrator.Core.Migration;
using CloudMigrator.Core.State;
using CloudMigrator.Providers.Dropbox;
using CloudMigrator.Providers.Dropbox.Auth;
using CloudMigrator.Providers.Graph;
using CloudMigrator.Providers.Graph.Auth;
using CloudMigrator.Providers.Graph.Http;
using CloudMigrator.Routes;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Dashboard.Runners;

/// <summary>
/// Dropbox 移行パイプラインのランナー。
/// <see cref="DropboxMigrationPipeline"/> の構築と実行を担う。
/// </summary>
public sealed class DropboxPipelineRunner : IMigrationPipelineRunner
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICredentialStore _credentialStore;
    private readonly IDropboxOAuthService _dropboxOAuthService;

    public DropboxPipelineRunner(
        ILoggerFactory loggerFactory,
        ICredentialStore credentialStore,
        IDropboxOAuthService dropboxOAuthService)
    {
        _loggerFactory = loggerFactory;
        _credentialStore = credentialStore;
        _dropboxOAuthService = dropboxOAuthService;
    }

    /// <inheritdoc/>
    public string ProviderName => RouteProviderNames.Dropbox;

    /// <inheritdoc/>
    public async Task RunAsync(MigratorOptions opts, ITransferStateDb stateDb, CancellationToken ct)
    {
        var clientSecret = await _credentialStore.GetAsync(CredentialKeys.AzureClientSecret).ConfigureAwait(false)
            ?? AppConfiguration.GetGraphClientSecret();
        var auth = new GraphAuthenticator(opts.Graph.ClientId, opts.Graph.TenantId, clientSecret);

        // Dropbox は HasFolderCreationPhase=false のため folderController は不要
        var acc = RateControllerBuilder.BuildAcc(opts, ProviderName, _loggerFactory, withFolderController: false);
        var rate = RateControllerBuilder.BuildRateController(
            opts, stateDb,
            acc.OnRateLimit,
            acc.ConcurrencyController,
            _loggerFactory);

        var effectiveOnRateLimit = rate.IsEmpty ? acc.OnRateLimit : rate.FinalOnRateLimit;
        var effectiveConcurrencyController = rate.IsEmpty ? acc.ConcurrencyController : rate.FinalConcurrencyController;

        try
        {
            var graphClient = GraphClientFactory.Create(
                auth,
                timeoutSec: opts.TimeoutSec,
                maxRetry: opts.RetryCount,
                onRateLimit: effectiveOnRateLimit ?? (_ => { }),
                rateLimitLogger: _loggerFactory.CreateLogger<GraphStorageProvider>());

            var storageOptions = new GraphStorageOptions
            {
                OneDriveUserId = opts.Graph.OneDriveUserId,
                SharePointDriveId = opts.Graph.SharePointDriveId,
                OneDriveSourceFolder = opts.Graph.OneDriveSourceFolder,
            };

            var sessionStore = new UploadSessionStore(
                Path.Combine(AppDataPaths.LogsDirectory, "upload_sessions.json"));

            var graphStorageProvider = new GraphStorageProvider(
                graphClient,
                _loggerFactory.CreateLogger<GraphStorageProvider>(),
                storageOptions,
                largeFileThresholdMb: opts.LargeFileThresholdMb,
                chunkSizeMb: opts.ChunkSizeMb,
                sessionStore: sessionStore);

            var dropboxOptions = new DropboxStorageOptions
            {
                RootPath = opts.Dropbox.RootPath,
                SimpleUploadLimitMb = opts.Dropbox.SimpleUploadLimitMb,
                UploadChunkSizeMb = opts.Dropbox.UploadChunkSizeMb,
            };
            var normalizedTimeoutSec = Math.Max(1, opts.TimeoutSec);
            var dropboxHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(normalizedTimeoutSec) };

            // コンストラクタで例外が発生した場合も dropboxHttpClient を確実に Dispose する
            DropboxStorageProvider? dropboxProvider = null;
            try
            {
                dropboxProvider = new DropboxStorageProvider(
                    _loggerFactory.CreateLogger<DropboxStorageProvider>(),
                    _credentialStore,
                    _dropboxOAuthService,
                    dropboxOptions,
                    httpClient: dropboxHttpClient,
                    disposeHttpClient: true,
                    maxRetry: opts.RetryCount,
                    onRateLimit: effectiveOnRateLimit);

                var pipeline = new DropboxMigrationPipeline(
                    graphStorageProvider,
                    dropboxProvider,
                    stateDb,
                    opts,
                    _loggerFactory.CreateLogger<DropboxMigrationPipeline>(),
                    effectiveConcurrencyController);
                await pipeline.RunAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (dropboxProvider is not null)
                    dropboxProvider.Dispose();
                else
                    dropboxHttpClient.Dispose();
            }
        }
        finally
        {
            // AdaptiveConcurrencyControllerAdapter は内部 ACC を所有しないため ACC を直接 Dispose する
            acc.AccMain?.Dispose();
            // MetricsBuffer は stateDb への Flush を行うため、stateDb より先に Dispose して最終 Flush を完了させる
            await rate.DisposeAsync().ConfigureAwait(false);
        }
    }
}
