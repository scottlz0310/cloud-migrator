using CloudMigrator.Providers.Graph.Auth;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace CloudMigrator.Providers.Graph.Http;

/// <summary>
/// GraphServiceClient を生成するファクトリー。
/// retry / timeout / rate-limit の共通ミドルウェアを組み込む。
/// </summary>
public static class GraphClientFactory
{
    /// <summary>
    /// 認証情報から GraphServiceClient を生成する。
    /// </summary>
    /// <param name="authenticator">GraphAuthenticator インスタンス</param>
    /// <param name="timeoutSec">HTTP タイムアウト（秒）。デフォルト 300</param>
    /// <param name="maxRetry">最大リトライ回数。デフォルト 3</param>
    public static GraphServiceClient Create(
        GraphAuthenticator authenticator,
        int timeoutSec = 300,
        int maxRetry = 3)
    {
        // Kiota 標準ミドルウェアスタック（RetryHandler / RedirectHandler 等）を組み込む
        var handlers = KiotaClientFactory.CreateDefaultHandlers();

        // RetryHandler のオプションをカスタマイズ（Retry-After ヘッダー準拠）
        var retryOption = new RetryHandlerOption
        {
            MaxRetry = maxRetry,
            ShouldRetry = (delay, attempt, response) =>
                response?.StatusCode is
                    System.Net.HttpStatusCode.ServiceUnavailable or   // 503
                    System.Net.HttpStatusCode.TooManyRequests         // 429
                || response is null
        };

        // CreateDefaultHandlers が生成した RetryHandler を、カスタムオプション付きのものと差し替える
        for (var i = 0; i < handlers.Count; i++)
        {
            if (handlers[i] is RetryHandler)
            {
                handlers[i] = new RetryHandler(retryOption);
                break;
            }
        }

        var httpClient = KiotaClientFactory.Create(handlers);
        httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);
        httpClient.DefaultRequestHeaders.Add("client-request-id", Guid.NewGuid().ToString());

        var authProvider = new BaseBearerTokenAuthenticationProvider(authenticator);
        var adapter = new HttpClientRequestAdapter(authProvider, httpClient: httpClient);
        adapter.BaseUrl = "https://graph.microsoft.com/v1.0";

        return new GraphServiceClient(adapter);
    }
}
