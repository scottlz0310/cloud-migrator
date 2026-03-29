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
        var spDbPath = opts.Paths.SharePointStateDb;

        // フルリビルド or 設定変更時は SQLite 状態 DB をリセット
        // DB 本体が存在しない場合でも WAL/SHM サイドカーだけ残存することがあるため個別に削除する
        if (resetState)
        {
            var deleted = false;
            if (File.Exists(spDbPath)) { File.Delete(spDbPath); deleted = true; }
            var walPath = spDbPath + "-wal";
            if (File.Exists(walPath)) { File.Delete(walPath); deleted = true; }
            var shmPath = spDbPath + "-shm";
            if (File.Exists(shmPath)) { File.Delete(shmPath); deleted = true; }
            if (deleted)
                logger.LogInformation("SharePoint 状態 DB をリセットしました: {Path}", spDbPath);
        }
        else if (!File.Exists(spDbPath) && File.Exists(opts.Paths.SkipList))
        {
            // 初回起動時: 既存 skip_list を SQLite の done レコードとして移行する
            logger.LogInformation("既存 skip_list を SQLite に移行します…");
            await using var migDb = new SqliteTransferStateDb(spDbPath);
            await migDb.InitializeAsync(ct).ConfigureAwait(false);
            var skipList = await svc.SkipListManager.LoadAsync(ct).ConfigureAwait(false);
            var migrated = 0;
            foreach (var skipKey in skipList)
            {
                var lastSlash = skipKey.LastIndexOf('/');
                var path = lastSlash >= 0 ? skipKey[..lastSlash] : string.Empty;
                var name = lastSlash >= 0 ? skipKey[(lastSlash + 1)..] : skipKey;
                if (!string.IsNullOrEmpty(name))
                {
                    await migDb.InsertDoneIfNotExistsAsync(path, name, ct).ConfigureAwait(false);
                    migrated++;
                }
            }
            logger.LogInformation("skip_list から {Count} 件を SQLite に移行しました", migrated);
        }

        logger.LogInformation("SharePoint 移行パイプラインを開始します…");

        svc.ActivateController("sharepoint");
        await using var stateDb = new SqliteTransferStateDb(opts.Paths.SharePointStateDb);
        var pipeline = new SharePointMigrationPipeline(
            svc.StorageProvider,              // OneDrive ソース（GraphStorageProvider）
            svc.StorageProvider,              // SharePoint 転送先（同一 GraphStorageProvider）
            stateDb,
            opts,
            svc.LoggerFactory.CreateLogger<SharePointMigrationPipeline>(),
            svc.GetController("sharepoint"));

        TransferSummary summary;
        var autoRetryRemaining = autoRetry;
        while (true)
        {
            summary = await pipeline.RunAsync(ct).ConfigureAwait(false);
            logger.LogInformation(
                "SharePoint 移行完了: 成功 {Success} / 失敗 {Failed} / 所要時間 {Elapsed:c}",
                summary.Success, summary.Failed, summary.Elapsed);

            if (summary.Failed == 0 || !ShouldRetry(summary.Failed, ref autoRetryRemaining, logger))
                break;

            logger.LogInformation("失敗ファイルを再試行します…");
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
        // フルリビルド or 設定変更時は SQLite 状態 DB をリセット
        if (resetState)
        {
            var dbPath = opts.Paths.DropboxStateDb;
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);

                // WAL モード利用時は .db-wal / .db-shm サイドカーファイルも残存するため併せて削除する。
                // 削除しないと次回起動時に古い WAL が復元され、意図しない状態になる可能性がある。
                var walPath = dbPath + "-wal";
                if (File.Exists(walPath)) File.Delete(walPath);

                var shmPath = dbPath + "-shm";
                if (File.Exists(shmPath)) File.Delete(shmPath);

                logger.LogInformation("Dropbox 状態 DB をリセットしました: {Path}", dbPath);
            }
        }

        logger.LogInformation("Dropbox 移行パイプラインを開始します…");

        svc.ActivateController("dropbox");
        await using var stateDb = new SqliteTransferStateDb(opts.Paths.DropboxStateDb);
        var pipeline = new DropboxMigrationPipeline(
            svc.StorageProvider,          // OneDrive ソース（GraphStorageProvider）
            svc.DropboxProvider,          // Dropbox 転送先
            stateDb,
            opts,
            svc.LoggerFactory.CreateLogger<DropboxMigrationPipeline>(),
            svc.GetController("dropbox"));

        TransferSummary summary;
        var autoRetryRemaining = autoRetry;
        while (true)
        {
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
