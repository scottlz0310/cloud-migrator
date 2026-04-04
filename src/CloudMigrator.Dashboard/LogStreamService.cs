using System.Text.Json;
using CloudMigrator.Observability;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CloudMigrator.Dashboard;

/// <summary>
/// SSE ログストリーミングサービスの契約。
/// </summary>
public interface ILogStreamService
{
    /// <summary>
    /// SSE ストリームを開始する。
    /// 接続時に直近バッファを送信し、以降はリアルタイムで追記する。
    /// クライアント切断時（ct キャンセル）はリソースを即時解放する。
    /// </summary>
    Task StreamAsync(HttpContext ctx, CancellationToken ct);
}

/// <summary>
/// <see cref="LogStreamSink"/> を通じて Serilog ログを SSE でブロードキャストする実装。
/// 接続時に直近 500 件のバッファを初回送信し、以降はリアルタイムで追記する。
/// 複数クライアントの同時接続に対応する。
/// </summary>
public sealed class LogStreamService : ILogStreamService
{
    private readonly LogStreamSink _sink;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public LogStreamService(LogStreamSink sink)
    {
        _sink = sink;
    }

    /// <inheritdoc />
    public async Task StreamAsync(HttpContext ctx, CancellationToken ct)
    {
        // レスポンスバッファリングを無効化（nginx / IIS 対応）
        var bufferFeature = ctx.Features.Get<IHttpResponseBodyFeature>();
        bufferFeature?.DisableBuffering();

        ctx.Response.StatusCode = 200;
        ctx.Response.Headers["Content-Type"] = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        // Subscribe を先に行ってリアルタイム受信を開始し、その後バッファを送信する。
        // こうすることでスナップショット取得〜Subscribe 完了の間のログ取りこぼしを防ぐ
        // （重複は許容する）。
        var (clientId, reader) = _sink.Subscribe();
        try
        {
            // 接続時: 直近バッファを初回送信
            foreach (var entry in _sink.GetRecentEntries())
                await WriteEventAsync(ctx.Response, entry, ct).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);

            // リアルタイム: 新しいログを継続送信
            await foreach (var entry in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await WriteEventAsync(ctx.Response, entry, ct).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // クライアント切断またはサーバーシャットダウン — 正常終了
        }
        catch (IOException)
        {
            // broken pipe 等によるクライアント切断 — 正常終了
        }
        finally
        {
            _sink.Unsubscribe(clientId);
        }
    }

    private static Task WriteEventAsync(HttpResponse response, LogEntry entry, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            timestamp = entry.Timestamp.ToString("O"),
            level = entry.Level,
            message = entry.Message,
        }, _jsonOptions);
        return response.WriteAsync($"data: {json}\n\n", ct);
    }
}
