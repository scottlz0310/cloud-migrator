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
/// SQLite 状態管理 + Bounded Channel による省メモリ設計。
/// <list type="bullet">
///   <item>Phase A（リカバリ）: permanent_failed を failed に戻し、前回失敗ファイルを再試行対象にする</item>
///   <item>Phase B（クロール）: OneDrive を全量クロールし新規アイテムを SQLite に登録する（Channel には書かない）</item>
///   <item>Phase D（転送）: SQLite の pending/processing/failed をストリームで Bounded Channel に投入し、並列転送する</item>
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
    private int _completedTransfers;
    private DateTimeOffset _pipelineStartTime;
    // 並列度変化検出用（前回記録値。変化があれば即時メトリクス書き込み）
    private int _lastRecordedParallelism = -1;

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

        // パイプライン開始時刻を初回のみ保存（リカバリ再起動時は上書きしない）
        var existingStartedAt = await _stateDb.GetCheckpointAsync("pipeline_started_at", ct).ConfigureAwait(false);
        if (existingStartedAt is null)
            await _stateDb.SaveCheckpointAsync("pipeline_started_at", _pipelineStartTime.ToString("O"), ct).ConfigureAwait(false);

        // Phase A + Phase B: クロールを完了まで先行実行（SQLite に登録し Channel には書かない）
        await PhasesABAsync(ct).ConfigureAwait(false);

        // Phase D: SQLite の pending/processing/failed を全件ストリームで転送
        var (success, failed) = await PhaseDAsync(ct).ConfigureAwait(false);

        sw.Stop();

        var totalRlCount = _concurrencyController is not null
            ? _concurrencyController.RateLimitCount
            : (long)_rateLimitHitCount;
        var rateLimitRate = _totalTransferAttempts > 0
            ? (double)totalRlCount / _totalTransferAttempts * 100.0
            : 0.0;
        _logger.LogInformation(
            "Dropbox 移行完了: 成功 {Success} / 失敗 {Failed} / 所要時間 {Elapsed:c} | フォルダAPI {EnsureFolder} 回 / 転送試行 {Total} 回 / 429 {RateLimit} 回 ({RateLimitRate:F1}%)",
            success, failed, sw.Elapsed,
            _ensureFolderCallCount, _totalTransferAttempts, totalRlCount, rateLimitRate);

        return new TransferSummary
        {
            Success = success,
            Failed = failed,
            Skipped = 0,
            Elapsed = sw.Elapsed,
        };
    }

    /// <summary>クロール完了をダッシュボードに知らせるチェックポイントキー。</summary>
    internal const string CrawlCompleteKey = "crawl_complete";
    internal const string CrawlTotalKey = "crawl_total";

    /// <summary>
    /// Phase A（リカバリ）+ Phase B（クロール）を順次実行する。
    /// Phase B はクロールを最後まで完了させ、新規アイテムを SQLite に登録するのみで Channel には書かない。
    /// </summary>
    private async Task PhasesABAsync(CancellationToken ct)
    {
        // ── Phase A: クラッシュリカバリ + permanent_failed リセット ──────────────
        // processing → pending: 前回クラッシュ時に処理中だったファイルをリカバリ対象に戻す。
        // Phase B 完了まで転送が始まらない設計のため、リセットせずに放置するとダッシュボードの
        // 進捗表示が長時間「処理中」のままになる。
        await _stateDb.ResetProcessingAsync(ct).ConfigureAwait(false);

        var resetCount = await _stateDb.ResetPermanentFailedAsync(ct).ConfigureAwait(false);
        if (resetCount > 0)
            _logger.LogInformation("Phase A: 前回リトライ上限到達ファイル {Count} 件を再試行対象に戻します", resetCount);

        // ── Phase B: ソースクロールで新規アイテムを SQLite に登録 ──────────────
        // Channel への書き込みは Phase D が担うため、Phase B はクロールのみ行う。
        // GraphStorageProvider は ListPagedAsync で Graph Delta API を使ったネイティブページングを実装しており、
        // @odata.nextLink が cursor として保存されるため、中断→再開時も途中ページから再開可能。
        // クロール完了後は @odata.deltaLink が cursor に保存され、次回実行では差分のみ取得する。

        // クロール完了フラグをリセット（前回の crawl_complete フラグを上書き）
        await _stateDb.SaveCheckpointAsync(CrawlCompleteKey, "false", ct).ConfigureAwait(false);

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

                // InsertPendingIfNewAsync: 未登録なら pending で INSERT し true を返す（ON CONFLICT DO NOTHING）。
                // GetStatusAsync + UpsertPendingAsync の 2 クエリを 1 クエリに集約して N+1 を回避する。
                if (await _stateDb.InsertPendingIfNewAsync(item, ct).ConfigureAwait(false))
                    newItems++;
                // 既存アイテム（pending/processing/failed/done/permanent_failed）はスキップ
                // pending/processing/failed → Phase D の GetPendingStreamAsync でキューイングされる
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

        // クロール完了時点の真の総数（pending + processing + done + failed + permanent_failed）を
        // チェックポイントに保存する。Consumer が並列で done に変化させてもカウント自体は変わらない。
        var crawlSummary = await _stateDb.GetSummaryAsync(ct).ConfigureAwait(false);
        await _stateDb.SaveCheckpointAsync(CrawlTotalKey, crawlSummary.Total.ToString(), ct).ConfigureAwait(false);
        _logger.LogInformation("クロール確定総数: {Total} 件", crawlSummary.Total);

        // クロール完了フラグを保存（ダッシュボードで総数が確定済みかを判定するために使用）
        await _stateDb.SaveCheckpointAsync(CrawlCompleteKey, "true", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase D: SQLite の pending/processing/failed 全件を Bounded Channel 経由で並列転送する。
    /// </summary>
    private async Task<(int success, int failed)> PhaseDAsync(CancellationToken ct)
    {
        var channel = Channel.CreateBounded<TransferJob>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });

        // Producer（SQLite ストリーム → Channel）と Consumer（並列転送）を並行実行
        // linked CTS により、一方が異常終了した際にもう一方をキャンセルする
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producerTask = PhaseDProduceAsync(channel.Writer, linkedCts.Token);
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
        return (success, failed);
    }

    /// <summary>
    /// Phase D Producer: SQLite の pending/processing/failed をストリームで Channel に投入する。
    /// </summary>
    private async Task PhaseDProduceAsync(ChannelWriter<TransferJob> writer, CancellationToken ct)
    {
        try
        {
            var count = 0;
            await foreach (var record in _stateDb.GetPendingStreamAsync(ct).ConfigureAwait(false))
            {
                var job = RecordToJob(record);
                await writer.WriteAsync(job, ct).ConfigureAwait(false);
                count++;
            }

            if (count > 0)
                _logger.LogInformation("Phase D: 転送キューイング完了: {Count} 件 (pending/processing/failed)", count);
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

    private async Task<(int success, int failed)> ConsumeAsync(
        ChannelReader<TransferJob> reader, CancellationToken ct)
    {
        var success = 0;
        var failed = 0;

        var controller = _concurrencyController;
        var maxDegree = controller?.MaxDegree ?? _options.MaxParallelTransfers;

        var workers = Enumerable.Range(0, maxDegree)
            .Select(workerId => WorkerLoopAsync(workerId, reader, controller, () => Interlocked.Increment(ref success), () => Interlocked.Increment(ref failed), ct))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);

        return (success, failed);
    }

    /// <summary>1 ファイルをクロスプロバイダー転送する（OneDrive ダウンロード → Dropbox アップロード）。</summary>
    private async Task<FileTransferTelemetry> TransferItemAsync(TransferJob job, CancellationToken ct)
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

        var retryBefore = (_destinationProvider as IRetryAwareStorageProvider)?.TotalRetryCount ?? 0;
        await using var rawSourceStream = await _sourceProvider.DownloadStreamAsync(job.Source, ct).ConfigureAwait(false);
        await using var measuredSourceStream = new MeasuredReadStream(rawSourceStream);

        var uploadWallSw = Stopwatch.StartNew();
        await _destinationProvider.UploadFromStreamAsync(
            measuredSourceStream,
            job.Source.SizeBytes.Value,
            job.DestinationFullPath,
            ct).ConfigureAwait(false);
        uploadWallSw.Stop();

        var retryAfter = (_destinationProvider as IRetryAwareStorageProvider)?.TotalRetryCount ?? retryBefore;
        return new FileTransferTelemetry(
            measuredSourceStream.TotalReadElapsed,
            uploadWallSw.Elapsed,
            retryAfter - retryBefore);
    }

    private async Task WorkerLoopAsync(
        int workerId,
        ChannelReader<TransferJob> reader,
        AdaptiveConcurrencyController? controller,
        Action onSuccess,
        Action onFailure,
        CancellationToken ct)
    {
        await foreach (var job in reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (controller is not null)
                await controller.AcquireAsync(ct).ConfigureAwait(false);

            var totalNow = Interlocked.Increment(ref _totalTransferAttempts);

            try
            {
                var telemetry = await TransferItemAsync(job, ct).ConfigureAwait(false);
                await _stateDb.MarkDoneAsync(job.Source.Path, job.Source.Name, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _completedTransfers);
                Interlocked.Add(ref _totalBytesTransferred, job.Source.SizeBytes ?? 0);
                onSuccess();
                controller?.NotifySuccess();

                var elapsedSeconds = Math.Max(0.001, (DateTimeOffset.UtcNow - _pipelineStartTime).TotalSeconds);
                var filesPerSec = Volatile.Read(ref _completedTransfers) / elapsedSeconds;
                _logger.LogInformation(
                    "Dropbox 転送完了: worker={WorkerId} file={SkipKey} downloadReadMs={DownloadReadMs} uploadWallMs={UploadWallMs} retries={RetryCount} filesPerSec={FilesPerSec:F2} concurrency={Concurrency} rateLimits={RateLimitCount}",
                    workerId,
                    job.Source.SkipKey,
                    (int)telemetry.DownloadReadElapsed.TotalMilliseconds,
                    (int)telemetry.UploadWallElapsed.TotalMilliseconds,
                    telemetry.RetryCount,
                    filesPerSec,
                    controller?.CurrentDegree ?? _options.MaxParallelTransfers,
                    controller?.RateLimitCount ?? Volatile.Read(ref _rateLimitHitCount));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dropbox 転送失敗: {SkipKey}", job.Source.SkipKey);
                await _stateDb.MarkFailedAsync(job.Source.Path, job.Source.Name, ex.Message, ct)
                    .ConfigureAwait(false);
                onFailure();

                var isRateLimit =
                    ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
                    || ex.Message.Contains("too_many", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
                if (isRateLimit)
                    Interlocked.Increment(ref _rateLimitHitCount);
            }
            finally
            {
                await RecordMetricsAsync(job.Source.SkipKey, totalNow, controller, ct).ConfigureAwait(false);
                controller?.Release();
            }
        }
    }

    private async Task RecordMetricsAsync(
        string skipKey,
        int totalNow,
        AdaptiveConcurrencyController? controller,
        CancellationToken ct)
    {
        if (controller is not null)
        {
            var currentDegree = controller.CurrentDegree;
            if (Interlocked.Exchange(ref _lastRecordedParallelism, currentDegree) != currentDegree)
            {
                try
                {
                    await _stateDb.RecordMetricAsync("current_parallelism", (double)currentDegree, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "current_parallelism の即時記録に失敗しました。");
                }
            }
        }

        if (totalNow % 100 != 0)
            return;

        var rlCount = controller is not null
            ? controller.RateLimitCount
            : (long)Volatile.Read(ref _rateLimitHitCount);
        var pct = rlCount > 0 ? (double)rlCount / totalNow * 100.0 : 0.0;
        var elapsedSeconds = (DateTimeOffset.UtcNow - _pipelineStartTime).TotalSeconds;
        var filesPerMin = elapsedSeconds > 0 ? totalNow / elapsedSeconds * 60.0 : 0.0;
        var bytesPerSec = elapsedSeconds > 0 ? Volatile.Read(ref _totalBytesTransferred) / elapsedSeconds : 0.0;

        try
        {
            await _stateDb.RecordMetricAsync("rate_limit_pct", pct, ct).ConfigureAwait(false);
            await _stateDb.RecordMetricAsync("throughput_files_per_min", filesPerMin, ct).ConfigureAwait(false);
            await _stateDb.RecordMetricAsync("throughput_bytes_per_sec", bytesPerSec, ct).ConfigureAwait(false);
            await _stateDb.RecordMetricAsync(
                "current_parallelism",
                (double)(controller?.CurrentDegree ?? _options.MaxParallelTransfers),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "メトリクス記録に失敗しました（SkipKey: {SkipKey}）。メイン処理は継続します。",
                skipKey);
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

    private sealed record FileTransferTelemetry(
        TimeSpan DownloadReadElapsed,
        TimeSpan UploadWallElapsed,
        long RetryCount);

    private sealed class MeasuredReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly Stopwatch _readStopwatch = new();

        public MeasuredReadStream(Stream inner)
        {
            _inner = inner;
        }

        public TimeSpan TotalReadElapsed => _readStopwatch.Elapsed;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            _readStopwatch.Start();
            try
            {
                return _inner.Read(buffer, offset, count);
            }
            finally
            {
                _readStopwatch.Stop();
            }
        }

        public override int Read(Span<byte> buffer)
        {
            _readStopwatch.Start();
            try
            {
                return _inner.Read(buffer);
            }
            finally
            {
                _readStopwatch.Stop();
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            _readStopwatch.Start();
            return FinishReadAsync(_inner.ReadAsync(buffer, offset, count, cancellationToken));
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _readStopwatch.Start();
            return FinishReadValueAsync(_inner.ReadAsync(buffer, cancellationToken));
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            _inner.Seek(offset, origin);

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() =>
            _inner.DisposeAsync();

        private async Task<int> FinishReadAsync(Task<int> readTask)
        {
            try
            {
                return await readTask.ConfigureAwait(false);
            }
            finally
            {
                _readStopwatch.Stop();
            }
        }

        private async ValueTask<int> FinishReadValueAsync(ValueTask<int> readTask)
        {
            try
            {
                return await readTask.ConfigureAwait(false);
            }
            finally
            {
                _readStopwatch.Stop();
            }
        }
    }
}
