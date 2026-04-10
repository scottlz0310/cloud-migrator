namespace CloudMigrator.Providers.Dropbox.Auth;

/// <summary>
/// Dropbox OAuth 2.0 PKCE フローを管理するサービス抽象。
/// </summary>
public interface IDropboxOAuthService
{
    /// <summary>
    /// PKCE フローを開始し、アクセストークンとリフレッシュトークンを返す。
    /// ユーザーはブラウザで認可ページを開き、コールバックを待つ。
    /// </summary>
    /// <param name="appKey">Dropbox App Key（App Console から取得）。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>OAuth 認可結果（access_token / refresh_token）。</returns>
    Task<DropboxTokenResult> AuthorizeAsync(string appKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// リフレッシュトークンを使用して新しいアクセストークンを取得する。
    /// </summary>
    /// <param name="appKey">Dropbox App Key。</param>
    /// <param name="refreshToken">保存済みリフレッシュトークン。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>新しいアクセストークンと有効期限。</returns>
    Task<DropboxRefreshResult> RefreshTokenAsync(string appKey, string refreshToken, CancellationToken cancellationToken = default);
}
