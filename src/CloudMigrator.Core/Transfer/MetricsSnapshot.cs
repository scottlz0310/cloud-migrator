namespace CloudMigrator.Core.Transfer;

/// <summary>
/// 指定時間窓での集計メトリクス値オブジェクト。
/// <see cref="IMetricsAggregator.GetSnapshot"/> が返す純粋な値オブジェクト。
/// </summary>
public sealed record MetricsSnapshot(
    double Rps,
    double Rate429,
    double AvgLatencyMs,
    DateTimeOffset Timestamp);
