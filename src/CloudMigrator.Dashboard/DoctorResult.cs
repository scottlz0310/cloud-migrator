using System.Text.Json.Serialization;

namespace CloudMigrator.Dashboard;

/// <summary>個別チェックのステータス。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DoctorStatus { Pass, Warning, Fail }

/// <summary>接続テスト全体のステータス。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverallStatus { Healthy, Degraded, Unhealthy }

/// <summary>個別チェック結果。</summary>
/// <param name="Name">チェック名（例: "Graph 認証"）。</param>
/// <param name="Status">Pass / Warning / Fail。</param>
/// <param name="Detail">補足情報またはエラー詳細。null は情報なし。</param>
public sealed record DoctorCheck(string Name, DoctorStatus Status, string? Detail);

/// <summary>POST /api/setup/doctor のレスポンスボディ。</summary>
/// <param name="OverallStatus">全体ステータス（全 Pass → Healthy, 一部 Warn → Degraded, 一部 Fail → Unhealthy）。</param>
/// <param name="Checks">各チェックの結果一覧。</param>
public sealed record DoctorResult(OverallStatus OverallStatus, IReadOnlyList<DoctorCheck> Checks);
