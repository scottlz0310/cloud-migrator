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

    /// <summary>
    /// 指定アイテムをローカル一時ファイルへダウンロードし、そのパスを返す。
    /// 呼び出し側は使用後に必ずファイルを削除すること。
    /// </summary>
    Task<string> DownloadToTempAsync(StorageItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// ローカル一時ファイルを転送先にアップロードする（クロスプロバイダー転送用）。
    /// </summary>
    Task UploadFromLocalAsync(string localFilePath, long fileSizeBytes, string destinationFullPath, CancellationToken cancellationToken = default);

    /// <summary>ファイルをアップロードする（サイズに応じてチャンク切替は実装側が行う）</summary>
    Task UploadFileAsync(TransferJob job, CancellationToken cancellationToken = default);

    /// <summary>フォルダを作成する（既存の場合は何もしない）</summary>
    Task EnsureFolderAsync(string folderPath, CancellationToken cancellationToken = default);
}
