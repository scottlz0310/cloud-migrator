using CloudMigrator.Core.Transfer;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// SlidingWindowMetrics のユニットテスト（#161）。
/// </summary>
public sealed class SlidingWindowMetricsTests
{
    // ─── コンストラクタ・バリデーション ──────────────────────────

    [Fact]
    public void Constructor_ExposesConfiguredValues()
    {
        // 検証対象: プロパティ  目的: mode / minSamples がコンストラクタ値を反映する
        var sut = new SlidingWindowMetrics(SlidingWindowMode.Count, minSamples: 7);
        sut.Mode.Should().Be(SlidingWindowMode.Count);
        sut.MinSamples.Should().Be(7);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenWindowSecLessThanOne(int windowSec)
    {
        // 検証対象: バリデーション  目的: windowSec < 1 で例外
        Action act = () => new SlidingWindowMetrics(windowSec: windowSec);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenMaxCountLessThanOne(int maxCount)
    {
        // 検証対象: バリデーション  目的: maxCount < 1 で例外
        Action act = () => new SlidingWindowMetrics(maxCount: maxCount);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenMinSamplesLessThanOne(int minSamples)
    {
        // 検証対象: バリデーション  目的: minSamples < 1 で例外
        Action act = () => new SlidingWindowMetrics(minSamples: minSamples);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_Throws_WhenSafetyCapLessThanMinSamples()
    {
        // 検証対象: バリデーション  目的: safetyCap < minSamples は無効
        Action act = () => new SlidingWindowMetrics(minSamples: 100, safetyCap: 50);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── 空のスナップショット ────────────────────────────────────

    [Fact]
    public void GetSnapshot_WhenEmpty_ReturnsZeroMetrics()
    {
        // 検証対象: 初期スナップショット  目的: イベント未投入時は全指標 0 で HasMinSamples=false
        var sut = new SlidingWindowMetrics(minSamples: 5);

        var snap = sut.GetSnapshot();

        snap.SampleCount.Should().Be(0);
        snap.HasMinSamples.Should().BeFalse();
        snap.Rate429.Should().Be(0.0);
        snap.SuccessRate.Should().Be(0.0);
        snap.AvgLatencyMs.Should().Be(0.0);
        snap.P95LatencyMs.Should().Be(0.0);
    }

    // ─── 基本集計 ────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_CountsRequests_And_ComputesRate429()
    {
        // 検証対象: Rate429  目的: rateLimitCount / sampleCount で算出される
        var sut = new SlidingWindowMetrics(minSamples: 1);

        for (int i = 0; i < 10; i++) sut.NotifyRequestSent();
        for (int i = 0; i < 8; i++) sut.NotifySuccess(TimeSpan.FromMilliseconds(100));
        for (int i = 0; i < 2; i++) sut.NotifyRateLimit(null);

        var snap = sut.GetSnapshot();
        snap.SampleCount.Should().Be(10);
        snap.Rate429.Should().BeApproximately(0.2, 1e-9);
        snap.SuccessRate.Should().BeApproximately(0.8, 1e-9);
    }

    [Fact]
    public void GetSnapshot_ComputesAverageLatency_FromSuccessEvents()
    {
        // 検証対象: AvgLatencyMs  目的: 成功イベントのみから平均を算出
        var sut = new SlidingWindowMetrics(minSamples: 1);
        sut.NotifyRequestSent();
        sut.NotifySuccess(TimeSpan.FromMilliseconds(100));
        sut.NotifyRequestSent();
        sut.NotifySuccess(TimeSpan.FromMilliseconds(200));
        sut.NotifyRequestSent();
        sut.NotifySuccess(TimeSpan.FromMilliseconds(300));

        var snap = sut.GetSnapshot();
        snap.AvgLatencyMs.Should().BeApproximately(200.0, 1e-9);
    }

    [Fact]
    public void GetSnapshot_ComputesP95Latency()
    {
        // 検証対象: P95LatencyMs  目的: 成功レイテンシの 95 パーセンタイル
        var sut = new SlidingWindowMetrics(minSamples: 1);
        // 100 件、1〜100 ms
        for (int i = 1; i <= 100; i++)
        {
            sut.NotifyRequestSent();
            sut.NotifySuccess(TimeSpan.FromMilliseconds(i));
        }

        var snap = sut.GetSnapshot();
        // 線形補間: rank = 0.95 × 99 = 94.05 → values[94]=95, values[95]=96 の補間
        snap.P95LatencyMs.Should().BeApproximately(95.05, 1e-6);
    }

    [Fact]
    public void GetSnapshot_SingleSuccess_P95EqualsThatValue()
    {
        // 検証対象: P95  目的: 成功 1 件のみの場合は P95=その値
        var sut = new SlidingWindowMetrics(minSamples: 1);
        sut.NotifyRequestSent();
        sut.NotifySuccess(TimeSpan.FromMilliseconds(50));

        sut.GetSnapshot().P95LatencyMs.Should().Be(50.0);
    }

    // ─── minSamples 保証 ─────────────────────────────────────────

    [Fact]
    public void GetSnapshot_HasMinSamples_IsFalse_BelowMinSamples()
    {
        // 検証対象: HasMinSamples  目的: minSamples 未満は false
        var sut = new SlidingWindowMetrics(minSamples: 10);
        for (int i = 0; i < 9; i++) sut.NotifyRequestSent();

        sut.GetSnapshot().HasMinSamples.Should().BeFalse();
    }

    [Fact]
    public void GetSnapshot_HasMinSamples_IsTrue_AtMinSamples()
    {
        // 検証対象: HasMinSamples  目的: minSamples に達したら true
        var sut = new SlidingWindowMetrics(minSamples: 10);
        for (int i = 0; i < 10; i++) sut.NotifyRequestSent();

        sut.GetSnapshot().HasMinSamples.Should().BeTrue();
    }

    [Fact]
    public void GetSnapshot_SampleCount_FallsBackToSuccessPlusRateLimit()
    {
        // 検証対象: SampleCount フォールバック  目的: NotifyRequestSent 未呼び出しでも
        //   success + rateLimit が sampleCount となり 0 除算を回避
        var sut = new SlidingWindowMetrics(minSamples: 1);
        sut.NotifySuccess(TimeSpan.FromMilliseconds(50));
        sut.NotifySuccess(TimeSpan.FromMilliseconds(60));
        sut.NotifyRateLimit(null);

        var snap = sut.GetSnapshot();
        snap.SampleCount.Should().Be(3);
        snap.Rate429.Should().BeApproximately(1.0 / 3, 1e-9);
        snap.SuccessRate.Should().BeApproximately(2.0 / 3, 1e-9);
    }

    // ─── 件数ベースウィンドウ ────────────────────────────────────

    [Fact]
    public void CountMode_EvictsOldestEvents_WhenExceeded()
    {
        // 検証対象: 件数 evict  目的: maxCount を超えた古いイベントが削除される
        var sut = new SlidingWindowMetrics(
            mode: SlidingWindowMode.Count,
            maxCount: 5,
            minSamples: 1);

        // 10 件投入（古い 5 件が evict されるべき）
        for (int i = 0; i < 10; i++)
        {
            sut.NotifyRequestSent();
            sut.NotifySuccess(TimeSpan.FromMilliseconds(i + 1));
        }

        // 1 サンプルが NotifyRequestSent+NotifySuccess = 2 イベントを生成するため、
        // maxCount=5 の場合は最新 5 イベント（= 2〜3 サンプル）が残る
        var snap = sut.GetSnapshot();
        snap.SampleCount.Should().BeGreaterThan(0);
        // 残っているイベントは最新 5 件なので、最古の小さな latency は含まれない
        // 直近 5 イベント中の成功は少なくとも 2 件以上で latency は 8 ms 以上
        snap.AvgLatencyMs.Should().BeGreaterThanOrEqualTo(8.0);
    }

    [Fact]
    public void CountMode_WithinCapacity_KeepsAllEvents()
    {
        // 検証対象: 件数 evict  目的: maxCount 以下なら全イベントを保持
        var sut = new SlidingWindowMetrics(
            mode: SlidingWindowMode.Count,
            maxCount: 100,
            minSamples: 1);

        for (int i = 0; i < 10; i++) sut.NotifyRequestSent();

        sut.GetSnapshot().SampleCount.Should().Be(10);
    }

    // ─── 時間ベースウィンドウ ────────────────────────────────────

    [Fact]
    public async Task TimeMode_EvictsEventsOlderThanWindow()
    {
        // 検証対象: 時間 evict  目的: windowSec を超えた古いイベントが削除される
        var sut = new SlidingWindowMetrics(
            mode: SlidingWindowMode.Time,
            windowSec: 1,
            minSamples: 1);

        // 旧イベント投入
        sut.NotifyRequestSent();
        sut.NotifySuccess(TimeSpan.FromMilliseconds(10));

        // ウィンドウを超えるまで待機
        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        // 新イベント投入
        sut.NotifyRequestSent();
        sut.NotifySuccess(TimeSpan.FromMilliseconds(500));

        var snap = sut.GetSnapshot();
        // 旧サンプル（10 ms）は evict、新サンプル（500 ms）のみ残る想定
        snap.SampleCount.Should().Be(1);
        snap.AvgLatencyMs.Should().BeApproximately(500.0, 1e-9);
    }

    [Fact]
    public void TimeMode_AllEventsWithinWindow_KeepsAll()
    {
        // 検証対象: 時間 evict  目的: ウィンドウ内は全て保持
        var sut = new SlidingWindowMetrics(
            mode: SlidingWindowMode.Time,
            windowSec: 60,
            minSamples: 1);

        for (int i = 0; i < 20; i++) sut.NotifyRequestSent();

        sut.GetSnapshot().SampleCount.Should().Be(20);
    }

    // ─── スレッドセーフ性 ────────────────────────────────────────

    [Fact]
    public async Task Notify_IsThreadSafe_UnderParallelCalls()
    {
        // 検証対象: スレッドセーフ性  目的: 並行 Notify 呼び出しで件数が正しく集計される
        var sut = new SlidingWindowMetrics(
            mode: SlidingWindowMode.Count,
            maxCount: 10000,
            minSamples: 1);

        const int totalThreads = 8;
        const int eventsPerThread = 500;

        var tasks = new Task[totalThreads];
        for (int t = 0; t < totalThreads; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < eventsPerThread; i++)
                    sut.NotifyRequestSent();
            });
        }
        await Task.WhenAll(tasks);

        sut.GetSnapshot().SampleCount.Should().Be(totalThreads * eventsPerThread);
    }

    // ─── Percentile ユーティリティ（internal 可視化） ────────────

    [Fact]
    public void Percentile_EmptyList_ReturnsZero()
    {
        // 検証対象: Percentile  目的: 空リストで 0 を返す（0 除算回避）
        SlidingWindowMetrics.Percentile(new List<double>(), 0.95).Should().Be(0.0);
    }

    [Fact]
    public void Percentile_SingleValue_ReturnsThatValue()
    {
        // 検証対象: Percentile  目的: 1 件なら値そのもの
        SlidingWindowMetrics.Percentile(new List<double> { 42.0 }, 0.95).Should().Be(42.0);
    }

    [Fact]
    public void Percentile_UnsortedInput_StillCorrect()
    {
        // 検証対象: Percentile  目的: 未ソート入力でも正しい（内部でコピー+ソート）
        var values = new List<double> { 5, 1, 4, 2, 3 };
        SlidingWindowMetrics.Percentile(values, 0.5).Should().BeApproximately(3.0, 1e-9);
        // 呼び出し側のリストは破壊されない
        values.Should().Equal(5, 1, 4, 2, 3);
    }

    // ─── #159 ウィンドウスループット ─────────────────────────────

    [Fact]
    public void GetSnapshot_TimeMode_FilesAndBytesPerSec_DividedByWindowSeconds()
    {
        // 検証対象: FilesPerSec / BytesPerSec  目的:
        //   時間モードでは設定ウィンドウ秒（windowSec）を分母として算出する。
        //   30 秒窓で 60 件成功なら 2 files/sec、合計 6000 bytes なら 200 bytes/sec。
        var sut = new SlidingWindowMetrics(
            mode: SlidingWindowMode.Time,
            windowSec: 30,
            minSamples: 1);

        for (int i = 0; i < 60; i++)
        {
            sut.NotifyRequestSent();
            sut.NotifySuccess(TimeSpan.FromMilliseconds(10), bytes: 100);
        }

        var snap = sut.GetSnapshot();

        snap.WindowSeconds.Should().Be(30.0);
        snap.FilesPerSec.Should().BeApproximately(2.0, 1e-9);
        snap.BytesPerSec.Should().BeApproximately(200.0, 1e-9);
    }

    [Fact]
    public void GetSnapshot_NoSuccess_FilesAndBytesPerSecZero()
    {
        // 検証対象: 成功 0 件時の表示  目的: 0 除算せず 0 を返す
        var sut = new SlidingWindowMetrics(windowSec: 30, minSamples: 1);

        sut.NotifyRequestSent();
        sut.NotifyRateLimit(null);

        var snap = sut.GetSnapshot();
        snap.FilesPerSec.Should().Be(0.0);
        snap.BytesPerSec.Should().Be(0.0);
        // 設定ウィンドウ秒は維持される
        snap.WindowSeconds.Should().Be(30.0);
    }

    [Fact]
    public void NotifySuccess_DefaultBytes_IsZero()
    {
        // 検証対象: 後方互換  目的: 既存呼び出し（bytes 省略）はバイト数 0 として扱われる
        var sut = new SlidingWindowMetrics(windowSec: 10, minSamples: 1);

        sut.NotifyRequestSent();
        sut.NotifySuccess(TimeSpan.FromMilliseconds(10));

        var snap = sut.GetSnapshot();
        snap.BytesPerSec.Should().Be(0.0);
        snap.FilesPerSec.Should().BeApproximately(0.1, 1e-9); // 1 件 / 10 秒
    }
}
