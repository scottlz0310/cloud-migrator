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
    /// <param name="onRateLimit">
    /// 429/503 レスポンス検出時に呼び出すコールバック（動的並列度制御用）。
    /// null の場合は通知なし。引数は Retry-After 値（null の場合は不明）。
    /// </param>
    public static GraphServiceClient Create(
        GraphAuthenticator authenticator,
        int timeoutSec = 300,
        int maxRetry = 3,
        Action<TimeSpan?>? onRateLimit = null)
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

                // RetryHandler の内側（後段）に RateLimitAwareHandler を挿入する。
                // これにより各リトライ試行ごとの 429/503 レスポンスを傍受できる。
                if (onRateLimit is not null)
                    handlers.Insert(i + 1, new RateLimitAwareHandler(onRateLimit));

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
