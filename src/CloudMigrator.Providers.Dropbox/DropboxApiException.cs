using System.Net;

namespace CloudMigrator.Providers.Dropbox;

/// <summary>
/// Dropbox API 呼び出しが HTTP エラーを返した場合にスローされる例外。
/// </summary>
public sealed class DropboxApiException : HttpRequestException
{
    /// <summary>HTTP ステータスコード。</summary>
    public new HttpStatusCode StatusCode { get; }

    /// <summary>
    /// 429 レスポンスの <c>Retry-After</c> ヘッダー値。
    /// ヘッダーが存在しない場合は <see langword="null"/>。
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>レスポンスボディの生テキスト。</summary>
    public string ResponseBody { get; }

    public DropboxApiException(
        string message,
        HttpStatusCode statusCode,
        string responseBody,
        TimeSpan? retryAfter = null)
        : base(message, null, statusCode)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
        RetryAfter = retryAfter;

        // Core 層（Providers.Dropbox を直接参照できない）が HttpRequestException.Data 経由で
        // Retry-After を取得できるよう、標準の Exception.Data ディクショナリにも格納する。
        if (retryAfter.HasValue)
            Data["Retry-After"] = retryAfter.Value;
    }
}
