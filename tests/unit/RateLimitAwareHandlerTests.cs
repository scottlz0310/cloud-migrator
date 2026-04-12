using System.Net;
using System.Net.Http.Headers;
using CloudMigrator.Providers.Graph.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: RateLimitAwareHandler
/// 目的: 429/503 レスポンス傍受・コールバック通知・ロギングが正しく動作することを確認する
/// </summary>
public sealed class RateLimitAwareHandlerTests
{
    // ── ヘルパー ──────────────────────────────────────────────────────────

    /// <summary>
    /// 固定レスポンスを返す末端 HttpMessageHandler。
    /// DelegatingHandler チェーンのテストに使用する。
    /// </summary>
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        internal StubHttpMessageHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_response);
    }

    /// <summary>
    /// テスト用ハンドラーチェーンを構築し HttpMessageInvoker を返す。
    /// </summary>
    private static (HttpMessageInvoker Invoker, RateLimitAwareHandler Handler) BuildChain(
        HttpResponseMessage response,
        Action<TimeSpan?> onRateLimit,
        ILogger? logger = null)
    {
        var inner = new StubHttpMessageHandler(response);
        // RateLimitAwareHandler は internal sealed かつ InnerHandler を外部設定可能
        var handler = (RateLimitAwareHandler)Activator.CreateInstance(
            typeof(RateLimitAwareHandler),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            [onRateLimit, logger],
            null)!;
        handler.InnerHandler = inner;
        return (new HttpMessageInvoker(handler), handler);
    }

    private static HttpRequestMessage MakeRequest() =>
        new(HttpMethod.Get, "https://graph.microsoft.com/v1.0/test");

    // ── コールバック呼び出しテスト ─────────────────────────────────────

    [Fact]
    public async Task SendAsync_WhenResponseIs200_DoesNotInvokeCallback()
    {
        // 検証対象: SendAsync  目的: 200 OK レスポンス時にコールバックが呼ばれないこと
        var called = false;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var (invoker, _) = BuildChain(response, _ => called = true);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        called.Should().BeFalse();
    }

    [Fact]
    public async Task SendAsync_WhenResponseIs429WithDeltaRetryAfter_InvokesCallbackWithTimeSpan()
    {
        // 検証対象: SendAsync  目的: 429 + Retry-After Delta ヘッダーがある場合にコールバックへ TimeSpan が渡されること
        TimeSpan? received = TimeSpan.Zero;
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var (invoker, _) = BuildChain(response, ts => received = ts);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        received.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task SendAsync_WhenResponseIs429WithDateRetryAfter_InvokesCallbackWithPositiveTimeSpan()
    {
        // 検証対象: SendAsync  目的: 429 + Retry-After Date ヘッダーがある場合に正の TimeSpan がコールバックへ渡されること
        TimeSpan? received = null;
        var futureDate = DateTimeOffset.UtcNow.AddSeconds(60);
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(futureDate);
        var (invoker, _) = BuildChain(response, ts => received = ts);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        received.Should().NotBeNull();
        received!.Value.TotalSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SendAsync_WhenResponseIs429WithPastDateRetryAfter_InvokesCallbackWithZeroTimeSpan()
    {
        // 検証対象: SendAsync  目的: Retry-After Date が過去時刻の場合にコールバックへ TimeSpan.Zero が渡されること
        TimeSpan? received = null;
        var pastDate = DateTimeOffset.UtcNow.AddSeconds(-10);
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(pastDate);
        var (invoker, _) = BuildChain(response, ts => received = ts);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        received.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task SendAsync_WhenResponseIs429WithoutRetryAfter_InvokesCallbackWithNull()
    {
        // 検証対象: SendAsync  目的: 429 で Retry-After ヘッダーがない場合にコールバックへ null が渡されること
        TimeSpan? received = TimeSpan.Zero; // null でないことを示す初期値
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var (invoker, _) = BuildChain(response, ts => received = ts);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        received.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_WhenResponseIs503_InvokesCallback()
    {
        // 検証対象: SendAsync  目的: 503 Service Unavailable でもコールバックが呼ばれること
        var called = false;
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
        var (invoker, _) = BuildChain(response, _ => called = true);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        called.Should().BeTrue();
    }

    // ── レスポンスパスルー確認 ─────────────────────────────────────────

    [Fact]
    public async Task SendAsync_AlwaysReturnsOriginalResponse()
    {
        // 検証対象: SendAsync  目的: 429 を検出した後も元のレスポンスがそのまま返されること
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var (invoker, _) = BuildChain(response, _ => { });

        var result = await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        result.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    // ── ロギングテスト ────────────────────────────────────────────────

    [Fact]
    public async Task SendAsync_WhenResponseIs429WithDelta_LogsWarningWithWaitSec()
    {
        // 検証対象: SendAsync  目的: Retry-After Delta がある場合に待機秒数付きの Warning ログが出力されること
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(LogLevel.Warning)).Returns(true);

        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
        var (invoker, _) = BuildChain(response, _ => { }, mockLogger.Object);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("45")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SendAsync_WhenResponseIs429WithoutRetryAfter_LogsWarningWithoutWaitSec()
    {
        // 検証対象: SendAsync  目的: Retry-After なしの場合にヘッダーなし旨の Warning ログが出力されること
        var mockLogger = new Mock<ILogger>();
        mockLogger.Setup(l => l.IsEnabled(LogLevel.Warning)).Returns(true);

        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var (invoker, _) = BuildChain(response, _ => { }, mockLogger.Object);

        await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        mockLogger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Retry-After なし")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task SendAsync_WhenLoggerIsNull_DoesNotThrow()
    {
        // 検証対象: SendAsync  目的: logger=null のときでも NullReferenceException が発生しないこと
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        var (invoker, _) = BuildChain(response, _ => { }, logger: null);

        var act = async () => await invoker.SendAsync(MakeRequest(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
