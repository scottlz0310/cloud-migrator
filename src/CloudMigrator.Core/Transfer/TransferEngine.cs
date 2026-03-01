using System.Diagnostics;
using System.Threading.Channels;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Storage;
using CloudMigrator.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// 転送エンジン。クロール済みソースアイテムを並列転送する（FR-14）。
/// <list type="bullet">
///   <item>フォルダを転送先に先行作成（FR-06）</item>
///   <item>skip_list 照合でスキップ（FR-07）</item>
///   <item><see cref="Channel{T}"/> + <see cref="Parallel.ForEachAsync"/> で上限付き並列転送</item>
///   <item>転送成功後に skip_list へ原子的追加（FR-08）</item>
/// </list>
/// </summary>
public sealed class TransferEngine
{
    private readonly IStorageProvider _destProvider;
    private readonly SkipListManager _skipList;
    private readonly MigratorOptions _options;
    private readonly ILogger<TransferEngine> _logger;

    public TransferEngine(
        IStorageProvider destProvider,
        SkipListManager skipList,
        MigratorOptions options,
        ILogger<TransferEngine> logger)
    {
        _destProvider = destProvider;
        _skipList = skipList;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// ソースアイテム一覧を転送先へ並列転送し、サマリーを返す。
    /// </summary>
    /// <param name="sourceItems">Phase 3 クロール済みアイテム（フォルダ含む）</param>
    /// <param name="destRoot">転送先ルートパス（例: "sharepoint/Documents"）</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    public async Task<TransferSummary> RunAsync(
        IReadOnlyList<StorageItem> sourceItems,
        string destRoot,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // ─── 1. フォルダを先行作成（親→子の順）(FR-06) ─────────────────
        var folders = sourceItems
            .Where(i => i.IsFolder)
            .OrderBy(i => i.SkipKey.Length)
            .ToList();

        foreach (var folder in folders)
        {
            var destFolderPath = $"{destRoot.TrimEnd('/')}/{folder.SkipKey.TrimStart('/')}";
            await _destProvider.EnsureFolderAsync(destFolderPath, cancellationToken)
                .ConfigureAwait(false);
        }

        // ─── 2. スキップ照合・ジョブリスト構築 ──────────────────────────
        var jobs = new List<TransferJob>();
        int skipped = 0;

        foreach (var item in sourceItems.Where(i => !i.IsFolder))
        {
            if (await _skipList.ContainsAsync(item.SkipKey).ConfigureAwait(false))
            {
                skipped++;
                _logger.LogDebug("スキップ（skip_list 登録済み）: {SkipKey}", item.SkipKey);
            }
            else
            {
                jobs.Add(new TransferJob { Source = item, DestinationRoot = destRoot });
            }
        }

        _logger.LogInformation(
            "転送開始: ファイル合計 {Total} 件 / スキップ {Skipped} 件 / 転送対象 {Transfer} 件",
            sourceItems.Count(i => !i.IsFolder), skipped, jobs.Count);

        if (jobs.Count == 0)
        {
            sw.Stop();
            return new TransferSummary { Success = 0, Failed = 0, Skipped = skipped, Elapsed = sw.Elapsed };
        }

        // ─── 3. Channel に全ジョブを投入 ────────────────────────────────
        var channel = Channel.CreateBounded<TransferJob>(
            new BoundedChannelOptions(jobs.Count)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            });

        foreach (var job in jobs)
            await channel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
        channel.Writer.Complete();

        // ─── 4. Parallel.ForEachAsync で並列消費 ────────────────────────
        int success = 0, failed = 0;

        await Parallel.ForEachAsync(
            channel.Reader.ReadAllAsync(cancellationToken),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxParallelTransfers,
                CancellationToken = cancellationToken,
            },
            async (job, innerCt) =>
            {
                try
                {
                    await _destProvider.UploadFileAsync(job, innerCt).ConfigureAwait(false);
                    await _skipList.AddAsync(job.Source.SkipKey).ConfigureAwait(false);
                    Interlocked.Increment(ref success);
                    _logger.LogInformation("転送完了: {SkipKey}", job.Source.SkipKey);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogError(ex, "転送失敗: {SkipKey}", job.Source.SkipKey);
                }
            }).ConfigureAwait(false);

        sw.Stop();

        _logger.LogInformation(
            "転送完了: 成功 {Success} / 失敗 {Failed} / スキップ {Skipped} (所要時間: {Elapsed:c})",
            success, failed, skipped, sw.Elapsed);

        return new TransferSummary
        {
            Success = success,
            Failed = failed,
            Skipped = skipped,
            Elapsed = sw.Elapsed,
        };
    }
}
