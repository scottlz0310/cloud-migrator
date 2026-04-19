using System.Diagnostics;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Transfer;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// <see cref="AimdFeedbackController"/> のユニットテスト（#162）。
/// <para>
/// 時刻は <c>Func&lt;long&gt;</c> 注入で完全制御する。<see cref="Stopwatch.Frequency"/> を使って
/// 秒→ticks 換算を行い、設計書§6 の各信号発動条件・クールダウン・ベースライン EMA・
/// clamp・最低サンプル未満スキップ・レイテンシモード切替を網羅する。
/// </para>
/// </summary>
public sealed class AimdFeedbackControllerTests
{
    private sealed class FakeClock
    {
        private long _ticks;
        public FakeClock(long start = 0) { _ticks = start; }
        public long Now() => _ticks;
        public void AdvanceSeconds(double seconds) => _ticks += (long)(seconds * Stopwatch.Frequency);
    }

    private static SlidingWindowSnapshot MakeSnapshot(
        int sampleCount = 50,
        bool hasMinSamples = true,
        double rate429 = 0.0,
        double successRate = 1.0,
        double avgLatencyMs = 100.0,
        double p95LatencyMs = 100.0) =>
        new(sampleCount, hasMinSamples, rate429, successRate, avgLatencyMs, p95LatencyMs,
            FilesPerSec: 0.0, BytesPerSec: 0.0, WindowSeconds: 30.0, Timestamp: DateTimeOffset.UtcNow);

    private static AimdFeedbackSettings MakeSettings(Action<AimdFeedbackSettings>? configure = null)
    {
        var s = new AimdFeedbackSettings
        {
            InitialRate = 10.0,
            MinRate = 1.0,
            MaxRate = 100.0,
            EmergencyThreshold = 0.10,
            EmergencyDecay = 0.7,
            SlowDecay = 0.9,
            AddStep = 1.0,
            LatencyRiseRatio = 0.3,
            BaselineSamples = 20,
            BaselineEmaAlpha = 0.1,
            TrendWindowSec = 10,
            StableWindowSec = 30,
            CooldownSec = 20,
            LatencyMode = LatencyEvaluationMode.Baseline,
        };
        configure?.Invoke(s);
        return s;
    }

    // ─── コンストラクタ・バリデーション ───────────────────────────

    [Fact]
    public void Constructor_ClampsInitialRate_IntoMinMaxRange()
    {
        // 検証対象: 初期レートクランプ  目的: InitialRate が範囲外なら [MinRate, MaxRate] にクランプされる
        var settings = MakeSettings(s => { s.InitialRate = 500.0; s.MaxRate = 100.0; });
        var sut = new AimdFeedbackController(settings, () => 0);
        sut.CurrentRate.Should().Be(100.0);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_Throws_WhenInitialRateInvalid(double initialRate)
    {
        // 検証対象: 設定検証  目的: 0 以下・非有限の InitialRate を拒否
        var settings = MakeSettings(s => s.InitialRate = initialRate);
        Action act = () => new AimdFeedbackController(settings, () => 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_Throws_WhenMinRateExceedsMaxRate()
    {
        // 検証対象: 設定検証  目的: MinRate > MaxRate は構成エラー
        var settings = MakeSettings(s => { s.MinRate = 100.0; s.MaxRate = 10.0; });
        Action act = () => new AimdFeedbackController(settings, () => 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── Hold 判定 ─────────────────────────────────────────────

    [Fact]
    public void Evaluate_ReturnsHold_WhenBelowMinSamples()
    {
        // 検証対象: HasMinSamples=false  目的: サンプル不足時は判定をスキップしレートを変えない
        var clock = new FakeClock();
        var sut = new AimdFeedbackController(MakeSettings(), clock.Now);
        var snap = MakeSnapshot(sampleCount: 3, hasMinSamples: false, rate429: 0.5);

        var result = sut.Evaluate(snap);

        result.Signal.Should().Be(AimdSignal.Hold);
        result.PreviousRate.Should().Be(10.0);
        result.NewRate.Should().Be(10.0);
    }

    [Fact]
    public void Evaluate_ReturnsHold_WhenStableWindowNotElapsed()
    {
        // 検証対象: Stable 判定の時間条件  目的: StableWindowSec 未経過は Stable にならない
        var clock = new FakeClock();
        var sut = new AimdFeedbackController(MakeSettings(), clock.Now);
        // StableWindowSec=30 未満しか経過していない → Stable ではなく Hold
        clock.AdvanceSeconds(5);
        var result = sut.Evaluate(MakeSnapshot(rate429: 0.0));

        result.Signal.Should().Be(AimdSignal.Hold);
        result.NewRate.Should().Be(10.0);
    }

    // ─── EmergencyDecrease ────────────────────────────────────

    [Fact]
    public void Evaluate_EmergencyDecrease_MultipliesRateByDecay_AndEntersCooldown()
    {
        // 検証対象: 429率 > 閾値  目的: EmergencyDecay 倍・クールダウン突入
        var clock = new FakeClock();
        var sut = new AimdFeedbackController(MakeSettings(), clock.Now);
        var snap = MakeSnapshot(rate429: 0.15);

        var result = sut.Evaluate(snap);

        result.Signal.Should().Be(AimdSignal.EmergencyDecrease);
        result.NewRate.Should().BeApproximately(10.0 * 0.7, 1e-9);
        result.InCooldown.Should().BeTrue();
        sut.InCooldown.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_EmergencyDecrease_ClampsAtMinRate()
    {
        // 検証対象: 下限クランプ  目的: 連続減速でも MinRate を下回らない
        var clock = new FakeClock();
        var settings = MakeSettings(s => { s.InitialRate = 2.0; s.MinRate = 1.5; });
        var sut = new AimdFeedbackController(settings, clock.Now);
        var snap = MakeSnapshot(rate429: 0.2);

        // 2.0 * 0.7 = 1.4 → MinRate=1.5 でクランプ
        var result = sut.Evaluate(snap);
        result.NewRate.Should().Be(1.5);
    }

    [Fact]
    public void Evaluate_ThresholdBoundary_IsExclusive()
    {
        // 検証対象: 閾値判定  目的: rate429 == threshold では EmergencyDecrease しない（> の条件）
        var clock = new FakeClock();
        var sut = new AimdFeedbackController(MakeSettings(), clock.Now);
        var snap = MakeSnapshot(rate429: 0.10);

        var result = sut.Evaluate(snap);

        result.Signal.Should().NotBe(AimdSignal.EmergencyDecrease);
    }

    // ─── SlowDecrease（ベースライン比） ─────────────────────────

    [Fact]
    public void Evaluate_SlowDecrease_WhenBaselineWorsened()
    {
        // 検証対象: ベースライン比悪化  目的: ベースライン確立後、P95 が閾値を超えると SlowDecrease
        var clock = new FakeClock();
        var settings = MakeSettings(s => s.BaselineSamples = 10);
        var sut = new AimdFeedbackController(settings, clock.Now);

        // 1 サイクル目: BaselineSamples 到達させる（P95=100ms、成功 100%）
        clock.AdvanceSeconds(31); // StableWindow 超過させる
        var baselineSetup = MakeSnapshot(sampleCount: 20, rate429: 0.0, successRate: 1.0, p95LatencyMs: 100.0);
        var r1 = sut.Evaluate(baselineSetup);
        r1.Signal.Should().Be(AimdSignal.Stable); // ベースライン確立時は Stable
        sut.BaselineP95Ms.Should().Be(100.0);

        // 2 サイクル目: P95 悪化（130ms < 100 * 1.3 = 130 は境界。135 で確実に超える）
        clock.AdvanceSeconds(1);
        var worsened = MakeSnapshot(sampleCount: 20, rate429: 0.0, successRate: 1.0, p95LatencyMs: 135.0);
        var r2 = sut.Evaluate(worsened);

        r2.Signal.Should().Be(AimdSignal.SlowDecrease);
        r2.NewRate.Should().BeApproximately(r1.NewRate * 0.9, 1e-9);
    }

    [Fact]
    public void Evaluate_NotSlowDecrease_BeforeBaselineEstablished()
    {
        // 検証対象: ベースライン未確立  目的: P95 値があっても、BaselineSamples 未到達なら SlowDecrease 判定しない
        var clock = new FakeClock();
        var settings = MakeSettings(s => { s.BaselineSamples = 100; s.LatencyMode = LatencyEvaluationMode.Baseline; });
        var sut = new AimdFeedbackController(settings, clock.Now);

        clock.AdvanceSeconds(5);
        // SampleCount=10、SuccessRate=1.0 → 累積 10 < BaselineSamples=100 で未確立
        var result = sut.Evaluate(MakeSnapshot(sampleCount: 10, p95LatencyMs: 9999.0));

        result.Signal.Should().NotBe(AimdSignal.SlowDecrease);
        sut.BaselineP95Ms.Should().Be(0.0);
    }

    // ─── SlowDecrease（直近比） ────────────────────────────────

    [Fact]
    public void Evaluate_SlowDecrease_WhenRecentTrendWorsened()
    {
        // 検証対象: 直近比判定  目的: Recent モードで直近 P95 が前窓比 +30% 超なら SlowDecrease
        var clock = new FakeClock();
        var settings = MakeSettings(s => { s.LatencyMode = LatencyEvaluationMode.Recent; s.TrendWindowSec = 5; });
        var sut = new AimdFeedbackController(settings, clock.Now);

        // 前窓（10〜5秒前）に 100ms を詰める
        for (int i = 0; i < 3; i++)
        {
            clock.AdvanceSeconds(1);
            sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 100.0));
        }
        // 経過 3 秒。StableWindow 未経過なので Hold になるが、P95 履歴は積まれている

        // 後窓（直近 5 秒）に 200ms を詰める（直近比 200/100 = 2.0 > 1.3）
        clock.AdvanceSeconds(6); // 前窓との境界を跨がせる
        sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 200.0));
        clock.AdvanceSeconds(1);
        var result = sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 200.0));

        result.Signal.Should().Be(AimdSignal.SlowDecrease);
    }

    [Fact]
    public void Evaluate_LatencyMode_Hold_WhenInsufficientHistory()
    {
        // 検証対象: 直近比のサンプル不足  目的: 前窓 or 後窓に 0 件なら判定せず Hold / Stable 経路
        var clock = new FakeClock();
        var settings = MakeSettings(s => s.LatencyMode = LatencyEvaluationMode.Recent);
        var sut = new AimdFeedbackController(settings, clock.Now);

        clock.AdvanceSeconds(1);
        // 1 件だけ追加。前窓には何もない
        var result = sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 9999.0));

        result.Signal.Should().NotBe(AimdSignal.SlowDecrease);
    }

    // ─── Stable・AddStep ──────────────────────────────────────

    [Fact]
    public void Evaluate_Stable_AddsStep_AfterStableWindow()
    {
        // 検証対象: 緩増加  目的: StableWindowSec 経過・429 なし・クールダウン外で +AddStep
        var clock = new FakeClock();
        var sut = new AimdFeedbackController(MakeSettings(), clock.Now);
        clock.AdvanceSeconds(30);
        var result = sut.Evaluate(MakeSnapshot(sampleCount: 20, rate429: 0.0, p95LatencyMs: 100.0));

        result.Signal.Should().Be(AimdSignal.Stable);
        result.NewRate.Should().Be(11.0);
    }

    [Fact]
    public void Evaluate_Stable_ClampsAtMaxRate()
    {
        // 検証対象: 上限クランプ  目的: 増加で MaxRate を超えない
        var clock = new FakeClock();
        var settings = MakeSettings(s => { s.InitialRate = 99.5; s.MaxRate = 100.0; s.AddStep = 5.0; });
        var sut = new AimdFeedbackController(settings, clock.Now);
        clock.AdvanceSeconds(30);

        var result = sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 100.0));

        result.NewRate.Should().Be(100.0);
    }

    // ─── クールダウン ──────────────────────────────────────────

    [Fact]
    public void Evaluate_SuppressesStable_DuringCooldown()
    {
        // 検証対象: クールダウン中 Stable 抑制
        // 目的: stableElapsed が true を満たす条件でもクールダウン中は Stable が発火しないことを検証。
        // そのため StableWindowSec < CooldownSec に設定し、stableElapsed=true だが cooldownActive=true の
        // ウィンドウを作ってクールダウン抑制のみを切り分けて検証する。
        var clock = new FakeClock();
        var settings = MakeSettings(s => { s.StableWindowSec = 5; s.CooldownSec = 20; });
        var sut = new AimdFeedbackController(settings, clock.Now);

        // 1. EmergencyDecrease でクールダウン突入（_lastRate429Ticks も now にリセットされる）
        clock.AdvanceSeconds(1);
        var r1 = sut.Evaluate(MakeSnapshot(rate429: 0.5));
        r1.Signal.Should().Be(AimdSignal.EmergencyDecrease);

        // 2. StableWindowSec=5 を超える 6 秒経過（stableElapsed=true）だが、
        //    CooldownSec=20 の範囲内（cooldownActive=true）のため Stable 抑制により Hold になる。
        clock.AdvanceSeconds(6);
        var r2 = sut.Evaluate(MakeSnapshot(sampleCount: 20, rate429: 0.0, p95LatencyMs: 100.0));
        r2.Signal.Should().Be(AimdSignal.Hold);
        r2.InCooldown.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_AllowsStable_AfterCooldownExpires()
    {
        // 検証対象: クールダウン明け  目的: クールダウン経過後は Stable 発動可能
        var clock = new FakeClock();
        var sut = new AimdFeedbackController(MakeSettings(), clock.Now);

        clock.AdvanceSeconds(1);
        sut.Evaluate(MakeSnapshot(rate429: 0.5));

        // CooldownSec=20 + StableWindowSec=30 より大きい時間を進める
        clock.AdvanceSeconds(31);
        var result = sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 100.0));

        result.Signal.Should().Be(AimdSignal.Stable);
        result.InCooldown.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_EmergencyDecrease_HoldsDuringCooldown()
    {
        // 検証対象: クールダウン中の 429  目的: 1回削減後はクールダウン中に Hold を返しレートを変えない（様子見）
        var clock = new FakeClock();
        var sut = new AimdFeedbackController(MakeSettings(), clock.Now);

        clock.AdvanceSeconds(1);
        var r1 = sut.Evaluate(MakeSnapshot(rate429: 0.5));
        r1.Signal.Should().Be(AimdSignal.EmergencyDecrease);
        var rateAfterFirst = r1.NewRate;

        clock.AdvanceSeconds(5); // クールダウン中
        var r2 = sut.Evaluate(MakeSnapshot(rate429: 0.5));
        r2.Signal.Should().Be(AimdSignal.Hold);   // クールダウン中は Hold で様子見
        r2.NewRate.Should().Be(rateAfterFirst);   // レートはそれ以上下げない
        r2.InCooldown.Should().BeTrue();
    }

    // ─── ベースライン EMA ──────────────────────────────────────

    [Fact]
    public void Evaluate_BaselineEma_UpdatesOnlyOnStable()
    {
        // 検証対象: EMA 更新  目的: Stable 信号時にのみ baseline が EMA 更新される
        var clock = new FakeClock();
        var settings = MakeSettings(s => { s.BaselineSamples = 10; s.BaselineEmaAlpha = 0.5; });
        var sut = new AimdFeedbackController(settings, clock.Now);

        // ベースライン確立（Stable 時に初期化＋更新）
        clock.AdvanceSeconds(31);
        var r1 = sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 100.0));
        r1.Signal.Should().Be(AimdSignal.Stable);
        sut.BaselineP95Ms.Should().Be(100.0);

        // 次の Stable で EMA 更新: 100 * 0.5 + 110 * 0.5 = 105
        clock.AdvanceSeconds(31);
        var r2 = sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 110.0));
        r2.Signal.Should().Be(AimdSignal.Stable);
        sut.BaselineP95Ms.Should().BeApproximately(105.0, 1e-9);
    }

    [Fact]
    public void Evaluate_BaselineEma_FrozenDuringSlowDecrease()
    {
        // 検証対象: ベースライン凍結  目的: SlowDecrease 中は baseline を更新しない（悪化を正常と学習しない）
        var clock = new FakeClock();
        var settings = MakeSettings(s => { s.BaselineSamples = 10; });
        var sut = new AimdFeedbackController(settings, clock.Now);

        clock.AdvanceSeconds(31);
        sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 100.0));
        var baselineBefore = sut.BaselineP95Ms;

        // SlowDecrease を発動させる（閾値 +30% 超）
        clock.AdvanceSeconds(1);
        var r = sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 200.0));
        r.Signal.Should().Be(AimdSignal.SlowDecrease);
        sut.BaselineP95Ms.Should().Be(baselineBefore);
    }

    [Fact]
    public void Evaluate_BaselineNotEstablished_OutsideStablePath()
    {
        // 検証対象: ベースライン確立の経路限定
        // 目的: HasMinSamples=false の Hold・StableWindow 未経過の Hold では
        // ベースラインが確立されず、後続の SlowDecrease が誤発動しないこと。
        var clock = new FakeClock();
        var settings = MakeSettings(s => { s.BaselineSamples = 1; });
        var sut = new AimdFeedbackController(settings, clock.Now);

        // 1. HasMinSamples=false で Hold → ベースラインは確立しない
        clock.AdvanceSeconds(1);
        sut.Evaluate(MakeSnapshot(sampleCount: 3, hasMinSamples: false, p95LatencyMs: 100.0));
        sut.BaselineP95Ms.Should().Be(0.0);

        // 2. HasMinSamples=true でも StableWindow 未経過で Hold → ベースラインは確立しない
        clock.AdvanceSeconds(1);
        sut.Evaluate(MakeSnapshot(sampleCount: 20, rate429: 0.0, p95LatencyMs: 100.0));
        sut.BaselineP95Ms.Should().Be(0.0);

        // 3. StableWindow 経過で Stable → ここで初めてベースライン確立
        clock.AdvanceSeconds(30);
        var r = sut.Evaluate(MakeSnapshot(sampleCount: 20, rate429: 0.0, p95LatencyMs: 100.0));
        r.Signal.Should().Be(AimdSignal.Stable);
        sut.BaselineP95Ms.Should().Be(100.0);
    }

    [Fact]
    public void Evaluate_P95History_IgnoredWhenBelowMinSamples()
    {
        // 検証対象: サンプル不足時の P95 履歴非混入
        // 目的: HasMinSamples=false のサイクルでは P95 履歴にエントリが追加されず、
        // その後の Recent モード直近比判定に不安定値が影響しないこと。
        var clock = new FakeClock();
        var settings = MakeSettings(s => { s.LatencyMode = LatencyEvaluationMode.Recent; s.TrendWindowSec = 5; });
        var sut = new AimdFeedbackController(settings, clock.Now);

        // 前窓に該当する時間帯に「サンプル不足 + 異常に低い P95」を詰める（履歴に混入したら後続で大幅悪化と誤認される）
        for (int i = 0; i < 3; i++)
        {
            clock.AdvanceSeconds(1);
            sut.Evaluate(MakeSnapshot(sampleCount: 3, hasMinSamples: false, p95LatencyMs: 10.0));
        }

        // 直近窓に通常の P95=100 を投入
        clock.AdvanceSeconds(6);
        sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 100.0));
        clock.AdvanceSeconds(1);
        var r = sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 100.0));

        // 前窓に履歴がなければ previousCount=0 で判定不能となり SlowDecrease にならない。
        // 旧実装では前窓に 10ms が混入し、100/10 = 10 倍で誤発動していた。
        r.Signal.Should().NotBe(AimdSignal.SlowDecrease);
    }

    [Fact]
    public void Evaluate_EvaluatedAt_UsesSnapshotTimestamp()
    {
        // 検証対象: EvaluatedAt の時刻ソース
        // 目的: BuildResult は snapshot.Timestamp をそのまま返し、DateTimeOffset.UtcNow に依存しない。
        var clock = new FakeClock();
        var sut = new AimdFeedbackController(MakeSettings(), clock.Now);
        var fixedAt = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new SlidingWindowSnapshot(20, true, 0.0, 1.0, 100.0, 100.0,
            FilesPerSec: 0.0, BytesPerSec: 0.0, WindowSeconds: 30.0, Timestamp: fixedAt);

        var r = sut.Evaluate(snapshot);

        r.EvaluatedAt.Should().Be(fixedAt);
    }

    // ─── LatencyEvaluationMode = Both ─────────────────────────

    [Fact]
    public void Evaluate_BothMode_FiresOnEitherCondition()
    {
        // 検証対象: OR 条件  目的: Both モードは baseline または recent のどちらかの悪化で発動
        var clock = new FakeClock();
        var settings = MakeSettings(s => { s.LatencyMode = LatencyEvaluationMode.Both; s.BaselineSamples = 10; });
        var sut = new AimdFeedbackController(settings, clock.Now);

        // ベースライン確立
        clock.AdvanceSeconds(31);
        sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 100.0));
        sut.BaselineP95Ms.Should().Be(100.0);

        // ベースライン比のみ悪化（直近比は履歴が足りず判定不能）
        clock.AdvanceSeconds(1);
        var result = sut.Evaluate(MakeSnapshot(sampleCount: 20, p95LatencyMs: 200.0));
        result.Signal.Should().Be(AimdSignal.SlowDecrease);
    }

    // ─── FromRateControlSettings コピー ────────────────────────

    [Fact]
    public void FromRateControlSettings_MapsAllFields()
    {
        // 検証対象: 設定マッピング  目的: RateControlSettings → AimdFeedbackSettings の全フィールド反映
        var src = new RateControlSettings
        {
            InitialTokensPerSec = 15.0,
            MinTokensPerSec = 2.0,
            MaxTokensPerSec = 150.0,
            AimdEmergencyThreshold = 0.12,
            EmergencyDecay = 0.6,
            SlowDecay = 0.85,
            AddStep = 2.0,
            LatencyRiseRatio = 0.4,
            BaselineSamples = 30,
            BaselineEmaAlpha = 0.2,
            TrendWindowSec = 15,
            StableWindowSec = 45,
            CooldownSec = 25,
            LatencyEvaluationMode = LatencyEvaluationMode.Both,
        };

        var dst = AimdFeedbackSettings.FromRateControlSettings(src);

        dst.InitialRate.Should().Be(15.0);
        dst.MinRate.Should().Be(2.0);
        dst.MaxRate.Should().Be(150.0);
        dst.EmergencyThreshold.Should().Be(0.12);
        dst.EmergencyDecay.Should().Be(0.6);
        dst.SlowDecay.Should().Be(0.85);
        dst.AddStep.Should().Be(2.0);
        dst.LatencyRiseRatio.Should().Be(0.4);
        dst.BaselineSamples.Should().Be(30);
        dst.BaselineEmaAlpha.Should().Be(0.2);
        dst.TrendWindowSec.Should().Be(15);
        dst.StableWindowSec.Should().Be(45);
        dst.CooldownSec.Should().Be(25);
        dst.LatencyMode.Should().Be(LatencyEvaluationMode.Both);
    }
}
