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
    private readonly IStorageProvider? _sourceProvider;
    private readonly SkipListManager _skipList;
    private readonly MigratorOptions _options;
    private readonly ILogger<TransferEngine> _logger;
    // TransferEngine は旧来のスキップリストベース転送エンジン。v0.5.0 以降は SQLite パイプライン推奨。
#pragma warning disable CS0618 // AdaptiveConcurrencyController / TokenBucketRateLimiter は Obsolete だが TransferEngine は後方互換維持のため継続使用
    private readonly AdaptiveConcurrencyController? _concurrencyController;
    private readonly TokenBucketRateLimiter? _rateLimiter;
#pragma warning restore CS0618

    /// <param name="destProvider">転送先プロバイダー</param>
    /// <param name="skipList">スキップリスト管理</param>
    /// <param name="options">設定</param>
    /// <param name="logger">ロガー</param>
    /// <param name="concurrencyController">動的並列度コントローラー（null = 無効）</param>
    /// <param name="rateLimiter">Token Bucket レートリミッター（null = 無効）</param>
    /// <param name="sourceProvider">
    /// 転送元プロバイダー（クロスプロバイダー転送用）。null の場合は destProvider が
    /// ダウンロード・アップロードを一括処理する（後方互換）。
    /// </param>
#pragma warning disable CS0618 // AdaptiveConcurrencyController / TokenBucketRateLimiter は Obsolete だが TransferEngine は後方互換維持のため継続使用
    public TransferEngine(
        IStorageProvider destProvider,
        SkipListManager skipList,
        MigratorOptions options,
        ILogger<TransferEngine> logger,
        AdaptiveConcurrencyController? concurrencyController = null,
        TokenBucketRateLimiter? rateLimiter = null,
        IStorageProvider? sourceProvider = null)
#pragma warning restore CS0618 // 復元
    {
        _destProvider = destProvider;
        _sourceProvider = sourceProvider;
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
        // AutoCreateParentFolders == true のプロバイダー（Dropbox 等）は
        // ファイルアップロード時に親フォルダを自動生成するため、このフェーズをスキップする。
        var destRootNormalized = destRoot.TrimEnd('/');

        if (!_destProvider.AutoCreateParentFolders)
        {
            // ListItemsAsync がフォルダを返さない実装でも動作するよう、
            // 1) IsFolder == true なアイテムの SkipKey
            // 2) 全アイテムの Path から導出したフォルダ階層
            // の両方から転送先フォルダパスを重複排除して作成する。
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

            // DestinationRoot 自体をフォルダセットに含め、深さ順ループで先に処理されるようにする。
            if (!string.IsNullOrEmpty(destRootNormalized))
                folderPathSet.Add(destRootNormalized);

            // 親フォルダから順に EnsureFolderAsync を呼び出す。
            // 同一深さのフォルダは並行処理可能（親は必ず前の深さで作成済み）。
            var totalFolders = folderPathSet.Count;
            _logger.LogInformation("フォルダ先行作成: {Count} 件のユニークフォルダを確認・作成中...", totalFolders);
            int foldersDone = 0;

            // 深さの計算は / と \ の両方を区切りとするセグメント数ベースで行う。
            var byDepth = folderPathSet
                .GroupBy(p => p.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Length)
                .OrderBy(g => g.Key);

            foreach (var depthGroup in byDepth)
            {
                await Parallel.ForEachAsync(
                    depthGroup,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = _options.MaxParallelFolderCreations,
                        CancellationToken = cancellationToken,
                    },
                    async (destFolderPath, ct) =>
                    {
                        await _destProvider.EnsureFolderAsync(destFolderPath, ct).ConfigureAwait(false);
                        var done = Interlocked.Increment(ref foldersDone);
                        if (done % 100 == 0)
                            _logger.LogInformation("フォルダ先行作成進捗: {Done}/{Total}", done, totalFolders);
                    }).ConfigureAwait(false);
            }
            _logger.LogInformation("フォルダ先行作成完了: {Count} 件", totalFolders);
        }
        else
        {
            _logger.LogInformation(
                "フォルダ先行作成スキップ: {Provider} は親フォルダを自動作成します",
                _destProvider.ProviderId);
        }

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
        int success = 0, failed = 0, done = 0;

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
                        await UploadItemAsync(job, innerCt).ConfigureAwait(false);
                        await _skipList.AddAsync(job.Source.SkipKey, innerCt).ConfigureAwait(false);
                        Interlocked.Increment(ref success);
                        var doneSnap = Interlocked.Increment(ref done);
                        _logger.LogInformation("転送完了: {SkipKey}", job.Source.SkipKey);
                        controller.NotifySuccess();
                        if (doneSnap % 500 == 0)
                            _logger.LogInformation(
                                "転送進捗: {Done}/{Total} 完了 (失敗: {Failed}, 現在の並列度: {Degree}/{Max})",
                                doneSnap, jobs.Count, Volatile.Read(ref failed),
                                controller.CurrentDegree, controller.MaxDegree);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failed);
                        Interlocked.Increment(ref done);
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
            // Channel のディスパッチに加え、TokenBucketRateLimiter のゲートにより file/sec を制御する。
            // 並列数（MaxParallelTransfers）はワーカープールサイズとして活用。
            _logger.LogInformation(
                "Token Bucket レートリミッターモードで転送開始 (初期レート: {Rate:F1}/{Max:F1} file/sec, バースト: {Burst} トークン, ワーカー: {Workers})",
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
                    // トークン取得で file/sec をゲート制御
                    await limiter.AcquireAsync(innerCt).ConfigureAwait(false);
                    try
                    {
                        await UploadItemAsync(job, innerCt).ConfigureAwait(false);
                        await _skipList.AddAsync(job.Source.SkipKey, innerCt).ConfigureAwait(false);
                        Interlocked.Increment(ref success);
                        var doneSnap = Interlocked.Increment(ref done);
                        _logger.LogInformation("転送完了: {SkipKey}", job.Source.SkipKey);
                        limiter.NotifySuccess();
                        if (doneSnap % 500 == 0)
                            _logger.LogInformation(
                                "転送進捗: {Done}/{Total} 完了 (失敗: {Failed}, 現在のレート: {Rate:F1}/{Max:F1} file/sec)",
                                doneSnap, jobs.Count, Volatile.Read(ref failed),
                                limiter.CurrentRate, limiter.MaxRate);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failed);
                        Interlocked.Increment(ref done);
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
                        await UploadItemAsync(job, innerCt).ConfigureAwait(false);
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

        if (_rateLimiter is not null)
        {
            _logger.LogInformation(
                "転送完了: 成功 {Success} / 失敗 {Failed} / スキップ {Skipped} " +
                "(最終レート: {Rate:F1}/{Max:F1} file/sec, 所要時間: {Elapsed:c})",
                success, failed, skipped,
                _rateLimiter.CurrentRate, _rateLimiter.MaxRate, sw.Elapsed);
        }
        else
        {
            _logger.LogInformation(
                "転送完了: 成功 {Success} / 失敗 {Failed} / スキップ {Skipped} (所要時間: {Elapsed:c})",
                success, failed, skipped, sw.Elapsed);
        }

        return new TransferSummary
        {
            Success = success,
            Failed = failed,
            Skipped = skipped,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>
    /// 1 ファイルを転送する。
    /// <list type="bullet">
    ///   <item>sourceProvider が null: destProvider.UploadFileAsync（単一プロバイダー後方互換）</item>
    ///   <item>sourceProvider 指定時: ソースからダウンロード → デスト へアップロード（クロスプロバイダー）</item>
    /// </list>
    /// </summary>
    private async Task UploadItemAsync(TransferJob job, CancellationToken ct)
    {
        if (_sourceProvider is null)
        {
            await _destProvider.UploadFileAsync(job, ct).ConfigureAwait(false);
            return;
        }

        if (job.Source.SizeBytes is null)
            throw new InvalidOperationException($"SizeBytes が未設定のため転送できません: {job.Source.SkipKey}");

        await using var sourceStream = await _sourceProvider.DownloadStreamAsync(job.Source, ct).ConfigureAwait(false);
        await _destProvider.UploadFromStreamAsync(
            sourceStream,
            job.Source.SizeBytes.Value,
            job.DestinationFullPath,
            ct).ConfigureAwait(false);
    }
}
