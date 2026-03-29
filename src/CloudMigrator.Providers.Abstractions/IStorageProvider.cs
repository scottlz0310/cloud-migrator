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
    /// 指定アイテムを読み取りストリームとして取得する。
    /// 既定実装は一時ファイルへダウンロードしてから、そのファイルを自動削除付きストリームとして開く。
    /// </summary>
    async Task<Stream> DownloadStreamAsync(StorageItem item, CancellationToken cancellationToken = default)
    {
        var tempPath = await DownloadToTempAsync(item, cancellationToken).ConfigureAwait(false);
        return await TempFileBackedReadStream.CreateAsync(tempPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ローカル一時ファイルを転送先にアップロードする（クロスプロバイダー転送用）。
    /// </summary>
    Task UploadFromLocalAsync(string localFilePath, long fileSizeBytes, string destinationFullPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// 読み取りストリームを転送先にアップロードする。
    /// 既定実装は一時ファイルへ退避して <see cref="UploadFromLocalAsync"/> を呼ぶ。
    /// </summary>
    async Task UploadFromStreamAsync(
        Stream sourceStream,
        long fileSizeBytes,
        string destinationFullPath,
        CancellationToken cancellationToken = default)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await using (var tempStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await sourceStream.CopyToAsync(tempStream, cancellationToken).ConfigureAwait(false);
            }

            await UploadFromLocalAsync(
                tempPath,
                fileSizeBytes,
                destinationFullPath,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* ベストエフォート */ }
        }
    }

    /// <summary>ファイルをアップロードする（サイズに応じてチャンク切替は実装側が行う）</summary>
    Task UploadFileAsync(TransferJob job, CancellationToken cancellationToken = default);

    /// <summary>フォルダを作成する（既存の場合は何もしない）</summary>
    Task EnsureFolderAsync(string folderPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// ファイルアップロード時に親フォルダを自動作成するか。
    /// <c>true</c> の場合、転送オーケストレーション処理はフォルダ先行作成フェーズをスキップする。
    /// デフォルト: <c>false</c>（Graph 等の従来実装への後方互換）。
    /// </summary>
    bool AutoCreateParentFolders => false;

    /// <summary>
    /// ページング列挙 API（カーソル / nextLink 対応）。
    /// デフォルト実装は <see cref="ListItemsAsync"/> をラップして単一ページとして返す。
    /// Dropbox 等はこのメソッドをオーバーライドしてネイティブページングを実装する。
    /// </summary>
    /// <param name="rootPath">クロール対象のルートパス</param>
    /// <param name="cursor">前回ページの継続カーソル。null の場合は先頭から取得する。</param>
    async Task<StoragePage> ListPagedAsync(
        string rootPath,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        // cursor を無視して全件を単一ページで返すデフォルト実装（後方互換）
        var items = await ListItemsAsync(rootPath, cancellationToken).ConfigureAwait(false);
        return new StoragePage { Items = items, Cursor = null, HasMore = false };
    }
}
