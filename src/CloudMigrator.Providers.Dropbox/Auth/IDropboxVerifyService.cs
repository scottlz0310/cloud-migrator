namespace CloudMigrator.Providers.Dropbox.Auth;

/// <summary>
/// Dropbox 接続検証の層。
/// </summary>
public enum DropboxVerifyLayer
{
    /// <summary>クレデンシャルの存在確認。</summary>
    Credential,

    /// <summary>Dropbox API 到達確認（ルートフォルダ一覧）。</summary>
    Discovery,

    /// <summary>小ファイル書き込み・削除による読み書き権限確認。</summary>
    Preflight,
}

/// <summary>
/// Dropbox 接続検証の各層の結果。
/// </summary>
/// <param name="Layer">検証層。</param>
/// <param name="IsSuccess">成功かどうか。</param>
/// <param name="Detail">補足情報またはエラー詳細。</param>
public sealed record DropboxVerifyCheck(DropboxVerifyLayer Layer, bool IsSuccess, string? Detail);

/// <summary>
/// Dropbox 接続検証の全体結果。
/// </summary>
/// <param name="IsSuccess">全層が成功した場合 true。</param>
/// <param name="Checks">各層の検証結果。</param>
public sealed record DropboxVerifyResult(bool IsSuccess, IReadOnlyList<DropboxVerifyCheck> Checks);

/// <summary>
/// Dropbox 接続を Credential / Discovery / Preflight の 3 層で検証するサービス抽象。
/// </summary>
public interface IDropboxVerifyService
{
    /// <summary>
    /// Credential Verify → Discovery Verify → Migration Preflight の順で検証を実行する。
    /// いずれかの層で失敗した場合、後続の層はスキップされる。
    /// </summary>
    Task<DropboxVerifyResult> VerifyAsync(CancellationToken cancellationToken = default);
}
