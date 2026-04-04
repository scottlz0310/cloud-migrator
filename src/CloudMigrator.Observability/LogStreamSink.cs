using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace CloudMigrator.Observability;

/// <summary>
/// Serilog カスタムシンク。
/// ログイベントをリングバッファ（直近 500 件）に保持しつつ、
/// 登録された SSE クライアントへリアルタイムにブロードキャストする。
/// </summary>
public sealed class LogStreamSink : ILogEventSink, IDisposable
{
    /// <summary>バッファ上限（超過時は最古から破棄）。</summary>
    public const int BufferCapacity = 500;

    private readonly object _lock = new();
    private readonly Queue<LogEntry> _recentBuffer = new(BufferCapacity);
    private readonly ConcurrentDictionary<Guid, System.Threading.Channels.Channel<LogEntry>> _clients = new();

    /// <summary>
    /// 直近バッファのスナップショットを返す。
    /// 新規 SSE 接続時の初回送信に使用する。
    /// </summary>
    public LogEntry[] GetRecentEntries()
    {
        lock (_lock)
            return [.. _recentBuffer];
    }

    /// <summary>
    /// SSE クライアントを登録してリーダーを返す。
    /// 切断時は <see cref="Unsubscribe"/> を呼び出すこと。
    /// チャネルは Bounded（容量 <see cref="BufferCapacity"/>）で、
    /// 読み取りが追いつかない場合は最古エントリを破棄する（DropOldest）。
    /// </summary>
    public (Guid ClientId, System.Threading.Channels.ChannelReader<LogEntry> Reader) Subscribe()
    {
        var id = Guid.NewGuid();
        var ch = System.Threading.Channels.Channel.CreateBounded<LogEntry>(
            new System.Threading.Channels.BoundedChannelOptions(BufferCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            });
        _clients[id] = ch;
        return (id, ch.Reader);
    }

    /// <summary>
    /// SSE クライアントを登録解除してチャネルを完了する。
    /// </summary>
    public void Unsubscribe(Guid clientId)
    {
        if (_clients.TryRemove(clientId, out var ch))
            ch.Writer.TryComplete();
    }

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry(
            logEvent.Timestamp.ToUniversalTime(),
            logEvent.Level.ToString(),
            logEvent.RenderMessage());

        // リングバッファへ追加（上限超過時は最古を削除）
        lock (_lock)
        {
            _recentBuffer.Enqueue(entry);
            while (_recentBuffer.Count > BufferCapacity)
                _recentBuffer.Dequeue();
        }

        // 各クライアントへブロードキャスト（完了済みチャネルへの書き込みは無視）
        foreach (var (_, ch) in _clients)
            ch.Writer.TryWrite(entry);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (_, ch) in _clients)
            ch.Writer.TryComplete();
        _clients.Clear();
    }
}
