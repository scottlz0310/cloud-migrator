namespace CloudMigrator.Core.Credentials;

/// <summary>
/// 環境変数から認証情報を読み取る <see cref="ICredentialStore"/> 実装（後方互換・v0.4.x のみ）。
/// v0.5.0 以降は削除予定のため、Windows Credential Manager への移行を推奨する。
/// </summary>
[Obsolete(
    "環境変数ベースの認証情報管理は v0.4.x のみのサポートです。" +
    "Windows Credential Manager（WindowsCredentialStore）への移行を検討してください。")]
public sealed class EnvironmentCredentialStore : ICredentialStore
{
    /// <summary>Credential Key → 環境変数名のマッピング。</summary>
    private static readonly IReadOnlyDictionary<string, string> KeyToEnvVar =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CredentialKeys.AzureClientSecret] = "MIGRATOR__GRAPH__CLIENTSECRET",
            [CredentialKeys.AzureAccessToken] = "MIGRATOR__GRAPH__ACCESSTOKEN",
            [CredentialKeys.DropboxAppKey] = "MIGRATOR__DROPBOX__CLIENTID",
            [CredentialKeys.DropboxAccessToken] = "MIGRATOR__DROPBOX__ACCESSTOKEN",
            [CredentialKeys.DropboxRefreshToken] = "MIGRATOR__DROPBOX__REFRESHTOKEN",
        };

    /// <inheritdoc/>
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!KeyToEnvVar.TryGetValue(key, out var envVar))
            return Task.FromResult<string?>(null);

        var value = Environment.GetEnvironmentVariable(envVar);
        return Task.FromResult(string.IsNullOrEmpty(value) ? null : value);
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">
    /// EnvironmentCredentialStore は読み取り専用のため SaveAsync はサポートしない。
    /// </exception>
    public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "EnvironmentCredentialStore は読み取り専用です。" +
            "認証情報の保存には WindowsCredentialStore を使用してください。");

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!KeyToEnvVar.TryGetValue(key, out var envVar))
            return Task.FromResult(false);

        return Task.FromResult(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)));
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">
    /// EnvironmentCredentialStore は読み取り専用のため DeleteAsync はサポートしない。
    /// </exception>
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "EnvironmentCredentialStore は読み取り専用です。");
}
