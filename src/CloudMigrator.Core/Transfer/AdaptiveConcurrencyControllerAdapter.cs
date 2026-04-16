namespace CloudMigrator.Core.Transfer;

/// <summary>
/// 旧 <see cref="AdaptiveConcurrencyController"/> を <see cref="ITransferRateController"/> として公開する互換 Adapter。
/// <para>
/// v0.5.0 では新旧コントローラーを並走させるために使用する。
/// v0.6.0 以降での <see cref="AdaptiveConcurrencyController"/> 削除時に本クラスも削除する。
/// </para>
/// </summary>
[Obsolete("AdaptiveConcurrencyController の互換 Adapter です。v0.6.0 で削除予定。ITransferRateController を直接使用してください。")]
public sealed class AdaptiveConcurrencyControllerAdapter : ITransferRateController, IDisposable
{
    private readonly AdaptiveConcurrencyController _inner;
    // インフライト = 実行中 + Retry 待ち（インメモリカウンターで管理）
    private int _activeCount;
    private int _retryWaitingCount;

    /// <summary>
    /// 旧コントローラーをラップする Adapter を初期化する。
    /// </summary>
    /// <param name="inner">ラップ対象の <see cref="AdaptiveConcurrencyController"/>。</param>
    public AdaptiveConcurrencyControllerAdapter(AdaptiveConcurrencyController inner)
    {
        _inner = inner;
    }

    /// <inheritdoc/>
    public Task AcquireAsync(CancellationToken ct) => _inner.AcquireAsync(ct);

    /// <inheritdoc/>
    public void Release() => _inner.Release();

    /// <inheritdoc/>
    public void NotifyRequestSent()
    {
        Interlocked.Increment(ref _activeCount);
    }

    /// <inheritdoc/>
    public void NotifySuccess(TimeSpan latency)
    {
        Interlocked.Decrement(ref _activeCount);
        _inner.NotifySuccess();
    }

    /// <inheritdoc/>
    public void NotifyRateLimit(TimeSpan? retryAfter)
    {
        Interlocked.Decrement(ref _activeCount);
        _inner.NotifyRateLimit(retryAfter);
    }

    /// <inheritdoc/>
    public void NotifyRetryScheduled(TimeSpan retryAfter)
    {
        Interlocked.Increment(ref _retryWaitingCount);
    }

    /// <inheritdoc/>
    public void NotifyRetryCompleted()
    {
        Interlocked.Decrement(ref _retryWaitingCount);
    }

    /// <inheritdoc/>
    public int CurrentInFlight =>
        Math.Max(0, Volatile.Read(ref _activeCount)) +
        Math.Max(0, Volatile.Read(ref _retryWaitingCount));

    /// <summary>旧コントローラーの現在並列度を返す（互換用）。</summary>
    public double CurrentRateLimit => _inner.CurrentDegree;

    /// <inheritdoc/>
    public void Dispose() => _inner.Dispose();
}
