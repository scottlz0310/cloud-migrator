using System.IO;
using System.Threading;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.State;
using CloudMigrator.Core.Transfer;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Dashboard.Runners;

/// <summary>
/// <see cref="AdaptiveConcurrencyController"/> / rate controller の構築ロジックを集約するヘルパー。
/// <see cref="SharePointPipelineRunner"/> / <see cref="DropboxPipelineRunner"/> が共用する。
/// </summary>
internal static class RateControllerBuilder
{
    /// <summary>
    /// ACC（Adaptive Concurrency Controller）を構築する。
    /// </summary>
    /// <param name="opts">実行時設定。</param>
    /// <param name="providerName">プロバイダー識別子（"sharepoint" / "dropbox" 等）。</param>
    /// <param name="loggerFactory">ロガーファクトリ。</param>
    /// <param name="withFolderController">フォルダ作成フェーズ用コントローラーが必要か（SharePoint のみ true）。</param>
    internal static AccBuildResult BuildAcc(
        MigratorOptions opts,
        string providerName,
        ILoggerFactory loggerFactory,
        bool withFolderController)
    {
        var adaptiveOpts = opts.GetAdaptiveConcurrency(providerName);
        if (!adaptiveOpts.Enabled || opts.RateControl.UseRateControl)
        {
            return new AccBuildResult(null, null, null, null, null, null);
        }

        var initialDegree = adaptiveOpts.InitialDegree > 0
            ? Math.Min(adaptiveOpts.InitialDegree, opts.MaxParallelTransfers)
            : opts.MaxParallelTransfers;
        var accMain = new AdaptiveConcurrencyController(
            initialDegree: initialDegree,
            minDegree: adaptiveOpts.MinDegree,
            maxDegree: opts.MaxParallelTransfers,
            increaseIntervalSec: adaptiveOpts.IncreaseIntervalSec,
            logger: loggerFactory.CreateLogger<AdaptiveConcurrencyController>(),
            increaseStep: adaptiveOpts.IncreaseStep,
            decreaseTriggerCount: adaptiveOpts.DecreaseTriggerCount,
            decreaseMultiplier: adaptiveOpts.DecreaseMultiplier);
        ITransferRateController concurrencyController = new AdaptiveConcurrencyControllerAdapter(accMain);

        AdaptiveConcurrencyController? accFolder = null;
        ITransferRateController? folderCreationController = null;
        if (withFolderController)
        {
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
                logger: loggerFactory.CreateLogger<AdaptiveConcurrencyController>(),
                increaseStep: adaptiveOpts.IncreaseStep,
                decreaseTriggerCount: adaptiveOpts.DecreaseTriggerCount,
                decreaseMultiplier: adaptiveOpts.DecreaseMultiplier);
            folderCreationController = new AdaptiveConcurrencyControllerAdapter(accFolder);
        }

        // onRateLimit はプロキシ経由にしてフェーズに応じて通知先を切り替える
        AdaptiveConcurrencyController? activeCtrl = accMain;
        Action<TimeSpan?> onRateLimit = retryAfter => Volatile.Read(ref activeCtrl)?.NotifyRateLimit(retryAfter);
        // 参照一致で folderCreationController（Phase C 用）か concurrencyController（Phase D 用）かを判別する
        Action<ITransferRateController?> activateController = ctrl =>
            Volatile.Write(ref activeCtrl, ReferenceEquals(ctrl, folderCreationController) ? accFolder : accMain);

        return new AccBuildResult(accMain, accFolder, concurrencyController, folderCreationController, onRateLimit, activateController);
    }

    /// <summary>
    /// rate controller（<see cref="RateControlledTransferController"/> または <see cref="HybridRateController"/>）を構築する。
    /// <c>UseRateControl=false</c> の場合は <see cref="RateControllerBuildResult.IsEmpty"/> が true。
    /// </summary>
    internal static RateControllerBuildResult BuildRateController(
        MigratorOptions opts,
        ITransferStateDb stateDb,
        Action<TimeSpan?>? accOnRateLimit,
        ILoggerFactory loggerFactory)
    {
        if (!opts.RateControl.UseRateControl)
        {
            return RateControllerBuildResult.Empty;
        }

        var rateMetricsBuffer = new MetricsBuffer(
            stateDb,
            opts.RateControl.MetricsFlushIntervalSec,
            loggerFactory.CreateLogger<MetricsBuffer>());

        Action<TimeSpan?> combinedOnRateLimit;
        ITransferRateController finalConcurrencyController;
        HybridRateController? hybridController = null;
        RateControlledTransferController? rateController = null;

        if (opts.RateControl.UseHybridController)
        {
            hybridController = BuildHybridController(opts, rateMetricsBuffer, loggerFactory);
            // RetryHandler が 429/503 をリトライして成功した場合、パイプライン側からは NotifyRateLimit が呼ばれない。
            // RateLimitAwareHandler 経由で AIMD の rate_429 入力を取り込めるよう onRateLimit チェーンへ接続する。
            combinedOnRateLimit = retryAfter =>
            {
                accOnRateLimit?.Invoke(retryAfter);
                hybridController.NotifyRateLimit(retryAfter);
            };
            finalConcurrencyController = hybridController;
        }
        else
        {
            var aggregator = new TransferMetricsAggregator();
            rateController = new RateControlledTransferController(
                aggregator,
                opts.RateControl,
                rateMetricsBuffer,
                loggerFactory.CreateLogger<RateControlledTransferController>());
            // 429 発生時に RateControlledTransferController へ通知する
            combinedOnRateLimit = retryAfter => rateController.NotifyRateLimit(retryAfter);
            finalConcurrencyController = rateController;
            loggerFactory.CreateLogger("MigrationWork").LogInformation(
                "RateControlledTransferController を構築しました（初期レート: {Rate:F1} req/sec）",
                opts.RateControl.InitialRatePerSec);
        }

        return new RateControllerBuildResult
        {
            RateController = rateController,
            HybridController = hybridController,
            MetricsBuffer = rateMetricsBuffer,
            FinalConcurrencyController = finalConcurrencyController,
            FinalOnRateLimit = combinedOnRateLimit,
        };
    }

    /// <summary>
    /// <see cref="HybridRateController"/> を構築する。
    /// <c>rate_state.json</c> から前回レートを復元し、<c>[minRate, maxRate]</c> にクランプして WeightedTokenBucket の初期レートに反映する。
    /// </summary>
    private static HybridRateController BuildHybridController(
        MigratorOptions opts, MetricsBuffer metricsBuffer, ILoggerFactory loggerFactory)
    {
        var rc = opts.RateControl;
        var logger = loggerFactory.CreateLogger("MigrationWork");

        var logsDir = Path.GetDirectoryName(opts.Paths.SkipList) ?? AppDataPaths.LogsDirectory;
        var rateStatePath = Path.Combine(logsDir, "rate_state.json");
        var stateStore = new RateStateStore(rateStatePath);

        var loaded = stateStore.Load();
        double initialRate = loaded is not null
            ? Math.Clamp(loaded.RateTokensPerSec, rc.MinTokensPerSec, rc.MaxTokensPerSec)
            : rc.InitialTokensPerSec;
        // ウォームスタート: max_inflight も [MinInflight, MaxInflight] にクランプして HybridRateController へ渡す。
        int? initialMaxInflight = loaded?.MaxInflight is int saved
            ? Math.Clamp(saved, rc.MinInflight, rc.MaxInflight)
            : null;
        if (loaded is not null)
        {
            logger.LogInformation(
                "rate_state.json から前回状態を復元しました（形式: {Format}, rate: {Rate:F2} tokens/sec, max_inflight: {MaxInflight}）",
                loaded.Format, initialRate, initialMaxInflight?.ToString() ?? "(未保存)");
        }

        var bucket = new WeightedTokenBucket(initialRate: initialRate, maxBurst: rc.MaxBurstTokens);
        var aimdSettings = AimdFeedbackSettings.FromRateControlSettings(rc);
        aimdSettings.InitialRate = initialRate;
        var aimd = new AimdFeedbackController(aimdSettings);

        var metrics = new SlidingWindowMetrics(
            mode: rc.WindowMode,
            windowSec: rc.WindowSec,
            maxCount: rc.MaxWindowCount,
            minSamples: rc.MinSamples);

        var controller = new HybridRateController(
            bucket,
            aimd,
            metrics,
            rc,
            metricsBuffer,
            stateStore,
            loggerFactory.CreateLogger<HybridRateController>(),
            initialMaxInflight);

        logger.LogInformation(
            "HybridRateController を構築しました（初期レート: {Rate:F2} tokens/sec, max_inflight: {MaxInflight}, 制御周期: {Interval}s）",
            initialRate, controller.CurrentMaxInflight, rc.ControlIntervalSec);
        return controller;
    }
}

/// <summary>ACC 構築結果。Dispose 管理のために各コントローラーを保持する。</summary>
internal readonly record struct AccBuildResult(
    AdaptiveConcurrencyController? AccMain,
    AdaptiveConcurrencyController? AccFolder,
    ITransferRateController? ConcurrencyController,
    ITransferRateController? FolderCreationController,
    Action<TimeSpan?>? OnRateLimit,
    Action<ITransferRateController?>? ActivateController);

/// <summary>rate controller 構築結果。Dispose 管理のために各コントローラーを保持する。</summary>
internal sealed class RateControllerBuildResult
{
    /// <summary>UseRateControl=false の場合に返す空の結果。</summary>
    public static readonly RateControllerBuildResult Empty = new();

    public bool IsEmpty => RateController is null && HybridController is null;

    public RateControlledTransferController? RateController { get; init; }
    public HybridRateController? HybridController { get; init; }
    public MetricsBuffer? MetricsBuffer { get; init; }
    public ITransferRateController? FinalConcurrencyController { get; init; }
    public Action<TimeSpan?>? FinalOnRateLimit { get; init; }

    public async ValueTask DisposeAsync()
    {
        if (HybridController is not null)
            await HybridController.DisposeAsync().ConfigureAwait(false);
        if (RateController is not null)
            await RateController.DisposeAsync().ConfigureAwait(false);
        if (MetricsBuffer is not null)
            await MetricsBuffer.DisposeAsync().ConfigureAwait(false);
    }
}
