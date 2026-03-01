using System.CommandLine;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Storage;
using CloudMigrator.Core.Transfer;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// transfer サブコマンド。
/// - 通常実行（FR-13）: skip_list がなければ自動再構築後に転送
/// - --full-rebuild（FR-12）: キャッシュ・skip_list をクリアして全転送
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

        // 1. 設定ハッシュ確認（FR-10）
        var hash = ConfigHashChecker.ComputeHash(opts);
        bool hashChanged = await ConfigHashChecker.HasChangedAsync(opts.Paths.ConfigHash, hash, ct)
            .ConfigureAwait(false);

        if (hashChanged)
        {
            logger.LogWarning("設定変更を検知しました。キャッシュをクリアします。");
            ConfigHashChecker.ClearCaches(opts.Paths, logger);
            await ConfigHashChecker.SaveHashAsync(opts.Paths.ConfigHash, hash, ct).ConfigureAwait(false);
        }

        // 2. --full-rebuild の場合は skip_list もクリア
        if (fullRebuild)
        {
            logger.LogInformation("--full-rebuild: キャッシュと skip_list をクリアします。");
            ConfigHashChecker.ClearCaches(opts.Paths, logger);
            ConfigHashChecker.ClearSkipList(opts.Paths, logger);
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

        // 4. skip_list がなければ SharePoint からリビルド（FR-13）
        bool skipListMissing = !File.Exists(opts.Paths.SkipList);
        if (skipListMissing || fullRebuild)
        {
            logger.LogInformation("SharePoint をクロールして skip_list を再構築します…");
            await RebuildSkipListFromSharePointAsync(svc, logger, ct).ConfigureAwait(false);
        }

        // 5. 転送実行
        logger.LogInformation("転送を開始します: {Count} 件のソースアイテム", sourceItems.Count);
        var engine = new TransferEngine(
            svc.StorageProvider,
            svc.SkipListManager,
            opts,
            svc.LoggerFactory.CreateLogger<TransferEngine>());

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

        int added = 0;
        foreach (var item in spItems.Where(i => !i.IsFolder))
        {
            await svc.SkipListManager.AddAsync(item.SkipKey, ct).ConfigureAwait(false);
            added++;
        }

        logger.LogInformation("skip_list を再構築しました: {Count} 件追加", added);
    }
}
