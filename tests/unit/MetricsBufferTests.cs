using CloudMigrator.Core.State;
using CloudMigrator.Core.Transfer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: MetricsBuffer
/// 目的: 非同期バッファリング・最終フラッシュ・バッファ溢れの動作を検証する
/// </summary>
public class MetricsBufferTests
{
    // ── バッファ溢れ ──────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_WhenBufferFull_DiscardsOldestItems()
    {
        // Arrange: フラッシュが走る前に Enqueue する（Setup を先に行う）
        var flushedBatches = new List<IEnumerable<(string, double, DateTimeOffset)>>();
        var mockDb = new Mock<ITransferStateDb>(MockBehavior.Loose);
        mockDb.Setup(db => db.RecordMetricsBatchAsync(It.IsAny<IEnumerable<(string, double, DateTimeOffset)>>(), It.IsAny<CancellationToken>()))
              .Callback<IEnumerable<(string, double, DateTimeOffset)>, CancellationToken>((batch, _) => flushedBatches.Add(batch.ToList()))
              .Returns(Task.CompletedTask);

        var sut = new MetricsBuffer(mockDb.Object, flushIntervalSec: 3600, NullLogger<MetricsBuffer>.Instance);

        // Act: 1001 件追加（上限 1000 を超える）
        for (var i = 0; i < 1001; i++)
            sut.Enqueue("metric", i);

        // DisposeAsync して最終フラッシュを実行
        await sut.DisposeAsync();

        var totalFlushed = flushedBatches.SelectMany(b => b).Count();
        totalFlushed.Should().BeLessThanOrEqualTo(1000, "バッファ上限 1000 を超えない");
    }

    // ── 最終フラッシュ（Dispose 時）──────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_FlushesRemainingItems_WithCancellationTokenNone()
    {
        // Arrange: DB が最終フラッシュで受け取る CancellationToken を記録する
        var capturedTokens = new List<CancellationToken>();
        var mockDb = new Mock<ITransferStateDb>(MockBehavior.Loose);
        mockDb.Setup(db => db.RecordMetricsBatchAsync(
                It.IsAny<IEnumerable<(string, double, DateTimeOffset)>>(),
                It.IsAny<CancellationToken>()))
              .Callback<IEnumerable<(string, double, DateTimeOffset)>, CancellationToken>(
                  (_, ct) => capturedTokens.Add(ct))
              .Returns(Task.CompletedTask);

        var sut = new MetricsBuffer(mockDb.Object, flushIntervalSec: 3600, NullLogger<MetricsBuffer>.Instance);
        sut.Enqueue("rps", 1.0);
        sut.Enqueue("rate_429", 0.0);

        // Act
        await sut.DisposeAsync();

        // Assert: 最終フラッシュは CancellationToken.None（またはキャンセルされていない CT）で実行される
        capturedTokens.Should().NotBeEmpty("フラッシュが実行されること");
        capturedTokens.Last().IsCancellationRequested.Should().BeFalse(
            "Dispose 時の最終フラッシュはキャンセルされていないトークンで実行される");
    }

    [Fact]
    public async Task DisposeAsync_EmptyQueue_DoesNotCallDb()
    {
        var mockDb = new Mock<ITransferStateDb>(MockBehavior.Loose);
        var sut = new MetricsBuffer(mockDb.Object, flushIntervalSec: 3600, NullLogger<MetricsBuffer>.Instance);

        await sut.DisposeAsync();

        mockDb.Verify(db => db.RecordMetricsBatchAsync(
            It.IsAny<IEnumerable<(string, double, DateTimeOffset)>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── 正常なフラッシュ ─────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_FlushesAllEnqueuedItems()
    {
        var flushed = new List<(string Name, double Value, DateTimeOffset Timestamp)>();
        var mockDb = new Mock<ITransferStateDb>(MockBehavior.Loose);
        mockDb.Setup(db => db.RecordMetricsBatchAsync(
                It.IsAny<IEnumerable<(string, double, DateTimeOffset)>>(),
                It.IsAny<CancellationToken>()))
              .Callback<IEnumerable<(string, double, DateTimeOffset)>, CancellationToken>(
                  (batch, _) => flushed.AddRange(batch))
              .Returns(Task.CompletedTask);

        var sut = new MetricsBuffer(mockDb.Object, flushIntervalSec: 3600, NullLogger<MetricsBuffer>.Instance);
        sut.Enqueue("rps", 1.5);
        sut.Enqueue("rate_429", 0.1);
        sut.Enqueue("avg_latency", 250.0);

        await sut.DisposeAsync();

        flushed.Should().HaveCount(3);
        flushed.Select(f => f.Name).Should().BeEquivalentTo(["rps", "rate_429", "avg_latency"]);
    }

    // ── DB 失敗時は破棄（転送処理を優先）────────────────────────────────

    [Fact]
    public async Task DisposeAsync_WhenDbThrows_DoesNotPropagateException()
    {
        var mockDb = new Mock<ITransferStateDb>(MockBehavior.Loose);
        mockDb.Setup(db => db.RecordMetricsBatchAsync(
                It.IsAny<IEnumerable<(string, double, DateTimeOffset)>>(),
                It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("DB error"));

        var sut = new MetricsBuffer(mockDb.Object, flushIntervalSec: 3600, NullLogger<MetricsBuffer>.Instance);
        sut.Enqueue("rps", 1.0);

        // DB が例外を投げても DisposeAsync が例外を上げないこと
        var act = async () => await sut.DisposeAsync();
        await act.Should().NotThrowAsync();
    }
}
