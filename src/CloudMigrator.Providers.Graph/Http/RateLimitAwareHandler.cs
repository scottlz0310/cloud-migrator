using System.Net;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Providers.Graph.Http;

/// <summary>
/// Graph API からの 429 (Too Many Requests) / 503 (Service Unavailable) レスポンスを傍受し、
/// <c>onRateLimit</c> コールバックへ通知する <see cref="DelegatingHandler"/>。
/// <para>
/// 実際のリトライは Kiota の <c>RetryHandler</c> が担当する。
/// 本ハンドラーは RetryHandler の内側（後段）に配置することで、
/// 各リトライ試行ごとのレスポンスを確認できる。
/// </para>
/// </summary>
internal sealed class RateLimitAwareHandler : DelegatingHandler
{
    private readonly Action<TimeSpan?> _onRateLimit;
    private readonly ILogger? _logger;

    /// <param name="onRateLimit">429/503 検出時に呼び出すコールバック。引数は Retry-After 値（null の場合は不明）。</param>
    /// <param name="logger">レートリミット標準放出用ロガー。null の場合はログ出力なし。</param>
    internal RateLimitAwareHandler(Action<TimeSpan?> onRateLimit, ILogger? logger = null)
    {
        _onRateLimit = onRateLimit;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.TooManyRequests    // 429
            or HttpStatusCode.ServiceUnavailable)                     // 503
        {
            // Retry-After は Delta（秒数）または Date（絶対時刻）のどちらかで返される。
            // 両形式に対応し、非負の待機時間として取り出す。
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            if (retryAfter is null && response.Headers.RetryAfter?.Date is DateTimeOffset date)
            {
                var delta = date - DateTimeOffset.UtcNow;
                retryAfter = delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }

            var waitSec = retryAfter.HasValue ? (int)Math.Ceiling(retryAfter.Value.TotalSeconds) : (int?)null;
            if (waitSec.HasValue)
                _logger?.LogWarning(
                    "429/503 レートリミット検出。{WaitSec}秒待機要求: {Method} {Url}",
                    waitSec, request.Method, request.RequestUri?.AbsolutePath);
            else
                _logger?.LogWarning(
                    "429/503 レートリミット検出（Retry-After なし）: {Method} {Url}",
                    request.Method, request.RequestUri?.AbsolutePath);

            _onRateLimit(retryAfter);
        }

        return response;
    }
}
