using CloudMigrator.Core.Transfer;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// AdaptiveConcurrencyController のユニットテスト。
/// </summary>
public sealed class AdaptiveConcurrencyControllerTests
{
    private static AdaptiveConcurrencyController CreateController(
        int initial = 4,
        int min = 1,
        int max = 4,
        int increaseIntervalSec = 0) =>
        new(initial, min, max, increaseIntervalSec,
            Mock.Of<ILogger<AdaptiveConcurrencyController>>());

    // ─── 初期状態 ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_SetsCurrentDegreeToInitialDegree()
    {
        // 検証対象: CurrentDegree  目的: コンストラクタ後の初期値が initialDegree と一致すること
        var controller = CreateController(initial: 3, max: 4);
        controller.CurrentDegree.Should().Be(3);
    }

    [Fact]
    public void Constructor_ExposesMaxAndMinDegree()
    {
        // 検証対象: MaxDegree / MinDegree  目的: コンストラクタで設定した上限・下限が参照できること
        var controller = CreateController(initial: 3, min: 2, max: 8);
        controller.MaxDegree.Should().Be(8);
        controller.MinDegree.Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenMinDegreeIsLessThanOne(int minDegree)
    {
        // 検証対象: コンストラクタバリデーション  目的: minDegree < 1 は ArgumentOutOfRangeException になること
        Action act = () => CreateController(min: minDegree, max: 4);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_Throws_WhenMaxDegreeIsLessThanMinDegree()
    {
        // 検証対象: コンストラクタバリデーション  目的: max < min は ArgumentOutOfRangeException になること
        Action act = () => CreateController(min: 3, max: 2);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_Throws_WhenIncreaseIntervalSecIsNegative()
    {
        // 検証対象: コンストラクタバリデーション  目的: increaseIntervalSec < 0 は ArgumentOutOfRangeException になること
        Action act = () => new AdaptiveConcurrencyController(
            4, 1, 4, -1,
            Mock.Of<ILogger<AdaptiveConcurrencyController>>());
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_ExposesIncreaseIntervalSec()
    {
        // 検証対象: IncreaseIntervalSec  目的: コンストラクタで設定した値が参照できること
        var controller = CreateController(increaseIntervalSec: 30);
        controller.IncreaseIntervalSec.Should().Be(30);
    }

    // ─── NotifyRateLimit ──────────────────────────────────────────────────────

    [Fact]
    public void NotifyRateLimit_DecreasesDegreeByMultiplier()
    {
        // 検証対象: NotifyRateLimit  目的: 現在の並列度が decreaseMultiplier（デフォルト 0.5）倍に削減されること
        var controller = CreateController(initial: 4, min: 1, max: 4);

        controller.NotifyRateLimit(retryAfter: null);

        controller.CurrentDegree.Should().Be(2); // 4 * 0.5 = 2
    }

    [Fact]
    public void NotifyRateLimit_PreventsIncreaseBeforeIntervalElapsed()
    {
        // 検証対象: NotifyRateLimit  目的: 減速後はインターバル時間経過前に NotifySuccess を呼んでも増速しないこと
        var controller = CreateController(initial: 4, min: 1, max: 4, increaseIntervalSec: 30);

        controller.NotifyRateLimit(null); // 減速: 4 * 0.5 = 2、_increaseAvailableAfterTicks = now + 30s

        // 時間が経過していないので増速しない
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(2); // 2 のまま
    }

    [Fact]
    public void NotifyRateLimit_DoesNotGoBelowMinDegree()
    {
        // 検証対象: NotifyRateLimit  目的: MinDegree の下限を守ること
        var controller = CreateController(initial: 1, min: 1, max: 4);

        controller.NotifyRateLimit(null);
        controller.NotifyRateLimit(null); // 連続しても min 以下にはならない

        controller.CurrentDegree.Should().Be(1);
    }

    [Fact]
    public async Task NotifyRateLimit_AbsorbsSlot_WhenSemaphoreHasFreeSlots()
    {
        // 検証対象: NotifyRateLimit → AbsorbSlotAsync  目的: セマフォに空きがある状態では吸収が完了すること
        var controller = CreateController(initial: 2, min: 1, max: 2, increaseIntervalSec: 0);

        // Rate limit: degree 2 → 1, AbsorbSlotAsync がバックグラウンドで起動
        controller.NotifyRateLimit(null);

        // 短時間待機（AbsorbSlotAsync が空きスロットを即座に取得できるため）
        var absorbed = await WaitUntilAsync(() => controller.AbsorbedSlotCount > 0, timeoutMs: 1000);
        absorbed.Should().BeTrue("空きスロットがあるため吸収がタイムアウト前に完了するはず");
        controller.AbsorbedSlotCount.Should().Be(1);
    }

    // ─── NotifySuccess ───────────────────────────────────────────────────────

    [Fact]
    public void NotifySuccess_DoesNotIncreaseAboveMaxDegree_WhenNothingAbsorbed()
    {
        // 検証対象: NotifySuccess  目的: 吸収済みスロットがない場合は MaxDegree を超えないこと
        var controller = CreateController(initial: 4, min: 1, max: 4, increaseIntervalSec: 0);

        controller.NotifySuccess();
        controller.NotifySuccess();

        controller.CurrentDegree.Should().Be(4);
    }

    [Fact]
    public void NotifySuccess_IncreasesCurrentDegree_FromInitialHeadroomWithoutAbsorption()
    {
        // 検証対象: NotifySuccess  目的: initialDegree < maxDegree のソフトスタート時も NotifySuccess 1 回で増加できること
        // increaseIntervalSec: 0 なので即時増速可能
        var controller = CreateController(initial: 2, min: 1, max: 4, increaseIntervalSec: 0);

        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(3); // 即時増加
    }

    [Fact]
    public async Task NotifySuccess_IncreasesCurrentDegree_AfterAbsorptionAndIntervalElapsed()
    {
        // 検証対象: NotifySuccess  目的: 吸収済みスロットがある状態でインターバル経過後に並列度が回復すること
        var controller = CreateController(initial: 2, min: 1, max: 2, increaseIntervalSec: 30);

        // rate limit: degree 2→1、吸収を待機
        controller.NotifyRateLimit(null);
        await WaitUntilAsync(() => controller.AbsorbedSlotCount > 0, timeoutMs: 1000);

        // インターバル経過前は増速しない
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(1);

        // SetIncreaseAvailableNow で待機解除→増速
        controller.SetIncreaseAvailableNow();
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(2);
    }

    [Fact]
    public async Task NotifySuccess_DoesNotThrowOrIncrease_WhenDecreaseAbsorptionIsStillPending()
    {
        // 検証対象: NotifySuccess  目的: 減速直後で吸収未完了の間は回復せず、SemaphoreFullException も起こさないこと
        var controller = CreateController(initial: 4, min: 1, max: 4, increaseIntervalSec: 0);

        await controller.AcquireAsync(CancellationToken.None);
        await controller.AcquireAsync(CancellationToken.None);
        await controller.AcquireAsync(CancellationToken.None);
        await controller.AcquireAsync(CancellationToken.None);

        controller.NotifyRateLimit(null); // 4 -> 2（4 * 0.5 = 2）、ただし吸収は in-flight のため未完了

        // SetIncreaseAvailableNow で待機解除して増速を試みても、吸収未完なので増速しない
        controller.SetIncreaseAvailableNow();
        controller.NotifySuccess();

        controller.CurrentDegree.Should().Be(2);

        controller.Release();
        controller.Release();
        controller.Release();
        controller.Release();

        var absorbed = await WaitUntilAsync(() => controller.AbsorbedSlotCount == 2, timeoutMs: 1000);
        absorbed.Should().BeTrue("解放後にバックグラウンド吸収が完了するはず");
    }

    [Fact]
    public async Task NotifySuccess_RespectsIntervalAfterIncrease()
    {
        // 検証対象: NotifySuccess  目的: 増速後に再度インターバル待機が起き、SetIncreaseAvailableNow 後に次の増速が可能なこと
        var controller = CreateController(initial: 4, min: 1, max: 4, increaseIntervalSec: 30);

        // rate limit ×2 → degree 1（4*0.5=2、2*0.5=1）、吸収を待機（計3スロット）
        controller.NotifyRateLimit(null);
        controller.NotifyRateLimit(null);
        await WaitUntilAsync(() => controller.AbsorbedSlotCount >= 2, timeoutMs: 1000);

        // 1 回目の増速（1→2）
        controller.SetIncreaseAvailableNow();
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(2);

        // 増速後は即座に増速できない（インターバル待機）
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(2);

        // SetIncreaseAvailableNow 後は再増速可能（2→3）
        controller.SetIncreaseAvailableNow();
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(3);
    }

    // ─── AcquireAsync / Release ──────────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_LimitsConcurrency_ToCurrentDegree()
    {
        // 検証対象: AcquireAsync  目的: CurrentDegree 個のスロットしか同時取得できないこと
        var controller = CreateController(initial: 2, min: 1, max: 2, increaseIntervalSec: 0);

        // 2 スロット取得（これが上限）
        await controller.AcquireAsync(CancellationToken.None);
        await controller.AcquireAsync(CancellationToken.None);

        // 3 つ目は即座には取得できない
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var blocked = controller.AcquireAsync(cts.Token);

        // blocked タスク自体がキャンセルされることを直接検証する
        await blocked.Invoking(async t => await t)
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Release_AllowsNextAcquire()
    {
        // 検証対象: Release  目的: Release 後に次の AcquireAsync が成功すること
        var controller = CreateController(initial: 1, min: 1, max: 1, increaseIntervalSec: 0);

        await controller.AcquireAsync(CancellationToken.None);
        controller.Release();

        // リリース後は再度取得できる
        var acquireTask = controller.AcquireAsync(CancellationToken.None);
        await acquireTask.WaitAsync(TimeSpan.FromMilliseconds(200));
        acquireTask.IsCompletedSuccessfully.Should().BeTrue();
    }

    // ─── RateLimitCount ──────────────────────────────────────────────────────

    [Fact]
    public void RateLimitCount_StartsAtZero()
    {
        // 検証対象: RateLimitCount  目的: 初期値が 0 であること
        var controller = CreateController();
        controller.RateLimitCount.Should().Be(0);
    }

    [Fact]
    public void RateLimitCount_IncrementsOnEachNotifyRateLimit()
    {
        // 検証対象: RateLimitCount  目的: NotifyRateLimit 呼び出しのたびにカウントが増加すること
        var controller = CreateController(initial: 4, min: 1, max: 4);
        controller.NotifyRateLimit(null);
        controller.NotifyRateLimit(null);
        controller.NotifyRateLimit(null);
        controller.RateLimitCount.Should().Be(3);
    }

    [Fact]
    public void RateLimitCount_IncrementsEvenAtMinDegree()
    {
        // 検証対象: RateLimitCount  目的: 並列度が MinDegree の場合もカウントされること（並列度は減らないがカウントは増える）
        var controller = CreateController(initial: 1, min: 1, max: 4);
        controller.NotifyRateLimit(null); // min なので CurrentDegree は変わらない
        controller.RateLimitCount.Should().Be(1);
        controller.CurrentDegree.Should().Be(1);
    }

    // ─── decreaseTriggerCount ────────────────────────────────────────────────

    [Fact]
    public void NotifyRateLimit_WithDecreaseTriggerCount2_DecreasesOnlyOnSecondNotify()
    {
        // 検証対象: decreaseTriggerCount  目的: 2 回目の NotifyRateLimit で初めて並列度が下がること
        var controller = new AdaptiveConcurrencyController(
            4, 1, 4, 30,
            Mock.Of<ILogger<AdaptiveConcurrencyController>>(),
            decreaseTriggerCount: 2);

        controller.NotifyRateLimit(null); // 1 回目: まだ下がらない
        controller.CurrentDegree.Should().Be(4);

        controller.NotifyRateLimit(null); // 2 回目: 減速（4 * 0.5 = 2）
        controller.CurrentDegree.Should().Be(2);
    }

    [Fact]
    public void NotifyRateLimit_WithDecreaseTriggerCount2_ResetsTriggerCounterAfterDecrease()
    {
        // 検証対象: decreaseTriggerCount  目的: 減速発火後にカウンターがリセットされ、さらに 2 回必要になること
        var controller = new AdaptiveConcurrencyController(
            4, 1, 4, 30,
            Mock.Of<ILogger<AdaptiveConcurrencyController>>(),
            decreaseTriggerCount: 2);

        controller.NotifyRateLimit(null);
        controller.NotifyRateLimit(null); // 1 回目の減速: 4→2（4 * 0.5 = 2）
        controller.CurrentDegree.Should().Be(2);

        controller.NotifyRateLimit(null); // カウンター 1: まだ下がらない
        controller.CurrentDegree.Should().Be(2);

        controller.NotifyRateLimit(null); // カウンター 2: 2 回目の減速: 2→1（2 * 0.5 = 1）
        controller.CurrentDegree.Should().Be(1);
    }

    // ─── decreaseMultiplier ──────────────────────────────────────────────────

    [Fact]
    public void NotifyRateLimit_WithDecreaseMultiplier025_QuartersAndCeils()
    {
        // 検証対象: decreaseMultiplier  目的: multiplier=0.25 のとき 4*0.25=1.0→Ceiling→1 になること
        var controller = new AdaptiveConcurrencyController(
            4, 1, 4, 30,
            Mock.Of<ILogger<AdaptiveConcurrencyController>>(),
            decreaseMultiplier: 0.25);

        controller.NotifyRateLimit(null);
        controller.CurrentDegree.Should().Be(1);
    }

    [Fact]
    public void NotifyRateLimit_WithOddDegreeAndDecreaseMultiplier05_UsesCeiling()
    {
        // 検証対象: decreaseMultiplier（Ceiling 丸め）  目的: 3 * 0.5 = 1.5 → Ceiling → 2 になること（切り捨てではなく切り上げ）
        var controller = new AdaptiveConcurrencyController(
            3, 1, 3, 30,
            Mock.Of<ILogger<AdaptiveConcurrencyController>>(),
            decreaseMultiplier: 0.5);

        controller.NotifyRateLimit(null);
        controller.CurrentDegree.Should().Be(2); // Ceiling(3*0.5=1.5)=2（切り捨てなら1になる）
    }

    // ─── increaseStep ────────────────────────────────────────────────────────

    [Fact]
    public async Task NotifySuccess_WithIncreaseStep2_IncreasesByTwo()
    {
        // 検証対象: increaseStep  目的: SetIncreaseAvailableNow 後に並列度が 2 上がること
        var controller = new AdaptiveConcurrencyController(
            4, 1, 4, 30,
            Mock.Of<ILogger<AdaptiveConcurrencyController>>(),
            increaseStep: 2);

        // rate limit ×2 → degree 1（4*0.5=2、2*0.5=1）, absorbed = 3
        controller.NotifyRateLimit(null);
        controller.NotifyRateLimit(null);
        await WaitUntilAsync(() => controller.AbsorbedSlotCount >= 2, timeoutMs: 1000);

        // SetIncreaseAvailableNow 後に 1 回成功で +2 回復（1+2=3、absorb残り1）
        controller.SetIncreaseAvailableNow();
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(3);
    }

    [Fact]
    public async Task NotifySuccess_WithIncreaseStep_ClampsAtMaxDegree()
    {
        // 検証対象: increaseStep  目的: step が大きくても MaxDegree を超えないこと
        var controller = new AdaptiveConcurrencyController(
            4, 1, 4, 30,
            Mock.Of<ILogger<AdaptiveConcurrencyController>>(),
            increaseStep: 5);

        controller.NotifyRateLimit(null); // degree 3
        await WaitUntilAsync(() => controller.AbsorbedSlotCount >= 1, timeoutMs: 1000);

        controller.SetIncreaseAvailableNow();
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(4); // 3+5 → clamp → 4
    }

    // ─── Dispose ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // 検証対象: Dispose  目的: 正常に Dispose できること
        var controller = CreateController();
        Action act = () => controller.Dispose();
        act.Should().NotThrow();
    }

    // ─── InitialDegree ソフトスタート ──────────────────────────────────────────────

    [Fact]
    public void NotifySuccess_SoftStart_IncreasesFromInitialDegreeToMax()
    {
        // 検証対象: InitialDegree (ソフトスタート)  目的: initialDegree < maxDegree の場合、NotifySuccess ごとに 1 回ずつ増加できること
        var controller = CreateController(initial: 1, min: 1, max: 4, increaseIntervalSec: 0);
        controller.CurrentDegree.Should().Be(1);

        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(2);

        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(3);

        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(4); // max に到達

        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(4); // max を超えない
    }

    [Fact]
    public void NotifySuccess_SoftStart_RespectsIntervalBetweenIncreases()
    {
        // 検証対象: InitialDegree のインターバル  目的: increaseIntervalSec > 0 の場合、ソフトスタート中もインターバル内の連続呼び出しでは増加しないこと
        var controller = CreateController(initial: 1, min: 1, max: 4, increaseIntervalSec: 30);

        // 最初は即時増速可能（long.MinValue）
        controller.NotifySuccess(); // 1→2
        controller.CurrentDegree.Should().Be(2);

        // 増速後はインターバル待機中
        controller.NotifySuccess(); // 増加しない
        controller.CurrentDegree.Should().Be(2);

        // SetIncreaseAvailableNow 後に増速再開
        controller.SetIncreaseAvailableNow();
        controller.NotifySuccess(); // 2→3
        controller.CurrentDegree.Should().Be(3);
    }

    // ─── Retry-After 加算 ──────────────────────────────────────────────────────────

    [Fact]
    public void NotifyRateLimit_WithRetryAfter_PreventsIncreaseBeforeRetryAfterElapses()
    {
        // 検証対象: Retry-After 加算  目的: Retry-After 付きのレート制限後、即座に NotifySuccess しても増速しないこと
        var controller = CreateController(initial: 4, min: 1, max: 4, increaseIntervalSec: 30);

        // Retry-After = 60秒 + IncreaseIntervalSec 30秒 = 合計90秒待機、減速: 4 * 0.5 = 2
        controller.NotifyRateLimit(retryAfter: TimeSpan.FromSeconds(60));

        // SetIncreaseAvailableNow なしでは増速しない
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(2);
    }

    // ─── ヘルパー ─────────────────────────────────────────────────────────────

    /// <summary>条件が true になるまで短いポーリングで待機する。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(5);
        }
        return condition();
    }
}
