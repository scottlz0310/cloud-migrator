namespace CloudMigrator.Core.Transfer;

/// <summary>
/// 転送ジョブ状態レコード（ダッシュボード専用・インメモリ管理）。
/// </summary>
/// <param name="JobId">ジョブの一意識別子（GUID 形式）。</param>
/// <param name="Status">ジョブの現在状態。</param>
/// <param name="StartedAt">ジョブ実行開始時刻（UTC）。Running 遷移時に設定。Pending 中は <c>null</c>。</param>
/// <param name="CompletedAt">ジョブ完了・失敗・キャンセル時刻（UTC）。未完了の場合は <c>null</c>。</param>
/// <param name="ErrorMessage">Failed 状態の場合のエラーメッセージ。それ以外は <c>null</c>。</param>
public sealed record TransferJobInfo(
    string JobId,
    JobStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage);
