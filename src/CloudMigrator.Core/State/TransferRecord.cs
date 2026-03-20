namespace CloudMigrator.Core.State;

/// <summary>
/// SQLite 状態 DB に保存する転送レコード。
/// </summary>
public sealed record TransferRecord
{
    /// <summary>転送元プロバイダー内部 ID（OneDrive driveItem.id 等）。クラッシュリカバリ時の再ダウンロードに使用。</summary>
    public required string SourceId  { get; init; }

    /// <summary>ルートからの相対パス（末尾スラッシュなし）</summary>
    public required string Path      { get; init; }

    /// <summary>ファイル名（パス区切りなし）</summary>
    public required string Name      { get; init; }

    /// <summary>ファイルサイズ（バイト）。記録用（スキップ判定には使用しない）</summary>
    public long?   SizeBytes         { get; init; }

    /// <summary>最終更新日時（ISO 8601）。記録用（スキップ判定には使用しない）</summary>
    public string? Modified          { get; init; }

    /// <summary>転送ステータス</summary>
    public required TransferStatus Status { get; init; }

    /// <summary>リトライ回数。<see cref="SqliteTransferStateDb.MaxRetry"/> 以上で PermanentFailed に遷移。</summary>
    public int RetryCount            { get; init; }

    /// <summary>失敗時エラーメッセージ</summary>
    public string? Error             { get; init; }

    /// <summary>最終更新日時（UTC）</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>転送レコードのステータス。</summary>
public enum TransferStatus
{
    /// <summary>未処理</summary>
    Pending,

    /// <summary>処理中（クラッシュリカバリ向け: プロセス再起動時に再キューイングされる）</summary>
    Processing,

    /// <summary>転送完了</summary>
    Done,

    /// <summary>失敗（リトライ対象）</summary>
    Failed,

    /// <summary>永続失敗（MaxRetry 超過、以後リトライしない）</summary>
    PermanentFailed,
}
