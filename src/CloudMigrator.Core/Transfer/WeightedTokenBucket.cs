using System.Diagnostics;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// v0.6.0 スループット主制御用の重み付きトークンバケット（#160）。
/// <para>
/// v1 の <see cref="TokenBucketRateLimiter"/>（file/sec・単位トークン）に対して、
/// 本クラスは <b>tokens/sec</b> を制御対象とし、ファイルサイズに応じた重み付きコストを消費する。
/// AIMD 制御（#162）や並列数制御（#163）は別コンポーネントが担い、本クラスは純粋に
/// 「レート × バースト容量のトークンバケット」としての責務のみを持つ。
/// </para>
/// <para>
/// 補充は <see cref="Stopwatch.GetTimestamp"/> ベースの monotonic clock による実時間算出で、
/// 制御ループの周期揺らぎ（GC / スレッドスケジューリング）や wall-clock の逆行（NTP 補正）の
/// 影響を受けない。バックグラウンドスレッドは持たず、<see cref="AcquireAsync"/> 呼び出し時に遅延補充する。
/// </para>
/// </summary>
public sealed class WeightedTokenBucket
{
    private readonly object _lock = new();
    private double _rate;
    private double _maxBurst;
    private double _tokens;
    private long _lastRefillTicks;

    /// <summary>
    /// トークンバケットを初期化する。
    /// </summary>
    /// <param name="initialRate">初期補充レート（tokens/sec、0 より大きい値）。</param>
    /// <param name="maxBurst">バケット容量（最大蓄積トークン数、0 より大きい値）。</param>
    /// <param name="initialTokens">
    /// 起動直後のトークン残量。<c>null</c> の場合は <paramref name="maxBurst"/>（満タン）。
    /// 0〜<paramref name="maxBurst"/> の範囲にクランプされる。
    /// </param>
    public WeightedTokenBucket(double initialRate, double maxBurst, double? initialTokens = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialRate, 0.0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBurst, 0.0);

        _rate = initialRate;
        _maxBurst = maxBurst;
        _tokens = Math.Clamp(initialTokens ?? maxBurst, 0.0, maxBurst);
        _lastRefillTicks = Stopwatch.GetTimestamp();
    }

    /// <summary>現在の補充レート（tokens/sec）。</summary>
    public double CurrentRate { get { lock (_lock) return _rate; } }

    /// <summary>現在のバケット容量（最大蓄積トークン数）。</summary>
    public double MaxBurst { get { lock (_lock) return _maxBurst; } }

    /// <summary>
    /// 現在のトークン残量（遅延補充を適用した値）。
    /// ダッシュボード表示・テスト用のスナップショット。
    /// </summary>
    public double AvailableTokens
    {
        get
        {
            lock (_lock)
            {
                Refill();
                return _tokens;
            }
        }
    }

    /// <summary>
    /// 補充レートを更新する（AIMD フィードバックループから呼び出される、#162）。
    /// 更新前に残量を refill しておくことで、直前のレートでの蓄積が失われないようにする。
    /// </summary>
    /// <param name="rate">新しい補充レート（tokens/sec、0 より大きい値）。</param>
    public void SetRate(double rate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rate, 0.0);
        lock (_lock)
        {
            Refill();
            _rate = rate;
        }
    }

    /// <summary>
    /// バケット容量を更新する。現在のトークン残量は新しい容量でクランプする。
    /// </summary>
    /// <param name="maxBurst">新しいバケット容量（0 より大きい値）。</param>
    public void SetMaxBurst(double maxBurst)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxBurst, 0.0);
        lock (_lock)
        {
            Refill();
            _maxBurst = maxBurst;
            if (_tokens > _maxBurst) _tokens = _maxBurst;
        }
    }

    /// <summary>
    /// 指定コスト分のトークンを非同期に取得する。
    /// 残量不足の場合は補充されるまで sleep + retry で待機する（ビジーウェイトではない）。
    /// </summary>
    /// <param name="cost">消費トークン数（1 以上、<see cref="MaxBurst"/> 以下）。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cost"/> が 1 未満、または <see cref="MaxBurst"/> を超える場合
    /// （後者はバケットが常に満たせず無限待機となるため、呼び出し側のバグとして拒否する）。
    /// </exception>
    public async Task AcquireAsync(int cost, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cost, 1);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan wait;
            lock (_lock)
            {
                if (cost > _maxBurst)
                    throw new ArgumentOutOfRangeException(
                        nameof(cost),
                        $"cost ({cost}) が maxBurst ({_maxBurst}) を超えています。バケットが満タンでも取得できず無限待機となるため、呼び出し側で cost を maxBurst 以下に抑えるか、maxBurst を拡大してください。");

                Refill();
                if (_tokens >= cost)
                {
                    _tokens -= cost;
                    return;
                }

                var deficit = cost - _tokens;
                var waitSec = deficit / _rate;
                wait = TimeSpan.FromSeconds(Math.Min(waitSec, 1.0));
                if (wait < TimeSpan.FromMilliseconds(1))
                    wait = TimeSpan.FromMilliseconds(1);
            }

            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 実時間経過分のトークンを補充する（<see cref="_lock"/> 取得中に呼び出すこと）。
    /// monotonic clock ベースで計算し、<see cref="_maxBurst"/> でクランプする。
    /// </summary>
    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSec = (now - _lastRefillTicks) / (double)Stopwatch.Frequency;
        _lastRefillTicks = now;
        if (elapsedSec <= 0.0) return;

        var refilled = _tokens + _rate * elapsedSec;
        _tokens = refilled > _maxBurst ? _maxBurst : refilled;
    }
}
