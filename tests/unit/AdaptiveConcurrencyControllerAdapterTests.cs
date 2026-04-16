using CloudMigrator.Core.Transfer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: AdaptiveConcurrencyControllerAdapter
/// 目的: ITransferRateController 委譲・インフライトカウンター管理・所有権（inner を Dispose しない）を検証する
/// </summary>
public class AdaptiveConcurrencyControllerAdapterTests
{
    private AdaptiveConcurrencyController CreateAcc(int degree = 4) =>
        new(initialDegree: degree, minDegree: 1, maxDegree: degree,
            increaseIntervalSec: 0, NullLogger<AdaptiveConcurrencyController>.Instance);

    // ── CurrentRateLimit ────────────────────────────────────────────────

    [Fact]
    public void CurrentRateLimit_ReturnsInnerCurrentDegree()
    {
        using var acc = CreateAcc(degree: 4);
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        sut.CurrentRateLimit.Should().Be(acc.CurrentDegree);
    }

    // ── CurrentInFlight ─────────────────────────────────────────────────

    [Fact]
    public void CurrentInFlight_InitiallyZero()
    {
        using var acc = CreateAcc();
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        sut.CurrentInFlight.Should().Be(0);
    }

    [Fact]
    public void NotifyRequestSent_IncrementsInFlight()
    {
        using var acc = CreateAcc();
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        sut.NotifyRequestSent();
        sut.NotifyRequestSent();

        sut.CurrentInFlight.Should().Be(2);
    }

    [Fact]
    public void NotifySuccess_DecrementsInFlight()
    {
        using var acc = CreateAcc();
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        sut.NotifyRequestSent();
        sut.NotifyRequestSent();
        sut.NotifySuccess(TimeSpan.FromMilliseconds(100));

        sut.CurrentInFlight.Should().Be(1);
    }

    [Fact]
    public void NotifyRateLimit_DecrementsInFlight()
    {
        using var acc = CreateAcc();
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        sut.NotifyRequestSent();
        sut.NotifyRateLimit(TimeSpan.FromSeconds(5));

        sut.CurrentInFlight.Should().Be(0);
    }

    [Fact]
    public void NotifyRetryScheduled_IncrementsInFlight()
    {
        using var acc = CreateAcc();
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        sut.NotifyRetryScheduled(TimeSpan.FromSeconds(1));

        sut.CurrentInFlight.Should().Be(1);
    }

    [Fact]
    public void NotifyRetryCompleted_DecrementsRetryCount()
    {
        using var acc = CreateAcc();
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        sut.NotifyRetryScheduled(TimeSpan.FromSeconds(1));
        sut.NotifyRetryCompleted();

        sut.CurrentInFlight.Should().Be(0);
    }

    [Fact]
    public void CurrentInFlight_NeverGoesNegative()
    {
        using var acc = CreateAcc();
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        // カウンターが 0 の状態で DecrementSource を呼んでも負にならない
        sut.NotifySuccess(TimeSpan.Zero);
        sut.NotifyRateLimit(null);

        sut.CurrentInFlight.Should().Be(0);
    }

    // ── AcquireAsync / Release ──────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_DelegatesToInner()
    {
        using var acc = CreateAcc(degree: 2);
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        // 2 スロットすべて Acquire できること
        await sut.AcquireAsync(CancellationToken.None);
        await sut.AcquireAsync(CancellationToken.None);

        // 3 件目は即座には取れない（キャンセルして確認）
        using var cts = new CancellationTokenSource(millisecondsDelay: 50);
        var act = async () => await sut.AcquireAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Release_DelegatesToInner()
    {
        using var acc = CreateAcc(degree: 1);
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        // Acquire 後に Release でスロットが返却される
        await sut.AcquireAsync(CancellationToken.None);
        sut.Release();

        // Release 後は再 Acquire できること
        var act = async () => await sut.AcquireAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── 所有権確認（inner を Dispose しない）──────────────────────────────

    [Fact]
    public void Adapter_DoesNotImplementIDisposable()
    {
        using var acc = CreateAcc();
        var sut = new AdaptiveConcurrencyControllerAdapter(acc);

        // IDisposable を実装していないことを確認（所有権を持たない）
        sut.Should().NotBeAssignableTo<IDisposable>(
            "Adapter は inner を所有しないため IDisposable を実装してはならない");
    }

    [Fact]
    public async Task Acc_RemainsUsableAfterAdapterIsAbanoned()
    {
        using var acc = CreateAcc(degree: 2);
        {
            // Adapter を生成してスコープを抜ける（GC に委ねる）
            _ = new AdaptiveConcurrencyControllerAdapter(acc);
        }

        // acc は Dispose されておらず引き続き使用可能であること
        await acc.AcquireAsync(CancellationToken.None);
        acc.Should().NotBeNull();
    }
}
