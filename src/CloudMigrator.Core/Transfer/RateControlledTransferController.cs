using CloudMigrator.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// Graph API レートベース転送制御エンジン（v0.5.0）。
/// <para>
/// 設計方針（三層分離原則 – Controller 層）:
/// <list type="bullet">
///   <item>意思決定のみを担当する。<see cref="IMetricsAggregator"/> の Snapshot を参照してレートを決定する。</item>
///   <item>DB への直接依存はない。メトリクス書き込みは <see cref="MetricsBuffer"/> を経由する。</item>
///   <item>状態管理はインメモリカウンターのみ（イベント駆動）。DB クエリによる状態補完は行わない。</item>
/// </list>
/// </para>
/// <para>
/// 制御ループ（1 秒周期）:
/// <list type="number">
///   <item>短期/中期 Snapshot 取得</item>
///   <item>ヒステリシス制御（緊急減速 / 緩減速 / 加速の 3 段階）</item>
///   <item>可変減衰によるレート調整</item>
///   <item>インフライトチェック</item>
///   <item>トークン補充（dispatch 実行）</item>
/// </list>
/// </para>
/// </summary>
public sealed class RateControlledTransferController : ITransferRateController, IAsyncDisposable
{
    private readonly IMetricsAggregator _aggregator;
    private readonly RateControlSettings _settings;
    private readonly ILogger<RateControlledTransferController> _logger;
    private readonly MetricsBuffer _metricsBuffer;

    // ── 制御状態（volatile / Interlocked でスレッドセーフに保護）──
    private double _currentRate;    // req/sec（lock で保護）
    private int _activeCount;       // 実行中リクエスト数
    private int _retryWaitingCount; // Retry 待ちリクエスト数
    private readonly object _rateLock = new();

    // ── トークンバケット（レート制御・dispatch ゲート）──
    // 1 秒ごとに _currentRate 個のトークンを補充する
    private readonly SemaphoreSlim _semaphore;

    // ── 制御ループ ──
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _controlLoop;
    private int _disposed;

    /// <summary>
    /// コントローラーを初期化する。
    /// </summary>
    /// <param name="aggregator">メトリクス集計コンポーネント（Aggregator 層）。</param>
    /// <param name="settings">制御パラメーター設定。</param>
    /// <param name="metricsBuffer">DB 書き込み用非同期バッファ（Controller/Aggregator 境界）。</param>
    /// <param name="logger">ロガー。</param>
    public RateControlledTransferController(
        IMetricsAggregator aggregator,
        RateControlSettings settings,
        MetricsBuffer metricsBuffer,
        ILogger<RateControlledTransferController> logger)
    {
        _aggregator = aggregator;
        _settings = settings;
        _metricsBuffer = metricsBuffer;
        _logger = logger;

        if (_settings.MinRatePerSec < 1.0)
            throw new ArgumentOutOfRangeException(nameof(settings),
                $"MinRatePerSec は 1.0 以上にしてください（現在値: {_settings.MinRatePerSec}）。" +
                "1.0 未満だと RefillTokens が永続的に 0 トークンを補充し AcquireAsync が進行不能になります。");

        var initialRate = Math.Max(_settings.MinRatePerSec, _settings.InitialRatePerSec);
        lock (_rateLock) { _currentRate = initialRate; }

        // 並列上限（maxConcurrency）で SemaphoreSlim を初期化
        // 初期スロット数 = min(initialRate の切り上げ, maxConcurrency)
        var initialSlots = Math.Min(
            (int)Math.Ceiling(initialRate),
            _settings.MaxConcurrency);
        _semaphore = new SemaphoreSlim(initialSlots, _settings.MaxConcurrency);

        _controlLoop = Task.Run(ControlLoopAsync);
    }

    // ─────────────────────────────────────────────────────────────
    // ITransferRateController 実装
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task AcquireAsync(CancellationToken ct) => _semaphore.WaitAsync(ct);

    /// <summary>
    /// レート制御専用 SemaphoreSlim では、ワーカーはトークンを返却しない。
    /// トークンの補充は制御ループ（<see cref="RefillTokens"/>）が 1 秒ごとに行う。
    /// Release を no-op にすることで二重補充（RefillTokens + ワーカーの Release）を防ぐ。
    /// </summary>
    public void Release() { /* no-op: トークン補充は RefillTokens のみが担う */ }

    /// <inheritdoc/>
    public void NotifyRequestSent()
    {
        Interlocked.Increment(ref _activeCount);
        _aggregator.NotifyRequestSent();
    }

    /// <inheritdoc/>
    public void NotifySuccess(TimeSpan latency)
    {
        DecrementIfPositive(ref _activeCount);
        _aggregator.NotifySuccess(latency);
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
        _aggregator.NotifyRateLimit(retryAfter);
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

    /// <inheritdoc/>
    public double CurrentRateLimit { get { lock (_rateLock) return _currentRate; } }

    // ─────────────────────────────────────────────────────────────
    // 制御ループ（1 秒周期）
    // ─────────────────────────────────────────────────────────────

    private async Task ControlLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
                RunControlCycle();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常終了
        }
    }

    private void RunControlCycle()
    {
        var shortWindow = TimeSpan.FromSeconds(_settings.ShortWindowSec);
        var longWindow = TimeSpan.FromSeconds(_settings.LongWindowSec);

        var shortSnap = _aggregator.GetSnapshot(shortWindow);
        var longSnap = _aggregator.GetSnapshot(longWindow);

        double newRate;
        string action;
        int stateCode;
        lock (_rateLock)
        {
            (newRate, action, stateCode) = AdjustRate(_currentRate, shortSnap, longSnap);
            _currentRate = newRate;
        }

        // インフライトチェック
        var inFlight = CurrentInFlight;
        bool dispatchStopped = inFlight > _settings.InFlightThreshold;

        // トークン補充（dispatch 実行）
        if (!dispatchStopped)
        {
            RefillTokens(newRate);
        }
        else
        {
            _logger.LogWarning(
                "インフライト数 {InFlight} が閾値 {Threshold} を超えました。dispatch を停止します。",
                inFlight, _settings.InFlightThreshold);
        }

        // メトリクスバッファにエンキュー（制御ループが集計するので DB は一切触らない）
        _metricsBuffer.Enqueue("rps_short", shortSnap.Rps);
        _metricsBuffer.Enqueue("rate_429_short", shortSnap.Rate429);
        _metricsBuffer.Enqueue("rate_429_long", longSnap.Rate429);
        _metricsBuffer.Enqueue("avg_latency_ms", longSnap.AvgLatencyMs);
        _metricsBuffer.Enqueue("current_rate_limit", newRate);
        _metricsBuffer.Enqueue("current_in_flight", inFlight);
        // ヒステリシス状態コード: 0=安定, 1=加速, 2=緩減速, 3=緊急減速（AdjustRate が直接返す）
        _metricsBuffer.Enqueue("hysteresis_state_code", stateCode);

        _logger.LogDebug(
            "制御サイクル: レート={Rate:F2} req/sec, 429率短期={Short:P1}/中期={Long:P1}, インフライト={InFlight}, アクション={Action}",
            newRate, shortSnap.Rate429, longSnap.Rate429, inFlight, action);
    }

    /// <summary>
    /// ヒステリシス制御 + 可変減衰でレートを調整する。
    /// </summary>
    private (double newRate, string action, int stateCode) AdjustRate(
        double currentRate, MetricsSnapshot shortSnap, MetricsSnapshot longSnap)
    {
        var maxRate = _settings.MaxConcurrency; // req/sec の上限は MaxConcurrency と同値

        // ── ヒステリシス制御（3 段階）──────────────────────────────────
        // stateCode: 0=安定, 1=加速, 2=緩減速, 3=緊急減速

        // 緊急減速: 短期 429 率 > emergencyThreshold
        if (shortSnap.Rate429 > _settings.EmergencyThreshold)
        {
            var factor = ComputeDecayFactor(shortSnap.Rate429);
            var newRate = Math.Max(_settings.MinRatePerSec, currentRate * factor);
            return (newRate, $"緊急減速(factor={factor:F2})", 3);
        }

        // 緩減速: 中期 429 率 > slowdownThreshold
        if (longSnap.Rate429 > _settings.SlowdownThreshold)
        {
            var factor = ComputeDecayFactor(longSnap.Rate429);
            var newRate = Math.Max(_settings.MinRatePerSec, currentRate * factor);
            return (newRate, $"緩減速(factor={factor:F2})", 2);
        }

        // 加速: 中期 429 率 = 0
        if (longSnap.Rate429 == 0)
        {
            var newRate = Math.Min(maxRate, currentRate * (1.0 + _settings.AccelerateRatio));
            return (newRate, "加速", 1);
        }

        // 安定域: 変更なし
        return (currentRate, "維持", 0);
    }

    /// <summary>
    /// 可変減衰係数を計算する。
    /// <c>factor = clamp(1 - decayK * rate429, minDecayFactor, maxDecayFactor)</c>
    /// </summary>
    private double ComputeDecayFactor(double rate429)
    {
        var raw = 1.0 - _settings.DecayK * rate429;
        return Math.Clamp(raw, _settings.MinDecayFactor, _settings.MaxDecayFactor);
    }

    /// <summary>
    /// 1 サイクル分のトークンをセマフォに補充する。
    /// 現在の利用可能スロット数 + 補充量が MaxConcurrency を超えないようにする。
    /// </summary>
    private void RefillTokens(double rate)
    {
        var tokensToAdd = (int)Math.Round(rate);
        if (tokensToAdd <= 0) return;

        var currentAvailable = _semaphore.CurrentCount;
        var canAdd = Math.Max(0, _settings.MaxConcurrency - currentAvailable);
        var actualAdd = Math.Min(tokensToAdd, canAdd);

        if (actualAdd > 0)
            _semaphore.Release(actualAdd);
    }

    // ─────────────────────────────────────────────────────────────
    // Dispose
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return; // 2 回目以降の呼び出しは no-op
        _cts.Cancel();
        try { await _controlLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
        _semaphore.Dispose();
    }
}
