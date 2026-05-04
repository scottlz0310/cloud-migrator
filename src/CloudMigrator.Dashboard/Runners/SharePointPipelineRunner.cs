using System.IO;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Credentials;
using CloudMigrator.Core.Migration;
using CloudMigrator.Core.State;
using CloudMigrator.Providers.Graph;
using CloudMigrator.Providers.Graph.Auth;
using CloudMigrator.Providers.Graph.Http;
using CloudMigrator.Routes;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Dashboard.Runners;

/// <summary>
/// SharePoint Online 移行パイプラインのランナー。
/// <see cref="SharePointMigrationPipeline"/> の構築と実行を担う。
/// </summary>
public sealed class SharePointPipelineRunner : IMigrationPipelineRunner
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ICredentialStore _credentialStore;

    public SharePointPipelineRunner(ILoggerFactory loggerFactory, ICredentialStore credentialStore)
    {
        _loggerFactory = loggerFactory;
        _credentialStore = credentialStore;
    }

    /// <inheritdoc/>
    public string ProviderName => RouteProviderNames.SharePoint;

    /// <inheritdoc/>
    public async Task RunAsync(MigratorOptions opts, ITransferStateDb stateDb, CancellationToken ct)
    {
        var clientSecret = await _credentialStore.GetAsync(CredentialKeys.AzureClientSecret).ConfigureAwait(false)
            ?? AppConfiguration.GetGraphClientSecret();
        var auth = new GraphAuthenticator(opts.Graph.ClientId, opts.Graph.TenantId, clientSecret);

        // SharePoint は Phase C（フォルダ先行作成）があるため folderController を構築する
        var acc = RateControllerBuilder.BuildAcc(opts, ProviderName, _loggerFactory, withFolderController: true);
        var rate = RateControllerBuilder.BuildRateController(
            opts, stateDb,
            acc.OnRateLimit,
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

            var storageProvider = new GraphStorageProvider(
                graphClient,
                _loggerFactory.CreateLogger<GraphStorageProvider>(),
                storageOptions,
                largeFileThresholdMb: opts.LargeFileThresholdMb,
                chunkSizeMb: opts.ChunkSizeMb,
                sessionStore: sessionStore);

            var pipeline = new SharePointMigrationPipeline(
                storageProvider,
                storageProvider,
                stateDb,
                opts,
                _loggerFactory.CreateLogger<SharePointMigrationPipeline>(),
                effectiveConcurrencyController,
                acc.FolderCreationController,
                acc.ActivateController);
            await pipeline.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            // AdaptiveConcurrencyControllerAdapter は内部 ACC を所有しないため ACC を直接 Dispose する
            acc.AccMain?.Dispose();
            acc.AccFolder?.Dispose();
            // MetricsBuffer は stateDb への Flush を行うため、stateDb より先に Dispose して最終 Flush を完了させる
            await rate.DisposeAsync().ConfigureAwait(false);
        }
    }
}
