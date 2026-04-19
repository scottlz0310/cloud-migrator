using System.Diagnostics;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// AIMD フィードバック制御の実装（#162）。
/// <para>
/// 設計書§6 に基づき、スライディングウィンドウ指標から 4 信号
/// （<c>EmergencyDecrease</c> / <c>SlowDecrease</c> / <c>Stable</c> / <c>Hold</c>）を判定し、
/// トークンバケット補充レートを <c>[minRate, maxRate]</c> の範囲で動的調整する。
/// </para>
/// <para>
/// 本クラスは純粋ロジックであり、副作用（<c>WeightedTokenBucket.SetRate</c> 呼び出し・
/// メトリクス書き込み）は持たない。制御ループへの組込みは #163 で実装する。
/// </para>
/// <para>
/// 時刻は <see cref="Stopwatch.GetTimestamp"/> ベースの monotonic clock で扱い、
/// NTP 補正・手動時刻変更の影響を受けない。テストでは <c>timestampProvider</c> を注入して制御する。
/// </para>
/// </summary>
public sealed class AimdFeedbackController : IAimdFeedbackController
{
    private readonly AimdFeedbackSettings _settings;
    private readonly Func<long> _timestampProvider;
    private readonly object _lock = new();

    private readonly long _cooldownTicks;
    private readonly long _stableWindowTicks;
    private readonly long _trendWindowTicks;

    private double _currentRate;
    private double _baselineP95Ms;
    private long _baselineSuccessSamples;
    private long _cooldownEndTicks;
    private long _lastRate429Ticks;

    // 直近比判定用の P95 タイムシリーズ（(ticks, p95ms)）。
    // 2 * TrendWindowSec より古いものは evict する。サイズは制御周期に比例するが、
    // デフォルト TrendWindowSec=10 / 制御周期 1 秒なら ~20 件で十分少量。
    private readonly Queue<(long ticks, double p95Ms)> _p95History = new();

    /// <summary>AIMD フィードバックコントローラーを初期化する。</summary>
    /// <param name="settings">AIMD 設定値。</param>
    /// <param name="timestampProvider">
    /// monotonic ticks 取得関数。<c>null</c> の場合は <see cref="Stopwatch.GetTimestamp"/>。
    /// 単体テストで任意時刻を注入する用途に使う。
    /// </param>
    public AimdFeedbackController(AimdFeedbackSettings settings, Func<long>? timestampProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        _settings = settings;
        _timestampProvider = timestampProvider ?? Stopwatch.GetTimestamp;

        _cooldownTicks = (long)(settings.CooldownSec * (double)Stopwatch.Frequency);
        _stableWindowTicks = (long)(settings.StableWindowSec * (double)Stopwatch.Frequency);
        _trendWindowTicks = (long)(settings.TrendWindowSec * (double)Stopwatch.Frequency);

        _currentRate = Math.Clamp(settings.InitialRate, settings.MinRate, settings.MaxRate);
        _baselineP95Ms = 0.0;
        _baselineSuccessSamples = 0;
        // 起動直後は「直近 429 なし」のカウントを 0 秒前から始める
        _lastRate429Ticks = _timestampProvider();
        _cooldownEndTicks = 0;
    }

    /// <inheritdoc/>
    public double CurrentRate { get { lock (_lock) return _currentRate; } }

    /// <inheritdoc/>
    public bool InCooldown
    {
        get
        {
            lock (_lock)
            {
                return _timestampProvider() < _cooldownEndTicks;
            }
        }
    }

    /// <inheritdoc/>
    public double BaselineP95Ms { get { lock (_lock) return _baselineP95Ms; } }

    /// <inheritdoc/>
    public AimdEvaluation Evaluate(SlidingWindowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_lock)
        {
            var now = _timestampProvider();
            var previousRate = _currentRate;

            // 最低サンプル未満は判定をスキップ（Hold）。
            // P95 履歴・ベースライン確立は安定期の代表値で行うため、不安定な P95 を
            // 履歴に混入させないよう HasMinSamples=false のサイクルでは一切更新しない。
            if (!snapshot.HasMinSamples)
            {
                return BuildResult(AimdSignal.Hold, previousRate, snapshot, now);
            }

            AppendP95History(now, snapshot.P95LatencyMs);

            // 1. 急減速判定（最優先）
            if (snapshot.Rate429 > _settings.EmergencyThreshold)
            {
                var inCooldown = now < _cooldownEndTicks;
                if (!inCooldown)
                {
                    // クールダウン外: 初回削減とクールダウン開始
                    _currentRate = Math.Clamp(
                        _currentRate * _settings.EmergencyDecay,
                        _settings.MinRate,
                        _settings.MaxRate);
                    _cooldownEndTicks = now + _cooldownTicks;
                }
                // クールダウン中でも _lastRate429Ticks を更新して Stable 復帰を抑制する
                _lastRate429Ticks = now;
                // ベースライン凍結（§6.1）
                // クールダウン中は Hold を返して「半分落としたまま様子見」を実現する
                return BuildResult(
                    inCooldown ? AimdSignal.Hold : AimdSignal.EmergencyDecrease,
                    previousRate, snapshot, now);
            }

            // 429 が現ウィンドウに存在する場合も安定カウンターを更新しておく
            if (snapshot.Rate429 > 0.0)
            {
                _lastRate429Ticks = now;
            }

            // 2. レイテンシ悪化判定
            if (IsLatencyWorsened(snapshot.P95LatencyMs, now))
            {
                _currentRate = Math.Clamp(
                    _currentRate * _settings.SlowDecay,
                    _settings.MinRate,
                    _settings.MaxRate);
                // ベースライン凍結
                return BuildResult(AimdSignal.SlowDecrease, previousRate, snapshot, now);
            }

            // 3. Stable 判定。ベースライン確立・EMA 更新はこの経路でのみ実施し、
            //    Hold やサンプル不足時に「安定でない P95」を学習しないようにする。
            var cooldownActive = now < _cooldownEndTicks;
            var stableElapsed = (now - _lastRate429Ticks) >= _stableWindowTicks;
            if (!cooldownActive && stableElapsed)
            {
                _currentRate = Math.Clamp(
                    _currentRate + _settings.AddStep,
                    _settings.MinRate,
                    _settings.MaxRate);
                UpdateBaselineEma(snapshot);
                return BuildResult(AimdSignal.Stable, previousRate, snapshot, now);
            }

            // 4. Hold（いずれにも該当せず）。ベースライン初期化はここでは行わない。
            return BuildResult(AimdSignal.Hold, previousRate, snapshot, now);
        }
    }

    /// <summary>
    /// P95 タイムシリーズに追加し、<c>2 * TrendWindowSec</c> より古いエントリを evict する。
    /// P95 が 0（成功サンプルなし）の場合は履歴から除外し、判定に用いない。
    /// </summary>
    private void AppendP95History(long now, double p95Ms)
    {
        if (p95Ms > 0.0)
        {
            _p95History.Enqueue((now, p95Ms));
        }
        var cutoff = now - 2 * _trendWindowTicks;
        while (_p95History.TryPeek(out var head) && head.ticks < cutoff)
        {
            _p95History.Dequeue();
        }
    }

    /// <summary>
    /// レイテンシ悪化判定。モードに応じてベースライン比 / 直近比 / 併用を使い分ける。
    /// </summary>
    private bool IsLatencyWorsened(double currentP95Ms, long now)
    {
        if (currentP95Ms <= 0.0) return false;

        var threshold = 1.0 + _settings.LatencyRiseRatio;

        var baselineWorsened = _baselineP95Ms > 0.0
            && currentP95Ms > _baselineP95Ms * threshold;

        var recentWorsened = IsRecentTrendWorsened(now, threshold);

        return _settings.LatencyMode switch
        {
            LatencyEvaluationMode.None => false,
            LatencyEvaluationMode.Baseline => baselineWorsened,
            LatencyEvaluationMode.Recent => recentWorsened,
            LatencyEvaluationMode.Both => baselineWorsened || recentWorsened,
            _ => false,
        };
    }

    /// <summary>
    /// 直近 <c>TrendWindowSec</c> 秒の P95 平均と、その前の同時間窓の P95 平均を比較する。
    /// 両窓にエントリが 1 件以上なければ判定不能として false を返す。
    /// </summary>
    private bool IsRecentTrendWorsened(long now, double threshold)
    {
        var recentStart = now - _trendWindowTicks;
        var previousStart = now - 2 * _trendWindowTicks;

        double recentSum = 0, previousSum = 0;
        int recentCount = 0, previousCount = 0;

        foreach (var (ticks, p95) in _p95History)
        {
            if (ticks >= recentStart)
            {
                recentSum += p95;
                recentCount++;
            }
            else if (ticks >= previousStart)
            {
                previousSum += p95;
                previousCount++;
            }
        }

        if (recentCount == 0 || previousCount == 0) return false;

        var recentAvg = recentSum / recentCount;
        var previousAvg = previousSum / previousCount;
        return recentAvg > previousAvg * threshold;
    }

    /// <summary>
    /// 安定期のベースライン EMA 更新（§6.1）。<c>baseline = baseline*(1-α) + current*α</c>。
    /// 未確立の場合は初期化ルートに合流する。
    /// </summary>
    private void UpdateBaselineEma(SlidingWindowSnapshot snapshot)
    {
        if (snapshot.P95LatencyMs <= 0.0) return;

        if (_baselineP95Ms <= 0.0)
        {
            TryInitializeBaseline(snapshot);
            return;
        }

        var alpha = _settings.BaselineEmaAlpha;
        _baselineP95Ms = _baselineP95Ms * (1.0 - alpha) + snapshot.P95LatencyMs * alpha;
    }

    /// <summary>
    /// ベースライン初期化。Stable 経路（<see cref="UpdateBaselineEma"/> のフォールバック）でのみ呼ばれる。
    /// <para>
    /// 設計書§6.1 は「最初の baselineSamples 件の成功リクエストの P95」と記述しているが、
    /// <see cref="SlidingWindowSnapshot"/> は累積成功数を公開していないため、単一スナップショット内で
    /// 観測された成功サンプル推定数（<c>SampleCount × SuccessRate</c>）の最大値が <c>BaselineSamples</c> に
    /// 達した時点で、そのサイクルの P95 を初期ベースラインとして採用する。
    /// 呼び出し経路が Stable に限定されているため、確立される P95 は常に「安定期のスナップショット P95」である。
    /// </para>
    /// </summary>
    private void TryInitializeBaseline(SlidingWindowSnapshot snapshot)
    {
        if (_baselineP95Ms > 0.0) return;
        if (snapshot.P95LatencyMs <= 0.0) return;

        // スナップショット内の成功サンプル推定数（小数誤差は数件レベルで閾値判定に十分）。
        var approxSuccessCountInSnapshot = (long)Math.Round(snapshot.SampleCount * snapshot.SuccessRate);
        _baselineSuccessSamples = Math.Max(_baselineSuccessSamples, approxSuccessCountInSnapshot);

        if (_baselineSuccessSamples >= _settings.BaselineSamples)
        {
            _baselineP95Ms = snapshot.P95LatencyMs;
        }
    }

    private AimdEvaluation BuildResult(AimdSignal signal, double previousRate, SlidingWindowSnapshot snapshot, long nowTicks)
    {
        var inCooldown = nowTicks < _cooldownEndTicks;
        // EvaluatedAt は snapshot.Timestamp に統一する。呼び出し側のテストでは
        // snapshot 生成時に任意時刻を注入できるため、これにより DateTimeOffset の
        // 完全な時刻制御が可能になる（monotonic ticks は時刻注入で別管理）。
        return new AimdEvaluation(
            Signal: signal,
            PreviousRate: previousRate,
            NewRate: _currentRate,
            BaselineP95Ms: _baselineP95Ms,
            InCooldown: inCooldown,
            Snapshot: snapshot,
            EvaluatedAt: snapshot.Timestamp);
    }
}

/// <summary>
/// <see cref="AimdFeedbackController"/> に渡す設定値。
/// <see cref="CloudMigrator.Core.Configuration.RateControlSettings"/> から値をコピーして生成する想定。
/// </summary>
public sealed class AimdFeedbackSettings
{
    public double InitialRate { get; set; } = 10.0;
    public double MinRate { get; set; } = 1.0;
    public double MaxRate { get; set; } = 200.0;
    public double EmergencyThreshold { get; set; } = 0.10;
    public double EmergencyDecay { get; set; } = 0.9;
    public double SlowDecay { get; set; } = 0.9;
    public double AddStep { get; set; } = 1.0;
    public double LatencyRiseRatio { get; set; } = 0.3;
    public int BaselineSamples { get; set; } = 20;
    public double BaselineEmaAlpha { get; set; } = 0.1;
    public int TrendWindowSec { get; set; } = 10;
    public int StableWindowSec { get; set; } = 30;
    public int CooldownSec { get; set; } = 20;
    public LatencyEvaluationMode LatencyMode { get; set; } = LatencyEvaluationMode.None;

    /// <summary>設定値の整合性を検証する。不正値があれば <see cref="ArgumentOutOfRangeException"/>。</summary>
    public void Validate()
    {
        ThrowIfNotFinitePositive(InitialRate, nameof(InitialRate));
        ThrowIfNotFinitePositive(MinRate, nameof(MinRate));
        ThrowIfNotFinitePositive(MaxRate, nameof(MaxRate));
        if (MinRate > MaxRate)
            throw new ArgumentOutOfRangeException(nameof(MinRate),
                $"MinRate ({MinRate}) は MaxRate ({MaxRate}) 以下でなければなりません。");
        ThrowIfOutOfRange(EmergencyThreshold, 0.0, 1.0, nameof(EmergencyThreshold));
        ThrowIfOutOfRange(EmergencyDecay, 0.0, 1.0, nameof(EmergencyDecay), exclusiveMin: true, exclusiveMax: true);
        ThrowIfOutOfRange(SlowDecay, 0.0, 1.0, nameof(SlowDecay), exclusiveMin: true, exclusiveMax: true);
        ThrowIfNotFinitePositive(AddStep, nameof(AddStep));
        if (LatencyRiseRatio <= 0.0 || !double.IsFinite(LatencyRiseRatio))
            throw new ArgumentOutOfRangeException(nameof(LatencyRiseRatio), LatencyRiseRatio,
                "LatencyRiseRatio は 0 より大きい有限値でなければなりません。");
        if (BaselineSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(BaselineSamples), BaselineSamples,
                "BaselineSamples は 1 以上でなければなりません。");
        ThrowIfOutOfRange(BaselineEmaAlpha, 0.0, 1.0, nameof(BaselineEmaAlpha), exclusiveMin: true, exclusiveMax: true);
        if (TrendWindowSec < 1)
            throw new ArgumentOutOfRangeException(nameof(TrendWindowSec), TrendWindowSec,
                "TrendWindowSec は 1 以上でなければなりません。");
        if (StableWindowSec < 1)
            throw new ArgumentOutOfRangeException(nameof(StableWindowSec), StableWindowSec,
                "StableWindowSec は 1 以上でなければなりません。");
        if (CooldownSec < 0)
            throw new ArgumentOutOfRangeException(nameof(CooldownSec), CooldownSec,
                "CooldownSec は 0 以上でなければなりません。");
    }

    private static void ThrowIfNotFinitePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0.0)
            throw new ArgumentOutOfRangeException(name, value, $"{name} は 0 より大きい有限値でなければなりません。");
    }

    private static void ThrowIfOutOfRange(double value, double min, double max, string name,
        bool exclusiveMin = false, bool exclusiveMax = false)
    {
        if (!double.IsFinite(value)
            || (exclusiveMin ? value <= min : value < min)
            || (exclusiveMax ? value >= max : value > max))
        {
            var left = exclusiveMin ? "(" : "[";
            var right = exclusiveMax ? ")" : "]";
            throw new ArgumentOutOfRangeException(name, value,
                $"{name} は {left}{min}, {max}{right} の範囲でなければなりません。");
        }
    }

    /// <summary><see cref="Configuration.RateControlSettings"/> から AIMD 用設定を抽出する。</summary>
    public static AimdFeedbackSettings FromRateControlSettings(Configuration.RateControlSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AimdFeedbackSettings
        {
            InitialRate = source.InitialTokensPerSec,
            MinRate = source.MinTokensPerSec,
            MaxRate = source.MaxTokensPerSec,
            EmergencyThreshold = source.AimdEmergencyThreshold,
            EmergencyDecay = source.EmergencyDecay,
            SlowDecay = source.SlowDecay,
            AddStep = source.AddStep,
            LatencyRiseRatio = source.LatencyRiseRatio,
            BaselineSamples = source.BaselineSamples,
            BaselineEmaAlpha = source.BaselineEmaAlpha,
            TrendWindowSec = source.TrendWindowSec,
            StableWindowSec = source.StableWindowSec,
            CooldownSec = source.CooldownSec,
            LatencyMode = source.LatencyEvaluationMode,
        };
    }
}
