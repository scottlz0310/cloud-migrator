using System.Net;
using System.Reflection;
using CloudMigrator.Providers.Graph.Http;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

public sealed class CopyLocationCaptureHandlerTests
{
    [Fact]
    public async Task SendAsync_ShouldCaptureLocationAndClearAsyncLocal_ForAcceptedCopyPost()
    {
        using var handler = new CopyLocationCaptureHandler
        {
            InnerHandler = new StubHttpMessageHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Location = new Uri("https://monitor.example/jobs/123");
                return response;
            })
        };
        using var invoker = new HttpMessageInvoker(handler);

        TaskCompletionSource<string?> taskSource;
        using (var capture = CopyLocationCaptureHandler.BeginCapture())
        {
            taskSource = capture.TaskSource;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/drives/drive/items/item/copy");
            using var response = await invoker.SendAsync(request, CancellationToken.None);

            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            (await taskSource.Task).Should().Be("https://monitor.example/jobs/123");
        }

        object? currentCapture = GetCaptureSlot().Value;
        currentCapture.Should().BeNull();
    }

    [Fact]
    public async Task SendAsync_ShouldIgnoreNonCopyRequests()
    {
        try
        {
            using var handler = new CopyLocationCaptureHandler
            {
                InnerHandler = new StubHttpMessageHandler(_ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                    response.Headers.Location = new Uri("https://monitor.example/jobs/ignored");
                    return response;
                })
            };
            using var invoker = new HttpMessageInvoker(handler);

            TaskCompletionSource<string?> taskSource;
            using (var capture = CopyLocationCaptureHandler.BeginCapture())
            {
                taskSource = capture.TaskSource;
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/drives/drive/items/item");
                using var response = await invoker.SendAsync(request, CancellationToken.None);

                response.StatusCode.Should().Be(HttpStatusCode.Accepted);
                taskSource.Task.IsCompleted.Should().BeFalse();
            }

            object? currentCapture = GetCaptureSlot().Value;
            currentCapture.Should().BeNull();
        }
        finally
        {
            GetCaptureSlot().Value = null;
        }
    }

    private static AsyncLocal<TaskCompletionSource<string?>?> GetCaptureSlot()
    {
        var field = typeof(CopyLocationCaptureHandler).GetField("s_tcs", BindingFlags.NonPublic | BindingFlags.Static);
        field.Should().NotBeNull();
        return field!.GetValue(null).Should().BeOfType<AsyncLocal<TaskCompletionSource<string?>?>>().Subject;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(factory(request));
    }
}
