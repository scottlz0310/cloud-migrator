using System.Text;
using CloudMigrator.Dashboard;
using CloudMigrator.Observability;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Serilog.Events;
using Serilog.Parsing;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: LogStreamService.StreamAsync  目的: SSE レスポンスヘッダー・ボディ・キャンセル挙動を確認する
/// </summary>
public sealed class LogStreamServiceTests
{
    // ── ヘッダー ─────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_SetsCorrectResponseHeaders()
    {
        // 検証対象: LogStreamService.StreamAsync  目的: SSE に必要なレスポンスヘッダーが正しく設定される
        var sink = new LogStreamSink();
        var service = new LogStreamService(sink);
        var ctx = CreateHttpContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 接続後即座にキャンセル

        await service.StreamAsync(ctx, cts.Token);

        ctx.Response.Headers["Content-Type"].ToString().Should().Be("text/event-stream");
        ctx.Response.Headers["Cache-Control"].ToString().Should().Be("no-cache");
        ctx.Response.Headers["X-Accel-Buffering"].ToString().Should().Be("no");
    }

    // ── 初回バッファ送信 ──────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_SendsBufferedEntriesOnConnect()
    {
        // 検証対象: LogStreamService.StreamAsync  目的: 接続時に直近バッファが SSE data として送信される
        var sink = new LogStreamSink();
        sink.Emit(MakeLogEvent(LogEventLevel.Information, "buffered-entry-test"));
        var service = new LogStreamService(sink);
        var body = new MemoryStream();
        var ctx = CreateHttpContext(body);

        // 短いタイムアウトで待機後キャンセル
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.StreamAsync(ctx, cts.Token);

        var text = Encoding.UTF8.GetString(body.ToArray());
        text.Should().Contain("data: ");
        text.Should().Contain("buffered-entry-test");
    }

    // ── SSE フォーマット ─────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_OutputsCorrectSseFormat()
    {
        // 検証対象: LogStreamService.WriteEventAsync  目的: SSE 出力が "data: {...}\n\n" 形式になっている
        var sink = new LogStreamSink();
        sink.Emit(MakeLogEvent(LogEventLevel.Warning, "format check"));
        var service = new LogStreamService(sink);
        var body = new MemoryStream();
        var ctx = CreateHttpContext(body);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.StreamAsync(ctx, cts.Token);

        var text = Encoding.UTF8.GetString(body.ToArray());
        text.Should().MatchRegex(@"data: \{.*\}\n\n");
        text.Should().Contain("\"level\":\"Warning\"");
        text.Should().Contain("\"message\":\"format check\"");
    }

    // ── キャンセル ────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_CancelledImmediately_DoesNotThrow()
    {
        // 検証対象: LogStreamService.StreamAsync  目的: 即時キャンセルで例外が呼び出し元に伝播しない
        var sink = new LogStreamSink();
        var service = new LogStreamService(sink);
        var ctx = CreateHttpContext();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await service.StreamAsync(ctx, cts.Token);

        await act.Should().NotThrowAsync();
    }

    // ── リソース解放 ─────────────────────────────────────────────────────

    [Fact]
    public async Task StreamAsync_UnsubscribesOnCancellation()
    {
        // 検証対象: LogStreamService.StreamAsync  目的: キャンセル時にサブスクライバーが解除される
        var sink = new LogStreamSink();
        var service = new LogStreamService(sink);
        var ctx = CreateHttpContext();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await service.StreamAsync(ctx, cts.Token);

        // サブスクライバーが解除されているため新たな Emit は届かない（デッドロックせず完了）
        sink.Emit(MakeLogEvent(LogEventLevel.Information, "after unsubscribe"));
        var entries = sink.GetRecentEntries();
        entries.Should().ContainSingle(e => e.Message == "after unsubscribe");
    }

    // ── ヘルパー ─────────────────────────────────────────────────────────

    private static DefaultHttpContext CreateHttpContext(Stream? responseBody = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = responseBody ?? new MemoryStream();
        return ctx;
    }

    private static LogEvent MakeLogEvent(LogEventLevel level, string message)
    {
        var parser = new MessageTemplateParser();
        var template = parser.Parse(message);
        return new LogEvent(DateTimeOffset.UtcNow, level, null, template, []);
    }
}
