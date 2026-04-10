namespace CloudMigrator.Providers.Dropbox.Auth;

/// <summary>
/// OAuth 認可フロー完了後のトークン結果。
/// </summary>
/// <param name="AccessToken">取得したアクセストークン。</param>
/// <param name="RefreshToken">取得したリフレッシュトークン（offline アクセス時に返される）。</param>
/// <param name="ExpiresIn">アクセストークンの有効秒数。</param>
public sealed record DropboxTokenResult(string AccessToken, string? RefreshToken, int ExpiresIn);

/// <summary>
/// リフレッシュトークン交換後のアクセストークン結果。
/// </summary>
/// <param name="AccessToken">新しいアクセストークン。</param>
/// <param name="ExpiresIn">アクセストークンの有効秒数。</param>
public sealed record DropboxRefreshResult(string AccessToken, int ExpiresIn);
