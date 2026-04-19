using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.State;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Migration;

/// <summary>
/// OneDrive → SharePoint 移行パイプライン。
/// SQLite 状態管理 + 4フェーズ構造（リカバリ / クロール / フォルダ先行作成 / 転送）。
/// <list type="bullet">
///   <item>Phase A（リカバリ）: processing 状態を pending にリセット（クラッシュリカバリ）</item>
///   <item>Phase B（クロール）: OneDrive ページングクロールで全ファイルを SQLite に保存。カーソルチェックポイントで中断再開可能。</item>
///   <item>Phase C（フォルダ先行作成）: DB のパスから全祖先フォルダを展開し、深さ順に並列 EnsureFolderAsync。</item>
///   <item>Phase D（転送）: AdaptiveConcurrencyController で動的並列制御しながら SharePoint 転送。</item>
/// </list>
/// </summary>
public sealed class SharePointMigrationPipeline : IMigrationPipeline
{
    public const string SpCursorKey = "sp_cursor";
    public const string CrawlCompleteKey = "crawl_complete";
    public const string CrawlTotalKey = "crawl_total";
    public const string FolderTotalKey = "folder_total";
    public const string FolderCreationCompleteKey = "folder_creation_complete";
    internal const string SourceRootPath = "onedrive";
    private const int ChannelCapacity = 1000;

    private readonly IStorageProvider _sourceProvider;
    private readonly IStorageProvider _destinationProvider;
    private readonly ITransferStateDb _stateDb;
    private readonly MigratorOptions _options;
    private readonly ITransferRateController? _concurrencyController;
    private readonly ITransferRateController? _folderCreationController;
    private readonly Action<ITransferRateController?>? _activateController;
    private readonly ILogger<SharePointMigrationPipeline> _logger;

    // Phase D メトリクスカウンタ（Interlocked によるスレッドセーフな並列カウント）
    private int _totalTransferAttempts;
    private int _rateLimitHitCount;
    private long _totalBytesTransferred;
    private DateTimeOffset _pipelineStartTime;
    private int _folderDoneCount;
    private int _lastRecordedParallelism = -1;

    public SharePointMigrationPipeline(
        IStorageProvider sourceProvider,
        IStorageProvider destinationProvider,
        ITransferStateDb stateDb,
        MigratorOptions options,
        ILogger<SharePointMigrationPipeline> logger,
        ITransferRateController? concurrencyController = null,
        ITransferRateController? folderCreationController = null,
        Action<ITransferRateController?>? activateController = null)
    {
        _sourceProvider = sourceProvider;
        _destinationProvider = destinationProvider;
        _stateDb = stateDb;
        _options = options;
        _logger = logger;
        _concurrencyController = concurrencyController;
        _folderCreationController = folderCreationController;
        _activateController = activateController;
    }

    /// <inheritdoc/>
    public async Task<TransferSummary> RunAsync(CancellationToken ct)
    {
        await _stateDb.InitializeAsync(ct).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        _pipelineStartTime = DateTimeOffset.UtcNow;

        // パイプライン開始時刻を初回のみ保存（リカバリ再起動時は上書きしない）
        if (await _stateDb.GetCheckpointAsync("pipeline_started_at", ct).ConfigureAwait(false) is null)
            await _stateDb.SaveCheckpointAsync("pipeline_started_at", _pipelineStartTime.ToString("O"), ct).ConfigureAwait(false);

        // 前回までの累積実稼働秒数を読み込む（再起動をまたいだ合計実稼働時間のため）
        var prevWorkingSecondsStr = await _stateDb.GetCheckpointAsync("pipeline_working_seconds", ct).ConfigureAwait(false);
        double prevWorkingSeconds = double.TryParse(prevWorkingSecondsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        // ── Phase A: クラッシュリカバリ ────────────────────────────────────────────
        await _stateDb.ResetProcessingAsync(ct).ConfigureAwait(false);
        var resetCount = await _stateDb.ResetPermanentFailedAsync(ct).ConfigureAwait(false);
        if (resetCount > 0)
            _logger.LogInformation("Phase A: 前回リトライ上限到達ファイル {Count} 件を再試行対象に戻します", resetCount);
        _logger.LogInformation("Phase A: クラッシュリカバリ完了（processing → pending リセット）");

        // ── Phase B: クロール ───────────────────────────────────────────────────────
        var crawlCompleteVal = await _stateDb.GetCheckpointAsync(CrawlCompleteKey, ct).ConfigureAwait(false);
        if (crawlCompleteVal == "true")
        {
            _logger.LogInformation("Phase B: クロール完了チェックポイントあり - スキップ");
        }
        else
        {
            // フレッシュスタートまたはクロール未完了の場合は "false" を明示保存する。
            // これによりダッシュボードが CrawlComplete=false を正しく読み取り "クロール中" を表示できる。
            await _stateDb.SaveCheckpointAsync(CrawlCompleteKey, "false", ct).ConfigureAwait(false);
            await PhaseBCrawlAsync(ct).ConfigureAwait(false);
        }

        // ── Phase C: フォルダ先行作成 ───────────────────────────────────────────────
        // フォルダ作成専用コントローラーがある場合は、429 通知先を切り替える
        _activateController?.Invoke(_folderCreationController);
        if (await _stateDb.GetCheckpointAsync(FolderCreationCompleteKey, ct).ConfigureAwait(false) == "true")
        {
            _logger.LogInformation("Phase C: フォルダ作成完了チェックポイントあり - スキップ");
        }
        else
        {
            await PhaseCFolderCreationAsync(ct).ConfigureAwait(false);
        }

        // ── Phase D: ファイル転送 ───────────────────────────────────────────────────
        // 転送用コントローラーに戻す
        _activateController?.Invoke(_concurrencyController);
        var channel = Channel.CreateBounded<TransferJob>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producerTask = PhaseDProduceAsync(channel.Writer, linkedCts.Token);
        var consumerTask = PhaseDConsumeAsync(channel.Reader, linkedCts.Token);

        int success;
        int failed;
        try
        {
            (success, failed) = await consumerTask.ConfigureAwait(false);
        }
        catch
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await producerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }
        finally
        {
            sw.Stop();
            // 中断・完了問わず今回の実稼働時間を累積保存する
            // CancellationToken がキャンセル済みの場合は CancellationToken.None で保存する
            var saveToken = ct.IsCancellationRequested ? CancellationToken.None : ct;
            var totalWorking = prevWorkingSeconds + sw.Elapsed.TotalSeconds;
            await _stateDb.SaveCheckpointAsync("pipeline_working_seconds", totalWorking.ToString("F3", CultureInfo.InvariantCulture), saveToken)
                          .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        await producerTask.ConfigureAwait(false);

        var totalRlCount = (long)_rateLimitHitCount;
        var rateLimitRate = _totalTransferAttempts > 0
            ? (double)totalRlCount / _totalTransferAttempts * 100.0
            : 0.0;
        _logger.LogInformation(
            "SharePoint 移行完了: 成功 {Success} / 失敗 {Failed} / 所要時間 {Elapsed:c} | 転送試行 {Total} 回 / 429 {RateLimit} 回 ({RateLimitRate:F1}%)",
            success, failed, sw.Elapsed, _totalTransferAttempts, totalRlCount, rateLimitRate);

        return new TransferSummary
        {
            Success = success,
            Failed = failed,
            Skipped = 0,
            Elapsed = sw.Elapsed,
        };
    }

    private async Task PhaseBCrawlAsync(CancellationToken ct)
    {
        var cursor = await _stateDb.GetCheckpointAsync(SpCursorKey, ct).ConfigureAwait(false);
        var pageCount = 0;

        while (true)
        {
            var page = await _sourceProvider.ListPagedAsync(SourceRootPath, cursor, ct).ConfigureAwait(false);
            pageCount++;

            foreach (var item in page.Items.Where(i => !i.IsFolder))
            {
                ct.ThrowIfCancellationRequested();
                // done / permanent_failed は上書きせず、新規・pending/processing/failed は pending にリセット
                // GetStatusAsync を省いて 1 クエリで処理（N+1 回避）
                await _stateDb.UpsertPendingIfNotTerminalAsync(item, ct).ConfigureAwait(false);
            }

            // ページ単位でカーソルをチェックポイント保存（中断再開に備える）
            if (page.Cursor is not null)
                await _stateDb.SaveCheckpointAsync(SpCursorKey, page.Cursor, ct).ConfigureAwait(false);

            try
            {
                await _stateDb.RecordMetricAsync("sp_crawl_pages", (double)pageCount, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "sp_crawl_pages メトリクス記録に失敗しました。");
            }

            _logger.LogDebug("Phase B クロール進捗: ページ {Page}, HasMore={HasMore}", pageCount, page.HasMore);

            if (!page.HasMore)
                break;

            cursor = page.Cursor;
        }

        var crawlSummary = await _stateDb.GetSummaryAsync(ct).ConfigureAwait(false);
        await _stateDb.SaveCheckpointAsync(CrawlTotalKey, crawlSummary.Total.ToString(), ct).ConfigureAwait(false);
        await _stateDb.SaveCheckpointAsync(CrawlCompleteKey, "true", ct).ConfigureAwait(false);

        _logger.LogInformation("Phase B: クロール完了 {Pages} ページ, 総数 {Total} 件", pageCount, crawlSummary.Total);
    }

    private async Task PhaseCFolderCreationAsync(CancellationToken ct)
    {
        var distinctPaths = await _stateDb.GetDistinctFolderPathsAsync(ct).ConfigureAwait(false);

        // 各 path の全祖先パスを HashSet に展開してフォルダ一覧を作成
        // 例: "Documents/Projects/2026" → {"Documents", "Documents/Projects", "Documents/Projects/2026"}
        var allFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in distinctPaths)
        {
            var normalized = path.Trim('/');
            if (string.IsNullOrEmpty(normalized))
                continue;

            var segments = normalized.Split('/');
            for (var i = 1; i <= segments.Length; i++)
                allFolders.Add(string.Join("/", segments.Take(i)));
        }

        // folder_total チェックポイント保存（ダッシュボードの進捗バー用）
        await _stateDb.SaveCheckpointAsync(FolderTotalKey, allFolders.Count.ToString(), ct).ConfigureAwait(false);

        // Phase C 専用コントローラーがあればそちらを優先する（MaxParallelFolderCreations ベース）
        // ない場合は転送用コントローラーにフォールバックし、maxDegree で上限を補正する
        var controller = _folderCreationController ?? _concurrencyController;
        var maxFolderCreationDegree = Math.Max(1, _options.MaxParallelFolderCreations);
        var maxDegree = maxFolderCreationDegree;

        // Phase C の初期並列度をメトリクスに記録（ダッシュボードの「現在並列数」表示用）
        try
        {
            var initialParallelism = controller is not null
                ? Math.Min(maxDegree, (int)Math.Round(controller.CurrentRateLimit))
                : maxDegree;
            await _stateDb.RecordMetricAsync(
                "current_parallelism", (double)initialParallelism, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Phase C current_parallelism メトリクス記録に失敗しました。");
        }

        if (allFolders.Count == 0)
        {
            _logger.LogInformation("Phase C: フォルダ作成対象なし");
            await _stateDb.SaveCheckpointAsync(FolderCreationCompleteKey, "true", ct).ConfigureAwait(false);
            return;
        }

        // 深さ順ソート（浅いフォルダから先に作成する必要がある）
        var sortedFolders = allFolders
            .OrderBy(f => f.Count(c => c == '/'))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Phase C: フォルダ先行作成開始 {Count} 件", sortedFolders.Count);

        // 同一深さのフォルダを並列作成（異なる深さは順次: 親が存在してから子を作成する）
        foreach (var depthGroup in sortedFolders.GroupBy(f => f.Count(c => c == '/')))
        {
            await Parallel.ForEachAsync(
                depthGroup,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxDegree,
                    CancellationToken = ct,
                },
                async (folderRelPath, folderCt) =>
                {
                    // 動的並列度制御のゲート（ITransferRateController が有効な場合）
                    if (controller is not null)
                        await controller.AcquireAsync(folderCt).ConfigureAwait(false);

                    // AcquireAsync 成功後に NotifyRequestSent（インフライトカウンターのインクリメント）
                    // これにより NotifySuccess/NotifyRateLimit と対になりカウンターが整合する
                    controller?.NotifyRequestSent();

                    try
                    {
                        var normalizedRoot = _options.DestinationRoot?.Replace('\\', '/').Trim('/');
                        var destFolderPath = string.IsNullOrEmpty(normalizedRoot)
                            ? folderRelPath
                            : $"{normalizedRoot}/{folderRelPath}";

                        // EnsureFolderAsync は 409 Conflict を無視する冪等実装のため並列・再実行で安全
                        await _destinationProvider.EnsureFolderAsync(destFolderPath, folderCt).ConfigureAwait(false);

                        controller?.NotifySuccess(TimeSpan.Zero);

                        var count = Interlocked.Increment(ref _folderDoneCount);
                        try
                        {
                            await _stateDb.RecordMetricAsync("sp_folder_done", (double)count, folderCt).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "sp_folder_done メトリクス記録に失敗しました。");
                        }

                        // 並列度が変化した場合は即時記録（Phase C 上限 maxDegree でキャップ）
                        if (controller is not null)
                        {
                            var currentDegree = Math.Min(maxDegree, (int)Math.Round(controller.CurrentRateLimit));
                            if (Interlocked.Exchange(ref _lastRecordedParallelism, currentDegree) != currentDegree)
                            {
                                try
                                {
                                    await _stateDb.RecordMetricAsync(
                                        "current_parallelism", (double)currentDegree, folderCt).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Phase C current_parallelism 即時記録に失敗しました。");
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // キャンセル時はカウンターを戻す（NotifyRequestSent のペアとして必要）
                        controller?.NotifyCompleted(TimeSpan.Zero);
                        throw;
                    }
                    catch (Exception)
                    {
                        // 一般例外時もインフライトカウンターを戻す（NotifyRequestSent のペアとして必要）
                        controller?.NotifyCompleted(TimeSpan.Zero);
                        throw;
                    }
                    finally
                    {
                        controller?.Release();
                    }
                }).ConfigureAwait(false);
        }

        await _stateDb.SaveCheckpointAsync(FolderCreationCompleteKey, "true", ct).ConfigureAwait(false);
        _logger.LogInformation("Phase C: フォルダ先行作成完了 {Count} 件", sortedFolders.Count);
    }

    private async Task PhaseDProduceAsync(ChannelWriter<TransferJob> writer, CancellationToken ct)
    {
        try
        {
            await foreach (var record in _stateDb.GetPendingStreamAsync(ct).ConfigureAwait(false))
            {
                var job = RecordToJob(record);
                await writer.WriteAsync(job, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            writer.TryComplete();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Phase D Producer でエラーが発生しました");
            writer.Complete(ex);
            throw;
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task<(int success, int failed)> PhaseDConsumeAsync(
        ChannelReader<TransferJob> reader, CancellationToken ct)
    {
        var success = 0;
        var failed = 0;

        var controller = _concurrencyController;
        var maxDegree = _options.MaxParallelTransfers;

        await Parallel.ForEachAsync(
            reader.ReadAllAsync(ct),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegree,
                CancellationToken = ct,
            },
            async (job, itemCt) =>
            {
                // レート制御のゲート（ITransferRateController が有効な場合）
                if (controller is not null)
                    await controller.AcquireAsync(itemCt).ConfigureAwait(false);

                var totalNow = Interlocked.Increment(ref _totalTransferAttempts);
                var sw = System.Diagnostics.Stopwatch.StartNew();

                controller?.NotifyRequestSent();

                try
                {
                    await TransferItemAsync(job, itemCt).ConfigureAwait(false);
                    var latency = sw.Elapsed;
                    await _stateDb.MarkDoneAsync(job.Source.Path, job.Source.Name, itemCt).ConfigureAwait(false);
                    Interlocked.Increment(ref success);
                    Interlocked.Add(ref _totalBytesTransferred, job.Source.SizeBytes ?? 0);
                    _logger.LogInformation("SharePoint 転送完了: {SkipKey}", job.Source.SkipKey);
                    controller?.NotifySuccess(latency, job.Source.SizeBytes ?? 0);
                }
                catch (OperationCanceledException)
                {
                    controller?.NotifyCompleted(sw.Elapsed); // リリース前にカウンターを戻す
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SharePoint 転送失敗: {SkipKey}", job.Source.SkipKey);
                    await _stateDb.MarkFailedAsync(job.Source.Path, job.Source.Name, ex.Message, itemCt).ConfigureAwait(false);
                    Interlocked.Increment(ref failed);

                    var isRateLimit =
                        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                        || ex.Message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
                    if (isRateLimit)
                    {
                        Interlocked.Increment(ref _rateLimitHitCount);

                        // プロバイダ層で解析済みの Retry-After を HttpRequestException.Data 経由で受け取る
                        TimeSpan? retryAfter = null;
                        if (ex is HttpRequestException httpEx && httpEx.Data.Contains("Retry-After"))
                        {
                            retryAfter = httpEx.Data["Retry-After"] switch
                            {
                                TimeSpan ts => ts,
                                string s when TimeSpan.TryParse(s, out var parsed) => parsed,
                                _ => null,
                            };
                        }
                        controller?.NotifyRateLimit(retryAfter);
                    }
                    else
                    {
                        controller?.NotifyCompleted(sw.Elapsed); // 非レート制限エラーはカウンターを戻す
                    }
                }
                finally
                {
                    // 並列度（またはレート）が変化した場合は即時記録
                    if (controller is not null)
                    {
                        var currentRate = (int)Math.Round(controller.CurrentRateLimit);
                        if (Interlocked.Exchange(ref _lastRecordedParallelism, currentRate) != currentRate)
                        {
                            try
                            {
                                await _stateDb.RecordMetricAsync(
                                    "current_parallelism", (double)currentRate, itemCt).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "current_parallelism の即時記録に失敗しました。");
                            }
                        }
                    }

                    // 100 回ごとにメトリクスを記録する（ダッシュボード向け）
                    if (totalNow % 100 == 0)
                    {
                        var rlCount = (long)Volatile.Read(ref _rateLimitHitCount);
                        var pct = rlCount > 0 ? (double)rlCount / totalNow * 100.0 : 0.0;
                        var elapsedSeconds = (DateTimeOffset.UtcNow - _pipelineStartTime).TotalSeconds;
                        var filesPerMin = elapsedSeconds > 0 ? totalNow / elapsedSeconds * 60.0 : 0.0;
                        var bytesPerSec = elapsedSeconds > 0
                            ? Volatile.Read(ref _totalBytesTransferred) / elapsedSeconds
                            : 0.0;

                        // #159: HybridRateController 経路ではウィンドウ集計値で上書きする（直近の実効スループット）。
                        // 旧経路は累積平均のまま。Snapshot 取得は制御ループと別タイミングで安全。
                        double? windowSeconds = null;
                        if (controller is HybridRateController hybrid)
                        {
                            var snap = hybrid.GetCurrentSnapshot();
                            filesPerMin = snap.FilesPerSec * 60.0;
                            bytesPerSec = snap.BytesPerSec;
                            windowSeconds = snap.WindowSeconds;
                        }

                        try
                        {
                            await _stateDb.RecordMetricAsync("rate_limit_pct", pct, itemCt).ConfigureAwait(false);
                            await _stateDb.RecordMetricAsync("throughput_files_per_min", filesPerMin, itemCt).ConfigureAwait(false);
                            await _stateDb.RecordMetricAsync("throughput_bytes_per_sec", bytesPerSec, itemCt).ConfigureAwait(false);
                            if (windowSeconds.HasValue)
                                await _stateDb.RecordMetricAsync("throughput_window_sec", windowSeconds.Value, itemCt).ConfigureAwait(false);
                            await _stateDb.RecordMetricAsync(
                                "current_parallelism",
                                (double)(int)Math.Round(controller?.CurrentRateLimit ?? _options.MaxParallelTransfers),
                                itemCt).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "メトリクス記録に失敗しました（SkipKey: {SkipKey}）。メイン処理は継続します。",
                                job.Source.SkipKey);
                        }
                    }

                    controller?.Release();
                }
            }).ConfigureAwait(false);

        return (success, failed);
    }

    /// <summary>1 ファイルをクロスプロバイダー転送する（サーバーサイドコピー優先、失敗時はクライアント経由にフォールバック）。</summary>
    private async Task TransferItemAsync(TransferJob job, CancellationToken ct)
    {
        await _stateDb.MarkProcessingAsync(job.Source.Path, job.Source.Name, ct).ConfigureAwait(false);

        if (job.Source.SizeBytes is null)
            throw new InvalidOperationException(
                $"SizeBytes が未設定のため転送できません: {job.Source.SkipKey}");

        // ── サーバーサイドコピーを試みる ───────────────────────────────────────
        try
        {
            await _destinationProvider.ServerSideCopyAsync(
                job.Source.Id,
                job.DestinationPath,
                job.Source.Name,
                ct).ConfigureAwait(false);
            return;
        }
        catch (NotSupportedException)
        {
            // プロバイダーがサーバーサイドコピーを実装していない → クライアント経由にフォールバック
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "サーバーサイドコピー失敗。クライアント経由にフォールバックします: {SkipKey}", job.Source.SkipKey);
        }

        // ── クライアント経由フォールバック（ダウンロード → アップロード）─────────
        var tempPath = await _sourceProvider.DownloadToTempAsync(job.Source, ct).ConfigureAwait(false);
        try
        {
            await _destinationProvider.UploadFromLocalAsync(
                tempPath,
                job.Source.SizeBytes.Value,
                job.DestinationFullPath,
                ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "テンポラリファイルの削除に失敗: {Path}", tempPath); }
        }
    }

    private TransferJob RecordToJob(TransferRecord record) =>
        new TransferJob
        {
            Source = new StorageItem
            {
                Id = record.SourceId,
                Name = record.Name,
                Path = record.Path,
                SizeBytes = record.SizeBytes,
                IsFolder = false,
            },
            DestinationRoot = _options.DestinationRoot,
        };
}
