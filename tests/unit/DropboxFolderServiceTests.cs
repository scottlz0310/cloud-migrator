using System.Net;
using System.Text;
using CloudMigrator.Providers.Dropbox.Auth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: DropboxFolderService.ListFoldersAsync
/// 目的: ページネーション/JSON パース/失敗ステータス/例外の各ケースを網羅する
/// </summary>
public sealed class DropboxFolderServiceTests
{
    // Dropbox API の ".tag" フィールドは C# 匿名型で表現できないため生 JSON を使う

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static IHttpClientFactory BuildFactory(params HttpResponseMessage[] responses)
    {
        var handler = new SequentialHttpHandler(responses);
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        return factory.Object;
    }

    // ── 成功 1 ページ ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListFoldersAsync_SinglePage_ReturnsFoldersSortedByName()
    {
        // 検証対象: ListFoldersAsync  目的: 1 ページのレスポンスからフォルダのみを取得し名前順で返すこと
        const string json = """
            {
              "entries": [
                { ".tag": "folder", "name": "Zulu",    "path_display": "/Zulu" },
                { ".tag": "file",   "name": "file.txt","path_display": "/file.txt" },
                { ".tag": "folder", "name": "Alpha",   "path_display": "/Alpha" }
              ],
              "has_more": false
            }
            """;
        var sut = new DropboxFolderService(BuildFactory(JsonResponse(json)), NullLogger<DropboxFolderService>.Instance);

        var result = await sut.ListFoldersAsync("token", "");

        result.Success.Should().BeTrue();
        result.Folders.Should().HaveCount(2);
        result.Folders![0].Name.Should().Be("Alpha");
        result.Folders![1].Name.Should().Be("Zulu");
    }

    // ── 成功 複数ページ ───────────────────────────────────────────────────

    [Fact]
    public async Task ListFoldersAsync_MultiplePaged_ReturnsAllFoldersMerged()
    {
        // 検証対象: ListFoldersAsync  目的: has_more=true でページネーションを自動処理し全フォルダを返すこと
        const string page1 = """
            {
              "entries": [{ ".tag": "folder", "name": "Beta", "path_display": "/Beta" }],
              "has_more": true,
              "cursor": "cursor-abc"
            }
            """;
        const string page2 = """
            {
              "entries": [{ ".tag": "folder", "name": "Alpha", "path_display": "/Alpha" }],
              "has_more": false
            }
            """;
        var sut = new DropboxFolderService(
            BuildFactory(JsonResponse(page1), JsonResponse(page2)),
            NullLogger<DropboxFolderService>.Instance);

        var result = await sut.ListFoldersAsync("token", "");

        result.Success.Should().BeTrue();
        result.Folders.Should().HaveCount(2);
        result.Folders![0].Name.Should().Be("Alpha");
        result.Folders![1].Name.Should().Be("Beta");
    }

    // ── 失敗ステータス ────────────────────────────────────────────────────

    [Fact]
    public async Task ListFoldersAsync_WhenHttpError_ReturnsFailureWithStatusCode()
    {
        // 検証対象: ListFoldersAsync  目的: Dropbox API が非成功ステータスを返した場合に Success=false を返すこと
        var sut = new DropboxFolderService(
            BuildFactory(JsonResponse("""{"error":"path/not_found"}""", HttpStatusCode.Conflict)),
            NullLogger<DropboxFolderService>.Instance);

        var result = await sut.ListFoldersAsync("token", "/NonExistent");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("409");
    }

    [Fact]
    public async Task ListFoldersAsync_When409PathNotFound_SetsIsPathNotFound()
    {
        // 検証対象: ListFoldersAsync  目的: 409 かつ error_summary に path/not_found を含む場合に IsPathNotFound=true を返すこと
        var sut = new DropboxFolderService(
            BuildFactory(JsonResponse("""{"error_summary":"path/not_found/.","error":{".tag":"path","path":{".tag":"not_found"}}}""", HttpStatusCode.Conflict)),
            NullLogger<DropboxFolderService>.Instance);

        var result = await sut.ListFoldersAsync("token", "/NotExist");

        result.Success.Should().BeFalse();
        result.IsPathNotFound.Should().BeTrue();
    }

    [Fact]
    public async Task ListFoldersAsync_WhenNon409Error_DoesNotSetIsPathNotFound()
    {
        // 検証対象: ListFoldersAsync  目的: 認証エラー（401）など path/not_found 以外のエラーでは IsPathNotFound=false のまま返すこと
        var sut = new DropboxFolderService(
            BuildFactory(JsonResponse("""{"error":"invalid_access_token"}""", HttpStatusCode.Unauthorized)),
            NullLogger<DropboxFolderService>.Instance);

        var result = await sut.ListFoldersAsync("token", "/folder");

        result.Success.Should().BeFalse();
        result.IsPathNotFound.Should().BeFalse();
    }

    // ── 例外 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListFoldersAsync_WhenHttpThrows_ReturnsFailureWithMessage()
    {
        // 検証対象: ListFoldersAsync  目的: HTTP 通信で例外が発生した場合に Success=false を返すこと
        var handler = new ExceptionHttpHandler(new HttpRequestException("network error"));
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(client);
        var sut = new DropboxFolderService(factory.Object, NullLogger<DropboxFolderService>.Instance);

        var result = await sut.ListFoldersAsync("token", "");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("network error");
    }

    // ── ヘルパークラス ────────────────────────────────────────────────────

    private sealed class SequentialHttpHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_index >= responses.Length)
                throw new InvalidOperationException($"予期しない HTTP 呼び出し（index={_index}）");
            return Task.FromResult(responses[_index++]);
        }
    }

    private sealed class ExceptionHttpHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(ex);
    }
}
