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
            FilesPerSec: 0.0,
            BytesPerSec: 0.0,
            WindowSeconds: 30.0,
            Timestamp: DateTimeOffset.UtcNow);

        public long LastBytes { get; private set; }
        public int SuccessCalls { get; private set; }

        public void NotifyRequestSent() { }
        public void NotifySuccess(TimeSpan latency, long bytes = 0)
        {
            SuccessCalls++;
            LastBytes = bytes;
        }
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

    // ─── ウォームスタート（initialMaxInflight） ───────────────────

    [Theory]
    [InlineData(null, 8, 8)]   // 未指定 → 設定値
    [InlineData(5, 8, 5)]      // 範囲内 → そのまま
    [InlineData(1, 8, 2)]      // < MinInflight=2 → クランプ
    [InlineData(99, 8, 8)]     // > MaxInflight=8 → クランプ
    public async Task Constructor_ClampsInitialMaxInflight(int? initial, int maxInflight, int expected)
    {
        var settings = MakeSettings(s => { s.MaxInflight = maxInflight; s.MinInflight = 2; });
        var bucket = new WeightedTokenBucket(10.0, 100.0);
        await using var controller = new HybridRateController(
            bucket, new FakeAimd(), new FakeMetrics(),
            settings, null, null,
            NullLogger<HybridRateController>.Instance,
            initialMaxInflight: initial);

        controller.CurrentMaxInflight.Should().Be(expected);
    }

    // ─── 縮小予約（_shrinkDebt）の消化／相殺 ─────────────────────

    [Fact]
    public async Task EmergencyDecrease_ConsumesReleaseWithoutOverShoot()
    {
        // 縮小直後にワーカーが Release してきても、_shrinkDebt が消化することで
        // SemaphoreSlim.CurrentCount が上限を超えないことを確認する。
        var settings = MakeSettings(s =>
        {
            s.MaxInflight = 4;
            s.MinInflight = 2;
            s.EmergencyInflightDecay = 0.5; // 4 → 2
        });
        var aimd = new FakeAimd();
        await using var controller = Build(settings, aimd, new FakeMetrics(), out _);

        // 4 つすべて Acquire（CurrentCount=0）
        for (int i = 0; i < 4; i++)
            await controller.AcquireAsync(CancellationToken.None);

        // 縮小: 4 → 2、_shrinkDebt = 2
        aimd.NextSignal = AimdSignal.EmergencyDecrease;
        controller.RunControlCycle();
        controller.CurrentMaxInflight.Should().Be(2);

        // 4 個 Release: 最初の 2 個は _shrinkDebt で消化、残り 2 個が実 Release
        for (int i = 0; i < 4; i++)
            controller.Release();

        // 仮想上限 2 を超えて Acquire できないことを検証
        await controller.AcquireAsync(CancellationToken.None);
        await controller.AcquireAsync(CancellationToken.None);
        var third = controller.AcquireAsync(CancellationToken.None);
        third.IsCompleted.Should().BeFalse("縮小後の virtualMaxInflight=2 を超えて Acquire してはならない");

        controller.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(2));
        controller.Release();
        controller.Release();
    }

    [Fact]
    public async Task Stable_AfterShrink_CancelsShrinkDebtBeforePhysicalRelease()
    {
        // 縮小直後の Stable 拡大は、まず _shrinkDebt を相殺し、
        // 残った分だけ inflightSlots.Release で物理補充する。
        var settings = MakeSettings(s =>
        {
            s.MaxInflight = 4;
            s.MinInflight = 1;
            s.EmergencyInflightDecay = 0.5; // 4 → 2
        });
        var aimd = new FakeAimd();
        await using var controller = Build(settings, aimd, new FakeMetrics(), out _);

        // すべて Acquire 中にしておく
        for (int i = 0; i < 4; i++)
            await controller.AcquireAsync(CancellationToken.None);

        // 縮小 4 → 2 (_shrinkDebt = 2)
        aimd.NextSignal = AimdSignal.EmergencyDecrease;
        controller.RunControlCycle();
        controller.CurrentMaxInflight.Should().Be(2);

        // Stable で +1 拡大: _shrinkDebt 2 → 1（物理 Release は発生しない）
        aimd.NextSignal = AimdSignal.Stable;
        controller.RunControlCycle();
        controller.CurrentMaxInflight.Should().Be(3);

        // Release を 4 回: _shrinkDebt(=1) を 1 個消化、残り 3 個は実 Release
        for (int i = 0; i < 4; i++)
            controller.Release();

        // 仮想上限 3 まで Acquire できるが、4 個目は待機
        await controller.AcquireAsync(CancellationToken.None);
        await controller.AcquireAsync(CancellationToken.None);
        await controller.AcquireAsync(CancellationToken.None);
        var fourth = controller.AcquireAsync(CancellationToken.None);
        fourth.IsCompleted.Should().BeFalse("拡大後 virtualMaxInflight=3 を超えて Acquire してはならない");

        controller.Release();
        await fourth.WaitAsync(TimeSpan.FromSeconds(2));
        controller.Release();
        controller.Release();
        controller.Release();
    }

    [Fact]
    public async Task EmergencyDecrease_WhenIdle_PhysicallyReclaimsAvailablePermits()
    {
        // アイドル時（Acquire 中スロットなし）に縮小信号が来た場合、
        // 余剰 permit は Wait(0) で物理回収され、_shrinkDebt は積まれない。
        // → 縮小直後に新仮想上限を超えて Acquire できないことで検証する。
        var settings = MakeSettings(s =>
        {
            s.MaxInflight = 4;
            s.MinInflight = 2;
            s.EmergencyInflightDecay = 0.5; // 4 → 2
        });
        var aimd = new FakeAimd();
        await using var controller = Build(settings, aimd, new FakeMetrics(), out _);

        // Acquire せず、すべて空き permit のまま縮小
        aimd.NextSignal = AimdSignal.EmergencyDecrease;
        controller.RunControlCycle();
        controller.CurrentMaxInflight.Should().Be(2);

        // 物理回収済みなら 2 個までしか取得できない
        await controller.AcquireAsync(CancellationToken.None);
        await controller.AcquireAsync(CancellationToken.None);
        var third = controller.AcquireAsync(CancellationToken.None);
        third.IsCompleted.Should().BeFalse("アイドル時縮小後は Wait(0) で物理回収され、新仮想上限 2 を超えて Acquire してはならない");

        controller.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(2));
        controller.Release();
        controller.Release();
    }

    [Fact]
    public async Task EmergencyDecrease_PartiallyBusy_ReclaimsAvailableAndAccruesRemainderToShrinkDebt()
    {
        // 一部 Acquire 中の状態で縮小すると、空き permit を Wait(0) で先に物理回収し、
        // 不足分のみ _shrinkDebt に積む。
        // 上限 4 / 2 個 Acquire 中 / EmergencyDecrease 0.25 → 上限 1（min=1） / delta=3
        // → 空き 2 を回収、残り 1 を _shrinkDebt に積む
        var settings = MakeSettings(s =>
        {
            s.MaxInflight = 4;
            s.MinInflight = 1;
            s.EmergencyInflightDecay = 0.25; // 4 → 1
        });
        var aimd = new FakeAimd();
        await using var controller = Build(settings, aimd, new FakeMetrics(), out _);

        // 2 個だけ Acquire（残り 2 個は空き）
        await controller.AcquireAsync(CancellationToken.None);
        await controller.AcquireAsync(CancellationToken.None);

        aimd.NextSignal = AimdSignal.EmergencyDecrease;
        controller.RunControlCycle();
        controller.CurrentMaxInflight.Should().Be(1);

        // Acquire 中の 2 個を Release: 1 個は _shrinkDebt 消化、1 個は実 Release
        controller.Release();
        controller.Release();

        // 上限 1 を超えて Acquire できないこと
        await controller.AcquireAsync(CancellationToken.None);
        var second = controller.AcquireAsync(CancellationToken.None);
        second.IsCompleted.Should().BeFalse("縮小後 virtualMaxInflight=1 を超えて Acquire してはならない");

        controller.Release();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        controller.Release();
    }

    // ─── #159 ウィンドウスループット ─────────────────────────────

    [Fact]
    public async Task NotifySuccess_ForwardsBytes_ToSlidingWindowMetrics()
    {
        // 検証対象: bytes 透過  目的: HybridRateController が内部 ISlidingWindowMetrics に bytes を渡す
        var aimd = new FakeAimd();
        var metrics = new FakeMetrics();
        var controller = Build(MakeSettings(), aimd, metrics, out _);
        await using var _disposer = controller;

        controller.NotifyRequestSent();
        controller.NotifySuccess(TimeSpan.FromMilliseconds(50), bytes: 1024);

        metrics.SuccessCalls.Should().Be(1);
        metrics.LastBytes.Should().Be(1024);
    }

    [Fact]
    public async Task GetCurrentSnapshot_ReturnsMetricsSnapshot()
    {
        // 検証対象: GetCurrentSnapshot  目的: ダッシュボード経路から最新スナップショットを取得できる
        var aimd = new FakeAimd();
        var metrics = new FakeMetrics
        {
            Snapshot = new SlidingWindowSnapshot(
                SampleCount: 30,
                HasMinSamples: true,
                Rate429: 0.0,
                SuccessRate: 1.0,
                AvgLatencyMs: 50.0,
                P95LatencyMs: 80.0,
                FilesPerSec: 1.5,
                BytesPerSec: 1500.0,
                WindowSeconds: 30.0,
                Timestamp: DateTimeOffset.UtcNow),
        };
        var controller = Build(MakeSettings(), aimd, metrics, out _);
        await using var _disposer = controller;

        var snap = controller.GetCurrentSnapshot();

        snap.FilesPerSec.Should().Be(1.5);
        snap.BytesPerSec.Should().Be(1500.0);
        snap.WindowSeconds.Should().Be(30.0);
    }
}
