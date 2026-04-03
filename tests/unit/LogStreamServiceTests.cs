using CloudMigrator.Observability;
using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: LogStreamSink  目的: リングバッファ・SSE ブロードキャスト動作を確認する
/// </summary>
public sealed class LogStreamServiceTests
{
    // ── バッファ追加 ──────────────────────────────────────────────────────

    [Fact]
    public void Emit_AddsEntryToRecentBuffer()
    {
        // 検証対象: LogStreamSink.Emit  目的: エントリがリングバッファに追加される
        var sink = new LogStreamSink();
        var logEvent = MakeLogEvent(LogEventLevel.Information, "テストメッセージ");

        sink.Emit(logEvent);

        var entries = sink.GetRecentEntries();
        entries.Should().HaveCount(1);
        entries[0].Level.Should().Be("Information");
        entries[0].Message.Should().Be("テストメッセージ");
    }

    [Fact]
    public void Emit_TimestampIsUtc()
    {
        // 検証対象: LogEntry.Timestamp  目的: タイムスタンプが UTC で格納される
        var sink = new LogStreamSink();

        sink.Emit(MakeLogEvent(LogEventLevel.Information, "utc check"));

        var entry = sink.GetRecentEntries()[0];
        entry.Timestamp.Offset.Should().Be(TimeSpan.Zero);
    }

    // ── バッファ上限（DropOldest） ────────────────────────────────────────

    [Fact]
    public void GetRecentEntries_LimitedTo500_DropOldest()
    {
        // 検証対象: LogStreamSink バッファ上限
        // 目的: 501 件目以降は最古エントリから順に破棄される
        var sink = new LogStreamSink();
        for (int i = 1; i <= LogStreamSink.BufferCapacity + 1; i++)
            sink.Emit(MakeLogEvent(LogEventLevel.Information, $"msg {i}"));

        var entries = sink.GetRecentEntries();
        entries.Should().HaveCount(LogStreamSink.BufferCapacity);
        entries[0].Message.Should().Be("msg 2");          // msg 1 は破棄
        entries[^1].Message.Should().Be($"msg {LogStreamSink.BufferCapacity + 1}");
    }

    // ── SSE ブロードキャスト ──────────────────────────────────────────────

    [Fact]
    public async Task Subscribe_ReceivesEmittedEntries()
    {
        // 検証対象: LogStreamSink.Subscribe + Emit
        // 目的: Emit されたエントリがサブスクライバーに届く
        var sink = new LogStreamSink();
        var (clientId, reader) = sink.Subscribe();

        sink.Emit(MakeLogEvent(LogEventLevel.Warning, "broadcast test"));
        sink.Unsubscribe(clientId);

        var entries = new List<LogEntry>();
        await foreach (var e in reader.ReadAllAsync())
            entries.Add(e);

        entries.Should().HaveCount(1);
        entries[0].Level.Should().Be("Warning");
        entries[0].Message.Should().Be("broadcast test");
    }

    [Fact]
    public async Task Subscribe_MultipleClients_AllReceive()
    {
        // 検証対象: LogStreamSink 複数クライアント
        // 目的: 複数の SSE クライアントそれぞれにブロードキャストされる
        var sink = new LogStreamSink();
        var (id1, reader1) = sink.Subscribe();
        var (id2, reader2) = sink.Subscribe();

        sink.Emit(MakeLogEvent(LogEventLevel.Error, "multi-cast"));
        sink.Unsubscribe(id1);
        sink.Unsubscribe(id2);

        var list1 = new List<LogEntry>();
        await foreach (var e in reader1.ReadAllAsync()) list1.Add(e);

        var list2 = new List<LogEntry>();
        await foreach (var e in reader2.ReadAllAsync()) list2.Add(e);

        list1.Should().HaveCount(1).And.ContainSingle(e => e.Message == "multi-cast");
        list2.Should().HaveCount(1).And.ContainSingle(e => e.Message == "multi-cast");
    }

    [Fact]
    public void Emit_DoesNotThrow_WhenClientDisconnected()
    {
        // 検証対象: LogStreamSink.Emit（切断済みクライアント）
        // 目的: Unsubscribe 後の Emit が例外を投げない
        var sink = new LogStreamSink();
        var (clientId, _) = sink.Subscribe();
        sink.Unsubscribe(clientId);

        var act = () => sink.Emit(MakeLogEvent(LogEventLevel.Information, "no crash"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CompletesAllClientChannels()
    {
        // 検証対象: LogStreamSink.Dispose  目的: Dispose 後にすべてのクライアントチャネルが完了する
        var sink = new LogStreamSink();
        var (_, reader) = sink.Subscribe();

        sink.Dispose();

        reader.Completion.IsCompleted.Should().BeTrue();
    }

    // ── ヘルパー ─────────────────────────────────────────────────────────

    private static LogEvent MakeLogEvent(LogEventLevel level, string message)
    {
        var parser = new MessageTemplateParser();
        var template = parser.Parse(message);
        return new LogEvent(DateTimeOffset.UtcNow, level, null, template, []);
    }
}
