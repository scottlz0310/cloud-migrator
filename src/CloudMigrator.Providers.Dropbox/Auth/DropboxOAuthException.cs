namespace CloudMigrator.Providers.Dropbox.Auth;

/// <summary>
/// Dropbox OAuth フロー中に発生する例外。
/// </summary>
public sealed class DropboxOAuthException : Exception
{
    /// <summary>OAuth エラーコード（例: invalid_grant, token_revoked, token_not_found）。</summary>
    public string? ErrorCode { get; }

    /// <summary>トークンまたは認証情報が失効または欠如しており、再認証が必要かどうか。</summary>
    public bool IsTokenExpired { get; }

    public DropboxOAuthException(string message, bool isTokenExpired = false)
        : base(message)
    {
        IsTokenExpired = isTokenExpired;
    }

    public DropboxOAuthException(string message, string? errorCode, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
        IsTokenExpired = errorCode is "token_expired" or "token_revoked" or "token_not_found"
            or "invalid_grant" or "expired_access_token";
    }

    public DropboxOAuthException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
