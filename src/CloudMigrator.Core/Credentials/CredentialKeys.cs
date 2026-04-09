namespace CloudMigrator.Core.Credentials;

/// <summary>
/// Credential Manager で使用するキー定数。
/// これらのキーは確定値であり、変更は禁止。
/// </summary>
public static class CredentialKeys
{
    /// <summary>Azure Entra ID アプリケーションのクライアントシークレット。</summary>
    public const string AzureClientSecret = "cloud-migrator/azure/client-secret";

    /// <summary>Azure Entra ID のアクセストークン（将来拡張用）。</summary>
    public const string AzureAccessToken = "cloud-migrator/azure/access-token";

    /// <summary>Dropbox アプリの App Key（OAuth 2.0 Public Client 方式）。</summary>
    public const string DropboxAppKey = "cloud-migrator/dropbox/app-key";

    /// <summary>Dropbox アクセストークン。</summary>
    public const string DropboxAccessToken = "cloud-migrator/dropbox/access-token";

    /// <summary>Dropbox リフレッシュトークン。</summary>
    public const string DropboxRefreshToken = "cloud-migrator/dropbox/refresh-token";
}
