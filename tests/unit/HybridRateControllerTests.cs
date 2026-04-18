using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Transfer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// <see cref="HybridRateController"/> のユニットテスト（#163）。
/// <para>
/// 制御ループ本体は 1 秒周期でバックグラウンド駆動されるが、
/// 決定的なテストのため <c>internal</c> メソッド <c>RunControlCycle()</c> を単発呼び出しで検証する。
/// </para>
/// </summary>
public sealed class HybridRateControllerTests
{
    // ─── フェイク ───────────────────────────────────────────────

    private sealed class FakeAimd : IAimdFeedbackController
    {
        public AimdSignal NextSignal { get; set; } = AimdSignal.Hold;
        public double NextRate { get; set; } = 10.0;
        public bool NextInCooldown { get; set; }
        public int EvaluateCallCount { get; private set; }

        public double CurrentRate => NextRate;
        public bool InCooldown => NextInCooldown;
        public double BaselineP95Ms => 0.0;

        public AimdEvaluation Evaluate(SlidingWindowSnapshot snapshot)
        {
            EvaluateCallCount++;
            return new AimdEvaluation(
                Signal: NextSignal,
                PreviousRate: NextRate,
                NewRate: NextRate,
                BaselineP95Ms: 0.0,
                InCooldown: NextInCooldown,
                Snapshot: snapshot,
                EvaluatedAt: DateTimeOffset.UtcNow);
        }
    }

    private sealed class FakeMetrics : ISlidingWindowMetrics
    {
        public SlidingWindowSnapshot Snapshot { get; set; } = new(
            SampleCount: 50,
            HasMinSamples: true,
            Rate429: 0.0,
            SuccessRate: 1.0,
            AvgLatencyMs: 100.0,
            P95LatencyMs: 100.0,
            Timestamp: DateTimeOffset.UtcNow);

        public void NotifyRequestSent() { }
        public void NotifySuccess(TimeSpan latency) { }
        public void NotifyRateLimit(TimeSpan? retryAfter) { }
        public SlidingWindowSnapshot GetSnapshot() => Snapshot;
    }

    // ─── ヘルパー ───────────────────────────────────────────────

    private static RateControlSettings MakeSettings(Action<RateControlSettings>? configure = null)
    {
        var s = new RateControlSettings
        {
            // 制御ループが頻繁に走ると計測値が安定しない。テストでは十分長い周期に設定し、
            // RunControlCycle() を手動で呼び出して検証する。
            ControlIntervalSec = 3600,
            MaxInflight = 8,
            MinInflight = 2,
            EmergencyInflightDecay = 0.5,
        };
        configure?.Invoke(s);
        return s;
    }

    private static HybridRateController Build(
        RateControlSettings settings,
        FakeAimd aimd,
        FakeMetrics metrics,
        out WeightedTokenBucket bucket)
    {
        bucket = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 100.0);
        return new HybridRateController(
            bucket: bucket,
            aimd: aimd,
            metrics: metrics,
            settings: settings,
            metricsBuffer: null,
            stateStore: null,
            logger: NullLogger<HybridRateController>.Instance);
    }

    // ─── RunControlCycle ─────────────────────────────────────────

    [Fact]
    public async Task RunControlCycle_UpdatesBucketRate_FromEvaluation()
    {
        var settings = MakeSettings();
        var aimd = new FakeAimd { NextRate = 42.5, NextSignal = AimdSignal.Hold };
        var metrics = new FakeMetrics();

        await using var controller = Build(settings, aimd, metrics, out var bucket);

        controller.RunControlCycle();

        bucket.CurrentRate.Should().Be(42.5);
        aimd.EvaluateCallCount.Should().Be(1);
    }

    [Fact]
    public async Task RunControlCycle_EmergencyDecrease_ShrinksMaxInflight()
    {
        var settings = MakeSettings(s =>
        {
            s.MaxInflight = 8;
            s.MinInflight = 2;
            s.EmergencyInflightDecay = 0.5; // 8 → floor(8*0.5)=4
        });
        var aimd = new FakeAimd { NextSignal = AimdSignal.EmergencyDecrease };
        var metrics = new FakeMetrics();

        await using var controller = Build(settings, aimd, metrics, out _);

        controller.CurrentMaxInflight.Should().Be(8);
        controller.RunControlCycle();
        controller.CurrentMaxInflight.Should().Be(4);
    }

    [Fact]
    public async Task RunControlCycle_EmergencyDecrease_ClampsToMinInflight()
    {
        var settings = MakeSettings(s =>
        {
            s.MaxInflight = 4;
            s.MinInflight = 3;
            s.EmergencyInflightDecay = 0.5; // 4*0.5=2 だが MinInflight=3 でクランプ
        });
        var aimd = new FakeAimd { NextSignal = AimdSignal.EmergencyDecrease };
        var metrics = new FakeMetrics();

        await using var controller = Build(settings, aimd, metrics, out _);

        controller.RunControlCycle();
        controller.CurrentMaxInflight.Should().Be(3);
    }

    [Fact]
    public async Task RunControlCycle_Stable_IncrementsMaxInflight_CappedAtConfiguredMax()
    {
        var settings = MakeSettings(s =>
        {
            s.MaxInflight = 4;
            s.MinInflight = 2;
            s.EmergencyInflightDecay = 0.5;
        });
        var aimd = new FakeAimd { NextSignal = AimdSignal.EmergencyDecrease };
        var metrics = new FakeMetrics();

        await using var controller = Build(settings, aimd, metrics, out _);

        controller.RunControlCycle(); // 4 → 2
        controller.CurrentMaxInflight.Should().Be(2);

        aimd.NextSignal = AimdSignal.Stable;
        controller.RunControlCycle(); // 2 → 3
        controller.CurrentMaxInflight.Should().Be(3);
        controller.RunControlCycle(); // 3 → 4
        controller.CurrentMaxInflight.Should().Be(4);
        controller.RunControlCycle(); // 4 → 4（上限）
        controller.CurrentMaxInflight.Should().Be(4);
    }

    [Fact]
    public async Task RunControlCycle_Hold_DoesNotChangeMaxInflight()
    {
        var settings = MakeSettings();
        var aimd = new FakeAimd { NextSignal = AimdSignal.Hold };
        var metrics = new FakeMetrics();

        await using var controller = Build(settings, aimd, metrics, out _);
        var initial = controller.CurrentMaxInflight;

        controller.RunControlCycle();
        controller.RunControlCycle();
        controller.RunControlCycle();

        controller.CurrentMaxInflight.Should().Be(initial);
    }

    [Fact]
    public async Task RunControlCycle_SlowDecrease_DoesNotChangeMaxInflight()
    {
        var settings = MakeSettings();
        var aimd = new FakeAimd { NextSignal = AimdSignal.SlowDecrease };
        var metrics = new FakeMetrics();

        await using var controller = Build(settings, aimd, metrics, out _);
        var initial = controller.CurrentMaxInflight;

        controller.RunControlCycle();
        controller.CurrentMaxInflight.Should().Be(initial,
            "SlowDecrease はレートのみ変更し max_inflight は据え置くのが§4.3 の仕様");
    }

    // ─── AcquireAsync / Release ───────────────────────────────────

    [Fact]
    public async Task AcquireAsync_AcquiresBothGates_SemaphoreThenBucket()
    {
        var settings = MakeSettings(s => s.MaxInflight = 2);
        var aimd = new FakeAimd();
        var metrics = new FakeMetrics();

        await using var controller = Build(settings, aimd, metrics, out _);

        await controller.AcquireAsync(CancellationToken.None);
        await controller.AcquireAsync(CancellationToken.None);

        // 3 件目は MaxInflight=2 を超えるためブロックされる
        var third = controller.AcquireAsync(CancellationToken.None);
        third.IsCompleted.Should().BeFalse("MaxInflight を超えたら待機する");

        controller.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(2));
        third.IsCompletedSuccessfully.Should().BeTrue();

        controller.Release();
        controller.Release();
    }

    // ─── コンストラクタ・バリデーション ──────────────────────────

    [Theory]
    [InlineData(0, 2, 0.5, "MaxInflight")]
    [InlineData(4, 0, 0.5, "MinInflight")]
    [InlineData(4, 5, 0.5, "MinInflight > MaxInflight")]
    [InlineData(4, 2, 0.0, "EmergencyInflightDecay = 0")]
    [InlineData(4, 2, 1.0, "EmergencyInflightDecay = 1")]
    [InlineData(4, 2, -0.1, "EmergencyInflightDecay < 0")]
    public void Constructor_RejectsInvalidSettings(int maxInflight, int minInflight, double decay, string reason)
    {
        var settings = new RateControlSettings
        {
            MaxInflight = maxInflight,
            MinInflight = minInflight,
            EmergencyInflightDecay = decay,
            ControlIntervalSec = 3600,
        };
        var bucket = new WeightedTokenBucket(10.0, 100.0);

        var act = () => new HybridRateController(
            bucket, new FakeAimd(), new FakeMetrics(),
            settings, null, null,
            NullLogger<HybridRateController>.Instance);

        act.Should().Throw<ArgumentOutOfRangeException>(because: reason);
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var settings = MakeSettings();
        var controller = Build(settings, new FakeAimd(), new FakeMetrics(), out _);

        await controller.DisposeAsync();
        await controller.DisposeAsync(); // 2 回目は no-op
    }
}
