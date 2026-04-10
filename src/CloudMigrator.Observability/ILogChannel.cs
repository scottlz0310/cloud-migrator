using System.Threading.Channels;

namespace CloudMigrator.Observability;

/// <summary>
/// Channel ベースのインプロセスログ配信サービスの契約。
/// SSE の代替として Blazor コンポーネントがリアルタイムログを購読するために使用する。
/// </summary>
public interface ILogChannel
{
    /// <summary>
    /// 直近のログエントリのスナップショットを返す（接続時の初回表示用）。
    /// </summary>
    LogEntry[] GetRecentEntries();

    /// <summary>
    /// ログストリームを購読する。
    /// 呼び出し元は <see cref="Unsubscribe"/> で必ず購読解除すること。
    /// </summary>
    (Guid SubscriberId, ChannelReader<LogEntry> Reader) Subscribe();

    /// <summary>購読を解除する。</summary>
    void Unsubscribe(Guid subscriberId);
}

/// <summary>
/// <see cref="LogStreamSink"/> を <see cref="ILogChannel"/> としてラップするアダプター。
/// LogStreamSink がリングバッファと購読管理を担い、このクラスはインターフェースのブリッジのみを行う。
/// </summary>
public sealed class LogChannelAdapter : ILogChannel
{
    private readonly LogStreamSink _sink;

    public LogChannelAdapter(LogStreamSink sink)
    {
        _sink = sink;
    }

    /// <inheritdoc />
    public LogEntry[] GetRecentEntries() => _sink.GetRecentEntries();

    /// <inheritdoc />
    public (Guid SubscriberId, ChannelReader<LogEntry> Reader) Subscribe()
    {
        var (id, reader) = _sink.Subscribe();
        return (id, reader);
    }

    /// <inheritdoc />
    public void Unsubscribe(Guid subscriberId) => _sink.Unsubscribe(subscriberId);
}
