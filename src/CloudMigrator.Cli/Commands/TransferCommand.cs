// TransferCommand は AdaptiveConcurrencyController の後方互換ラッパーを使用するため
// Obsolete 警告を抑制する。v0.6.0 で AdaptiveConcurrencyController 削除時に対応する。
#pragma warning disable CS0618
using System.CommandLine;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Migration;
using CloudMigrator.Core.State;
using CloudMigrator.Core.Storage;
using CloudMigrator.Core.Transfer;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// transfer サブコマンド。
/// - 通常実行（FR-13）: skip_list がなければ SP を再クロールして自動再構築後に転送。
///   SP 再構築済み skip_list により既転送ファイルはスキップされる。
/// - --full-rebuild（FR-12）: キャッシュと skip_list をクリアして SP から再構築後に転送
/// </summary>
internal static class TransferCommand
{
    public static Command Build()
    {
        var cmd = new Command("transfer", "OneDrive から SharePoint へファイルを転送します");
        var fullRebuildOpt = new Option<bool>("--full-rebuild")
        {
            Description = "キャッシュと skip_list をクリアしてフル再実行します (FR-12)",
        };
        var autoRetryOpt = new Option<int>("--auto-retry")
        {
            Description = "失敗ファイルを自動的に再試行する最大回数。0=無効（デフォルト）。" +
                          "非対話環境（cron 等）ではこのオプションで再試行回数を制御してください。",
            DefaultValueFactory = _ => 0,
        };
        cmd.Add(fullRebuildOpt);
        cmd.Add(autoRetryOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            bool fullRebuild = parseResult.GetValue(fullRebuildOpt);
            int autoRetry = parseResult.GetValue(autoRetryOpt);
            if (autoRetry < 0)
            {
                Console.Error.WriteLine("エラー: --auto-retry には 0 以上の整数を指定してください。");
                Environment.ExitCode = 1;
                return;
            }
            await RunAsync(fullRebuild, autoRetry, ct).ConfigureAwait(false);
        });

        return cmd;
    }

    private static async Task RunAsync(bool fullRebuild, int autoRetry, CancellationToken ct)
    {
        using var svc = CliServices.Build();
        var logger = svc.LoggerFactory.CreateLogger("transfer");
        var opts = svc.Options;
        try
        {
            await RunCoreAsync(fullRebuild, autoRetry, ct, svc, logger, opts).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "転送で予期しないエラーが発生しました");
            throw;
        }
    }

    private static async Task RunCoreAsync(
        bool fullRebuild, int autoRetry, CancellationToken ct,
        CliServices svc, Microsoft.Extensions.Logging.ILogger logger,
        CloudMigrator.Core.Configuration.MigratorOptions opts)
    {
        // 1. 設定ハッシュ確認（FR-10）
        var hash = ConfigHashChecker.ComputeHash(opts);
        bool hashChanged = await ConfigHashChecker.HasChangedAsync(opts.Paths.ConfigHash, hash, ct)
            .ConfigureAwait(false);

        if (hashChanged)
        {
            logger.LogWarning("設定変更を検知しました。キャッシュと skip_list をクリアします。");
            ConfigHashChecker.ClearAll(opts.Paths, logger);
            await ConfigHashChecker.SaveHashAsync(opts.Paths.ConfigHash, hash, ct).ConfigureAwait(false);
        }

        // 2. --full-rebuild の場合は skip_list もクリア
        if (fullRebuild)
        {
            logger.LogInformation("--full-rebuild: キャッシュと skip_list をクリアします。");
            ConfigHashChecker.ClearAll(opts.Paths, logger);
        }

        // 3. 転送先プロバイダーで分岐
        var isDropbox = opts.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase);
        if (isDropbox)
        {
            await RunDropboxPipelineAsync(fullRebuild || hashChanged, autoRetry, svc, logger, opts, ct)
                .ConfigureAwait(false);
            return;
        }

        // 4. SharePoint: SQLite パイプライン実行
        await RunSharePointPipelineAsync(fullRebuild || hashChanged, autoRetry, svc, logger, opts, ct)
            .ConfigureAwait(false);
    }

    /// <summary>SharePoint 移行パイプライン（SQLite 状態管理 + 4フェーズ構造 [Phase A〜D]）を実行する。</summary>
    private static async Task RunSharePointPipelineAsync(
        bool resetState,
        int autoRetry,
        CliServices svc,
        ILogger logger,
        MigratorOptions opts,
        CancellationToken ct)
    {
        logger.LogInformation("SharePoint 移行パイプラインを開始します…");

        svc.ActivateController("sharepoint");

        // Phase C（フォルダ先行作成）専用コントローラー（maxDegree = MaxParallelFolderCreations）
        // 転送用コントローラーとは独立させ、Phase C の 429 が Phase D の並列度に影響しないようにする
        var adaptiveOpts = opts.GetAdaptiveConcurrency("sharepoint");
        ITransferRateController? folderCreationController = null;
        Action<ITransferRateController?>? activateController = null;
        AdaptiveConcurrencyController? accFolderController = null; // Dispose 用に保持
        if (adaptiveOpts.Enabled && !opts.RateControl.UseRateControl)
        {
            var maxFolderCreationDegree = Math.Max(1, opts.MaxParallelFolderCreations);
            var folderInitialDegree = adaptiveOpts.InitialDegree > 0
                ? Math.Min(adaptiveOpts.InitialDegree, maxFolderCreationDegree)
                : maxFolderCreationDegree;
            accFolderController = new AdaptiveConcurrencyController(
                initialDegree: folderInitialDegree,
                minDegree: Math.Min(adaptiveOpts.MinDegree, maxFolderCreationDegree),
                maxDegree: maxFolderCreationDegree,
                increaseIntervalSec: adaptiveOpts.IncreaseIntervalSec,
                logger: svc.LoggerFactory.CreateLogger<AdaptiveConcurrencyController>(),
                increaseStep: adaptiveOpts.IncreaseStep,
                decreaseTriggerCount: adaptiveOpts.DecreaseTriggerCount,
                decreaseMultiplier: adaptiveOpts.DecreaseMultiplier);
            folderCreationController = new AdaptiveConcurrencyControllerAdapter(accFolderController);
            // フェーズ切り替え時に onRateLimit の通知先を切り替えるコールバック
            // 参照一致で folderCreationController（Phase C 用）か concurrencyController（Phase D 用）かを判別する
            activateController = ctrl =>
            {
                var activeCtrl = ReferenceEquals(ctrl, folderCreationController)
                    ? accFolderController
                    : svc.GetAdaptiveController("sharepoint");
                svc.SetActiveController(activeCtrl);
            };
        }

        await using var stateDb = new SqliteTransferStateDb(opts.Paths.SharePointStateDb);

        // フルリビルド or 設定変更時は SQL で全テーブルをクリア（ファイル削除不要のためダッシュボード開放中でも動作可）
        if (resetState)
        {
            await stateDb.InitializeAsync(ct).ConfigureAwait(false);
            await stateDb.ResetAllAsync(ct).ConfigureAwait(false);
            logger.LogInformation("SharePoint 状態 DB をリセットしました: {Path}", opts.Paths.SharePointStateDb);
        }
        else if (!File.Exists(opts.Paths.SharePointStateDb) && File.Exists(opts.Paths.SkipList))
        {
            // 初回起動時: 既存 skip_list を SQLite の done レコードとして移行する
            logger.LogInformation("既存 skip_list を SQLite に移行します…");
            await stateDb.InitializeAsync(ct).ConfigureAwait(false);
            var skipList = await svc.SkipListManager.LoadAsync(ct).ConfigureAwait(false);
            var migrated = 0;
            foreach (var skipKey in skipList)
            {
                var lastSlash = skipKey.LastIndexOf('/');
                var path = lastSlash >= 0 ? skipKey[..lastSlash] : string.Empty;
                var name = lastSlash >= 0 ? skipKey[(lastSlash + 1)..] : skipKey;
                if (!string.IsNullOrEmpty(name))
                {
                    await stateDb.InsertDoneIfNotExistsAsync(path, name, ct).ConfigureAwait(false);
                    migrated++;
                }
            }
            logger.LogInformation("skip_list から {Count} 件を SQLite に移行しました", migrated);
        }

        // UseRateControl=true 時は stateDb 取得後に RateControlledTransferController を構築する
        // （MetricsBuffer が ITransferStateDb を必要とするため stateDb 確定後に組み立てる）
        RateControlledTransferController? rateController = null;
        MetricsBuffer? rateMetricsBuffer = null;
        if (opts.RateControl.UseRateControl)
        {
            var aggregator = new TransferMetricsAggregator();
            rateMetricsBuffer = new MetricsBuffer(
                stateDb,
                opts.RateControl.MetricsFlushIntervalSec,
                svc.LoggerFactory.CreateLogger<MetricsBuffer>());
            rateController = new RateControlledTransferController(
                aggregator,
                opts.RateControl,
                rateMetricsBuffer,
                svc.LoggerFactory.CreateLogger<RateControlledTransferController>());
            logger.LogInformation(
                "RateControlledTransferController を構築しました（初期レート: {Rate:F1} req/sec）",
                opts.RateControl.InitialRatePerSec);
        }

        // 使用するコントローラー: UseRateControl=true → rateController / false → AdaptiveConcurrencyController Adapter
        ITransferRateController? transferController = rateController ?? svc.GetController("sharepoint");

        // パイプラインは再試行ごとに新規生成してメトリクスカウンタの累積を防ぐ
        // stateDb は SQLite 状態を保持するため使い回す
        TransferSummary summary;
        var autoRetryRemaining = autoRetry;
        try
        {
            while (true)
            {
                var pipeline = new SharePointMigrationPipeline(
                    svc.StorageProvider,              // OneDrive ソース（GraphStorageProvider）
                    svc.StorageProvider,              // SharePoint 転送先（同一 GraphStorageProvider）
                    stateDb,
                    opts,
                    svc.LoggerFactory.CreateLogger<SharePointMigrationPipeline>(),
                    transferController,
                    folderCreationController,
                    activateController);

                summary = await pipeline.RunAsync(ct).ConfigureAwait(false);
                logger.LogInformation(
                    "SharePoint 移行完了: 成功 {Success} / 失敗 {Failed} / 所要時間 {Elapsed:c}",
                    summary.Success, summary.Failed, summary.Elapsed);

                if (summary.Failed == 0 || !ShouldRetry(summary.Failed, ref autoRetryRemaining, logger))
                    break;

                logger.LogInformation("失敗ファイルを再試行します…");
            }
        }
        finally
        {
            // AdaptiveConcurrencyController を Dispose（Adapter は内部コントローラーを所有しないため直接 Dispose）
            accFolderController?.Dispose();
            // RateControlledTransferController と MetricsBuffer を Dispose
            if (rateController is not null)
                await rateController.DisposeAsync().ConfigureAwait(false);
            if (rateMetricsBuffer is not null)
                await rateMetricsBuffer.DisposeAsync().ConfigureAwait(false);
        }

        if (summary.Failed > 0)
            Environment.ExitCode = 1;
    }

    /// <summary>Dropbox 移行パイプラインを実行する。</summary>
    private static async Task RunDropboxPipelineAsync(
        bool resetState,
        int autoRetry,
        CliServices svc,
        ILogger logger,
        MigratorOptions opts,
        CancellationToken ct)
    {
        logger.LogInformation("Dropbox 移行パイプラインを開始します…");

        svc.ActivateController("dropbox");
        await using var stateDb = new SqliteTransferStateDb(opts.Paths.DropboxStateDb);

        // フルリビルド or 設定変更時は SQL で全テーブルをクリア（ファイル削除不要のためダッシュボード開放中でも動作可）
        if (resetState)
        {
            await stateDb.InitializeAsync(ct).ConfigureAwait(false);
            await stateDb.ResetAllAsync(ct).ConfigureAwait(false);
            logger.LogInformation("Dropbox 状態 DB をリセットしました: {Path}", opts.Paths.DropboxStateDb);
        }

        // パイプラインは再試行ごとに新規生成してメトリクスカウンタの累積を防ぐ
        // stateDb は SQLite 状態を保持するため使い回す
        TransferSummary summary;
        var autoRetryRemaining = autoRetry;
        while (true)
        {
            var pipeline = new DropboxMigrationPipeline(
                svc.StorageProvider,          // OneDrive ソース（GraphStorageProvider）
                svc.DropboxProvider,          // Dropbox 転送先
                stateDb,
                opts,
                svc.LoggerFactory.CreateLogger<DropboxMigrationPipeline>(),
                svc.GetController("dropbox"));

            summary = await pipeline.RunAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "Dropbox 移行完了: 成功 {Success} / 失敗 {Failed} / 所要時間 {Elapsed:c}",
                summary.Success, summary.Failed, summary.Elapsed);

            if (summary.Failed == 0 || !ShouldRetry(summary.Failed, ref autoRetryRemaining, logger))
                break;

            logger.LogInformation("失敗ファイルを再試行します…");
        }

        if (summary.Failed > 0)
            Environment.ExitCode = 1;
    }

    /// <summary>
    /// 転送失敗があるとき、再試行するかどうかを判断する。
    /// <list type="bullet">
    ///   <item><paramref name="autoRetryRemaining"/> が残っている場合は自動で再試行する（デクリメントして true を返す）。</item>
    ///   <item>対話端末では「再試行しますか？」を表示し、ユーザーの入力で判断する。</item>
    ///   <item>標準入力がリダイレクトされている（cron 等）かつ自動再試行残数もない場合は false を返す。</item>
    /// </list>
    /// </summary>
    private static bool ShouldRetry(int failedCount, ref int autoRetryRemaining, ILogger logger)
    {
        if (autoRetryRemaining > 0)
        {
            autoRetryRemaining--;
            logger.LogInformation(
                "{Failed} 件の転送が失敗しました。自動再試行します（残り {Remaining} 回）。",
                failedCount, autoRetryRemaining);
            return true;
        }

        if (Console.IsInputRedirected)
        {
            logger.LogWarning(
                "{Failed} 件の転送が失敗しました。次回実行時に再試行されます。", failedCount);
            return false;
        }

        Console.Write($"\n{failedCount} 件の転送に失敗しています。再試行しますか？ [y/N]: ");
        var input = Console.ReadLine()?.Trim() ?? string.Empty;
        return input.Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dropbox をクロールして skip_list を再構築する（転送先 Dropbox 用、FR-13 Dropbox 版）。
    /// DestinationRoot プレフィックスを除去してソース側と同じキー体系に正規化する。
    /// </summary>
    internal static async Task RebuildSkipListFromDropboxAsync(
        CliServices svc, ILogger logger, CancellationToken ct)
    {
        var crawlRoot = string.IsNullOrWhiteSpace(svc.Options.DestinationRoot)
            ? "dropbox"
            : $"dropbox/{svc.Options.DestinationRoot.Trim('/')}";

        var dropboxItems = await svc.DropboxProvider.ListItemsAsync(crawlRoot, ct).ConfigureAwait(false);
        await svc.CrawlCache.SaveAsync(svc.Options.Paths.DropboxCache, dropboxItems, ct).ConfigureAwait(false);

        var destinationRoot = svc.Options.DestinationRoot;
        var hasDestinationRoot = !string.IsNullOrWhiteSpace(destinationRoot);
        if (hasDestinationRoot)
            destinationRoot = destinationRoot.Trim().Replace('\\', '/').Trim('/');

        var skipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in dropboxItems.Where(i => !i.IsFolder))
        {
            var skipKey = item.SkipKey;

            if (hasDestinationRoot)
            {
                if (!skipKey.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (skipKey.Length == destinationRoot.Length)
                    continue;

                if (skipKey.Length > destinationRoot.Length &&
                    (skipKey[destinationRoot.Length] == '/' || skipKey[destinationRoot.Length] == '\\'))
                {
                    skipKey = skipKey.Substring(destinationRoot.Length + 1);
                }
                else
                {
                    continue;
                }
            }

            if (string.IsNullOrWhiteSpace(skipKey))
                continue;

            skipKeys.Add(skipKey);
        }

        if (skipKeys.Count > 0)
            await svc.SkipListManager.SaveAsync(skipKeys, ct).ConfigureAwait(false);

        logger.LogInformation("Dropbox skip_list を再構築しました: {Count} 件追加", skipKeys.Count);
    }
}
