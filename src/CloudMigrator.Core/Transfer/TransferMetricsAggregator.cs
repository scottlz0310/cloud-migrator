namespace CloudMigrator.Core.Transfer;

/// <summary>
/// リングバッファで転送イベントを保持し、任意の時間窓で集計する Aggregator 層コンポーネント。
/// <para>
/// 三層分離原則 – Aggregator 層の実装:
/// <list type="bullet">
///   <item>イベントを受け取りインメモリで集計する（DB アクセスなし）</item>
///   <item><see cref="GetSnapshot"/> で任意の時間窓の統計を返す</item>
///   <item><see langword="lock"/> でスレッドセーフに保護されている</item>
/// </list>
/// </para>
/// </summary>
public sealed class TransferMetricsAggregator : IMetricsAggregator
{
    private enum EventType { Request, Success, RateLimit }

    private readonly record struct TimedEvent(DateTimeOffset Timestamp, EventType Type, double Value);

    // イベント保持の最大期間（これより古いイベントは切り捨て）
    private static readonly TimeSpan MaxRetentionWindow = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly Queue<TimedEvent> _events = new();

    /// <inheritdoc/>
    public void NotifyRequestSent()
    {
        lock (_lock)
        {
            _events.Enqueue(new TimedEvent(DateTimeOffset.UtcNow, EventType.Request, 0));
            Prune();
        }
    }

    /// <inheritdoc/>
    public void NotifySuccess(TimeSpan latency)
    {
        lock (_lock)
        {
            _events.Enqueue(new TimedEvent(DateTimeOffset.UtcNow, EventType.Success, latency.TotalMilliseconds));
            Prune();
        }
    }

    /// <inheritdoc/>
    public void NotifyRateLimit(TimeSpan? retryAfter)
    {
        lock (_lock)
        {
            _events.Enqueue(new TimedEvent(DateTimeOffset.UtcNow, EventType.RateLimit, 0));
            Prune();
        }
    }

    /// <inheritdoc/>
    public MetricsSnapshot GetSnapshot(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        int requestCount = 0;
        int successCount = 0;
        int rateLimitCount = 0;
        double totalLatencyMs = 0;

        lock (_lock)
        {
            foreach (var ev in _events)
            {
                if (ev.Timestamp < cutoff) continue;
                switch (ev.Type)
                {
                    case EventType.Request: requestCount++; break;
                    case EventType.Success:
                        successCount++;
                        totalLatencyMs += ev.Value;
                        break;
                    case EventType.RateLimit: rateLimitCount++; break;
                }
            }
        }

        var totalRequests = requestCount + rateLimitCount;
        var windowSec = window.TotalSeconds;
        var rps = windowSec > 0 ? requestCount / windowSec : 0;
        var rate429 = totalRequests > 0 ? (double)rateLimitCount / totalRequests : 0;
        var avgLatencyMs = successCount > 0 ? totalLatencyMs / successCount : 0;

        return new MetricsSnapshot(rps, rate429, avgLatencyMs, DateTimeOffset.UtcNow);
    }

    /// <summary>MaxRetentionWindow より古いイベントをキューから削除する（lock 内で呼び出すこと）。</summary>
    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - MaxRetentionWindow;
        while (_events.Count > 0 && _events.Peek().Timestamp < cutoff)
            _events.Dequeue();
    }
}
