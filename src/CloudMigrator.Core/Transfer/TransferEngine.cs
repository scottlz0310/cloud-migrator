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
///   <item><see cref="AdaptiveConcurrencyController"/> が指定された場合は動的並列度制御（FR-14 拡張）</item>
///   <item>指定がない場合は <see cref="Channel{T}"/> + <see cref="Parallel.ForEachAsync"/> で固定並列転送</item>
///   <item>転送成功後に skip_list へ原子的追加（FR-08）</item>
/// </list>
/// </summary>
public sealed class TransferEngine
{
    private readonly IStorageProvider _destProvider;
    private readonly SkipListManager _skipList;
    private readonly MigratorOptions _options;
    private readonly ILogger<TransferEngine> _logger;
    private readonly AdaptiveConcurrencyController? _concurrencyController;
    private readonly TokenBucketRateLimiter? _rateLimiter;

    public TransferEngine(
        IStorageProvider destProvider,
        SkipListManager skipList,
        MigratorOptions options,
        ILogger<TransferEngine> logger,
        AdaptiveConcurrencyController? concurrencyController = null,
        TokenBucketRateLimiter? rateLimiter = null)
    {
        _destProvider = destProvider;
        _skipList = skipList;
        _options = options;
        _logger = logger;
        _concurrencyController = concurrencyController;
        _rateLimiter = rateLimiter;
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
        // ListItemsAsync がフォルダを返さない実装でも動作するよう、
        // 1) IsFolder == true なアイテムの SkipKey
        // 2) 全アイテムの Path から導出したフォルダ階層
        // の両方から転送先フォルダパスを重複排除して作成する。
        var destRootNormalized = destRoot.TrimEnd('/');
        var folderPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 明示的なフォルダアイテム（存在すれば）
        foreach (var folder in sourceItems.Where(i => i.IsFolder))
        {
            var destFolderPath = $"{destRootNormalized}/{folder.SkipKey.TrimStart('/')}";
            folderPathSet.Add(destFolderPath);
        }

        // 2) ファイル（およびフォルダ）の Path からフォルダ階層を導出
        foreach (var item in sourceItems)
        {
            if (string.IsNullOrWhiteSpace(item.Path))
                continue;

            var relativePath = item.Path.Trim('/');
            if (string.IsNullOrEmpty(relativePath))
                continue;

            var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = destRootNormalized;
            foreach (var segment in segments)
            {
                current = $"{current}/{segment}";
                folderPathSet.Add(current);
            }
        }

        // 親フォルダから順に EnsureFolderAsync を呼び出す
        var sortedFolders = folderPathSet.OrderBy(p => p.Length).ToList();
        _logger.LogInformation("フォルダ先行作成: {Count} 件のユニークフォルダを確認・作成中...", sortedFolders.Count);
        int foldersDone = 0;
        foreach (var destFolderPath in sortedFolders)
        {
            await _destProvider.EnsureFolderAsync(destFolderPath, cancellationToken)
                .ConfigureAwait(false);
            foldersDone++;
            if (foldersDone % 100 == 0)
                _logger.LogInformation("フォルダ先行作成進捗: {Done}/{Total}", foldersDone, sortedFolders.Count);
        }
        _logger.LogInformation("フォルダ先行作成完了: {Count} 件", sortedFolders.Count);

        // ─── 2. スキップ照合・ジョブリスト構築 ──────────────────────────
        var jobs = new List<TransferJob>();
        int skipped = 0;

        foreach (var item in sourceItems.Where(i => !i.IsFolder))
        {
            if (await _skipList.ContainsAsync(item.SkipKey, cancellationToken).ConfigureAwait(false))
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

        // ─── 3 & 4. 並列転送 ─────────────────────────────────────────────
        int success = 0, failed = 0;

        if (_concurrencyController is not null)
        {
            // ── 動的並列度制御モード ──────────────────────────────────────
            // Channel + Parallel.ForEachAsync(MaxDegree ワーカー) + コントローラーセマフォのゲート。
            // ジョブが大量でもアクティブなタスク数は MaxDegree 以下に抑えられる。
            _logger.LogInformation(
                "動的並列度制御モードで転送開始 (初期並列度: {Degree}/{Max})",
                _concurrencyController.CurrentDegree, _concurrencyController.MaxDegree);

            var adaptiveChannel = Channel.CreateBounded<TransferJob>(
                new BoundedChannelOptions(jobs.Count)
                {
                    SingleWriter = true,
                    SingleReader = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });

            foreach (var job in jobs)
                await adaptiveChannel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
            adaptiveChannel.Writer.Complete();

            var controller = _concurrencyController; // ローカルにキャプチャ

            await Parallel.ForEachAsync(
                adaptiveChannel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = controller.MaxDegree,
                    CancellationToken = cancellationToken,
                },
                async (job, innerCt) =>
                {
                    // セマフォで現在の動的並列度に絞る（超過ワーカーはここで待機）
                    await controller.AcquireAsync(innerCt).ConfigureAwait(false);
                    try
                    {
                        await _destProvider.UploadFileAsync(job, innerCt).ConfigureAwait(false);
                        await _skipList.AddAsync(job.Source.SkipKey, innerCt).ConfigureAwait(false);
                        var done = Interlocked.Increment(ref success);
                        _logger.LogInformation("転送完了: {SkipKey}", job.Source.SkipKey);
                        controller.NotifySuccess();
                        if (done % 500 == 0)
                            _logger.LogInformation(
                                "転送進捗: {Done}/{Total} 完了 (失敗: {Failed}, 現在の並列度: {Degree}/{Max})",
                                done, jobs.Count, Volatile.Read(ref failed),
                                controller.CurrentDegree, controller.MaxDegree);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogError(ex, "転送失敗: {SkipKey}", job.Source.SkipKey);
                    }
                    finally
                    {
                        controller.Release();
                    }
                }).ConfigureAwait(false);
        }
        else if (_rateLimiter is not null)
        {
            // ── Token Bucket レートリミッターモード ─────────────────────────
            // Channel のディスパッチに加え、TokenBucketRateLimiter のゲートにより request/sec を制御する。
            // 並列数（MaxParallelTransfers）はワーカープールサイズとして活用。
            _logger.LogInformation(
                "Token Bucket レートリミッターモードで転送開始 (初期レート: {Rate:F1}/{Max:F1} req/sec, バースト: {Burst} トークン, ワーカー: {Workers})",
                _rateLimiter.CurrentRate, _rateLimiter.MaxRate, _rateLimiter.BurstCapacity, _options.MaxParallelTransfers);

            var rateLimiterChannel = Channel.CreateBounded<TransferJob>(
                new BoundedChannelOptions(jobs.Count)
                {
                    SingleWriter = true,
                    SingleReader = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });

            foreach (var job in jobs)
                await rateLimiterChannel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
            rateLimiterChannel.Writer.Complete();

            var limiter = _rateLimiter; // ローカルにキャプチャ

            await Parallel.ForEachAsync(
                rateLimiterChannel.Reader.ReadAllAsync(cancellationToken),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = _options.MaxParallelTransfers,
                    CancellationToken = cancellationToken,
                },
                async (job, innerCt) =>
                {
                    // トークン取得で request/sec をゲート制御
                    await limiter.AcquireAsync(innerCt).ConfigureAwait(false);
                    try
                    {
                        await _destProvider.UploadFileAsync(job, innerCt).ConfigureAwait(false);
                        await _skipList.AddAsync(job.Source.SkipKey, innerCt).ConfigureAwait(false);
                        var done = Interlocked.Increment(ref success);
                        _logger.LogInformation("転送完了: {SkipKey}", job.Source.SkipKey);
                        limiter.NotifySuccess();
                        if (done % 500 == 0)
                            _logger.LogInformation(
                                "転送進捗: {Done}/{Total} 完了 (失敗: {Failed}, 現在のレート: {Rate:F1}/{Max:F1} req/sec)",
                                done, jobs.Count, Volatile.Read(ref failed),
                                limiter.CurrentRate, limiter.MaxRate);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogError(ex, "転送失敗: {SkipKey}", job.Source.SkipKey);
                    }
                }).ConfigureAwait(false);
        }
        else
        {
            // ── 固定並列度モード（後方互換） ──────────────────────────────
            // Channel + Parallel.ForEachAsync による並列実行。
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
                        await _skipList.AddAsync(job.Source.SkipKey, innerCt).ConfigureAwait(false);
                        Interlocked.Increment(ref success);
                        _logger.LogInformation("転送完了: {SkipKey}", job.Source.SkipKey);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failed);
                        _logger.LogError(ex, "転送失敗: {SkipKey}", job.Source.SkipKey);
                    }
                }).ConfigureAwait(false);
        }

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
