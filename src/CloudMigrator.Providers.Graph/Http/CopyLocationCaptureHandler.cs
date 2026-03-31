using System.Net;

namespace CloudMigrator.Providers.Graph.Http;

/// <summary>
/// Captures the monitor URL returned by Graph /copy 202 responses.
/// </summary>
public sealed class CopyLocationCaptureHandler : DelegatingHandler
{
    private static readonly AsyncLocal<TaskCompletionSource<string?>?> s_tcs = new();

    /// <summary>
    /// Registers a capture slot for the current async flow.
    /// </summary>
    internal static CaptureScope BeginCapture()
    {
        var previous = s_tcs.Value;
        var taskSource = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_tcs.Value = taskSource;
        return new CaptureScope(previous, taskSource);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Accepted
            && request.Method == HttpMethod.Post
            && (request.RequestUri?.AbsolutePath.EndsWith("/copy", StringComparison.OrdinalIgnoreCase) ?? false)
            && s_tcs.Value is { } taskSource)
        {
            taskSource.TrySetResult(response.Headers.Location?.ToString());
            s_tcs.Value = null;
        }

        return response;
    }

    internal sealed class CaptureScope(TaskCompletionSource<string?>? previous, TaskCompletionSource<string?> taskSource) : IDisposable
    {
        private bool _disposed;

        internal TaskCompletionSource<string?> TaskSource { get; } = taskSource;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            TaskSource.TrySetResult(null);
            s_tcs.Value = previous;
        }
    }
}
