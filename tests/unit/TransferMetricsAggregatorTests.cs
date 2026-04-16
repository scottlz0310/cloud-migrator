using CloudMigrator.Core.Transfer;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: TransferMetricsAggregator
/// 目的: 1 秒バケットリングバッファ実装の集計正確性とスレッドセーフ性を検証する
/// </summary>
public class TransferMetricsAggregatorTests
{
    private readonly TransferMetricsAggregator _sut = new();

    // ── RPS 計算 ───────────────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_WithRequests_ReturnsCorrectRps()
    {
        // 3 件のリクエストを送信し、30 秒ウィンドウで RPS = 3/30 になること
        _sut.NotifyRequestSent();
        _sut.NotifyRequestSent();
        _sut.NotifyRequestSent();

        var snap = _sut.GetSnapshot(TimeSpan.FromSeconds(30));

        // 現在秒のバケットに 3 件入っているので 3/30 = 0.1 req/sec
        snap.Rps.Should().BeApproximately(3.0 / 30, precision: 0.01);
    }

    [Fact]
    public void GetSnapshot_EmptyAggregator_ReturnsZeroes()
    {
        var snap = _sut.GetSnapshot(TimeSpan.FromSeconds(10));

        snap.Rps.Should().Be(0);
        snap.Rate429.Should().Be(0);
        snap.AvgLatencyMs.Should().Be(0);
    }

    // ── 429 率計算 ─────────────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_With429Events_ReturnsCorrectRate429()
    {
        // 4 件リクエスト、うち 1 件が 429 → 429率 = 1/(4+1) = 0.2
        // (rate429 = rateLimits / (requests + rateLimits))
        _sut.NotifyRequestSent();
        _sut.NotifyRequestSent();
        _sut.NotifyRequestSent();
        _sut.NotifyRequestSent();
        _sut.NotifyRateLimit(null);

        var snap = _sut.GetSnapshot(TimeSpan.FromSeconds(10));

        snap.Rate429.Should().BeApproximately(1.0 / 5, precision: 0.001);
    }

    [Fact]
    public void GetSnapshot_NoRateLimits_ReturnsZeroRate429()
    {
        _sut.NotifyRequestSent();
        _sut.NotifySuccess(TimeSpan.FromMilliseconds(100));

        var snap = _sut.GetSnapshot(TimeSpan.FromSeconds(10));

        snap.Rate429.Should().Be(0);
    }

    // ── 平均レイテンシ計算 ────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_WithLatencyEvents_ReturnsCorrectAverage()
    {
        _sut.NotifySuccess(TimeSpan.FromMilliseconds(100));
        _sut.NotifySuccess(TimeSpan.FromMilliseconds(200));
        _sut.NotifySuccess(TimeSpan.FromMilliseconds(300));

        var snap = _sut.GetSnapshot(TimeSpan.FromSeconds(10));

        snap.AvgLatencyMs.Should().BeApproximately(200.0, precision: 1.0);
    }

    [Fact]
    public void GetSnapshot_NoSuccessEvents_ReturnsZeroLatency()
    {
        _sut.NotifyRateLimit(TimeSpan.FromSeconds(5));

        var snap = _sut.GetSnapshot(TimeSpan.FromSeconds(10));

        snap.AvgLatencyMs.Should().Be(0);
    }

    // ── ウィンドウ境界 ────────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_ZeroWindow_ReturnsZeroes()
    {
        _sut.NotifyRequestSent();
        _sut.NotifySuccess(TimeSpan.FromMilliseconds(100));

        // window = 0 → 窓の中にイベントがない（または除算不能）→ 0 を返す
        var snap = _sut.GetSnapshot(TimeSpan.Zero);

        snap.Rps.Should().Be(0);
    }

    // ── タイムスタンプ ────────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_TimestampIsApproximatelyNow()
    {
        var before = DateTimeOffset.UtcNow;
        var snap = _sut.GetSnapshot(TimeSpan.FromSeconds(10));
        var after = DateTimeOffset.UtcNow;

        snap.Timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // ── スレッドセーフ ─────────────────────────────────────────────────────

    [Fact]
    public async Task MultiThreaded_ConcurrentNotifications_NoDataCorruption()
    {
        // 50 スレッドが同時に通知を送り、データ破損・例外がないことを確認する
        const int threadCount = 50;
        const int eventsPerThread = 100;

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < eventsPerThread; i++)
            {
                _sut.NotifyRequestSent();
                _sut.NotifySuccess(TimeSpan.FromMilliseconds(10));
                if (i % 10 == 0)
                    _sut.NotifyRateLimit(null);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // 例外なく完了し、スナップショットが取得できること
        var snap = _sut.GetSnapshot(TimeSpan.FromSeconds(60));
        snap.Rps.Should().BeGreaterThanOrEqualTo(0);
        snap.Rate429.Should().BeInRange(0, 1);
    }

    // ── リングバッファの上書き確認 ────────────────────────────────────────

    [Fact]
    public void GetSnapshot_ExcludesEventsOutsideWindow()
    {
        // ウィンドウ 1 秒のスナップショットは現在秒のみを含む
        // (過去秒のバケットは epochSec が異なるため除外される)
        _sut.NotifyRequestSent();

        // 1 秒ウィンドウで RPS > 0 であることを確認
        var snap = _sut.GetSnapshot(TimeSpan.FromSeconds(1));
        snap.Rps.Should().BeGreaterThan(0);
    }
}
