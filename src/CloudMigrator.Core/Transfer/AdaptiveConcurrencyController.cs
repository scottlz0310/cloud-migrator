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
    private readonly ILogger<AdaptiveConcurrencyController> _logger;

    // lock(_syncRoot) で保護するフィールド
    private readonly object _syncRoot = new();
    private int _current;
    private int _absorbedActual;      // 実際にセマフォから吸収済みのスロット数
    private int _consecutiveSuccesses;

    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>
    /// コントローラーを初期化する。
    /// </summary>
    /// <param name="initialDegree">開始時の並列度（<paramref name="maxDegree"/> と同じ値を推奨）</param>
    /// <param name="minDegree">並列度の下限</param>
    /// <param name="maxDegree">並列度の上限</param>
    /// <param name="successThreshold">並列度を 1 回復するために必要な連続成功回数</param>
    /// <param name="logger">ロガー</param>
    public AdaptiveConcurrencyController(
        int initialDegree,
        int minDegree,
        int maxDegree,
        int successThreshold,
        ILogger<AdaptiveConcurrencyController> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minDegree, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegree, minDegree);
        ArgumentOutOfRangeException.ThrowIfLessThan(successThreshold, 1);

        _min = minDegree;
        _max = maxDegree;
        _current = Math.Clamp(initialDegree, minDegree, maxDegree);
        SuccessThreshold = successThreshold;
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

    /// <summary>並列度を 1 回復するために必要な連続成功回数。</summary>
    public int SuccessThreshold { get; }

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
    /// 現在の並列度が <see cref="MinDegree"/> より大きければ 1 削減し、
    /// 非同期にセマフォスロットを 1 つ吸収する。
    /// </summary>
    /// <param name="retryAfter">サーバーから返された Retry-After 値（null の場合は不明）</param>
    public void NotifyRateLimit(TimeSpan? retryAfter)
    {
        bool shouldAbsorb;
        int newDegree;
        lock (_syncRoot)
        {
            _consecutiveSuccesses = 0;
            shouldAbsorb = _current > _min;
            if (shouldAbsorb)
                _current--;
            newDegree = _current;
        }

        if (!shouldAbsorb)
            return;

        _logger.LogWarning(
            "レート制限を検出。並列度を {Current}/{Max} に削減します (Retry-After: {RetryAfterSec} 秒)",
            newDegree, _max,
            retryAfter.HasValue ? retryAfter.Value.TotalSeconds.ToString("F0") : "なし");

        // 次に解放されるスロットを非同期に吸収する（fire-and-forget）
        _ = AbsorbSlotAsync();
    }

    /// <summary>
    /// 転送成功を通知する。
    /// 連続成功数が <see cref="SuccessThreshold"/> に達し、吸収済みスロットが存在する場合に
    /// 並列度を 1 回復する。
    /// </summary>
    public void NotifySuccess()
    {
        bool doRelease = false;
        int newDegree = -1;
        lock (_syncRoot)
        {
            _consecutiveSuccesses++;
            if (_consecutiveSuccesses >= SuccessThreshold
                && _current < _max
                && _absorbedActual > 0)
            {
                _consecutiveSuccesses = 0;
                _current++;
                _absorbedActual--;
                doRelease = true;
                newDegree = _current;
            }
        }

        if (!doRelease)
            return;

        _semaphore.Release(); // 吸収済みスロットを 1 つ循環に戻す
        _logger.LogInformation(
            "連続成功 {Threshold} 回達成。並列度を {Current}/{Max} に回復します",
            SuccessThreshold, newDegree, _max);
    }

    // ─────────────────────────────────────────────────────────────
    // 内部実装
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// セマフォスロットを 1 つ吸収する（解放しない）。
    /// 次にスロットが解放されたタイミングで取得し、循環から除外する。
    /// </summary>
    private async Task AbsorbSlotAsync()
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
