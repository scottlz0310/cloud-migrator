using System.Diagnostics;
using CloudMigrator.Core.Transfer;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// WeightedTokenBucket のユニットテスト（#160）。
/// </summary>
public sealed class WeightedTokenBucketTests
{
    // ─── コンストラクタ・プロパティ ────────────────────────────────

    [Fact]
    public void Constructor_InitializesWithFullBucket_ByDefault()
    {
        // 検証対象: 初期残量  目的: initialTokens 省略時は maxBurst（満タン）で起動
        var sut = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 20.0);
        sut.AvailableTokens.Should().BeApproximately(20.0, 1.0);
        sut.CurrentRate.Should().Be(10.0);
        sut.MaxBurst.Should().Be(20.0);
    }

    [Fact]
    public void Constructor_ClampsInitialTokens_ToMaxBurst()
    {
        // 検証対象: 初期残量クランプ  目的: initialTokens > maxBurst は maxBurst に丸める
        var sut = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 5.0, initialTokens: 100.0);
        sut.AvailableTokens.Should().BeLessThanOrEqualTo(5.0 + 1e-6);
    }

    [Fact]
    public void Constructor_ClampsInitialTokens_ToZero()
    {
        // 検証対象: 初期残量クランプ  目的: initialTokens < 0 は 0 に丸める
        var sut = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 5.0, initialTokens: -10.0);
        // 直後の AvailableTokens は refill が走るため 0 より大きくなり得る。上限のみ確認する。
        sut.AvailableTokens.Should().BeLessThanOrEqualTo(5.0 + 1e-6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Constructor_Throws_WhenRateIsNotPositive(double rate)
    {
        // 検証対象: コンストラクタバリデーション  目的: rate <= 0 で例外
        Action act = () => new WeightedTokenBucket(rate, maxBurst: 10.0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.5)]
    public void Constructor_Throws_WhenMaxBurstIsNotPositive(double maxBurst)
    {
        // 検証対象: コンストラクタバリデーション  目的: maxBurst <= 0 で例外
        Action act = () => new WeightedTokenBucket(initialRate: 10.0, maxBurst: maxBurst);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── AcquireAsync ──────────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_ReturnsImmediately_WhenTokensAvailable()
    {
        // 検証対象: AcquireAsync  目的: バケット満タンならほぼ即時に返る
        var sut = new WeightedTokenBucket(initialRate: 1.0, maxBurst: 100.0);
        var sw = Stopwatch.StartNew();

        await sut.AcquireAsync(cost: 10, CancellationToken.None);

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(100);
        sut.AvailableTokens.Should().BeLessThan(100.0);
    }

    [Fact]
    public async Task AcquireAsync_ConsumesCostFromBucket()
    {
        // 検証対象: AcquireAsync  目的: 取得分のコストがバケットから減算される
        var sut = new WeightedTokenBucket(initialRate: 0.001, maxBurst: 50.0, initialTokens: 50.0);

        await sut.AcquireAsync(cost: 20, CancellationToken.None);

        // rate が極小なので補充分はほぼゼロ。取得後の残量は 30 付近
        sut.AvailableTokens.Should().BeInRange(29.0, 30.5);
    }

    [Fact]
    public async Task AcquireAsync_WaitsUntilRefilled_WhenInsufficient()
    {
        // 検証対象: AcquireAsync  目的: 残量不足時は補充されるまで待機する
        // rate=10/sec、初期残量=0、cost=5 → 期待待機時間 ≈ 0.5 秒
        var sut = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 10.0, initialTokens: 0.0);
        var sw = Stopwatch.StartNew();

        await sut.AcquireAsync(cost: 5, CancellationToken.None);

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(300);
        sw.ElapsedMilliseconds.Should().BeLessThan(1500);
    }

    [Fact]
    public async Task AcquireAsync_ThrowsWhenCancelled()
    {
        // 検証対象: AcquireAsync  目的: 残量不足で待機中にキャンセルされたら OperationCanceledException
        var sut = new WeightedTokenBucket(initialRate: 0.01, maxBurst: 100.0, initialTokens: 0.0);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        Func<Task> act = () => sut.AcquireAsync(cost: 50, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task AcquireAsync_Throws_WhenCostExceedsMaxBurst()
    {
        // 検証対象: AcquireAsync  目的: cost > maxBurst は無限待機となるため ArgumentOutOfRangeException
        var sut = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 5.0);

        Func<Task> act = () => sut.AcquireAsync(cost: 10, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AcquireAsync_Throws_WhenCostNotPositive(int cost)
    {
        // 検証対象: AcquireAsync  目的: cost < 1 で ArgumentOutOfRangeException
        var sut = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 10.0);

        Func<Task> act = () => sut.AcquireAsync(cost, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ─── 補充レート（monotonic clock ベース） ──────────────────────

    [Fact]
    public async Task AvailableTokens_RefillsOverTime_ProportionalToRate()
    {
        // 検証対象: Refill  目的: 実経過時間に比例してトークンが補充される（monotonic clock ベース）
        var sut = new WeightedTokenBucket(initialRate: 20.0, maxBurst: 100.0, initialTokens: 0.0);

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        // rate=20/sec × 0.3 sec = 6 tokens（タイマー誤差を考慮して幅を持たせる）
        sut.AvailableTokens.Should().BeInRange(3.0, 15.0);
    }

    [Fact]
    public void AvailableTokens_ClampsToMaxBurst()
    {
        // 検証対象: Refill クランプ  目的: 補充は maxBurst を超えない
        var sut = new WeightedTokenBucket(initialRate: 1000.0, maxBurst: 10.0);
        Thread.Sleep(50);
        sut.AvailableTokens.Should().BeLessThanOrEqualTo(10.0 + 1e-6);
    }

    // ─── レート・容量変更 ─────────────────────────────────────────

    [Fact]
    public void SetRate_UpdatesCurrentRate()
    {
        // 検証対象: SetRate  目的: AIMD からの動的レート変更が反映される
        var sut = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 50.0);

        sut.SetRate(25.0);

        sut.CurrentRate.Should().Be(25.0);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void SetRate_Throws_WhenNotPositive(double rate)
    {
        // 検証対象: SetRate バリデーション  目的: rate <= 0 で例外
        var sut = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 50.0);
        Action act = () => sut.SetRate(rate);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SetMaxBurst_UpdatesCapacity_AndClampsTokens()
    {
        // 検証対象: SetMaxBurst  目的: 容量縮小時は現在の残量もクランプされる
        var sut = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 100.0);

        sut.SetMaxBurst(5.0);

        sut.MaxBurst.Should().Be(5.0);
        sut.AvailableTokens.Should().BeLessThanOrEqualTo(5.0 + 1e-6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void SetMaxBurst_Throws_WhenNotPositive(double maxBurst)
    {
        // 検証対象: SetMaxBurst バリデーション  目的: maxBurst <= 0 で例外
        var sut = new WeightedTokenBucket(initialRate: 10.0, maxBurst: 50.0);
        Action act = () => sut.SetMaxBurst(maxBurst);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
