using Microsoft.Identity.Client;

namespace CloudMigrator.Providers.Graph.Auth;

/// <summary>
/// MSAL を使用して Azure Entra ID App-only 認証（Client Credentials フロー）を検証する
/// <see cref="IAzureAuthVerifyService"/> 実装。
/// </summary>
public sealed class AzureAuthVerifyService : IAzureAuthVerifyService
{
    private static readonly string[] Scopes = ["https://graph.microsoft.com/.default"];

    /// <inheritdoc/>
    public async Task<AzureAuthVerifyResult> VerifyAsync(
        string clientId,
        string tenantId,
        string clientSecret,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return new AzureAuthVerifyResult(false, "クライアント ID が入力されていません。");
        if (string.IsNullOrWhiteSpace(tenantId))
            return new AzureAuthVerifyResult(false, "テナント ID が入力されていません。");
        if (string.IsNullOrWhiteSpace(clientSecret))
            return new AzureAuthVerifyResult(false, "クライアントシークレットが入力されていません。");

        try
        {
            var app = ConfidentialClientApplicationBuilder
                .Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
                .Build();

            await app.AcquireTokenForClient(Scopes)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);

            return new AzureAuthVerifyResult(true);
        }
        catch (MsalServiceException ex) when (ex.Message.Contains("AADSTS"))
        {
            // Azure AD が返したエラー（無効な clientId/tenantId/secret、未同意など）
            return new AzureAuthVerifyResult(false, FormatMsalError(ex));
        }
        catch (MsalException ex)
        {
            return new AzureAuthVerifyResult(false, $"認証エラー: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AzureAuthVerifyResult(false, $"接続エラー: {ex.Message}");
        }
    }

    private static string FormatMsalError(MsalServiceException ex)
    {
        // AADSTS エラーコードを日本語メッセージに変換
        return ex.ErrorCode switch
        {
            "invalid_client" => "クライアントシークレットが無効です。正しい値を入力してください。",
            "unauthorized_client" => "このアプリに Client Credentials フローの許可がありません。管理者の同意が必要です。",
            "invalid_tenant" => "テナント ID が無効です。正しい値を入力してください。",
            "application_not_found" => "クライアント ID が見つかりません。正しい値を入力してください。",
            _ => $"Azure AD エラー ({ex.ErrorCode}): {ex.Message}",
        };
    }
}
