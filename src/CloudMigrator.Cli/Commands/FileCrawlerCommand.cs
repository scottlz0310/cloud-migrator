using System.CommandLine;
using CloudMigrator.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// file-crawler サブコマンド（FR-18）。
/// onedrive/sharepoint/dropbox/skiplist/compare/validate/explore を提供する。
/// </summary>
internal static class FileCrawlerCommand
{
    public static Command Build()
    {
        var cmd = new Command("file-crawler", "クロール結果や skip_list を確認・比較・検証します");

        cmd.Add(BuildProviderCrawlCommand("onedrive"));
        cmd.Add(BuildProviderCrawlCommand("sharepoint"));
        cmd.Add(BuildProviderCrawlCommand("dropbox"));
        cmd.Add(BuildSkipListCommand());
        cmd.Add(BuildCompareCommand());
        cmd.Add(BuildValidateCommand());
        cmd.Add(BuildExploreCommand());

        return cmd;
    }

    private static Command BuildProviderCrawlCommand(string source)
    {
        var cmd = new Command(source, $"{source} を再帰クロールしてキャッシュへ保存します");
        cmd.SetAction(async (_, ct) =>
        {
            await RunProviderCrawlAsync(source, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    private static Command BuildSkipListCommand()
    {
        var cmd = new Command("skiplist", "skip_list の件数とサンプルを表示します");
        var topOpt = new Option<int>("--top")
        {
            Description = "表示する先頭件数（デフォルト: 20）",
            DefaultValueFactory = _ => 20,
        };
        cmd.Add(topOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var top = parseResult.GetValue(topOpt);
            await RunSkipListAsync(top, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    private static Command BuildCompareCommand()
    {
        var cmd = new Command("compare", "2 つのデータセットを skipKey で比較します");
        var leftOpt = new Option<string>("--left")
        {
            Description = "比較元（onedrive/sharepoint/dropbox/skiplist）",
            DefaultValueFactory = _ => "onedrive",
        };
        var rightOpt = new Option<string>("--right")
        {
            Description = "比較先（onedrive/sharepoint/dropbox/skiplist）",
            DefaultValueFactory = _ => "sharepoint",
        };
        var topOpt = new Option<int>("--top")
        {
            Description = "差分として表示する件数（デフォルト: 20）",
            DefaultValueFactory = _ => 20,
        };
        cmd.Add(leftOpt);
        cmd.Add(rightOpt);
        cmd.Add(topOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var left = parseResult.GetValue(leftOpt) ?? "onedrive";
            var right = parseResult.GetValue(rightOpt) ?? "sharepoint";
            var top = parseResult.GetValue(topOpt);
            await RunCompareAsync(left, right, top, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    private static Command BuildValidateCommand()
    {
        var cmd = new Command("validate", "skip_list をクロール結果と照合して整合性を検証します");
        var sourceOpt = new Option<string>("--source")
        {
            Description = "検証対象のクロール元（onedrive/sharepoint/dropbox）",
            DefaultValueFactory = _ => "onedrive",
        };
        var topOpt = new Option<int>("--top")
        {
            Description = "エラーとして表示する件数（デフォルト: 20）",
            DefaultValueFactory = _ => 20,
        };
        cmd.Add(sourceOpt);
        cmd.Add(topOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOpt) ?? "onedrive";
            var top = parseResult.GetValue(topOpt);
            await RunValidateAsync(source, top, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    private static Command BuildExploreCommand()
    {
        var cmd = new Command("explore", "データセットの先頭 N 件を表示します");
        var sourceOpt = new Option<string>("--source")
        {
            Description = "対象データセット（onedrive/sharepoint/dropbox/skiplist）",
            DefaultValueFactory = _ => "onedrive",
        };
        var topOpt = new Option<int>("--top")
        {
            Description = "表示件数（デフォルト: 20）",
            DefaultValueFactory = _ => 20,
        };
        cmd.Add(sourceOpt);
        cmd.Add(topOpt);
        cmd.SetAction(async (parseResult, ct) =>
        {
            var source = parseResult.GetValue(sourceOpt) ?? "onedrive";
            var top = parseResult.GetValue(topOpt);
            await RunExploreAsync(source, top, ct).ConfigureAwait(false);
        });
        return cmd;
    }

    private static async Task RunProviderCrawlAsync(string source, CancellationToken ct)
    {
        using var svc = CliServices.Build();
        var logger = svc.LoggerFactory.CreateLogger("file-crawler");
        var items = await CrawlProviderAsync(svc, source, ct).ConfigureAwait(false);
        await SaveProviderCacheAsync(svc, source, items, ct).ConfigureAwait(false);

        logger.LogInformation(
            "file-crawler {Source} 完了: {Count} 件（キャッシュ保存済み）",
            source,
            items.Count);
    }

    private static async Task RunSkipListAsync(int top, CancellationToken ct)
    {
        using var svc = CliServices.Build();
        var keys = await svc.SkipListManager.LoadAsync(ct).ConfigureAwait(false);
        Console.WriteLine($"skip_list 件数: {keys.Count}");
        foreach (var key in keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(NormalizeTop(top)))
            Console.WriteLine($"- {key}");
    }

    private static async Task RunCompareAsync(string left, string right, int top, CancellationToken ct)
    {
        EnsureDatasetName(left, allowSkipList: true);
        EnsureDatasetName(right, allowSkipList: true);

        using var svc = CliServices.Build();
        var logger = svc.LoggerFactory.CreateLogger("file-crawler");
        var leftKeys = await LoadDatasetKeysAsync(svc, left, logger, ct).ConfigureAwait(false);
        var rightKeys = await LoadDatasetKeysAsync(svc, right, logger, ct).ConfigureAwait(false);

        var result = CompareKeySets(leftKeys, rightKeys, top);
        Console.WriteLine($"比較: {left} ({result.LeftCount}) vs {right} ({result.RightCount})");
        Console.WriteLine($"一致: {result.BothCount}");
        Console.WriteLine($"{left} のみ: {result.OnlyLeftCount} / {right} のみ: {result.OnlyRightCount}");

        if (result.OnlyLeftSamples.Count > 0)
        {
            Console.WriteLine($"--- {left} のみ（先頭 {result.OnlyLeftSamples.Count} 件） ---");
            foreach (var key in result.OnlyLeftSamples)
                Console.WriteLine($"- {key}");
        }

        if (result.OnlyRightSamples.Count > 0)
        {
            Console.WriteLine($"--- {right} のみ（先頭 {result.OnlyRightSamples.Count} 件） ---");
            foreach (var key in result.OnlyRightSamples)
                Console.WriteLine($"- {key}");
        }

        if (result.OnlyLeftCount > 0 || result.OnlyRightCount > 0)
            Environment.ExitCode = 1;
    }

    private static async Task RunValidateAsync(string source, int top, CancellationToken ct)
    {
        EnsureDatasetName(source, allowSkipList: false);

        using var svc = CliServices.Build();
        var logger = svc.LoggerFactory.CreateLogger("file-crawler");
        var sourceKeys = await LoadDatasetKeysAsync(svc, source, logger, ct).ConfigureAwait(false);
        var skipKeys = await LoadDatasetKeysAsync(svc, "skiplist", logger, ct).ConfigureAwait(false);

        var result = ValidateSkipList(skipKeys, sourceKeys, top);
        Console.WriteLine($"validate: source={source}");
        Console.WriteLine($"skip_list 件数: {result.SkipListCount}, source 件数: {result.SourceCount}");
        Console.WriteLine($"不正キー: {result.InvalidCount}, source 不在キー: {result.MissingCount}");

        if (result.InvalidSamples.Count > 0)
        {
            Console.WriteLine($"--- 不正キー（先頭 {result.InvalidSamples.Count} 件） ---");
            foreach (var key in result.InvalidSamples)
                Console.WriteLine($"- {key}");
        }

        if (result.MissingSamples.Count > 0)
        {
            Console.WriteLine($"--- source 不在キー（先頭 {result.MissingSamples.Count} 件） ---");
            foreach (var key in result.MissingSamples)
                Console.WriteLine($"- {key}");
        }

        if (result.InvalidCount > 0 || result.MissingCount > 0)
            Environment.ExitCode = 1;
    }

    private static async Task RunExploreAsync(string source, int top, CancellationToken ct)
    {
        EnsureDatasetName(source, allowSkipList: true);

        using var svc = CliServices.Build();
        var logger = svc.LoggerFactory.CreateLogger("file-crawler");
        var normalizedTop = NormalizeTop(top);

        if (source.Equals("skiplist", StringComparison.OrdinalIgnoreCase))
        {
            var keys = await svc.SkipListManager.LoadAsync(ct).ConfigureAwait(false);
            Console.WriteLine($"explore: skiplist (total={keys.Count})");
            foreach (var key in keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(normalizedTop))
                Console.WriteLine($"- {key}");
            return;
        }

        var items = await LoadProviderItemsAsync(svc, source, logger, ct).ConfigureAwait(false);
        Console.WriteLine($"explore: {source} (total={items.Count})");
        foreach (var item in items.OrderBy(i => i.SkipKey, StringComparer.OrdinalIgnoreCase).Take(normalizedTop))
        {
            Console.WriteLine(
                $"- {item.SkipKey} | size={item.SizeBytes?.ToString() ?? "n/a"} | modified={item.LastModifiedUtc?.ToString("O") ?? "n/a"}");
        }
    }

    internal static CompareResult CompareKeySets(
        IReadOnlyCollection<string> leftKeys,
        IReadOnlyCollection<string> rightKeys,
        int top)
    {
        var normalizedTop = NormalizeTop(top);
        var leftSet = new HashSet<string>(leftKeys, StringComparer.OrdinalIgnoreCase);
        var rightSet = new HashSet<string>(rightKeys, StringComparer.OrdinalIgnoreCase);

        var onlyLeftAll = leftSet.Except(rightSet, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var onlyRightAll = rightSet.Except(leftSet, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var onlyLeftSamples = onlyLeftAll
            .Take(normalizedTop)
            .ToList();
        var onlyRightSamples = onlyRightAll
            .Take(normalizedTop)
            .ToList();
        var bothCount = leftSet.Intersect(rightSet, StringComparer.OrdinalIgnoreCase).Count();

        return new CompareResult(
            leftSet.Count,
            rightSet.Count,
            bothCount,
            onlyLeftAll.Count,
            onlyRightAll.Count,
            onlyLeftSamples,
            onlyRightSamples);
    }

    internal static ValidateResult ValidateSkipList(
        IReadOnlyCollection<string> skipKeys,
        IReadOnlyCollection<string> sourceKeys,
        int top)
    {
        var normalizedTop = NormalizeTop(top);
        var skipSet = new HashSet<string>(skipKeys, StringComparer.OrdinalIgnoreCase);
        var sourceSet = new HashSet<string>(sourceKeys, StringComparer.OrdinalIgnoreCase);

        var invalidAll = skipSet
            .Where(IsInvalidSkipKey)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var missingAll = skipSet.Except(sourceSet, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var invalidSamples = invalidAll
            .Take(normalizedTop)
            .ToList();
        var missingSamples = missingAll
            .Take(normalizedTop)
            .ToList();

        return new ValidateResult(
            skipSet.Count,
            sourceSet.Count,
            invalidAll.Count,
            missingAll.Count,
            invalidSamples,
            missingSamples);
    }

    internal static bool IsInvalidSkipKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return true;

        if (key.StartsWith('/') || key.EndsWith('/'))
            return true;

        if (key.Contains('\\') || key.Contains("//", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static async Task<HashSet<string>> LoadDatasetKeysAsync(
        CliServices svc,
        string dataset,
        ILogger logger,
        CancellationToken ct)
    {
        if (dataset.Equals("skiplist", StringComparison.OrdinalIgnoreCase))
            return await svc.SkipListManager.LoadAsync(ct).ConfigureAwait(false);

        var items = await LoadProviderItemsAsync(svc, dataset, logger, ct).ConfigureAwait(false);
        return items
            .Where(i => !i.IsFolder)
            .Select(i => i.SkipKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<IReadOnlyList<StorageItem>> LoadProviderItemsAsync(
        CliServices svc,
        string source,
        ILogger logger,
        CancellationToken ct)
    {
        var cachePath = GetCachePath(svc, source);
        var cached = await svc.CrawlCache.LoadAsync(cachePath, ct).ConfigureAwait(false);
        if (cached.Count > 0)
            return cached;

        var crawled = await CrawlProviderAsync(svc, source, ct).ConfigureAwait(false);
        await SaveProviderCacheAsync(svc, source, crawled, ct).ConfigureAwait(false);
        logger.LogInformation("{Source} を再クロールしてキャッシュを再生成しました: {Count} 件", source, crawled.Count);
        return crawled;
    }

    private static async Task<IReadOnlyList<StorageItem>> CrawlProviderAsync(
        CliServices svc,
        string source,
        CancellationToken ct)
    {
        return source.ToLowerInvariant() switch
        {
            "onedrive" => await svc.StorageProvider.ListItemsAsync("onedrive", ct).ConfigureAwait(false),
            "sharepoint" => await svc.StorageProvider.ListItemsAsync("sharepoint", ct).ConfigureAwait(false),
            "dropbox" => await svc.DropboxProvider.ListItemsAsync("dropbox", ct).ConfigureAwait(false),
            _ => throw new ArgumentException($"未対応のデータセットです: {source}", nameof(source)),
        };
    }

    private static Task SaveProviderCacheAsync(
        CliServices svc,
        string source,
        IReadOnlyList<StorageItem> items,
        CancellationToken ct)
    {
        var cachePath = GetCachePath(svc, source);
        return svc.CrawlCache.SaveAsync(cachePath, items, ct);
    }

    private static string GetCachePath(CliServices svc, string source)
    {
        return source.ToLowerInvariant() switch
        {
            "onedrive" => svc.Options.Paths.OneDriveCache,
            "sharepoint" => svc.Options.Paths.SharePointCache,
            "dropbox" => svc.Options.Paths.DropboxCache,
            _ => throw new ArgumentException($"キャッシュ対象外のデータセットです: {source}", nameof(source)),
        };
    }

    private static void EnsureDatasetName(string source, bool allowSkipList)
    {
        var valid = allowSkipList
            ? new[] { "onedrive", "sharepoint", "dropbox", "skiplist" }
            : new[] { "onedrive", "sharepoint", "dropbox" };

        if (!valid.Contains(source, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"未対応のデータセットです: {source} (使用可能: {string.Join(", ", valid)})",
                nameof(source));
    }

    private static int NormalizeTop(int top) => Math.Max(1, top);
}

internal sealed record CompareResult(
    int LeftCount,
    int RightCount,
    int BothCount,
    int OnlyLeftCount,
    int OnlyRightCount,
    IReadOnlyList<string> OnlyLeftSamples,
    IReadOnlyList<string> OnlyRightSamples);

internal sealed record ValidateResult(
    int SkipListCount,
    int SourceCount,
    int InvalidCount,
    int MissingCount,
    IReadOnlyList<string> InvalidSamples,
    IReadOnlyList<string> MissingSamples);
