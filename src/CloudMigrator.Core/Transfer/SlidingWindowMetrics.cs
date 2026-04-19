using System.Diagnostics;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// v0.6.0 AIMD フィードバック制御向けのスライディングウィンドウ指標収集実装（#161）。
/// <para>
/// イベントを <see cref="Queue{T}"/> に時系列で蓄積し、<see cref="GetSnapshot"/> 呼び出し時に
/// ウィンドウ外を evict する遅延評価方式。時間モードは <see cref="Stopwatch.GetTimestamp"/> に
/// よる monotonic clock 基準で判定し、NTP 補正や手動時刻変更の影響を受けない。
/// </para>
/// <para>
/// メモリ安全: 時間モードでも安全上限 <see cref="SafetyCapDefault"/> 件で evict するため、
/// 異常なイベントレートでも Queue が無制限成長しない。
/// </para>
/// </summary>
public sealed class SlidingWindowMetrics : ISlidingWindowMetrics
{
    /// <summary>時間モードでの安全上限（件）。この値を超えると古いイベントから強制 evict する。</summary>
    public const int SafetyCapDefault = 100_000;

    private readonly SlidingWindowMode _mode;
    private readonly long _windowTicks;     // 時間モード用: windowSec × Stopwatch.Frequency
    private readonly int _maxCount;         // 件数モード用
    private readonly int _minSamples;
    private readonly int _safetyCap;
    private readonly object _lock = new();
    private readonly Queue<MetricEvent> _events = new();

    /// <summary>
    /// スライディングウィンドウ集計器を初期化する。
    /// </summary>
    /// <param name="mode">評価モード（時間 / 件数）。</param>
    /// <param name="windowSec">
    /// 時間モード時のウィンドウ幅（秒、1 以上）。
    /// 件数モードでは未使用だが、1 以上でなければ例外が発生する（設定バインド時の誤値を早期検出するため）。
    /// </param>
    /// <param name="maxCount">
    /// 件数モード時の最大イベント件数（1 以上）。
    /// <c>NotifyRequestSent</c> / <c>NotifySuccess</c> / <c>NotifyRateLimit</c> はそれぞれ別イベントとして計上されるため、
    /// 「直近 N リクエスト」ではなく「直近 N イベント」である点に注意。
    /// 件数モードでは <paramref name="maxCount"/> が実質の safetyCap を兼ねる。
    /// 時間モードでは未使用だが、1 以上でなければ例外が発生する。
    /// </param>
    /// <param name="minSamples">
    /// 有効判定に必要な最低サンプル数（1 以上）。
    /// <c>SampleCount &lt; minSamples</c> の場合 <see cref="SlidingWindowSnapshot.HasMinSamples"/> が <c>false</c> になる。
    /// </param>
    /// <param name="safetyCap">時間モードでの安全上限（<paramref name="minSamples"/> 以上）。</param>
    public SlidingWindowMetrics(
        SlidingWindowMode mode = SlidingWindowMode.Time,
        int windowSec = 30,
        int maxCount = 1000,
        int minSamples = 10,
        int safetyCap = SafetyCapDefault)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSec, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minSamples, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(safetyCap, minSamples);

        _mode = mode;
        _windowTicks = windowSec * Stopwatch.Frequency;
        _maxCount = maxCount;
        _minSamples = minSamples;
        _safetyCap = safetyCap;
    }

    /// <summary>現在の評価モード。</summary>
    public SlidingWindowMode Mode => _mode;

    /// <summary>最低サンプル数。<see cref="SlidingWindowSnapshot.HasMinSamples"/> の判定閾値。</summary>
    public int MinSamples => _minSamples;

    /// <inheritdoc/>
    public void NotifyRequestSent() =>
        Enqueue(new MetricEvent(Stopwatch.GetTimestamp(), EventKind.RequestSent, 0, 0));

    /// <inheritdoc/>
    public void NotifySuccess(TimeSpan latency, long bytes = 0) =>
        Enqueue(new MetricEvent(Stopwatch.GetTimestamp(), EventKind.Success, latency.TotalMilliseconds, bytes));

    /// <inheritdoc/>
    public void NotifyRateLimit(TimeSpan? retryAfter) =>
        // retryAfter は現時点では未使用（集計対象外）。#162 AIMD クールダウン計算での活用を予定。
        Enqueue(new MetricEvent(Stopwatch.GetTimestamp(), EventKind.RateLimit, 0, 0));

    /// <inheritdoc/>
    public SlidingWindowSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            Evict();

            int requestCount = 0, successCount = 0, rateLimitCount = 0;
            long totalBytes = 0;
            long oldestSuccessTicks = 0, newestSuccessTicks = 0;
            // P95 用に成功レイテンシ列を抽出。allocation は制御ループ（1 秒周期）で十分許容範囲
            var latencies = new List<double>(_events.Count);

            foreach (var e in _events)
            {
                switch (e.Kind)
                {
                    case EventKind.RequestSent:
                        requestCount++;
                        break;
                    case EventKind.Success:
                        successCount++;
                        latencies.Add(e.LatencyMs);
                        totalBytes += e.Bytes;
                        if (oldestSuccessTicks == 0 || e.TimestampTicks < oldestSuccessTicks)
                            oldestSuccessTicks = e.TimestampTicks;
                        if (e.TimestampTicks > newestSuccessTicks)
                            newestSuccessTicks = e.TimestampTicks;
                        break;
                    case EventKind.RateLimit:
                        rateLimitCount++;
                        break;
                }
            }

            // SampleCount: 基本は requestCount（NotifyRequestSent の呼び出し回数）。
            // ただし以下のケースでは success+rateLimit を下限にして 0 除算を避ける:
            //   (a) 呼び出し側が NotifyRequestSent を省略した
            //   (b) 件数モード evict で RequestSent イベントが先に落ち Success が残った（部分サンプル）
            // なお、NotifyRequestSent 後に応答なく evict された in-flight リクエストは
            // 分母に含まれるため、Rate429/SuccessRate は実態より低く見える場合がある。
            var sampleCount = Math.Max(requestCount, successCount + rateLimitCount);

            var rate429 = sampleCount > 0 ? (double)rateLimitCount / sampleCount : 0.0;
            var successRate = sampleCount > 0 ? (double)successCount / sampleCount : 0.0;
            var avgLatency = latencies.Count > 0 ? Average(latencies) : 0.0;
            var p95Latency = latencies.Count > 0 ? Percentile(latencies, 0.95) : 0.0;

            // ウィンドウ秒数: 時間モードでは設定値、件数モードでは最古〜最新成功イベントの実時間幅。
            // 0 件 / 1 件のときに 0 除算しないよう最低 1 秒で下限する。
            double windowSeconds;
            if (_mode == SlidingWindowMode.Time)
            {
                windowSeconds = (double)_windowTicks / Stopwatch.Frequency;
            }
            else
            {
                var spanTicks = newestSuccessTicks - oldestSuccessTicks;
                windowSeconds = spanTicks > 0 ? (double)spanTicks / Stopwatch.Frequency : 1.0;
            }
            if (windowSeconds < 1.0) windowSeconds = 1.0;

            var filesPerSec = successCount > 0 ? successCount / windowSeconds : 0.0;
            var bytesPerSec = successCount > 0 ? totalBytes / windowSeconds : 0.0;

            return new SlidingWindowSnapshot(
                SampleCount: sampleCount,
                HasMinSamples: sampleCount >= _minSamples,
                Rate429: rate429,
                SuccessRate: successRate,
                AvgLatencyMs: avgLatency,
                P95LatencyMs: p95Latency,
                FilesPerSec: filesPerSec,
                BytesPerSec: bytesPerSec,
                WindowSeconds: windowSeconds,
                Timestamp: DateTimeOffset.UtcNow);
        }
    }

    private void Enqueue(MetricEvent e)
    {
        lock (_lock)
        {
            _events.Enqueue(e);
            Evict();
        }
    }

    /// <summary>
    /// モードに応じてウィンドウ外 / 超過イベントを evict する（<see cref="_lock"/> 取得中に呼び出すこと）。
    /// 時間モードでも <see cref="_safetyCap"/> による上限 evict を適用する。
    /// </summary>
    private void Evict()
    {
        if (_mode == SlidingWindowMode.Count)
        {
            // 件数モードでは _maxCount が実質の safetyCap を兼ねる（別途 _safetyCap は適用しない）。
            // _maxCount は「イベント件数」上限（リクエスト数ではない）。
            while (_events.Count > _maxCount)
                _events.Dequeue();
            return;
        }

        // 時間モード: monotonic clock で windowTicks 以前を evict
        var cutoffTicks = Stopwatch.GetTimestamp() - _windowTicks;
        while (_events.TryPeek(out var head) && head.TimestampTicks < cutoffTicks)
            _events.Dequeue();

        // 安全上限
        while (_events.Count > _safetyCap)
            _events.Dequeue();
    }

    /// <summary>
    /// ソート済み・未ソートのどちらでも扱える P95 計算。
    /// サイズ数千件まで想定なので <see cref="List{T}.Sort()"/> + 線形補間で十分。
    /// </summary>
    internal static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0.0;
        if (values.Count == 1) return values[0];

        // 破壊しないようコピーしてからソート
        var sorted = new double[values.Count];
        values.CopyTo(sorted);
        Array.Sort(sorted);

        // 線形補間（NIST Method 3 / Excel PERCENTILE.INC と同等）
        var rank = percentile * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sorted[lo];
        var frac = rank - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }

    private static double Average(List<double> values)
    {
        // List<double>.Average() は LINQ 依存。本クラスのホットパスは制御ループ（1Hz）なので
        // 影響は軽微だが、allocation 最小化のためループで計算する。
        double sum = 0;
        for (int i = 0; i < values.Count; i++) sum += values[i];
        return sum / values.Count;
    }

    private enum EventKind : byte
    {
        RequestSent,
        Success,
        RateLimit,
    }

    private readonly record struct MetricEvent(long TimestampTicks, EventKind Kind, double LatencyMs, long Bytes);
}
