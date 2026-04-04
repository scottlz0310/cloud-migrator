namespace CloudMigrator.Observability;

/// <summary>
/// SSE 配信用ログエントリ。
/// timestamp は UTC ISO 8601、level は Serilog レベル名、message はレンダリング済みテキスト。
/// </summary>
public sealed record LogEntry(DateTimeOffset Timestamp, string Level, string Message);
