using CloudMigrator.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// v0.6.0 ハイブリッド制御（スループット主制御 + 並列数補助制御）の統合コントローラー（#163）。
/// <para>
/// 設計書 v2 の §7 制御ループ本体と §4.3 並列数補助制御を統合する。
/// <list type="number">
///   <item>ゲート B（並列数セマフォ、<c>max_inflight</c>）→ ゲート A（<see cref="WeightedTokenBucket"/>）の順でスロット取得</item>
///   <item>制御ループが <c>ControlIntervalSec</c> 秒周期で <see cref="ISlidingWindowMetrics.GetSnapshot"/> を取得</item>
///   <item><see cref="IAimdFeedbackController.Evaluate"/> で 4 信号を判定</item>
///   <item><see cref="WeightedTokenBucket.SetRate"/> で補充レートを反映</item>
///   <item>信号に応じて <c>max_inflight</c> を動的調整（§4.3）</item>
///   <item>§9 制御ループメトリクス（7 項目）を <see cref="MetricsBuffer"/> に出力</item>
/// </list>
/// </para>
/// <para>
/// トークンコストは重み付きコスト統合まで暫定的に 1 固定（後続 Issue で <see cref="FileCostCalculator"/> 経由に拡張予定）。
/// </para>
/// </summary>
public sealed class HybridRateController : ITransferRateController, IAsyncDisposable
{
    /// <summary>メトリクス名: 現在の補充レート（tokens/sec）。</summary>
    public const string MetricRateTokensPerSec = "rate_tokens_per_sec";
    /// <summary>メトリクス名: 現在の並列数上限。</summary>
    public const string MetricMaxInflight = "max_inflight";
    /// <summary>メトリクス名: バケット残量（遅延補充後）。</summary>
    public const string MetricTokensAvailable = "tokens_available";
    /// <summary>メトリクス名: ウィンドウ集計の 429 率（0–1）。</summary>
    public const string MetricRate429 = "rate_429";
    /// <summary>メトリクス名: ウィンドウ集計の P95 レイテンシ（ms）。</summary>
    public const string MetricP95LatencyMs = "p95_latency_ms";
    /// <summary>メトリクス名: AIMD 判定信号コード（0=Hold,1=Emergency,2=Slow,3=Stable）。</summary>
    public const string MetricSignal = "signal";
    /// <summary>メトリクス名: クールダウン中フラグ（0/1）。</summary>
    public const string MetricInCooldown = "in_cooldown";

    private readonly WeightedTokenBucket _bucket;
    private readonly IAimdFeedbackController _aimd;
    private readonly ISlidingWindowMetrics _metrics;
    private readonly MetricsBuffer? _metricsBuffer;
    private readonly RateStateStore? _stateStore;
    private readonly ILogger<HybridRateController> _logger;
    private readonly int _configuredMaxInflight;
    private readonly int _minInflight;
    private readonly double _emergencyInflightDecay;
    private readonly TimeSpan _controlInterval;

    // 並列数補助制御: 「仮想上限」方式
    // SemaphoreSlim は capacity 変更不可のため、_virtualMaxInflight を別途保持し、
    // 縮小時は「Release で消化すべき残数」(_shrinkDebt) をカウントしてワーカー Release を吸収する。
    // 拡大時は _shrinkDebt をまず相殺し、残りだけ inflightSlots.Release で補充する。
    private readonly SemaphoreSlim _inflightSlots;
    private readonly object _inflightLock = new();
    private int _virtualMaxInflight;
    private int _shrinkDebt;

    // インフライトカウンター（成功メトリクス・ダッシュボード表示用）
    private int _activeCount;
    private int _retryWaitingCount;

    // 制御ループ
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _controlLoop;
    private int _disposed;

    /// <summary>
    /// ハイブリッドコントローラーを構築する。
    /// </summary>
    /// <param name="bucket">スループット主制御のトークンバケット（#160）。</param>
    /// <param name="aimd">AIMD フィードバック評価器（#162）。純粋ロジック。</param>
    /// <param name="metrics">スライディングウィンドウ指標収集器（#161）。</param>
    /// <param name="settings">レート制御設定。</param>
    /// <param name="metricsBuffer">メトリクス非同期書込バッファ。<c>null</c> の場合はメトリクス出力をスキップする（テスト用）。</param>
    /// <param name="stateStore"><c>rate_state.json</c> の読み書きストア。<c>null</c> の場合は状態保存しない（テスト用）。</param>
    /// <param name="logger">ロガー。</param>
    /// <param name="initialMaxInflight">
    /// 起動時の <c>_virtualMaxInflight</c> の初期値（ウォームスタート用）。<c>null</c> の場合は <c>settings.MaxInflight</c> を使う。
    /// 値は <c>[settings.MinInflight, settings.MaxInflight]</c> にクランプされる。
    /// </param>
    public HybridRateController(
        WeightedTokenBucket bucket,
        IAimdFeedbackController aimd,
        ISlidingWindowMetrics metrics,
        RateControlSettings settings,
        MetricsBuffer? metricsBuffer,
        RateStateStore? stateStore,
        ILogger<HybridRateController> logger,
        int? initialMaxInflight = null)
    {
        ArgumentNullException.ThrowIfNull(bucket);
        ArgumentNullException.ThrowIfNull(aimd);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxInflight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.MinInflight, 1);
        if (settings.MinInflight > settings.MaxInflight)
            throw new ArgumentOutOfRangeException(nameof(settings),
                $"MinInflight ({settings.MinInflight}) は MaxInflight ({settings.MaxInflight}) 以下でなければなりません。");
        if (settings.EmergencyInflightDecay is <= 0.0 or >= 1.0 || !double.IsFinite(settings.EmergencyInflightDecay))
            throw new ArgumentOutOfRangeException(nameof(settings),
                $"EmergencyInflightDecay は 0 より大きく 1 未満の有限値でなければなりません（現在値: {settings.EmergencyInflightDecay}）。");
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.ControlIntervalSec, 1);

        _bucket = bucket;
        _aimd = aimd;
        _metrics = metrics;
        _metricsBuffer = metricsBuffer;
        _stateStore = stateStore;
        _logger = logger;

        _configuredMaxInflight = settings.MaxInflight;
        _minInflight = settings.MinInflight;
        _emergencyInflightDecay = settings.EmergencyInflightDecay;
        _controlInterval = TimeSpan.FromSeconds(settings.ControlIntervalSec);

        _virtualMaxInflight = Math.Clamp(
            initialMaxInflight ?? _configuredMaxInflight,
            _minInflight,
            _configuredMaxInflight);
        _inflightSlots = new SemaphoreSlim(_virtualMaxInflight, _configuredMaxInflight);

        _controlLoop = Task.Run(ControlLoopAsync);
    }

    // ─────────────────────────────────────────────────────────────
    // ITransferRateController 実装
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// ゲート B（並列数）→ ゲート A（トークン）の順で取得する。
    /// トークンコストは重み付きコスト統合まで 1 固定。
    /// </remarks>
    public async Task AcquireAsync(CancellationToken ct)
    {
        await _inflightSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _bucket.AcquireAsync(1, ct).ConfigureAwait(false);
        }
        catch
        {
            // バケット取得でキャンセルされた場合は並列数スロットも戻す
            ReleaseInflightSlot();
            throw;
        }
    }

    /// <inheritdoc/>
    public void Release() => ReleaseInflightSlot();

    /// <inheritdoc/>
    public void NotifyRequestSent()
    {
        Interlocked.Increment(ref _activeCount);
        _metrics.NotifyRequestSent();
    }

    /// <inheritdoc/>
    public void NotifySuccess(TimeSpan latency)
    {
        DecrementIfPositive(ref _activeCount);
        _metrics.NotifySuccess(latency);
    }

    /// <inheritdoc/>
    public void NotifyCompleted(TimeSpan latency)
    {
        // キャンセル・非レート制限エラー等。メトリクスには計上せずカウンターのみ戻す。
        DecrementIfPositive(ref _activeCount);
    }

    /// <inheritdoc/>
    public void NotifyRateLimit(TimeSpan? retryAfter)
    {
        DecrementIfPositive(ref _activeCount);
        _metrics.NotifyRateLimit(retryAfter);
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

    /// <inheritdoc/>
    public int CurrentInFlight =>
        Math.Max(0, Volatile.Read(ref _activeCount)) +
        Math.Max(0, Volatile.Read(ref _retryWaitingCount));

    /// <inheritdoc/>
    public double CurrentRateLimit => _bucket.CurrentRate;

    /// <summary>現在の並列数上限（動的調整後の仮想上限）。ダッシュボード表示用。</summary>
    public int CurrentMaxInflight { get { lock (_inflightLock) return _virtualMaxInflight; } }

    // ─────────────────────────────────────────────────────────────
    // 制御ループ（§7）
    // ─────────────────────────────────────────────────────────────

    private async Task ControlLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(_controlInterval, _cts.Token).ConfigureAwait(false);
                try
                {
                    RunControlCycle();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 1 サイクルの失敗でループ全体を止めない。
                    _logger.LogWarning(ex, "HybridRateController 制御サイクルで例外が発生しました。次サイクルを継続します。");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常終了
        }
    }

    /// <summary>
    /// 1 サイクル分の制御処理。テスト用に <c>internal</c> で公開する。
    /// </summary>
    internal void RunControlCycle()
    {
        var snapshot = _metrics.GetSnapshot();
        var evaluation = _aimd.Evaluate(snapshot);

        // スループット主制御: 新レートをバケットに反映
        _bucket.SetRate(evaluation.NewRate);

        // 並列数補助制御（§4.3）
        var newMaxInflight = AdjustMaxInflight(evaluation.Signal);

        // §9 メトリクス出力
        EmitMetrics(evaluation, newMaxInflight);

        _logger.LogDebug(
            "HybridRateController サイクル: レート={Rate:F2} tokens/sec, max_inflight={MaxInflight}, 信号={Signal}, クールダウン={Cooldown}",
            evaluation.NewRate, newMaxInflight, evaluation.Signal, evaluation.InCooldown);
    }

    /// <summary>
    /// §4.3 並列数補助制御。AIMD 信号に応じて <c>_virtualMaxInflight</c> を調整する。
    /// 縮小時は「Release で消化すべき残数」(<c>_shrinkDebt</c>) を増やして以降のワーカー Release を吸収させ、
    /// 拡大時はまず <c>_shrinkDebt</c> を相殺し、残りだけ <see cref="SemaphoreSlim.Release(int)"/> で補充する。
    /// </summary>
    private int AdjustMaxInflight(AimdSignal signal)
    {
        lock (_inflightLock)
        {
            switch (signal)
            {
                case AimdSignal.EmergencyDecrease:
                    {
                        var target = Math.Max(
                            (int)Math.Floor(_virtualMaxInflight * _emergencyInflightDecay),
                            _minInflight);
                        var delta = _virtualMaxInflight - target;
                        if (delta > 0)
                        {
                            // 縮小は即時に効かせるため、現在空いている permit はこの場で物理回収する。
                            // 使用中で回収できない分のみ Release 抑止予約として _shrinkDebt に積む。
                            // ロック内で Wait(0) するため AcquireAsync の WaitAsync とのレースは発生しない。
                            var reclaimed = 0;
                            while (reclaimed < delta && _inflightSlots.Wait(0))
                            {
                                reclaimed++;
                            }

                            var remainingDebt = delta - reclaimed;
                            if (remainingDebt > 0)
                            {
                                _shrinkDebt += remainingDebt;
                            }
                            _virtualMaxInflight = target;
                        }
                        break;
                    }
                case AimdSignal.Stable:
                    {
                        var target = Math.Min(_virtualMaxInflight + 1, _configuredMaxInflight);
                        var delta = target - _virtualMaxInflight;
                        if (delta > 0)
                        {
                            // 拡大はまず未消化の縮小予約を相殺し、残った分だけ実 Release で補充する。
                            var canceled = Math.Min(delta, _shrinkDebt);
                            _shrinkDebt -= canceled;
                            var remaining = delta - canceled;
                            if (remaining > 0)
                                _inflightSlots.Release(remaining);
                            _virtualMaxInflight = target;
                        }
                        break;
                    }
                case AimdSignal.SlowDecrease:
                case AimdSignal.Hold:
                default:
                    break;
            }
            return _virtualMaxInflight;
        }
    }

    /// <summary>
    /// スロット解放。未消化の縮小予約 (<c>_shrinkDebt</c>) があるときは実 Release を抑止して予約を 1 減らす。
    /// 予約が 0 のときのみ <see cref="SemaphoreSlim.Release()"/> を呼ぶ。
    /// </summary>
    private void ReleaseInflightSlot()
    {
        lock (_inflightLock)
        {
            if (_shrinkDebt > 0)
            {
                _shrinkDebt--;
                return;
            }
            _inflightSlots.Release();
        }
    }

    private void EmitMetrics(AimdEvaluation evaluation, int maxInflight)
    {
        if (_metricsBuffer is null) return;

        _metricsBuffer.Enqueue(MetricRateTokensPerSec, evaluation.NewRate);
        _metricsBuffer.Enqueue(MetricMaxInflight, maxInflight);
        _metricsBuffer.Enqueue(MetricTokensAvailable, _bucket.AvailableTokens);
        _metricsBuffer.Enqueue(MetricRate429, evaluation.Snapshot.Rate429);
        _metricsBuffer.Enqueue(MetricP95LatencyMs, evaluation.Snapshot.P95LatencyMs);
        _metricsBuffer.Enqueue(MetricSignal, (int)evaluation.Signal);
        _metricsBuffer.Enqueue(MetricInCooldown, evaluation.InCooldown ? 1 : 0);
    }

    private static void DecrementIfPositive(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0) return;
            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current) return;
        }
    }

    // ─────────────────────────────────────────────────────────────
    // Dispose
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        _cts.Cancel();
        try { await _controlLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }

        // 終了前に状態を保存（次回起動時の復元用）
        if (_stateStore is not null)
        {
            try
            {
                await _stateStore.SaveAsync(_bucket.CurrentRate, CurrentMaxInflight).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "rate_state.json の保存に失敗しました。");
            }
        }

        _cts.Dispose();
        _inflightSlots.Dispose();
    }
}
