using System.Collections.Concurrent;
using CloudMigrator.Core.State;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// メトリクス DB 書き込みを非同期バッファリングするコンポーネント（リスク 2 対応）。
/// <para>
/// 制御ループ（1 秒周期）が毎秒 <see cref="ITransferStateDb.RecordMetricAsync"/> を呼ぶと
/// <c>_writeLock</c> 競合が増大するため、一定間隔でまとめて書き込む。
/// </para>
/// <list type="bullet">
///   <item>Controller/Aggregator 境界に位置する。Controller は DB を直接触らない。</item>
///   <item>バッファ満杯時は古いデータを破棄する（可観測性 > 完全性）。</item>
///   <item>Flush 失敗時はリトライせず破棄する（転送処理を優先）。</item>
///   <item>Dispose 時の最終 Flush は <see cref="CancellationToken.None"/> で実行し、残データを確実に書き込む。</item>
/// </list>
/// </summary>
public sealed class MetricsBuffer : IAsyncDisposable
{
    private const int MaxBufferSize = 1000;

    private readonly ITransferStateDb _db;
    private readonly TimeSpan _flushInterval;
    private readonly ILogger<MetricsBuffer> _logger;
    private readonly ConcurrentQueue<(string Name, double Value, DateTimeOffset Timestamp)> _queue = new();
    // ConcurrentQueue.Count は O(N) 走査のためスレッド間競合が発生しやすい。
    // 専用の Interlocked カウンターで正確なサイズ管理を行う。
    private int _count;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushTask;

    public MetricsBuffer(ITransferStateDb db, int flushIntervalSec, ILogger<MetricsBuffer> logger)
    {
        _db = db;
        _flushInterval = TimeSpan.FromSeconds(Math.Max(1, flushIntervalSec));
        _logger = logger;
        _flushTask = Task.Run(FlushLoopAsync);
    }

    /// <summary>メトリクスをバッファに追加する。バッファが満杯の場合は古いエントリを捨てる。</summary>
    public void Enqueue(string name, double value)
    {
        // Interlocked カウンターでバッファ容量を管理する。
        // ConcurrentQueue.Count は O(N) 走査で複数スレッドからの同時エンキュー時に競合しやすい。
        if (Interlocked.Increment(ref _count) > MaxBufferSize)
        {
            // 上限超過: 古いエントリを捨ててカウンターを戻す
            if (_queue.TryDequeue(out _))
                Interlocked.Decrement(ref _count); // 捨てた分のカウンターは復帰しない（新しいエントリ分を上書き）
            else
                Interlocked.Decrement(ref _count); // デキュー失敗時はインクリメントを戻す
        }

        _queue.Enqueue((name, value, DateTimeOffset.UtcNow));
    }

    private async Task FlushLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_flushInterval, _cts.Token).ConfigureAwait(false);
                await FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常終了
        }

        // 終了前に残っているエントリをフラッシュ（キャンセル済みトークンは使わない）
        await FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_queue.IsEmpty) return;

        var batch = new List<(string Name, double Value, DateTimeOffset Timestamp)>();
        while (_queue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _count);
            batch.Add(item);
        }

        if (batch.Count == 0) return;

        try
        {
            await _db.RecordMetricsBatchAsync(batch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 書き込み失敗時はリトライせず破棄（転送処理を優先）
            _logger.LogWarning(ex, "メトリクスバッファの Flush に失敗しました。{Count} 件を破棄します。", batch.Count);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _flushTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
