using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// Token Bucket + AIMD アルゴリズムによるレートリミッター。
/// <para>
/// 並列数ではなく <b>file/sec</b>（アップロードファイル数/秒）を制御対象とすることで、
/// Graph API の sliding-window レート制限を安定的に回避する（FR-14 拡張）。
/// </para>
/// <list type="bullet">
///   <item>バックグラウンドループが <see cref="CurrentRate"/> file/sec でトークンを補充する（20 Hz）</item>
///   <item>ワーカーは <see cref="AcquireAsync"/> でトークンを取得してから API を呼び出す</item>
///   <item>成功時: <see cref="NotifySuccess"/> → <b>increaseIntervalSec 秒に 1 回</b> rate += increaseStep（時間ベース加算増加 / Additive Increase）</item>
///   <item>429/503 時: <see cref="NotifyRateLimit"/> → rate *= decreaseFactor（乗算減少 / Multiplicative Decrease）+ Retry-After 待機</item>
/// </list>
/// <para>
/// 成功ごとにレートを増加させると Graph API の sliding-window（30〜60 秒）をすぐに踏むため、
/// <b>increaseIntervalSec 秒以上 429 が発生しなかった場合のみ増加</b>する設計にしている。
/// </para>
/// <para>
/// アーキテクチャ: Queue → WorkerPool → <see cref="TokenBucketRateLimiter"/> → Graph API
/// </para>
/// </summary>
public sealed class TokenBucketRateLimiter : IDisposable
{
    private readonly SemaphoreSlim _tokens;     // ワーカー待機ゲート（バケット）
    private readonly int _burstCapacity;
    private readonly double _minRate;
    private readonly double _maxRate;
    private readonly double _increaseStep;
    private readonly double _decreaseFactor;
    private readonly TimeSpan _increaseInterval;            // AIMD 増加の最小間隔
    private readonly ILogger<TokenBucketRateLimiter> _logger;

    // lock(_rateLock) で保護するフィールド
    private readonly object _rateLock = new();
    private double _rate;
    private double _fractional;                             // 未満トークンの端数（補充精度向上用）
    private DateTime _blockedUntil = DateTime.MinValue;     // Retry-After による停止終端（UTC）
    private DateTime _nextIncreaseAt = DateTime.MinValue;   // 次に増加可能な時刻（UTC）

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _refillTask;

    /// <summary>
    /// レートリミッターを初期化する。
    /// </summary>
    /// <param name="initialRate">初期レート（file/sec）。<paramref name="minRate"/>～<paramref name="maxRate"/> にクランプ</param>
    /// <param name="minRate">レートの下限（file/sec）。正の値必須</param>
    /// <param name="maxRate">レートの上限（file/sec）。<paramref name="minRate"/> 以上必須</param>
    /// <param name="burstCapacity">バースト許容量（最大トークン数）。バケットが満タンのとき瞬間的に放出できる上限</param>
    /// <param name="increaseStep">AIMD 増加量（file/sec）。<paramref name="increaseIntervalSec"/> 秒ごとに加算される</param>
    /// <param name="decreaseFactor">429 時の乗算減少係数。0 &lt; factor &lt; 1 必須（例: 0.7 でレートを 70% に）</param>
    /// <param name="increaseIntervalSec">AIMD 増加の最小間隔（秒）。この間隔で 429 が発生しなかった場合のみレートを増加する。デフォルト 5.0</param>
    /// <param name="logger">ロガー</param>
    public TokenBucketRateLimiter(
        double initialRate,
        double minRate,
        double maxRate,
        int burstCapacity,
        double increaseStep,
        double decreaseFactor,
        ILogger<TokenBucketRateLimiter> logger,
        double increaseIntervalSec = 5.0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(minRate, 0.0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRate, minRate);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(burstCapacity, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(increaseStep, 0.0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(increaseIntervalSec, 0.0);
        if (decreaseFactor is <= 0.0 or >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(decreaseFactor),
                "decreaseFactor は 0 より大きく 1 より小さい値でなければなりません。");

        _minRate = minRate;
        _maxRate = maxRate;
        _rate = Math.Clamp(initialRate, minRate, maxRate);
        _burstCapacity = burstCapacity;
        _increaseStep = increaseStep;
        _decreaseFactor = decreaseFactor;
        _increaseInterval = TimeSpan.FromSeconds(increaseIntervalSec);
        _logger = logger;

        // バーストキャパシティ分のトークンを初期供給（起動直後のバーストを許容）
        _tokens = new SemaphoreSlim(burstCapacity, burstCapacity);
        _refillTask = RefillLoopAsync(_cts.Token);
    }

    // ─────────────────────────────────────────────────────────────
    // パブリックプロパティ
    // ─────────────────────────────────────────────────────────────

    /// <summary>現在の転送レート（file/sec）。429 で減少し、成功で増加する。</summary>
    public double CurrentRate { get { lock (_rateLock) return _rate; } }

    /// <summary>レートの上限（file/sec）。</summary>
    public double MaxRate => _maxRate;

    /// <summary>レートの下限（file/sec）。</summary>
    public double MinRate => _minRate;

    /// <summary>バースト許容量（最大トークン数）。</summary>
    public int BurstCapacity => _burstCapacity;

    // ─────────────────────────────────────────────────────────────
    // トークン取得（ワーカーから呼び出す）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 1 トークンを非同期に取得する。
    /// トークンがない場合は次の補充まで待機する（ブロッキングではなく非同期待機）。
    /// </summary>
    /// <param name="cancellationToken">キャンセルトークン</param>
    public Task AcquireAsync(CancellationToken cancellationToken) =>
        _tokens.WaitAsync(cancellationToken);

    // ─────────────────────────────────────────────────────────────
    // フィードバック通知（ワーカー / HTTP ハンドラーから呼び出す）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 転送成功を通知する。AIMD 加算増加: <c>increaseIntervalSec</c> 秒以上 429 が発生しなかった場合のみ
    /// rate += increaseStep。成功ごとにレートを増加させると Graph API の sliding-window を踏みやすいため、
    /// 時間インターバルで増加を制御する。
    /// </summary>
    public void NotifySuccess()
    {
        double prev, current;
        bool increased;
        lock (_rateLock)
        {
            var now = DateTime.UtcNow;
            // インターバル未満は増加しない（毎成功増加による急激なレート上昇を防ぐ）
            if (now < _nextIncreaseAt)
                return;

            prev = _rate;
            _rate = Math.Min(_rate + _increaseStep, _maxRate);
            current = _rate;
            increased = current != prev;
            if (increased)
                _nextIncreaseAt = now + _increaseInterval;
        }
        if (increased)
            _logger.LogDebug(
                "レート増加: {Prev:F2} → {Current:F2}/{Max:F1} file/sec (次回増加可能: {Interval} 秒後)",
                prev, current, _maxRate, _increaseInterval.TotalSeconds);
    }

    /// <summary>
    /// レート制限（429/503）を通知する。AIMD 乗算減少: rate = Max(rate * decreaseFactor, minRate)。
    /// Retry-After が指定された場合、その期間中はトークン補充を停止する。
    /// </summary>
    /// <param name="retryAfter">サーバーから返された Retry-After 値（null の場合は不明）</param>
    public void NotifyRateLimit(TimeSpan? retryAfter)
    {
        double prev, current;
        bool shouldDrain;
        lock (_rateLock)
        {
            var now = DateTime.UtcNow;
            prev = _rate;
            _rate = Math.Max(_rate * _decreaseFactor, _minRate);
            current = _rate;
            shouldDrain = false;
            if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
            {
                _blockedUntil = now + retryAfter.Value;
                shouldDrain = true;
            }
            // 429 発生時は増加インターバルをリセット（回復後すぐ増加しない）
            _nextIncreaseAt = now + _increaseInterval;
        }
        // Retry-After が指定された場合、バケット内の残留トークンをドレインして確実にブロックする
        if (shouldDrain)
        {
            while (_tokens.Wait(0)) { }
        }
        _logger.LogWarning(
            "レート削減: {Prev:F1} → {Current:F1}/{Max:F1} file/sec (Retry-After: {RetryAfterSec} 秒)",
            prev, current, _maxRate,
            retryAfter.HasValue ? retryAfter.Value.TotalSeconds.ToString("F0") : "なし");
    }

    // ─────────────────────────────────────────────────────────────
    // バックグラウンド補充ループ
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 20 Hz（50ms ごと）にトークンバケットを補充するバックグラウンドループ。
    /// Retry-After 期間中はトークン補充を停止し、新たなリクエストを抑制する。
    /// </summary>
    private async Task RefillLoopAsync(CancellationToken ct)
    {
        var tickInterval = TimeSpan.FromMilliseconds(50); // 20 Hz
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(tickInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            lock (_rateLock)
            {
                // Retry-After による停止期間中はトークン補充をスキップ
                if (DateTime.UtcNow < _blockedUntil)
                    continue;

                // rate × 経過時間（秒）分のトークンを端数込みで計算
                _fractional += _rate * tickInterval.TotalSeconds;
                var whole = (int)Math.Floor(_fractional);
                _fractional -= whole;

                // バーストキャパシティを超えないよう調整して補充
                var space = _burstCapacity - _tokens.CurrentCount;
                var actual = Math.Min(whole, Math.Max(0, space));
                if (actual > 0)
                    _tokens.Release(actual);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // テスト用内部プロパティ（InternalsVisibleTo により unit test からアクセス可能）
    // ─────────────────────────────────────────────────────────────

    /// <summary>Retry-After 待機の終端時刻（UTC）。テスト用。</summary>
    internal DateTime BlockedUntil { get { lock (_rateLock) return _blockedUntil; } }

    /// <summary>次に AIMD 増加が可能な時刻（UTC）。テスト用。</summary>
    internal DateTime NextIncreaseAt { get { lock (_rateLock) return _nextIncreaseAt; } }

    // ─────────────────────────────────────────────────────────────
    // Dispose
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _cts.Cancel();
        // 補充ループが完全に終了するまで待機（Cancel 後は 50ms 以内に完了する）
        try { _refillTask.Wait(); } catch { /* キャンセル例外は無視 */ }
        _cts.Dispose();
        _tokens.Dispose();
    }
}
