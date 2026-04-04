using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace CloudMigrator.Observability;

/// <summary>
/// Serilog を使用した構造化ログのセットアップ（NFR-01/NFR-02）。
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// ILoggerFactory を構築する。
    /// コンソール（JSON）とファイル（CLEF フォーマット）へ出力する。
    /// タイムスタンプは UTC 固定（.github/copilot-instructions.md 要件）。
    /// </summary>
    /// <param name="logFilePath">ログファイルパス</param>
    /// <param name="minimumLevel">最小ログレベル</param>
    /// <param name="logStreamSink">
    /// オプション: SSE ブロードキャスト用シンク。
    /// 非 null の場合は Serilog パイプラインに追加される。
    /// </param>
    public static ILoggerFactory CreateLoggerFactory(
        string logFilePath = "logs/transfer.log",
        Serilog.Events.LogEventLevel minimumLevel = Serilog.Events.LogEventLevel.Information,
        LogStreamSink? logStreamSink = null)
    {
        var dir = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var formatter = new UtcCompactJsonFormatter();

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.Console(formatter)
            .WriteTo.File(
                formatter,
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: false);

        if (logStreamSink is not null)
            config = config.WriteTo.Sink(logStreamSink);

        var serilogLogger = config.CreateLogger();

        return LoggerFactory.Create(builder =>
            builder.AddSerilog(serilogLogger, dispose: true));
    }

    /// <summary>
    /// タイムスタンプを UTC に変換してから CompactJsonFormatter へ委譲するフォーマッター。
    /// Serilog はデフォルトで DateTimeOffset.Now（ローカル時刻）を使うため、
    /// UTC 統一のためにここで変換する。
    /// </summary>
    private sealed class UtcCompactJsonFormatter : ITextFormatter
    {
        private static readonly CompactJsonFormatter _inner = new();

        public void Format(LogEvent logEvent, TextWriter output)
        {
            var utcEvent = new LogEvent(
                logEvent.Timestamp.ToUniversalTime(),
                logEvent.Level,
                logEvent.Exception,
                logEvent.MessageTemplate,
                logEvent.Properties.Select(p => new LogEventProperty(p.Key, p.Value)));
            _inner.Format(utcEvent, output);
        }
    }
}
