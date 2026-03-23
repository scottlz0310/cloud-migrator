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
        int threshold = 10) =>
        new(initial, min, max, threshold,
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
    public void Constructor_Throws_WhenSuccessThresholdIsZero()
    {
        // 検証対象: コンストラクタバリデーション  目的: successThreshold < 1 は ArgumentOutOfRangeException になること
        Action act = () => new AdaptiveConcurrencyController(
            4, 1, 4, 0,
            Mock.Of<ILogger<AdaptiveConcurrencyController>>());
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── NotifyRateLimit ──────────────────────────────────────────────────────

    [Fact]
    public void NotifyRateLimit_DecreasesCurrentDegreeByOne()
    {
        // 検証対象: NotifyRateLimit  目的: 現在の並列度が 1 減少すること
        var controller = CreateController(initial: 4, min: 1, max: 4);

        controller.NotifyRateLimit(retryAfter: null);

        controller.CurrentDegree.Should().Be(3);
    }

    [Fact]
    public void NotifyRateLimit_ResetsConsecutiveSuccessCounter()
    {
        // 検証対象: NotifyRateLimit  目的: 連続成功カウンターがリセットされること（threshold-1 まで積み上げた後でリセット）
        var controller = CreateController(initial: 4, min: 1, max: 4, threshold: 5);

        // 4回成功（閾値未達）
        for (int i = 0; i < 4; i++)
            controller.NotifySuccess();

        // レート制限 → カウンターリセット
        controller.NotifyRateLimit(null);

        // 再度 5 回未満成功しても増加しないこと
        for (int i = 0; i < 4; i++)
            controller.NotifySuccess();

        controller.CurrentDegree.Should().Be(3); // 3 のまま（吸収なしなので回復しない）
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
        var controller = CreateController(initial: 2, min: 1, max: 2, threshold: 5);

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
        var controller = CreateController(initial: 4, min: 1, max: 4, threshold: 2);

        controller.NotifySuccess();
        controller.NotifySuccess();

        controller.CurrentDegree.Should().Be(4);
    }

    [Fact]
    public async Task NotifySuccess_IncreasesCurrentDegree_AfterAbsorptionAndThreshold()
    {
        // 検証対象: NotifySuccess  目的: 吸収済みスロットがある状態で閾値回数成功すると並列度が回復すること
        var controller = CreateController(initial: 2, min: 1, max: 2, threshold: 3);

        // rate limit → degree 2→1、吸収を待機
        controller.NotifyRateLimit(null);
        await WaitUntilAsync(() => controller.AbsorbedSlotCount > 0, timeoutMs: 1000);

        // 閾値未満の成功（2 回）
        controller.NotifySuccess();
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(1); // まだ回復しない

        // 閾値達成（3 回目）
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(2); // 回復
    }

    [Fact]
    public async Task NotifySuccess_ResetsConsecutiveCounter_AfterIncrease()
    {
        // 検証対象: NotifySuccess  目的: 回復後に連続成功カウンターがリセットされること
        var controller = CreateController(initial: 4, min: 1, max: 4, threshold: 2);

        // rate limit ×2 → degree 2、吸収を待機
        controller.NotifyRateLimit(null);
        controller.NotifyRateLimit(null);
        await WaitUntilAsync(() => controller.AbsorbedSlotCount >= 2, timeoutMs: 1000);

        // 1回目の回復（2 回成功で +1）
        controller.NotifySuccess();
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(3);

        // 2回目の回復（もう 2 回成功で +1）
        controller.NotifySuccess();
        controller.NotifySuccess();
        controller.CurrentDegree.Should().Be(4);
    }

    // ─── AcquireAsync / Release ──────────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_LimitsConcurrency_ToCurrentDegree()
    {
        // 検証対象: AcquireAsync  目的: CurrentDegree 個のスロットしか同時取得できないこと
        var controller = CreateController(initial: 2, min: 1, max: 2, threshold: 5);

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
        var controller = CreateController(initial: 1, min: 1, max: 1, threshold: 5);

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

    // ─── Dispose ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // 検証対象: Dispose  目的: 正常に Dispose できること
        var controller = CreateController();
        Action act = () => controller.Dispose();
        act.Should().NotThrow();
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
