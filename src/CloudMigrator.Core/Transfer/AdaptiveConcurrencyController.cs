using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// Graph API のレート制限（429/503）に応じて並列転送数を動的に調整するコントローラー。
/// <list type="bullet">
///   <item>429/503 を受けると並列度を 1 削減する（<see cref="MinDegree"/> まで）</item>
///   <item>連続成功が <see cref="SuccessThreshold"/> に達すると並列度を 1 回復する（<see cref="MaxDegree"/> まで）</item>
///   <item>内部で <see cref="SemaphoreSlim"/> を使用し、スロット取得 / 解放で並列数を制御する</item>
/// </list>
/// </summary>
public sealed class AdaptiveConcurrencyController : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly int _min;
    private readonly int _max;
    private readonly int _increaseStep;
    private readonly int _decreaseStep;
    private readonly int _decreaseTriggerCount;
    private readonly bool _halveOnRateLimit;
    private readonly ILogger<AdaptiveConcurrencyController> _logger;

    // lock(_syncRoot) で保護するフィールド
    private readonly object _syncRoot = new();
    private int _current;
    private int _absorbedActual;      // 実際にセマフォから吸収済みのスロット数
    private int _consecutiveSuccesses;
    private int _pendingDecreases;    // 減速トリガーカウンター

    // レート制限通知の累計回数（Interlocked でスレッドセーフに更新）
    private long _rateLimitCount;

    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>
    /// コントローラーを初期化する。
    /// </summary>
    /// <param name="initialDegree">開始時の並列度（<paramref name="maxDegree"/> と同じ値を推奨）</param>
    /// <param name="minDegree">並列度の下限</param>
    /// <param name="maxDegree">並列度の上限</param>
    /// <param name="successThreshold">並列度を回復するために必要な連続成功回数</param>
    /// <param name="logger">ロガー</param>
    /// <param name="increaseStep">1 回の回復で増加する並列度の幅。デフォルト 1</param>
    /// <param name="decreaseStep">1 回の減速イベントで減少する並列度の幅。デフォルト 1</param>
    /// <param name="decreaseTriggerCount">減速を発火するために必要な 429/503 の累積回数。デフォルト 1</param>
    public AdaptiveConcurrencyController(
        int initialDegree,
        int minDegree,
        int maxDegree,
        int successThreshold,
        ILogger<AdaptiveConcurrencyController> logger,
        int increaseStep = 1,
        int decreaseStep = 1,
        int decreaseTriggerCount = 1,
        bool halveOnRateLimit = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minDegree, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegree, minDegree);
        ArgumentOutOfRangeException.ThrowIfLessThan(successThreshold, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(increaseStep, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(decreaseStep, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(decreaseTriggerCount, 1);

        _min = minDegree;
        _max = maxDegree;
        _current = Math.Clamp(initialDegree, minDegree, maxDegree);
        SuccessThreshold = successThreshold;
        _increaseStep = increaseStep;
        _decreaseStep = decreaseStep;
        _decreaseTriggerCount = decreaseTriggerCount;
        _halveOnRateLimit = halveOnRateLimit;
        _logger = logger;

        // 初期並列度でセマフォを作成（maxDegree が上限）
        _semaphore = new SemaphoreSlim(_current, maxDegree);
    }

    // ─────────────────────────────────────────────────────────────
    // パブリックプロパティ
    // ─────────────────────────────────────────────────────────────

    /// <summary>現在の並列度。レート制限で減少し、連続成功で回復する。</summary>
    public int CurrentDegree { get { lock (_syncRoot) return _current; } }

    /// <summary>並列度の上限（設定値 MaxParallelTransfers）。</summary>
    public int MaxDegree => _max;

    /// <summary>並列度の下限。</summary>
    public int MinDegree => _min;

    /// <summary>並列度を回復するために必要な連続成功回数。</summary>
    public int SuccessThreshold { get; }

    /// <summary>1 回の回復で増加する並列度の幅。</summary>
    public int IncreaseStep => _increaseStep;

    /// <summary>1 回の減速イベントで減少する並列度の幅。</summary>
    public int DecreaseStep => _decreaseStep;

    /// <summary>減速を発火するために必要な 429/503 の累積回数。</summary>
    public int DecreaseTriggerCount => _decreaseTriggerCount;

    /// <summary>
    /// レート制限（429/503）の累計通知回数。
    /// <see cref="NotifyRateLimit"/> が呼ばれるたびにインクリメントされる。
    /// 内部リトライで成功した 429 も計上されるため、ダッシュボードの実態に近い値を示す。
    /// </summary>
    public long RateLimitCount => Interlocked.Read(ref _rateLimitCount);

    // ─────────────────────────────────────────────────────────────
    // スロット管理（TransferEngine から呼び出す）
    // ─────────────────────────────────────────────────────────────

    /// <summary>転送スロットを非同期に取得する。利用可能になるまで待機する。</summary>
    public Task AcquireAsync(CancellationToken cancellationToken) =>
        _semaphore.WaitAsync(cancellationToken);

    /// <summary>取得済みの転送スロットを解放する。</summary>
    public void Release() => _semaphore.Release();

    // ─────────────────────────────────────────────────────────────
    // 通知メソッド（RateLimitAwareHandler / TransferEngine から呼び出す）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// レート制限（429/503）を通知する。
    /// <see cref="DecreaseTriggerCount"/> 回累積した時点で並列度を <see cref="DecreaseStep"/> 削減し、
    /// 非同期にセマフォスロットを吸収する。
    /// </summary>
    /// <param name="retryAfter">サーバーから返された Retry-After 値（null の場合は不明）</param>
    public void NotifyRateLimit(TimeSpan? retryAfter)
    {
        Interlocked.Increment(ref _rateLimitCount);

        int prevDegree = -1, newDegree = -1, step = 0;
        lock (_syncRoot)
        {
            _consecutiveSuccesses = 0;

            // MinDegree 到達時はカウンターを増やさない（回復後の最初の通知で即減速しないようにする）
            if (_current > _min)
                _pendingDecreases++;

            if (_pendingDecreases >= _decreaseTriggerCount && _current > _min)
            {
                _pendingDecreases = 0;
                prevDegree = _current;
                _current = _halveOnRateLimit
                    ? Math.Max(_min, _current / 2)
                    : _current - Math.Min(_decreaseStep, _current - _min);
                step = prevDegree - _current;
                newDegree = _current;
            }
        }

        if (step == 0)
            return;

        _logger.LogWarning(
            "レート制限を検出。並列度を {Prev} → {Current}/{Max} に削減します (Retry-After: {RetryAfterSec} 秒)",
            prevDegree, newDegree, _max,
            retryAfter.HasValue ? retryAfter.Value.TotalSeconds.ToString("F0") : "なし");

        // 削減した分だけスロットを非同期に吸収する（1 つのバックグラウンドループで順次処理）
        _ = AbsorbSlotsAsync(step);
    }

    /// <summary>
    /// 転送成功を通知する。
    /// 連続成功数が <see cref="SuccessThreshold"/> に達した場合、
    /// ソフトスタート時の未使用ヘッドルームまたは吸収済みスロットを使って
    /// 並列度を <see cref="IncreaseStep"/> 回復する。
    /// </summary>
    public void NotifySuccess()
    {
        int prevDegree = -1, newDegree = -1, step = 0;
        lock (_syncRoot)
        {
            _consecutiveSuccesses++;
            if (_consecutiveSuccesses >= SuccessThreshold && _current < _max)
            {
                _consecutiveSuccesses = 0;
                step = Math.Min(_increaseStep, _max - _current);
                prevDegree = _current;
                _current += step;
                _absorbedActual -= Math.Min(step, _absorbedActual);
                newDegree = _current;
            }
        }

        if (step == 0)
            return;

        for (int i = 0; i < step; i++)
            _semaphore.Release(); // 吸収済みスロットを循環に戻す
        _logger.LogInformation(
            "連続成功 {Threshold} 回達成。並列度を {Prev} → {Current}/{Max} に回復します",
            SuccessThreshold, prevDegree, newDegree, _max);
    }

    // ─────────────────────────────────────────────────────────────
    // 内部実装
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// セマフォスロットを <paramref name="count"/> 個まとめて吸収する（解放しない）。
    /// 1 つのバックグラウンドタスクで順次処理することで、429 ストーム時のタスク増殖を防止する。
    /// </summary>
    private async Task AbsorbSlotsAsync(int count)
    {
        for (int i = 0; i < count; i++)
        {
            try
            {
                await _semaphore.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
                lock (_syncRoot) { _absorbedActual++; }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Dispose 時のキャンセル（OperationCanceledException）または
                // セマフォ破棄後の呼び出し（ObjectDisposedException）のいずれも無視する。
                // _current は既にデクリメント済みだが、Dispose 後は使用しないため補正不要。
                return;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // テスト用内部プロパティ（InternalsVisibleTo により unit test からアクセス可能）
    // ─────────────────────────────────────────────────────────────

    /// <summary>実際に吸収済みのスロット数（テスト用）。</summary>
    internal int AbsorbedSlotCount { get { lock (_syncRoot) return _absorbedActual; } }

    // ─────────────────────────────────────────────────────────────
    // Dispose
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _semaphore.Dispose();
    }
}
