using System.Net;

namespace CloudMigrator.Providers.Graph.Http;

/// <summary>
/// Graph API /copy エンドポイントへの POST に対する 202 レスポンスから
/// Location ヘッダー（Monitor URL）を捕捉する <see cref="DelegatingHandler"/>。
/// <para>
/// <see cref="BeginCapture"/> を呼び出した非同期コンテキストと、そこから派生するすべての
/// 子コンテキスト（Kiota SDK 内の HTTP 呼び出しチェーンを含む）で値が共有される
/// <see cref="AsyncLocal{T}"/> を利用することで、並列コピー操作ごとに安全に相関させる。
/// </para>
/// </summary>
public sealed class CopyLocationCaptureHandler : DelegatingHandler
{
    private static readonly AsyncLocal<TaskCompletionSource<string?>?> s_tcs = new();

    /// <summary>
    /// 呼び出し元の非同期コンテキストに Monitor URL 捕捉用 TCS を設定し、その TCS を返す。
    /// 直後に <c>Copy.PostAsync</c> を呼び出すことで、202 レスポンスの Location ヘッダーが
    /// TCS に格納される。
    /// </summary>
    internal static TaskCompletionSource<string?> BeginCapture()  // internal: GraphStorageProvider のみが呼び出す
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_tcs.Value = tcs;
        return tcs;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // /copy エンドポイントへの POST で 202 Accepted が返った場合のみ Location を捕捉する
        if (response.StatusCode == HttpStatusCode.Accepted
            && request.Method == HttpMethod.Post
            && (request.RequestUri?.AbsolutePath.EndsWith("/copy", StringComparison.OrdinalIgnoreCase) ?? false)
            && s_tcs.Value is { } tcs)
        {
            tcs.TrySetResult(response.Headers.Location?.ToString());
        }

        return response;
    }
}
