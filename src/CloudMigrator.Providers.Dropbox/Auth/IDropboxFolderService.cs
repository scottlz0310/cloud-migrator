namespace CloudMigrator.Providers.Dropbox.Auth;

/// <summary>Dropbox フォルダ一覧取得サービス（ウィザード転送先選択用）。</summary>
public interface IDropboxFolderService
{
    /// <summary>
    /// 指定フォルダ直下のフォルダ一覧を返す。
    /// </summary>
    /// <param name="accessToken">Dropbox アクセストークン。</param>
    /// <param name="folderPath">Dropbox API パス（ルートは <c>""</c>、サブフォルダは <c>/folder</c>）。</param>
    /// <param name="ct">キャンセルトークン。</param>
    Task<DropboxFolderListResult> ListFoldersAsync(
        string accessToken,
        string folderPath,
        CancellationToken ct = default);
}

/// <summary>Dropbox フォルダエントリ。</summary>
public sealed record DropboxFolderEntry(string Name, string PathDisplay);

/// <summary>フォルダ一覧取得結果。</summary>
public sealed record DropboxFolderListResult(
    bool Success,
    IReadOnlyList<DropboxFolderEntry>? Folders = null,
    string? ErrorMessage = null,
    bool IsPathNotFound = false);
