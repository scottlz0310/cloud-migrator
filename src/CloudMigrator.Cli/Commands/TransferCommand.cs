using System.CommandLine;
using CloudMigrator.Core.Configuration;
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
        cmd.Add(fullRebuildOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            bool fullRebuild = parseResult.GetValue(fullRebuildOpt);
            await RunAsync(fullRebuild, ct).ConfigureAwait(false);
        });

        return cmd;
    }

    private static async Task RunAsync(bool fullRebuild, CancellationToken ct)
    {
        using var svc = CliServices.Build();
        var logger = svc.LoggerFactory.CreateLogger("transfer");
        var opts = svc.Options;
        try
        {
            await RunCoreAsync(fullRebuild, ct, svc, logger, opts).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "転送で予期しないエラーが発生しました");
            throw;
        }
    }

    private static async Task RunCoreAsync(
        bool fullRebuild, CancellationToken ct,
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

        // 3. OneDrive クロール（キャッシュ優先）
        var sourceItems = await svc.CrawlCache.LoadAsync(opts.Paths.OneDriveCache, ct).ConfigureAwait(false);
        if (sourceItems.Count == 0)
        {
            logger.LogInformation("OneDrive をクロールします…");
            sourceItems = await svc.StorageProvider.ListItemsAsync("onedrive", ct).ConfigureAwait(false);
            await svc.CrawlCache.SaveAsync(opts.Paths.OneDriveCache, sourceItems, ct).ConfigureAwait(false);
        }
        else
        {
            logger.LogInformation("OneDrive キャッシュを使用します: {Count} 件", sourceItems.Count);
        }

        // 4. skip_list がなければ転送先からリビルド（FR-13）
        bool skipListMissing = !File.Exists(opts.Paths.SkipList);
        if (skipListMissing || fullRebuild)
        {
            var isDropbox = opts.DestinationProvider.Equals("dropbox", StringComparison.OrdinalIgnoreCase);
            if (isDropbox)
            {
                logger.LogInformation("Dropbox をクロールして skip_list を再構築します…");
                await RebuildSkipListFromDropboxAsync(svc, logger, ct).ConfigureAwait(false);
            }
            else
            {
                logger.LogInformation("SharePoint をクロールして skip_list を再構築します…");
                await RebuildSkipListFromSharePointAsync(svc, logger, ct).ConfigureAwait(false);
            }
        }

        // 5. 転送実行
        logger.LogInformation("転送を開始します: {Count} 件のソースアイテム（転送先: {DestProvider}）",
            sourceItems.Count, opts.DestinationProvider);
        var engine = new TransferEngine(
            svc.DestinationProvider,
            svc.SkipListManager,
            opts,
            svc.LoggerFactory.CreateLogger<TransferEngine>(),
            svc.AdaptiveConcurrencyController,
            svc.RateLimiter,
            sourceProvider: svc.CrossProviderSource);

        var summary = await engine.RunAsync(sourceItems, opts.DestinationRoot, ct).ConfigureAwait(false);

        logger.LogInformation(
            "転送完了: 成功 {Success} / 失敗 {Failed} / スキップ {Skipped} / 所要時間 {Elapsed:c}",
            summary.Success, summary.Failed, summary.Skipped, summary.Elapsed);

        if (summary.Failed > 0)
            Environment.ExitCode = 1;
    }

    internal static async Task RebuildSkipListFromSharePointAsync(
        CliServices svc, ILogger logger, CancellationToken ct)
    {
        var spItems = await svc.StorageProvider.ListItemsAsync("sharepoint", ct).ConfigureAwait(false);
        await svc.CrawlCache.SaveAsync(svc.Options.Paths.SharePointCache, spItems, ct).ConfigureAwait(false);

        // DestinationRoot が設定されている場合は、その配下のみを対象にし、
        // SkipKey から DestinationRoot プレフィックスを除去してソース側と同じキー体系で保存する。
        var destinationRoot = svc.Options.DestinationRoot;
        var hasDestinationRoot = !string.IsNullOrWhiteSpace(destinationRoot);
        if (hasDestinationRoot)
            destinationRoot = destinationRoot.Trim().Replace('\\', '/').Trim('/');

        var skipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in spItems.Where(i => !i.IsFolder))
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

        logger.LogInformation("skip_list を再構築しました: {Count} 件追加", skipKeys.Count);
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
