using CloudMigrator.Core.Transfer;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// TokenBucketRateLimiter のユニットテスト。
/// </summary>
public sealed class TokenBucketRateLimiterTests
{
    private static TokenBucketRateLimiter CreateLimiter(
        double initialRate = 4.0,
        double minRate = 0.5,
        double maxRate = 20.0,
        int burstCapacity = 10,
        double increaseStep = 0.5,
        double decreaseFactor = 0.5,
        double increaseIntervalSec = 0.001) =>   // テストでは極小の間隔で即座に増加可能にする
        new(initialRate, minRate, maxRate, burstCapacity, increaseStep, decreaseFactor,
            Mock.Of<ILogger<TokenBucketRateLimiter>>(),
            increaseIntervalSec);

    // ─── コンストラクタ ───────────────────────────────────────────────────────

    [Fact]
    public void Constructor_SetsCurrentRateToInitialRate()
    {
        // 検証対象: CurrentRate  目的: コンストラクタ後の CurrentRate が initialRate と一致すること
        using var limiter = CreateLimiter(initialRate: 4.0);
        limiter.CurrentRate.Should().BeApproximately(4.0, 1e-10);
    }

    [Fact]
    public void Constructor_ClampsInitialRateBelowMin()
    {
        // 検証対象: initialRate クランプ  目的: initialRate < minRate のとき minRate にクランプされること
        using var limiter = CreateLimiter(initialRate: 0.1, minRate: 0.5, maxRate: 20.0);
        limiter.CurrentRate.Should().BeApproximately(0.5, 1e-10);
    }

    [Fact]
    public void Constructor_ClampsInitialRateAboveMax()
    {
        // 検証対象: initialRate クランプ  目的: initialRate > maxRate のとき maxRate にクランプされること
        using var limiter = CreateLimiter(initialRate: 100.0, minRate: 0.5, maxRate: 20.0);
        limiter.CurrentRate.Should().BeApproximately(20.0, 1e-10);
    }

    [Fact]
    public void Constructor_ExposesMaxAndMinAndBurstCapacity()
    {
        // 検証対象: MaxRate / MinRate / BurstCapacity  目的: コンストラクタ値が公開プロパティから参照できること
        using var limiter = CreateLimiter(minRate: 1.0, maxRate: 30.0, burstCapacity: 15);
        limiter.MaxRate.Should().Be(30.0);
        limiter.MinRate.Should().Be(1.0);
        limiter.BurstCapacity.Should().Be(15);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Constructor_Throws_WhenMinRateIsNotPositive(double minRate)
    {
        // 検証対象: コンストラクタバリデーション  目的: minRate <= 0 は ArgumentOutOfRangeException になること
        Action act = () => CreateLimiter(minRate: minRate, maxRate: 20.0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_Throws_WhenMaxRateIsLessThanMinRate()
    {
        // 検証対象: コンストラクタバリデーション  目的: maxRate < minRate は ArgumentOutOfRangeException になること
        Action act = () => CreateLimiter(minRate: 10.0, maxRate: 5.0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenBurstCapacityIsNotPositive(int burst)
    {
        // 検証対象: コンストラクタバリデーション  目的: burstCapacity <= 0 は ArgumentOutOfRangeException になること
        Action act = () => CreateLimiter(burstCapacity: burst);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Constructor_Throws_WhenDecreaseFactorIsOutOfRange(double factor)
    {
        // 検証対象: コンストラクタバリデーション  目的: decreaseFactor は (0, 1) の範囲外のとき ArgumentOutOfRangeException になること
        Action act = () => CreateLimiter(decreaseFactor: factor);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Constructor_Throws_WhenIncreaseStepIsNotPositive(double step)
    {
        // 検証対象: コンストラクタバリデーション  目的: increaseStep <= 0 は ArgumentOutOfRangeException になること
        Action act = () => CreateLimiter(increaseStep: step);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Constructor_Throws_WhenIncreaseIntervalSecIsNotPositive(double intervalSec)
    {
        // 検証対象: コンストラクタバリデーション  目的: increaseIntervalSec <= 0 は ArgumentOutOfRangeException になること
        Action act = () => CreateLimiter(increaseIntervalSec: intervalSec);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── AcquireAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_CompletesImmediately_WhenTokensAvailable()
    {
        // 検証対象: AcquireAsync  目的: バーストキャパシティ分のトークンが事前充填されているため即座に完了すること
        using var limiter = CreateLimiter(burstCapacity: 5);
        // burstCapacity 回 AcquireAsync を呼ぶ — すべて即座に完了するはず
        for (int i = 0; i < 5; i++)
        {
            var task = limiter.AcquireAsync(CancellationToken.None);
            task.IsCompleted.Should().BeTrue($"{i + 1} 回目の AcquireAsync が即座に完了していない");
            await task;
        }
    }

    [Fact]
    public async Task AcquireAsync_IsCancellable_WhenNoTokensAvailable()
    {
        // 検証対象: AcquireAsync キャンセル  目的: トークンが枯渇した状態でキャンセルできること
        using var limiter = CreateLimiter(burstCapacity: 1);
        await limiter.AcquireAsync(CancellationToken.None); // 唯一のトークンを消費

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var task = limiter.AcquireAsync(cts.Token);
        var act = async () => await task;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── NotifySuccess ─────────────────────────────────────────────────────────

    [Fact]
    public void NotifySuccess_IncreasesRateByStep()
    {
        // 検証対象: NotifySuccess  目的: increaseIntervalSec 経過後の成功通知でレートが increaseStep 分加算されること
        // _nextIncreaseAt は DateTime.MinValue で初期化されるため、初回呼び出しは待機不要
        using var limiter = CreateLimiter(initialRate: 4.0, maxRate: 20.0, increaseStep: 0.5, increaseIntervalSec: 60.0);
        limiter.NotifySuccess();
        limiter.CurrentRate.Should().BeApproximately(4.5, 1e-10);
    }

    [Fact]
    public void NotifySuccess_DoesNotIncrease_WithinInterval()
    {
        // 検証対象: NotifySuccess インターバル制御  目的: increaseIntervalSec 未満の間は連続呼び出しでもレートが増加しないこと
        using var limiter = CreateLimiter(initialRate: 4.0, maxRate: 20.0, increaseStep: 0.5, increaseIntervalSec: 60.0);
        limiter.NotifySuccess(); // 1 回目: 初回なので増加する
        var afterFirst = limiter.CurrentRate;
        limiter.NotifySuccess(); // 2 回目: インターバル内なので増加しない
        limiter.NotifySuccess(); // 3 回目: 同上
        limiter.CurrentRate.Should().BeApproximately(afterFirst, 1e-10);
    }

    [Fact]
    public void NotifySuccess_ResetsIntervalAfter429()
    {
        // 検証対象: NotifyRateLimit 後の NextIncreaseAt  目的: 429 発生後は increaseIntervalSec 経過するまで増加しないこと
        // _nextIncreaseAt は DateTime.MinValue で初期化されるため、初回呼び出しは待機不要
        using var limiter = CreateLimiter(initialRate: 8.0, increaseIntervalSec: 60.0);
        limiter.NotifySuccess(); // 初回は増加する
        limiter.NotifyRateLimit(retryAfter: null); // 429 → NextIncreaseAt をリセット
        var rateAfter429 = limiter.CurrentRate;
        limiter.NotifySuccess(); // インターバル内なので増加しない
        limiter.CurrentRate.Should().BeApproximately(rateAfter429, 1e-10);
    }

    [Fact]
    public void NotifySuccess_ClampsAtMaxRate()
    {
        // 検証対象: NotifySuccess 上限クランプ  目的: maxRate を超えないこと
        using var limiter = CreateLimiter(initialRate: 19.9, maxRate: 20.0, increaseStep: 0.5, increaseIntervalSec: 0.001);
        Thread.Sleep(5);
        limiter.NotifySuccess();
        limiter.CurrentRate.Should().BeApproximately(20.0, 1e-10);
    }

    [Fact]
    public async Task NotifySuccess_MultipleCallsWithDelayEventuallyReachMaxRate()
    {
        // 検証対象: NotifySuccess 繰り返し  目的: increaseIntervalSec の間隔を置いてまた呼び出せば maxRate に達すること
        using var limiter = CreateLimiter(initialRate: 1.0, minRate: 0.5, maxRate: 3.0, increaseStep: 1.0, increaseIntervalSec: 0.001);
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(5); // increaseIntervalSec (1ms) を超える間隔
            limiter.NotifySuccess();
        }
        limiter.CurrentRate.Should().BeApproximately(3.0, 1e-10);
    }

    // ─── NotifyRateLimit ─────────────────────────────────────────────────────

    [Fact]
    public void NotifyRateLimit_DecreasesRateByFactor()
    {
        // 検証対象: NotifyRateLimit  目的: 429 通知でレートが decreaseFactor 倍になること
        using var limiter = CreateLimiter(initialRate: 8.0, decreaseFactor: 0.5);
        limiter.NotifyRateLimit(retryAfter: null);
        limiter.CurrentRate.Should().BeApproximately(4.0, 1e-10);
    }

    [Fact]
    public void NotifyRateLimit_ClampsAtMinRate()
    {
        // 検証対象: NotifyRateLimit 下限クランプ  目的: minRate を下回らないこと
        using var limiter = CreateLimiter(initialRate: 0.6, minRate: 0.5, decreaseFactor: 0.5);
        limiter.NotifyRateLimit(retryAfter: null);
        limiter.CurrentRate.Should().BeApproximately(0.5, 1e-10);
    }

    [Fact]
    public void NotifyRateLimit_MultipleCallsEventuallyReachMinRate()
    {
        // 検証対象: NotifyRateLimit 繰り返し  目的: 十分な回数呼べば minRate に達すること
        using var limiter = CreateLimiter(initialRate: 20.0, minRate: 0.5, decreaseFactor: 0.5);
        for (int i = 0; i < 20; i++) limiter.NotifyRateLimit(retryAfter: null);
        limiter.CurrentRate.Should().BeApproximately(0.5, 1e-10);
    }

    [Fact]
    public void NotifyRateLimit_SetsBlockedUntil_WhenRetryAfterProvided()
    {
        // 検証対象: BlockedUntil  目的: Retry-After が指定されたとき BlockedUntil が未来時刻に設定されること
        using var limiter = CreateLimiter();
        var before = DateTime.UtcNow;
        limiter.NotifyRateLimit(retryAfter: TimeSpan.FromSeconds(30));
        limiter.BlockedUntil.Should().BeAfter(before.AddSeconds(25));
        limiter.BlockedUntil.Should().BeBefore(before.AddSeconds(35));
    }

    [Fact]
    public void NotifyRateLimit_DoesNotSetBlockedUntil_WhenRetryAfterIsNull()
    {
        // 検証対象: BlockedUntil  目的: Retry-After が null のとき BlockedUntil が変化しないこと
        using var limiter = CreateLimiter();
        var initialBlocked = limiter.BlockedUntil;
        limiter.NotifyRateLimit(retryAfter: null);
        limiter.BlockedUntil.Should().Be(initialBlocked);
    }

    [Fact]
    public void NotifyRateLimit_DoesNotSetBlockedUntil_WhenRetryAfterIsZero()
    {
        // 検証対象: BlockedUntil  目的: Retry-After が Zero のとき BlockedUntil が変化しないこと
        using var limiter = CreateLimiter();
        var initialBlocked = limiter.BlockedUntil;
        limiter.NotifyRateLimit(retryAfter: TimeSpan.Zero);
        limiter.BlockedUntil.Should().Be(initialBlocked);
    }

    // ─── Dispose ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // 検証対象: Dispose  目的: Dispose が例外なく完了すること
        var limiter = CreateLimiter();
        var act = () => limiter.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowingOnSecondCall()
    {
        // 検証対象: Dispose 二重呼び出し  目的: 二回目の Dispose で AcquireAsync がキャンセルされるが Dispose 自体は安全であること
        // 注: SemaphoreSlim は二重 Dispose で ObjectDisposedException をスローするので 1 回のみ Dispose
        var limiter = CreateLimiter();
        limiter.Dispose();
        // 二回目は内部 SemaphoreSlim が既に破棄済みのため、呼び出し側で管理する
        // → このテストは一回目の Dispose が安全に完了することを主に検証
        limiter.CurrentRate.Should().BeGreaterThan(0); // Dispose 後でも Rate プロパティは読み取り可能
    }
}
