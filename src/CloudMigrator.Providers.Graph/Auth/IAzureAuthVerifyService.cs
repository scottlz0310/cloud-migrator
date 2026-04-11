namespace CloudMigrator.Providers.Graph.Auth;

/// <summary>
/// Azure Entra ID App-only 認証（Client Credentials フロー）の疎通確認サービス契約。
/// </summary>
public interface IAzureAuthVerifyService
{
    /// <summary>
    /// 指定した ClientId / TenantId / ClientSecret で Graph API トークンを取得し、
    /// App-only 認証が成功するかを確認する。
    /// </summary>
    /// <param name="clientId">Azure AD アプリケーション（クライアント）ID。</param>
    /// <param name="tenantId">Azure AD テナント ID。</param>
    /// <param name="clientSecret">クライアントシークレット。</param>
    /// <param name="ct">キャンセルトークン。</param>
    /// <returns>疎通確認結果。</returns>
    Task<AzureAuthVerifyResult> VerifyAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        CancellationToken ct = default);
}

/// <summary>
/// Azure Entra ID App-only 認証の疎通確認結果。
/// </summary>
/// <param name="IsSuccess">認証が成功した場合は <c>true</c>。</param>
/// <param name="ErrorMessage">失敗時のエラーメッセージ。成功時は <c>null</c>。</param>
public sealed record AzureAuthVerifyResult(bool IsSuccess, string? ErrorMessage = null);
