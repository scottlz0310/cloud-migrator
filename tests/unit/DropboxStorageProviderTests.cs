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

    [Fact]
    public async Task ListItemsAsync_ShouldReturnEmpty_WhenPathNotFound()
    {
        // 検証対象: ListItemsAsync  目的: 転送先フォルダーが未作成の場合は空リストを返し例外を投げないこと
        var handler = new StubHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""
                {"error_summary": "path/not_found/...", "error": {".tag": "path", "path": {".tag": "not_found"}}}
                """)
        });

        using var httpClient = new HttpClient(handler);
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            "token",
            new DropboxStorageOptions { RootPath = "DEV" },
            httpClient);

        var result = await provider.ListItemsAsync("dropbox/DEV");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureFolderAsync_ShouldThrow_WhenConflictIsNotFolder()
    {
        // 検証対象: EnsureFolderAsync  目的: フォルダ以外の conflict は例外として扱うこと
        var handler = new StubHandler();
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("""{ "error_summary": "path/conflict/file/..." }""")
        });

        using var httpClient = new HttpClient(handler);
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            "token",
            new DropboxStorageOptions(),
            httpClient);

        var act = async () => await provider.EnsureFolderAsync("/dest");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*files/create_folder_v2*");
    }

    [Fact]
    public async Task EnsureFolderAsync_ShouldRefreshTokenBeforeFirstRequest_WhenAccessTokenEmpty()
    {
        // 検証対象: SendWithRetryAsync 事前リフレッシュ  目的: アクセストークンが空かつリフレッシュ資格情報がある場合、最初の API 呼び出し前にトークンを取得すること
        var handler = new StubHandler();
        // 1 回目: トークンエンドポイント
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"refreshed-token","token_type":"bearer"}""")
        });
        // 2 回目: フォルダ作成成功
        handler.Enqueue(_ => JsonResponse("""{}"""));

        using var httpClient = new HttpClient(handler);
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            accessToken: string.Empty,
            options: new DropboxStorageOptions(),
            httpClient: httpClient,
            refreshToken: "refresh-tok",
            clientId: "app-key",
            clientSecret: "app-secret");

        await provider.EnsureFolderAsync("/sub");

        handler.CapturedRequests.Should().HaveCount(2);
        handler.CapturedRequests[0].Path.Should().Be("/oauth2/token");
        handler.CapturedRequests[1].Path.Should().Be("/2/files/create_folder_v2");
    }

    [Fact]
    public async Task EnsureFolderAsync_ShouldRefreshTokenAndRetry_OnUnauthorized()
    {
        // 検証対象: SendWithRetryAsync 401 自動リフレッシュ  目的: 401 を受け取ったときトークンを更新して同じリクエストを再試行すること
        var handler = new StubHandler();
        // 1 回目: 期限切れトークンで呼び出し → 401
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error_summary":"expired_access_token/..."}""")
        });
        // 2 回目: トークンリフレッシュ
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"access_token":"new-token","token_type":"bearer"}""")
        });
        // 3 回目: 更新済みトークンで再試行 → 成功
        handler.Enqueue(_ => JsonResponse("""{}"""));

        using var httpClient = new HttpClient(handler);
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            accessToken: "expired-token",
            options: new DropboxStorageOptions(),
            httpClient: httpClient,
            refreshToken: "refresh-tok",
            clientId: "app-key",
            clientSecret: "app-secret");

        await provider.EnsureFolderAsync("/sub");

        handler.CapturedRequests.Should().HaveCount(3);
        handler.CapturedRequests[0].Path.Should().Be("/2/files/create_folder_v2");
        handler.CapturedRequests[1].Path.Should().Be("/oauth2/token");
        handler.CapturedRequests[2].Path.Should().Be("/2/files/create_folder_v2");
    }

    [Fact]
    public async Task EnsureFolderAsync_ShouldWait30Seconds_WhenTooManyWriteOperations()
    {
        // 検証対象: GetRetryDelayAsync  目的: too_many_write_operations (429) 時は 30 秒待機することを確認（CancellationToken で短絡）
        var handler = new StubHandler();
        // 1 回目: 429 + too_many_write_operations（30 秒待機を誘発）
        handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error_summary":"too_many_write_operations/...","error":{".tag":"too_many_write_operations"}}""")
        });
        // 2 回目: 成功（30 秒後リトライ想定）
        handler.Enqueue(_ => JsonResponse("""{}"""));

        using var httpClient = new HttpClient(handler);
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            "valid-token",
            new DropboxStorageOptions(),
            httpClient,
            maxRetry: 1);

        // 30 秒待機中に 2 秒でキャンセル → 30s 待機が発動していなければ 449ms 以内に完了するはず
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var act = async () => await provider.EnsureFolderAsync("/sub", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── onRateLimit コールバック ─────────────────────────────────────────────

    [Fact]
    public async Task OnRateLimit_CalledOnce_When429WithDeltaRetryAfter()
    {
        // 検証対象: onRateLimit  目的: 429 (Retry-After: Delta) でコールバックが 1 回呼ばれ正しい値が渡ること
        var handler = new StubHandler();
        // 1 回目: 429 (Retry-After: 5秒)
        handler.Enqueue(_ =>
        {
            var res = new HttpResponseMessage((HttpStatusCode)429);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return res;
        });
        // 2 回目: 成功
        handler.Enqueue(_ => JsonResponse("""{"metadata":{"name":"a.txt",".tag":"file"}}"""));

        TimeSpan? capturedRetryAfter = TimeSpan.FromDays(1); // sentinel
        var callCount = 0;
        void OnRateLimit(TimeSpan? ra) { callCount++; capturedRetryAfter = ra; }

        using var httpClient = new HttpClient(handler);
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            "valid-token",
            new DropboxStorageOptions(),
            httpClient,
            maxRetry: 1,
            onRateLimit: OnRateLimit);

        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, "data");
            await provider.UploadFromLocalAsync(tmp, 4, "/a.txt", CancellationToken.None);
        }
        finally { File.Delete(tmp); }

        callCount.Should().Be(1);
        capturedRetryAfter.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OnRateLimit_CalledOnce_When429WithDateRetryAfter()
    {
        // 検証対象: onRateLimit  目的: 429 (Retry-After: Date) でコールバックが 1 回呼ばれ非負値が渡ること
        var handler = new StubHandler();
        // 1 回目: 429 (Retry-After: 3秒後の絶対日時)
        handler.Enqueue(_ =>
        {
            var res = new HttpResponseMessage((HttpStatusCode)429);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                DateTimeOffset.UtcNow.AddSeconds(3));
            return res;
        });
        // 2 回目: 成功
        handler.Enqueue(_ => JsonResponse("""{"metadata":{"name":"a.txt",".tag":"file"}}"""));

        TimeSpan? capturedRetryAfter = null;
        void OnRateLimit(TimeSpan? ra) => capturedRetryAfter = ra;

        using var httpClient = new HttpClient(handler);
        var provider = new DropboxStorageProvider(
            NullLogger<DropboxStorageProvider>.Instance,
            "valid-token",
            new DropboxStorageOptions(),
            httpClient,
            maxRetry: 1,
            onRateLimit: OnRateLimit);

        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, "data");
            await provider.UploadFromLocalAsync(tmp, 4, "/a.txt", CancellationToken.None);
        }
        finally { File.Delete(tmp); }

        capturedRetryAfter.Should().NotBeNull();
        capturedRetryAfter!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
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
