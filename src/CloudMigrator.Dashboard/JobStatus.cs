namespace CloudMigrator.Dashboard;

/// <summary>
/// 転送ジョブの実行状態を表す列挙型。
/// </summary>
public enum JobStatus
{
    /// <summary>登録済み・未開始</summary>
    Pending,

    /// <summary>転送実行中</summary>
    Running,

    /// <summary>正常完了</summary>
    Completed,

    /// <summary>エラー終了</summary>
    Failed,

    /// <summary>停止操作によりキャンセル</summary>
    Cancelled,
}
