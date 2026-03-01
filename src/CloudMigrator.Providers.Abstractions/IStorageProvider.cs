namespace CloudMigrator.Providers.Abstractions;

/// <summary>
/// ストレージプロバイダーの共通契約。
/// Graph、Dropbox 等の実装はこのインターフェースを実装する。
/// </summary>
public interface IStorageProvider
{
    /// <summary>プロバイダー識別子（例: "graph", "dropbox"）</summary>
    string ProviderId { get; }

    /// <summary>指定パス配下のファイル一覧を再帰的に取得する</summary>
    Task<IReadOnlyList<StorageItem>> ListItemsAsync(string rootPath, CancellationToken cancellationToken = default);

    /// <summary>ファイルをアップロードする（サイズに応じてチャンク切替は実装側が行う）</summary>
    Task UploadFileAsync(TransferJob job, CancellationToken cancellationToken = default);

    /// <summary>フォルダを作成する（既存の場合は何もしない）</summary>
    Task EnsureFolderAsync(string folderPath, CancellationToken cancellationToken = default);
}
