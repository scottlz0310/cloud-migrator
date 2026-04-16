using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.State;
using CloudMigrator.Core.Transfer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: RateControlledTransferController
/// 目的: トークンバケット・ヒステリシス制御・インフライトカウンター・Dispose を検証する
/// </summary>
public class RateControlledTransferControllerTests : IAsyncDisposable
{
    private readonly Mock<IMetricsAggregator> _mockAggregator = new(MockBehavior.Loose);
    private readonly Mock<ITransferStateDb> _mockDb = new(MockBehavior.Loose);
    private RateControlledTransferController? _sut;
    // MetricsBuffer のバックグラウンド flush タスクをテスト間でリークさせないためフィールドに保持する
    private MetricsBuffer? _metricsBuffer;

    private RateControlledTransferController CreateSut(
        double initialRate = 10,
        double minRate = 1,
        int maxConcurrency = 20,
        int inFlightThreshold = 100)
    {
        var settings = new RateControlSettings
        {
            UseRateControl = true,
            InitialRatePerSec = initialRate,
            MinRatePerSec = minRate,
            MaxConcurrency = maxConcurrency,
            InFlightThreshold = inFlightThreshold,
            ShortWindowSec = 5,
            LongWindowSec = 30,
            EmergencyThreshold = 0.1,
            SlowdownThreshold = 0.03,
            MinDecayFactor = 0.5,
            MaxDecayFactor = 0.9,
            DecayK = 2.0,
            AccelerateRatio = 0.1,
            MetricsFlushIntervalSec = 3,
        };

        // DB のデフォルト: 書き込みを受け付けるだけ（各テストで aggregator の Setup を行う）
        _mockDb.Setup(db => db.RecordMetricsBatchAsync(
            It.IsAny<IEnumerable<(string, double, DateTimeOffset)>>(),
            It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // aggregator のデフォルト: すべてゼロ（429 なし）
        // 各テストで上書きしたい場合は CreateSut() 呼び出し後に Setup を追加する
        _mockAggregator.Setup(a => a.GetSnapshot(It.IsAny<TimeSpan>()))
            .Returns(new MetricsSnapshot(Rps: 0, Rate429: 0, AvgLatencyMs: 0, Timestamp: DateTimeOffset.UtcNow));

        var metricsBuffer = new MetricsBuffer(
            _mockDb.Object,
            flushIntervalSec: 3600,
            NullLogger<MetricsBuffer>.Instance);
        _metricsBuffer = metricsBuffer;

        _sut = new RateControlledTransferController(
            _mockAggregator.Object,
            settings,
            metricsBuffer,
            NullLogger<RateControlledTransferController>.Instance);

        return _sut;
    }

    // ── AcquireAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_WithAvailableToken_CompletesImmediately()
    {
        var sut = CreateSut(initialRate: 10, maxConcurrency: 20);

        // 初期トークンが存在するため即座に完了する
        var task = sut.AcquireAsync(CancellationToken.None);
        await task;

        task.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var sut = CreateSut(initialRate: 0.001, maxConcurrency: 1); // トークンがすぐ枯渇

        // 1 件目で初期トークンを消費
        await sut.AcquireAsync(CancellationToken.None);

        // 2 件目はトークンなし → キャンセルで抜ける
        using var cts = new CancellationTokenSource(millisecondsDelay: 50);
        var act = async () => await sut.AcquireAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Release（no-op）─────────────────────────────────────────────────

    [Fact]
    public async Task Release_IsNoOp_DoesNotAddToken()
    {
        var sut = CreateSut(initialRate: 1, maxConcurrency: 1);

        // Acquire でトークンを 1 個消費
        await sut.AcquireAsync(CancellationToken.None);

        // Release を呼んでも SemaphoreSlim のカウントは増えない → 次の Acquire はタイムアウト
        sut.Release();

        using var cts = new CancellationTokenSource(millisecondsDelay: 80);
        var act = async () => await sut.AcquireAsync(cts.Token);
        // Release が no-op であれば RefillTokens が走るまでトークンはない
        await act.Should().ThrowAsync<OperationCanceledException>(
            "Release が no-op のため、次のトークンは制御ループの RefillTokens まで来ない");
    }

    // ── インフライトカウンター ────────────────────────────────────────────

    [Fact]
    public void NotifyRequestSent_IncrementsInFlight()
    {
        var sut = CreateSut();

        sut.NotifyRequestSent();
        sut.NotifyRequestSent();

        sut.CurrentInFlight.Should().Be(2);
    }

    [Fact]
    public void NotifySuccess_DecrementsInFlight()
    {
        var sut = CreateSut();

        sut.NotifyRequestSent();
        sut.NotifyRequestSent();
        sut.NotifySuccess(TimeSpan.FromMilliseconds(200));

        sut.CurrentInFlight.Should().Be(1);
    }

    [Fact]
    public void NotifyRateLimit_DecrementsInFlight()
    {
        var sut = CreateSut();

        sut.NotifyRequestSent();
        sut.NotifyRateLimit(TimeSpan.FromSeconds(5));

        sut.CurrentInFlight.Should().Be(0);
    }

    [Fact]
    public void NotifyRetryScheduled_IncreasesInFlight()
    {
        var sut = CreateSut();

        sut.NotifyRetryScheduled(TimeSpan.FromSeconds(1));

        sut.CurrentInFlight.Should().Be(1);
    }

    [Fact]
    public void NotifyRetryCompleted_DecreasesInFlight()
    {
        var sut = CreateSut();

        sut.NotifyRetryScheduled(TimeSpan.FromSeconds(1));
        sut.NotifyRetryCompleted();

        sut.CurrentInFlight.Should().Be(0);
    }

    [Fact]
    public void CurrentInFlight_NeverGoesNegative()
    {
        var sut = CreateSut();

        sut.NotifySuccess(TimeSpan.Zero); // カウンター 0 で Decrement
        sut.NotifyRateLimit(null);

        sut.CurrentInFlight.Should().Be(0);
    }

    // ── CurrentRateLimit ────────────────────────────────────────────────

    [Fact]
    public void CurrentRateLimit_InitiallyEqualsInitialRate()
    {
        var sut = CreateSut(initialRate: 8);

        sut.CurrentRateLimit.Should().Be(8);
    }

    // ── ヒステリシス制御（AdjustRate の間接検証）─────────────────────────

    [Fact]
    public async Task ControlLoop_WhenNoRateLimits_RateIncreasesOrStaysAboveMin()
    {
        // 429 なし → 加速フェーズ → レートが初期値以上になることを検証
        // CreateSut() 後に aggregator の Setup を上書きして 429 なしを確実に設定する
        var sut = CreateSut(initialRate: 5, maxConcurrency: 20);
        _mockAggregator.Setup(a => a.GetSnapshot(It.IsAny<TimeSpan>()))
            .Returns(new MetricsSnapshot(Rps: 5, Rate429: 0, AvgLatencyMs: 50, Timestamp: DateTimeOffset.UtcNow));

        // 制御ループが 1 サイクル（~1秒）走るのを待つ
        await Task.Delay(1200);

        sut.CurrentRateLimit.Should().BeGreaterThanOrEqualTo(5,
            "429 なし・加速フェーズではレートが下がらない");
    }

    [Fact]
    public async Task ControlLoop_WhenHighRateLimit_RateDecreasesOrStaysAboveMin()
    {
        // 緊急減速: 短期 429 率 > 10%
        // CreateSut() 後に aggregator の Setup を上書きして高 429 率を設定する
        var sut = CreateSut(initialRate: 10, minRate: 1, maxConcurrency: 20);
        var initialRate = sut.CurrentRateLimit;
        _mockAggregator.Setup(a => a.GetSnapshot(It.IsAny<TimeSpan>()))
            .Returns(new MetricsSnapshot(Rps: 5, Rate429: 0.5, AvgLatencyMs: 50, Timestamp: DateTimeOffset.UtcNow));

        // 制御ループが 1 サイクル走るのを待つ
        await Task.Delay(1200);

        sut.CurrentRateLimit.Should().BeLessThan(initialRate,
            "429 率 50% → 緊急減速でレートが下がる");
        sut.CurrentRateLimit.Should().BeGreaterThanOrEqualTo(1,
            "MinRatePerSec = 1 以下にはならない");
    }

    // ── DisposeAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CompletesWithoutException()
    {
        var sut = CreateSut();

        var act = async () => await sut.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        // 検証対象: DisposeAsync  目的: 2 回目の DisposeAsync 呼び出しでも例外が発生しないことを確認する
        var sut = CreateSut();

        await sut.DisposeAsync();

        // 2 回目の Dispose で例外が出ないこと（CTS が既にキャンセル済みでも安全）
        // Note: DisposeAsync は idempotent ではないが、制御ループ終了後は安全
        var act = async () => await sut.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    // ── スレッドセーフ ─────────────────────────────────────────────────────

    [Fact]
    public async Task MultiThreaded_ConcurrentNotifications_NoDataCorruption()
    {
        var sut = CreateSut(initialRate: 100, maxConcurrency: 200);

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                sut.NotifyRequestSent();
                sut.NotifySuccess(TimeSpan.FromMilliseconds(10));
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // 例外なく完了し、InFlight カウンターが整合していること
        sut.CurrentInFlight.Should().Be(0);
    }

    public async ValueTask DisposeAsync()
    {
        if (_sut is not null)
            await _sut.DisposeAsync();
        if (_metricsBuffer is not null)
            await _metricsBuffer.DisposeAsync();
    }
}
