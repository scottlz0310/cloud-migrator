using System.Diagnostics;
using System.Threading.Channels;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.State;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Migration;

/// <summary>
/// OneDrive → Dropbox 移行パイプライン。
/// ストリーミングクロール + SQLite 状態管理 + Bounded Channel による省メモリ設計。
/// <list type="bullet">
///   <item>Phase A（リカバリ）: DB の pending/processing/failed レコードを先行キューイング</item>
///   <item>Phase B（クロール）: OneDrive ストリーミングクロールで新規アイテムを発見・キューイング</item>
///   <item>Consumer: AdaptiveConcurrencyController で並列制御しながら Dropbox 転送</item>
/// </list>
/// <para>
/// 「空フォルダの非転送」: Phase B クロール時に IsFolder=true のアイテムはスキップされます。
/// Dropbox はファイルアップロード時に親フォルダを自動作成するため、EnsureFolderAsync はデフォルトで無効です。
/// 有効化には <see cref="CloudMigrator.Core.Configuration.DropboxProviderOptions.EnableEnsureFolder"/> を参照してください。
/// </para>
/// </summary>
public sealed class DropboxMigrationPipeline : IMigrationPipeline
{
    internal const string CursorKey = "dropbox_cursor";
    internal const string SourceRootPath = "onedrive";
    private const int ChannelCapacity = 1000;

    private readonly IStorageProvider _sourceProvider;
    private readonly IStorageProvider _destinationProvider;
    private readonly ITransferStateDb _stateDb;
    private readonly MigratorOptions _options;
    private readonly AdaptiveConcurrencyController? _concurrencyController;
    private readonly ILogger<DropboxMigrationPipeline> _logger;

    // メトリクスカウンタ（Interlocked によるスレッドセーフな並列カウント）
    private int _ensureFolderCallCount;
    private int _totalTransferAttempts;
    private int _rateLimitHitCount;
    private long _totalBytesTransferred;
    private DateTimeOffset _pipelineStartTime;

    public DropboxMigrationPipeline(
        IStorageProvider sourceProvider,
        IStorageProvider destinationProvider,
        ITransferStateDb stateDb,
        MigratorOptions options,
        ILogger<DropboxMigrationPipeline> logger,
        AdaptiveConcurrencyController? concurrencyController = null)
    {
        _sourceProvider = sourceProvider;
        _destinationProvider = destinationProvider;
        _stateDb = stateDb;
        _options = options;
        _logger = logger;
        _concurrencyController = concurrencyController;
    }

    /// <inheritdoc/>
    public async Task<TransferSummary> RunAsync(CancellationToken ct)
    {
        await _stateDb.InitializeAsync(ct).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        _pipelineStartTime = DateTimeOffset.UtcNow;

        var channel = Channel.CreateBounded<TransferJob>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });

        // Producer（Phase A + Phase B）と Consumer を並行実行
        // linked CTS により、一方が異常終了した際にもう一方をキャンセルする
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producerTask = ProduceAsync(channel.Writer, linkedCts.Token);
        var consumerTask = ConsumeAsync(channel.Reader, linkedCts.Token);

        int success;
        int failed;
        try
        {
            (success, failed) = await consumerTask.ConfigureAwait(false);
        }
        catch
        {
            // Consumer が例外で終了した場合、Producer もキャンセルして待機する
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await producerTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        await producerTask.ConfigureAwait(false);

        sw.Stop();

        var rateLimitRate = _totalTransferAttempts > 0
            ? (double)_rateLimitHitCount / _totalTransferAttempts * 100.0
            : 0.0;
        _logger.LogInformation(
            "Dropbox 移行完了: 成功 {Success} / 失敗 {Failed} / 所要時間 {Elapsed:c} | フォルダAPI {EnsureFolder} 回 / 転送試行 {Total} 回 / 429 {RateLimit} 回 ({RateLimitRate:F1}%)",
            success, failed, sw.Elapsed,
            _ensureFolderCallCount, _totalTransferAttempts, _rateLimitHitCount, rateLimitRate);

        return new TransferSummary
        {
            Success = success,
            Failed = failed,
            Skipped = 0,
            Elapsed = sw.Elapsed,
        };
    }

    private async Task ProduceAsync(ChannelWriter<TransferJob> writer, CancellationToken ct)
    {
        try
        {
            // ── Phase A: DB の未完了・失敗レコードをリカバリキューイング ──────────────
            var recovered = 0;
            await foreach (var record in _stateDb.GetPendingStreamAsync(ct).ConfigureAwait(false))
            {
                var job = RecordToJob(record);
                await writer.WriteAsync(job, ct).ConfigureAwait(false);
                recovered++;
            }

            if (recovered > 0)
                _logger.LogInformation("リカバリキューイング: {Count} 件 (pending/processing/failed)", recovered);

            // ── Phase B: ソースクロールで新規アイテムを発見 ──────────────────────────
            // 【前提】_sourceProvider.ListPagedAsync がネイティブページング（cursor/nextLink）を
            // 実装している場合のみ、省メモリ・途中再開の効果が得られます。
            // デフォルト実装（IStorageProvider.ListPagedAsync の基底）は ListItemsAsync
            // の全件取得ラッパーであり、GraphStorageProvider 等はそれを利用します。
            // 真のストリーミングが必要な場合は、ソースプロバイダーで ListPagedAsync を
            // オーバーライドしてください（例: DropboxStorageProvider）。
            var cursor = await _stateDb.GetCheckpointAsync(CursorKey, ct).ConfigureAwait(false);
            var pageCount = 0;
            var newItems = 0;

            while (true)
            {
                var page = await _sourceProvider.ListPagedAsync(SourceRootPath, cursor, ct)
                    .ConfigureAwait(false);
                pageCount++;

                foreach (var item in page.Items.Where(i => !i.IsFolder))
                {
                    ct.ThrowIfCancellationRequested();

                    var status = await _stateDb.GetStatusAsync(item.Path, item.Name, ct)
                        .ConfigureAwait(false);

                    // null = 未登録の新規アイテム → INSERT して channel に投入
                    if (status is null)
                    {
                        await _stateDb.UpsertPendingAsync(item, ct).ConfigureAwait(false);
                        var job = new TransferJob { Source = item, DestinationRoot = _options.DestinationRoot };
                        await writer.WriteAsync(job, ct).ConfigureAwait(false);
                        newItems++;
                    }
                    // それ以外（pending/processing/failed/done/permanent_failed）はスキップ
                    // pending/processing/failed → Phase A でキューイング済み
                    // done/permanent_failed → 転送済み・永続失敗
                }

                // ページ単位でチェックポイント保存（クロール中断時の再開に備える）
                if (page.Cursor is not null)
                    await _stateDb.SaveCheckpointAsync(CursorKey, page.Cursor, ct).ConfigureAwait(false);

                _logger.LogDebug("クロール進捗: ページ {Page}, HasMore={HasMore}", pageCount, page.HasMore);

                if (!page.HasMore)
                    break;

                cursor = page.Cursor;
            }

            _logger.LogInformation(
                "ソースクロール完了: {Pages} ページ, 新規 {New} 件", pageCount, newItems);
        }
        catch (OperationCanceledException)
        {
            writer.TryComplete();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Producer でエラーが発生しました");
            writer.Complete(ex);
            throw;
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task<(int success, int failed)> ConsumeAsync(
        ChannelReader<TransferJob> reader, CancellationToken ct)
    {
        var success = 0;
        var failed = 0;

        var controller = _concurrencyController;
        var maxDegree = controller?.MaxDegree ?? _options.MaxParallelTransfers;

        await Parallel.ForEachAsync(
            reader.ReadAllAsync(ct),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegree,
                CancellationToken = ct,
            },
            async (job, itemCt) =>
            {
                // 動的並列度制御のゲート（AdaptiveConcurrencyController が有効な場合）
                if (controller is not null)
                    await controller.AcquireAsync(itemCt).ConfigureAwait(false);

                var totalNow = Interlocked.Increment(ref _totalTransferAttempts);

                try
                {
                    await TransferItemAsync(job, itemCt).ConfigureAwait(false);
                    await _stateDb.MarkDoneAsync(job.Source.Path, job.Source.Name, itemCt)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref success);
                    Interlocked.Add(ref _totalBytesTransferred, job.Source.SizeBytes ?? 0);
                    _logger.LogInformation("Dropbox 転送完了: {SkipKey}", job.Source.SkipKey);
                    controller?.NotifySuccess();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Dropbox 転送失敗: {SkipKey}", job.Source.SkipKey);
                    await _stateDb.MarkFailedAsync(job.Source.Path, job.Source.Name, ex.Message, itemCt)
                        .ConfigureAwait(false);
                    Interlocked.Increment(ref failed);

                    // 429 / rate limit 検出 → 動的並列度を減少
                    // DropboxApiException : HttpRequestException なので StatusCode で型安全に判定できる
                    var isRateLimit =
                        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                        || ex.Message.Contains("too_many", StringComparison.OrdinalIgnoreCase)
                        || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
                    if (isRateLimit)
                    {
                        Interlocked.Increment(ref _rateLimitHitCount);

                        // プロバイダ層で解析済みの Retry-After を HttpRequestException.Data 経由で受け取る
                        // （Core 層は Providers.Dropbox を直接参照できないため Data ディクショナリを使用）
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
                }
                finally
                {
                    // 100 回ごとに metricsテーブルへ転送状況を記録する（ダッシュボード向け）
                    if (totalNow % 100 == 0)
                    {
                        var pct = (double)Volatile.Read(ref _rateLimitHitCount) / totalNow * 100.0;
                        var elapsedSeconds = (DateTimeOffset.UtcNow - _pipelineStartTime).TotalSeconds;
                        var filesPerMin = elapsedSeconds > 0
                            ? Volatile.Read(ref _totalTransferAttempts) / elapsedSeconds * 60.0
                            : 0.0;
                        var bytesPerSec = elapsedSeconds > 0
                            ? Volatile.Read(ref _totalBytesTransferred) / elapsedSeconds
                            : 0.0;
                        try
                        {
                            await _stateDb.RecordMetricAsync("rate_limit_pct", pct, itemCt)
                                .ConfigureAwait(false);
                            await _stateDb.RecordMetricAsync("throughput_files_per_min", filesPerMin, itemCt)
                                .ConfigureAwait(false);
                            await _stateDb.RecordMetricAsync("throughput_bytes_per_sec", bytesPerSec, itemCt)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // メトリクス失敗はメイン処理に影響させないが、障害検知のためにログは出力する
                            _logger.LogWarning(
                                ex,
                                "rate_limit_pct メトリクス記録に失敗しました（SkipKey: {SkipKey}）。メイン処理は継続します。",
                                job.Source.SkipKey);
                        }
                    }

                    controller?.Release();
                }
            }).ConfigureAwait(false);

        return (success, failed);
    }

    /// <summary>1 ファイルをクロスプロバイダー転送する（OneDrive ダウンロード → Dropbox アップロード）。</summary>
    private async Task TransferItemAsync(TransferJob job, CancellationToken ct)
    {
        await _stateDb.MarkProcessingAsync(job.Source.Path, job.Source.Name, ct).ConfigureAwait(false);

        // EnsureFolderAsync（フォルダ事前作成）は Feature Flag 制御。
        // Dropbox はアップロード時に親フォルダを自動作成するため、デフォルト false。
        // 有効化には DropboxProviderOptions.EnableEnsureFolder = true を設定すること。
        if (_options.Dropbox.EnableEnsureFolder)
        {
            Interlocked.Increment(ref _ensureFolderCallCount);
            await _destinationProvider.EnsureFolderAsync(job.DestinationPath, ct).ConfigureAwait(false);
        }

        if (job.Source.SizeBytes is null)
            throw new InvalidOperationException(
                $"SizeBytes が未設定のため転送できません: {job.Source.SkipKey}");

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
