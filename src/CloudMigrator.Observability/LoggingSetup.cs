using Microsoft.Extensions.Logging;
using Serilog;
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
    /// </summary>
    /// <param name="logFilePath">ログファイルパス</param>
    /// <param name="minimumLevel">最小ログレベル</param>
    public static ILoggerFactory CreateLoggerFactory(
        string logFilePath = "logs/transfer.log",
        Serilog.Events.LogEventLevel minimumLevel = Serilog.Events.LogEventLevel.Information)
    {
        var dir = Path.GetDirectoryName(logFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.FromLogContext()
            .WriteTo.Console(new CompactJsonFormatter())
            .WriteTo.File(
                new CompactJsonFormatter(),
                logFilePath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: false)
            .CreateLogger();

        return LoggerFactory.Create(builder =>
            builder.AddSerilog(serilogLogger, dispose: true));
    }
}
