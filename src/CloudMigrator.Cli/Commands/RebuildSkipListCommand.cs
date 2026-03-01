using System.CommandLine;
using CloudMigrator.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// rebuild-skiplist サブコマンド。
/// SharePoint を再クロールして skip_list を再構築するだけで転送は行わない（FR-11）。
/// </summary>
internal static class RebuildSkipListCommand
{
    public static Command Build()
    {
        var cmd = new Command(
            "rebuild-skiplist",
            "SharePoint を再クロールして skip_list を再構築します（転送なし）");

        cmd.SetAction(async (parseResult, ct) =>
        {
            await RunAsync(ct).ConfigureAwait(false);
        });

        return cmd;
    }

    private static async Task RunAsync(CancellationToken ct)
    {
        using var svc = CliServices.Build();
        var logger = svc.LoggerFactory.CreateLogger("rebuild-skiplist");
        var opts = svc.Options;

        // 設定ハッシュ確認（FR-10）
        var hash = ConfigHashChecker.ComputeHash(opts);
        bool hashChanged = await ConfigHashChecker.HasChangedAsync(opts.Paths.ConfigHash, hash, ct)
            .ConfigureAwait(false);

        if (hashChanged)
        {
            logger.LogWarning("設定変更を検知しました。キャッシュと skip_list をクリアします。");
            ConfigHashChecker.ClearAll(opts.Paths, logger);
            await ConfigHashChecker.SaveHashAsync(opts.Paths.ConfigHash, hash, ct).ConfigureAwait(false);
        }
        else
        {
            // 設定ハッシュに変更がない場合でも skip_list をクリアして再構築
            ConfigHashChecker.ClearSkipList(opts.Paths, logger);
        }

        // skip_list を SharePoint から再構築

        logger.LogInformation("skip_list を再構築します…");

        await TransferCommand.RebuildSkipListFromSharePointAsync(svc, logger, ct).ConfigureAwait(false);

        logger.LogInformation("rebuild-skiplist 完了");
    }
}
