namespace CloudMigrator.Core.Transfer;

/// <summary>
/// 1 秒バケット固定リングバッファで転送イベントを集計する Aggregator 層コンポーネント。
/// <para>
/// 三層分離原則 – Aggregator 層の実装:
/// <list type="bullet">
///   <item>イベントを受け取りインメモリで集計する（DB アクセスなし）</item>
///   <item><see cref="GetSnapshot"/> で任意の時間窓の統計を O(window秒) で返す</item>
///   <item>固定 300 スロット（5 分）× 1 秒バケットにより無制限成長を防止する</item>
///   <item><see langword="lock"/> でスレッドセーフに保護されている</item>
/// </list>
/// </para>
/// </summary>
public sealed class TransferMetricsAggregator : IMetricsAggregator
{
    // 1 秒ごとのバケット数（5 分 = 300 秒）
    private const int BucketCount = 300;

    // バケットごとの集計データ（配列インデックス = epochSec % BucketCount）
    private readonly long[] _epochSec = new long[BucketCount];
    private readonly int[] _requests = new int[BucketCount];
    private readonly int[] _successes = new int[BucketCount];
    private readonly double[] _totalLatencyMs = new double[BucketCount];
    private readonly int[] _rateLimits = new int[BucketCount];

    private readonly object _lock = new();

    /// <inheritdoc/>
    public void NotifyRequestSent()
    {
        lock (_lock)
        {
            var slot = GetOrClearSlot(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _requests[slot]++;
        }
    }

    /// <inheritdoc/>
    public void NotifySuccess(TimeSpan latency)
    {
        lock (_lock)
        {
            var slot = GetOrClearSlot(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _successes[slot]++;
            _totalLatencyMs[slot] += latency.TotalMilliseconds;
        }
    }

    /// <inheritdoc/>
    public void NotifyRateLimit(TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            var slot = GetOrClearSlot(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _rateLimits[slot]++;
        }
    }

    /// <inheritdoc/>
    public MetricsSnapshot GetSnapshot(TimeSpan window)
    {
        var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // BucketCount でクランプし、O(300) 上限を保証する（300 秒超の窓を指定しても O(windowSec) にならない）
        var windowSec = Math.Min((long)Math.Ceiling(window.TotalSeconds), BucketCount);
        var cutoffEpoch = nowEpoch - windowSec;

        int requestCount = 0, successCount = 0, rateLimitCount = 0;
        double totalLatencyMs = 0;

        lock (_lock)
        {
            for (var sec = cutoffEpoch + 1; sec <= nowEpoch; sec++)
            {
                var slot = (int)(sec % BucketCount);
                if (_epochSec[slot] != sec) continue; // このスロットは別の秒（空か古い）

                requestCount += _requests[slot];
                successCount += _successes[slot];
                rateLimitCount += _rateLimits[slot];
                totalLatencyMs += _totalLatencyMs[slot];
            }
        }

        var totalRequests = requestCount + rateLimitCount;
        var rps = windowSec > 0 ? (double)requestCount / windowSec : 0;
        var rate429 = totalRequests > 0 ? (double)rateLimitCount / totalRequests : 0;
        var avgLatencyMs = successCount > 0 ? totalLatencyMs / successCount : 0;

        return new MetricsSnapshot(rps, rate429, avgLatencyMs, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 指定の epoch 秒に対応するスロットインデックスを返す。
    /// スロットが別の秒のデータを保持している場合は初期化してから返す。
    /// lock 内で呼び出すこと。
    /// </summary>
    private int GetOrClearSlot(long epochSec)
    {
        var slot = (int)(epochSec % BucketCount);
        if (_epochSec[slot] != epochSec)
        {
            _epochSec[slot] = epochSec;
            _requests[slot] = 0;
            _successes[slot] = 0;
            _totalLatencyMs[slot] = 0;
            _rateLimits[slot] = 0;
        }
        return slot;
    }
}
