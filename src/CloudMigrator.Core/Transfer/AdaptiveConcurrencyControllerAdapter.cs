namespace CloudMigrator.Core.Transfer;

/// <summary>
/// 旧 <see cref="AdaptiveConcurrencyController"/> を <see cref="ITransferRateController"/> として公開する互換 Adapter。
/// <para>
/// v0.5.0 では新旧コントローラーを並走させるために使用する。
/// v0.6.0 以降での <see cref="AdaptiveConcurrencyController"/> 削除時に本クラスも削除する。
/// </para>
/// <para>
/// 所有権: 本クラスは inner を<b>所有しない</b>。inner の Dispose は呼び出し元の責務。
/// </para>
/// </summary>
[Obsolete("AdaptiveConcurrencyController の互換 Adapter です。v0.6.0 で削除予定。ITransferRateController を直接使用してください。")]
public sealed class AdaptiveConcurrencyControllerAdapter : ITransferRateController
{
    private readonly AdaptiveConcurrencyController _inner;
    // インフライト = 実行中 + Retry 待ち（インメモリカウンターで管理）
    private int _activeCount;
    private int _retryWaitingCount;

    /// <summary>
    /// 旧コントローラーをラップする Adapter を初期化する。
    /// </summary>
    /// <param name="inner">ラップ対象の <see cref="AdaptiveConcurrencyController"/>。所有権は移譲しない。</param>
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
    public void NotifySuccess(TimeSpan latency, long bytes = 0)
    {
        // 旧コントローラーはバイト数を扱わないため bytes は無視する。
        DecrementIfPositive(ref _activeCount);
        _inner.NotifySuccess();
    }

    /// <inheritdoc/>
    public void NotifyCompleted(TimeSpan latency)
    {
        // インフライトカウンターを戻すが成功メトリクスには計上しない（キャンセル/失敗完了）
        DecrementIfPositive(ref _activeCount);
    }

    /// <inheritdoc/>
    public void NotifyRateLimit(TimeSpan? retryAfter)
    {
        DecrementIfPositive(ref _activeCount);
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
        DecrementIfPositive(ref _retryWaitingCount);
    }

    /// <summary>
    /// カウンターを 0 未満にならないようデクリメントする（CAS ループ）。
    /// </summary>
    private static void DecrementIfPositive(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0) return;
            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current) return;
        }
    }

    /// <inheritdoc/>
    public int CurrentInFlight =>
        Math.Max(0, Volatile.Read(ref _activeCount)) +
        Math.Max(0, Volatile.Read(ref _retryWaitingCount));

    /// <summary>旧コントローラーの現在並列度を返す（互換用）。</summary>
    public double CurrentRateLimit => _inner.CurrentDegree;
}
