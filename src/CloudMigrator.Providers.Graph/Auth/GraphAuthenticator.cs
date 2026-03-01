using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;

namespace CloudMigrator.Providers.Graph.Auth;

/// <summary>
/// Microsoft Graph client credentials 認証（FR-01）。
/// MSAL が内部でトークンキャッシュを管理し、有効期限前に自動再取得する。
/// </summary>
public sealed class GraphAuthenticator : IAccessTokenProvider
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    private readonly IConfidentialClientApplication _app;

    public AllowedHostsValidator AllowedHostsValidator { get; } =
        new(["graph.microsoft.com"]);

    public GraphAuthenticator(string clientId, string tenantId, string clientSecret)
    {
        _app = ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .Build();
    }

    /// <inheritdoc/>
    public async Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        // MSAL がトークンをキャッシュ。有効期限 5 分前に自動再取得する。
        var result = await _app
            .AcquireTokenForClient(Scopes)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return result.AccessToken;
    }
}
