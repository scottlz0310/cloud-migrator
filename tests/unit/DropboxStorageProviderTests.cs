using System.Net;
using System.Net.Http.Headers;
using CloudMigrator.Providers.Dropbox;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// DropboxStorageProvider のユニットテスト（Phase 7）。
/// </summary>
public class DropboxStorageProviderTests
{
    [Fact]
    public void ProviderId_ShouldReturnDropbox()
    {
        // 検証対象: ProviderId  目的: プロバイダー識別子が "dropbox" であること
        using var httpClient = new HttpClient(new StubHandler());
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            "token",
            new DropboxStorageOptions(),
            httpClient);

        provider.ProviderId.Should().Be("dropbox");
    }

    [Fact]
    public async Task ListItemsAsync_ShouldReturnEmpty_WhenRootPathUnknown()
    {
        // 検証対象: ListItemsAsync  目的: 未対応 rootPath では空リストを返すこと
        var handler = new StubHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            string.Empty,
            new DropboxStorageOptions(),
            httpClient);

        var result = await provider.ListItemsAsync("unknown");

        result.Should().BeEmpty();
        handler.CapturedRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ListItemsAsync_ShouldThrow_WhenAccessTokenIsMissing()
    {
        // 検証対象: ListItemsAsync  目的: トークン未設定時は明示的に例外を返すこと
        using var httpClient = new HttpClient(new StubHandler());
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            string.Empty,
            new DropboxStorageOptions(),
            httpClient);

        var act = async () => await provider.ListItemsAsync("dropbox");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MIGRATOR__DROPBOX__ACCESSTOKEN*");
    }

    [Fact]
    public async Task ListItemsAsync_ShouldParseFilesAcrossPagination()
    {
        // 検証対象: ListItemsAsync  目的: ページングしつつファイルのみを返すこと
        var handler = new StubHandler();
        handler.Enqueue(_ => JsonResponse("""
            {
              "entries": [
                {".tag":"folder","id":"id:folder","name":"docs","path_display":"/docs","path_lower":"/docs"},
                {".tag":"file","id":"id:file1","name":"report1.txt","path_display":"/docs/report1.txt","path_lower":"/docs/report1.txt","server_modified":"2026-03-02T10:00:00Z","size":100}
              ],
              "cursor": "cursor-1",
              "has_more": true
            }
            """));
        handler.Enqueue(_ => JsonResponse("""
            {
              "entries": [
                {".tag":"file","id":"id:file2","name":"root.bin","path_display":"/root.bin","path_lower":"/root.bin","server_modified":"2026-03-02T10:01:00Z","size":200}
              ],
              "cursor": "cursor-2",
              "has_more": false
            }
            """));

        using var httpClient = new HttpClient(handler);
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            "token",
            new DropboxStorageOptions(),
            httpClient);

        var result = await provider.ListItemsAsync("dropbox");

        result.Should().HaveCount(2);
        result.Select(x => x.SkipKey).Should().BeEquivalentTo("docs/report1.txt", "root.bin");
        handler.CapturedRequests.Should().HaveCount(2);
        handler.CapturedRequests[0].Path.Should().EndWith("/2/files/list_folder");
        handler.CapturedRequests[1].Path.Should().EndWith("/2/files/list_folder/continue");
    }

    [Fact]
    public async Task EnsureFolderAsync_ShouldIgnoreConflict_WhenFolderAlreadyExists()
    {
        // 検証対象: EnsureFolderAsync  目的: 409 Conflict を既存フォルダとして扱い継続すること
        var handler = new StubHandler();
        handler.Enqueue(_ => JsonResponse("""{ "metadata": { "id": "id:dest" } }"""));
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{ "error_summary": "path/conflict/folder/..." }""")
        });

        using var httpClient = new HttpClient(handler);
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            "token",
            new DropboxStorageOptions(),
            httpClient);

        var act = async () => await provider.EnsureFolderAsync("/dest/sub");

        await act.Should().NotThrowAsync();
        handler.CapturedRequests.Should().HaveCount(2);
        handler.CapturedRequests.All(x => x.Path.EndsWith("/2/files/create_folder_v2"))
            .Should().BeTrue();
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<CapturedRequest> CapturedRequests { get; } = [];

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            => _responses.Enqueue(responseFactory);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            request.Headers.TryGetValues("Dropbox-API-Arg", out var values);
            CapturedRequests.Add(new CapturedRequest(path, values?.FirstOrDefault()));

            if (_responses.Count == 0)
                throw new InvalidOperationException("テストレスポンスが不足しています。");

            var response = _responses.Dequeue().Invoke(request);
            if (response.Content is not null &&
                response.Content.Headers.ContentType is null)
            {
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            return Task.FromResult(response);
        }
    }

    private sealed record CapturedRequest(string Path, string? DropboxApiArg);
}
