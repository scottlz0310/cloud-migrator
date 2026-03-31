using CloudMigrator.Providers.Abstractions;
using CloudMigrator.Providers.Graph;
using CloudMigrator.Providers.Graph.Http;
using FluentAssertions;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Drives.Item.Items.Item.Delta;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Abstractions.Serialization;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Moq;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// GraphStorageProvider のユニットテスト（Phase 2）
/// 検証対象: IStorageProvider 契約の基本動作
/// </summary>
public class GraphStorageProviderTests
{
    private readonly GraphServiceClient _graphClient;
    private readonly Mock<ILogger<GraphStorageProvider>> _mockLogger;

    public GraphStorageProviderTests()
    {
        // GraphServiceClient は IRequestAdapter を介して生成（直接モック不可）
        var mockAdapter = new Mock<IRequestAdapter>();
        mockAdapter.Setup(a => a.SerializationWriterFactory).Returns(new Mock<ISerializationWriterFactory>().Object);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");
        _graphClient = new GraphServiceClient(mockAdapter.Object);
        _mockLogger = new Mock<ILogger<GraphStorageProvider>>();
    }

    private GraphStorageProvider CreateProvider() =>
        new(_graphClient, _mockLogger.Object);

    [Fact]
    public void ProviderId_ShouldReturnGraph()
    {
        // 検証対象: ProviderId  目的: プロバイダー識別子が "graph" であること
        var provider = CreateProvider();
        provider.ProviderId.Should().Be("graph");
    }

    [Fact]
    public async Task ListItemsAsync_ShouldReturnEmpty_WhenRootPathUnknown()
    {
        // 検証対象: ListItemsAsync  目的: 不明な rootPath は空リストを返すこと
        var provider = CreateProvider();

        var result = await provider.ListItemsAsync("/root");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListItemsAsync_ShouldReturnEmpty_WhenOneDriveUserIdIsEmpty()
    {
        // 検証対象: ListItemsAsync("onedrive")  目的: OneDriveUserId 未設定時は空リストを返すこと
        var provider = CreateProvider(); // options 省略 → OneDriveUserId = ""

        var result = await provider.ListItemsAsync("onedrive");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListItemsAsync_ShouldReturnEmpty_WhenSharePointDriveIdIsEmpty()
    {
        // 検証対象: ListItemsAsync("sharepoint")  目的: SharePointDriveId 未設定時は空リストを返すこと
        var provider = CreateProvider(); // options 省略 → SharePointDriveId = ""

        var result = await provider.ListItemsAsync("sharepoint");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task UploadFileAsync_ShouldThrow_WhenSizeBytesIsNull()
    {
        // 検証対象: UploadFileAsync  目的: SizeBytes が未設定の場合に InvalidOperationException をスローすること
        var provider = CreateProvider();
        var job = new TransferJob
        {
            Source = new StorageItem { Id = "1", Name = "file.txt", Path = "docs", SizeBytes = null },
            DestinationRoot = "/dest"
        };

        var act = async () => await provider.UploadFileAsync(job);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SizeBytes*");
    }

    [Fact]
    public async Task UploadFileAsync_SmallFile_ShouldThrow_WhenOneDriveUserIdIsEmpty()
    {
        // 検証対象: UploadFileAsync（小ファイルパス）  目的: 必須設定が未設定の場合は例外をスローすること
        var provider = CreateProvider();
        var job = new TransferJob
        {
            Source = new StorageItem { Id = "2", Name = "small.txt", Path = "docs", SizeBytes = 1024 },
            DestinationRoot = "/dest"
        };

        var act = async () => await provider.UploadFileAsync(job);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未設定*");
    }

    [Fact]
    public async Task UploadFileAsync_LargeFile_ShouldThrow_WhenOneDriveUserIdIsEmpty()
    {
        // 検証対象: UploadFileAsync（大ファイルパス）  目的: 必須設定が未設定の場合は例外をスローすること
        var provider = CreateProvider();
        var largeFileSizeBytes = 5L * 1024 * 1024; // 5MB
        var job = new TransferJob
        {
            Source = new StorageItem { Id = "3", Name = "large.zip", Path = "docs", SizeBytes = largeFileSizeBytes },
            DestinationRoot = "/dest"
        };

        var act = async () => await provider.UploadFileAsync(job);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*未設定*");
    }

    [Fact]
    public async Task EnsureFolderAsync_ShouldCompleteWithoutException()
    {
        // 検証対象: EnsureFolderAsync  目的: Phase 3 実装前はスタブとして正常完了すること
        var provider = CreateProvider();

        var act = async () => await provider.EnsureFolderAsync("/dest/docs");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ServerSideCopyAsync_ShouldThrowNotSupported_WhenCaptureHandlerIsMissing()
    {
        var provider = CreateProvider();

        var act = async () => await provider.ServerSideCopyAsync("source-item", "", "file.txt");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*CopyLocationCaptureHandler*");
    }

    [Fact]
    public async Task ServerSideCopyAsync_ShouldThrowInvalidOperation_WhenSharePointDriveIdIsMissing()
    {
        var (_, client) = CreateServerSideCopyMockClient(new DriveItem { Id = "copied-item" });
        var provider = new GraphStorageProvider(
            client,
            _mockLogger.Object,
            new GraphStorageOptions { OneDriveUserId = "user1" },
            copyLocationCapture: new CopyLocationCaptureHandler());

        var act = async () => await provider.ServerSideCopyAsync("source-item", "", "file.txt");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*SharePointDriveId*");
    }

    [Fact]
    public async Task ServerSideCopyAsync_ShouldComplete_WhenCopyReturnsDriveItemImmediately()
    {
        var (mockAdapter, client) = CreateServerSideCopyMockClient(new DriveItem { Id = "copied-item" });
        var provider = new GraphStorageProvider(
            client,
            _mockLogger.Object,
            new GraphStorageOptions
            {
                OneDriveUserId = "user1",
                SharePointDriveId = "sharepoint-drive",
            },
            copyLocationCapture: new CopyLocationCaptureHandler());

        var act = async () => await provider.ServerSideCopyAsync("source-item", "", "file.txt");

        await act.Should().NotThrowAsync();
        mockAdapter.Verify(a => a.SendAsync(
                It.Is<RequestInformation>(r => r.HttpMethod == Method.POST),
                It.IsAny<ParsableFactory<DriveItem>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ServerSideCopyAsync_ShouldThrow_WhenMonitorUrlIsNotCaptured()
    {
        var (_, client) = CreateServerSideCopyMockClient(copyResponse: null);
        var provider = new GraphStorageProvider(
            client,
            _mockLogger.Object,
            new GraphStorageOptions
            {
                OneDriveUserId = "user1",
                SharePointDriveId = "sharepoint-drive",
            },
            copyLocationCapture: new CopyLocationCaptureHandler());

        var act = async () => await provider.ServerSideCopyAsync("source-item", "", "file.txt");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Monitor URL*");
    }

    [Fact]
    public async Task ServerSideCopyAsync_ShouldComplete_WhenMonitorReturnsCompletedStatus()
    {
        await using var monitorServer = new LoopbackMonitorServer(
            new MonitorResponse(HttpStatusCode.OK, """{"status":"completed"}"""));
        var (copyCapture, client) = CreateHttpServerSideCopyClient(monitorServer.Url);
        var provider = CreateServerSideCopyProvider(client, copyCapture, timeoutSec: 2);

        var act = async () => await provider.ServerSideCopyAsync("source-item", "", "file.txt");

        await act.Should().NotThrowAsync();
        monitorServer.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task ServerSideCopyAsync_ShouldThrow_WhenMonitorReturnsFailedStatus()
    {
        await using var monitorServer = new LoopbackMonitorServer(
            new MonitorResponse(HttpStatusCode.OK, """{"status":"failed","error":{"message":"boom"}}"""));
        var (copyCapture, client) = CreateHttpServerSideCopyClient(monitorServer.Url);
        var provider = CreateServerSideCopyProvider(client, copyCapture, timeoutSec: 2);

        var act = async () => await provider.ServerSideCopyAsync("source-item", "", "file.txt");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*boom*");
    }

    [Fact]
    public async Task ServerSideCopyAsync_ShouldContinueAfterInvalidMonitorJson_AndEventuallyComplete()
    {
        await using var monitorServer = new LoopbackMonitorServer(
            new MonitorResponse(HttpStatusCode.OK, "not-json"),
            new MonitorResponse(HttpStatusCode.OK, """{"status":"completed"}"""));
        var (copyCapture, client) = CreateHttpServerSideCopyClient(monitorServer.Url);
        var provider = CreateServerSideCopyProvider(client, copyCapture, timeoutSec: 2);

        var act = async () => await provider.ServerSideCopyAsync("source-item", "", "file.txt");

        await act.Should().NotThrowAsync();
        monitorServer.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task ServerSideCopyAsync_ShouldContinueAfterRetryAfter429_AndEventuallyComplete()
    {
        await using var monitorServer = new LoopbackMonitorServer(
            new MonitorResponse(HttpStatusCode.TooManyRequests, string.Empty, RetryAfterSeconds: 1),
            new MonitorResponse(HttpStatusCode.OK, """{"status":"completed"}"""));
        var (copyCapture, client) = CreateHttpServerSideCopyClient(monitorServer.Url);
        var provider = CreateServerSideCopyProvider(client, copyCapture, timeoutSec: 3);

        var act = async () => await provider.ServerSideCopyAsync("source-item", "", "file.txt");

        await act.Should().NotThrowAsync();
        monitorServer.RequestCount.Should().Be(2);
    }

    // ── Phase 3 クロール挙動テスト ─────────────────────────────────────

    private static (Mock<IRequestAdapter> adapter, GraphServiceClient client) CreateMockClient(
        DriveItemCollectionResponse itemsResponse)
    {
        var mockAdapter = new Mock<IRequestAdapter>();
        mockAdapter.Setup(a => a.SerializationWriterFactory)
            .Returns(new Mock<ISerializationWriterFactory>().Object);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");

        // ドライブ取得 (Users[userId].Drive.GetAsync → Drive 型)
        mockAdapter.Setup(a => a.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<Drive>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Drive { Id = "drive123" });

        // アイテム一覧取得: 1回目は指定応答、以降はnull（フォルダ再帰を抑制）
        mockAdapter.SetupSequence(a => a.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<DriveItemCollectionResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(itemsResponse)
            .ReturnsAsync((DriveItemCollectionResponse?)null)
            .ReturnsAsync((DriveItemCollectionResponse?)null)
            .ReturnsAsync((DriveItemCollectionResponse?)null);

        return (mockAdapter, new GraphServiceClient(mockAdapter.Object));
    }

    private static (Mock<IRequestAdapter> adapter, GraphServiceClient client) CreateServerSideCopyMockClient(
        DriveItem? copyResponse)
    {
        var mockWriter = new Mock<ISerializationWriter>();
        mockWriter.Setup(w => w.GetSerializedContent()).Returns(new MemoryStream());

        var mockWriterFactory = new Mock<ISerializationWriterFactory>();
        mockWriterFactory
            .Setup(f => f.GetSerializationWriter(It.IsAny<string>()))
            .Returns(mockWriter.Object);

        var mockAdapter = new Mock<IRequestAdapter>();
        mockAdapter.Setup(a => a.SerializationWriterFactory).Returns(mockWriterFactory.Object);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");
        mockAdapter.Setup(a => a.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<Drive>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Drive { Id = "source-drive-id" });
        mockAdapter.Setup(a => a.SendAsync(
                It.Is<RequestInformation>(r => r.HttpMethod == Method.POST),
                It.IsAny<ParsableFactory<DriveItem>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(copyResponse);

        return (mockAdapter, new GraphServiceClient(mockAdapter.Object));
    }

    private GraphStorageProvider CreateServerSideCopyProvider(
        GraphServiceClient client,
        CopyLocationCaptureHandler copyCapture,
        int timeoutSec)
        => new(
            client,
            _mockLogger.Object,
            new GraphStorageOptions
            {
                OneDriveUserId = "user1",
                SharePointDriveId = "sharepoint-drive",
            },
            copyLocationCapture: copyCapture,
            serverSideCopy: new CloudMigrator.Core.Configuration.ServerSideCopyOptions
            {
                TimeoutSec = timeoutSec,
                PollJitterMaxMs = 0,
                PollInitialDelayMs = 0,
                PollMaxDelayMs = 0,
            });

    private static (CopyLocationCaptureHandler capture, GraphServiceClient client) CreateHttpServerSideCopyClient(
        Uri? monitorUrl,
        DriveItem? immediateCopyResponse = null)
    {
        var copyCapture = new CopyLocationCaptureHandler();
        var transport = new StubGraphTransportHandler(request =>
        {
            if (request.Method == HttpMethod.Get
                && request.RequestUri?.AbsolutePath.EndsWith("/users/user1/drive", StringComparison.OrdinalIgnoreCase) == true)
            {
                return JsonResponse(HttpStatusCode.OK, """{"id":"source-drive-id"}""");
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath.EndsWith("/copy", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (immediateCopyResponse is not null)
                    return JsonResponse(HttpStatusCode.OK, """{"id":"copied-item"}""");

                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
                if (monitorUrl is not null)
                    accepted.Headers.Location = monitorUrl;
                return accepted;
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });
        copyCapture.InnerHandler = transport;

        var httpClient = new HttpClient(copyCapture);
        var authProvider = new BaseBearerTokenAuthenticationProvider(new FakeAccessTokenProvider());
        var adapter = new HttpClientRequestAdapter(authProvider, httpClient: httpClient)
        {
            BaseUrl = "https://graph.microsoft.com/v1.0",
        };

        return (copyCapture, new GraphServiceClient(adapter));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class FakeAccessTokenProvider : IAccessTokenProvider
    {
        public AllowedHostsValidator AllowedHostsValidator { get; } = new(["graph.microsoft.com"]);

        public Task<string> GetAuthorizationTokenAsync(
            Uri uri,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult("test-token");
    }

    private sealed class StubGraphTransportHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed record MonitorResponse(HttpStatusCode StatusCode, string Body, int? RetryAfterSeconds = null);

    private sealed class LoopbackMonitorServer : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        private readonly Queue<MonitorResponse> _responses;
        private readonly MonitorResponse _fallbackResponse;
        private int _requestCount;

        public LoopbackMonitorServer(params MonitorResponse[] responses)
        {
            _responses = new Queue<MonitorResponse>(responses);
            _fallbackResponse = responses.Length > 0
                ? responses[^1]
                : new MonitorResponse(HttpStatusCode.OK, """{"status":"completed"}""");
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            Url = new Uri($"http://127.0.0.1:{port}/monitor");
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public Uri Url { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
            }
        }

        private async Task HandleClientAsync(System.Net.Sockets.TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null || line.Length == 0)
                        break;
                }

                Interlocked.Increment(ref _requestCount);
                var response = _responses.Count > 0 ? _responses.Dequeue() : _fallbackResponse;
                var bodyBytes = System.Text.Encoding.UTF8.GetBytes(response.Body);
                var header = new System.Text.StringBuilder()
                    .Append($"HTTP/1.1 {(int)response.StatusCode} {GetReasonPhrase(response.StatusCode)}\r\n")
                    .Append("Connection: close\r\n")
                    .Append($"Content-Length: {bodyBytes.Length}\r\n");

                if (bodyBytes.Length > 0)
                    header.Append("Content-Type: application/json\r\n");
                if (response.RetryAfterSeconds is { } retryAfter)
                    header.Append($"Retry-After: {retryAfter}\r\n");

                header.Append("\r\n");

                var headerBytes = System.Text.Encoding.ASCII.GetBytes(header.ToString());
                await stream.WriteAsync(headerBytes, _cts.Token).ConfigureAwait(false);
                if (bodyBytes.Length > 0)
                    await stream.WriteAsync(bodyBytes, _cts.Token).ConfigureAwait(false);
            }
        }

        private static string GetReasonPhrase(HttpStatusCode statusCode)
            => statusCode switch
            {
                HttpStatusCode.OK => "OK",
                HttpStatusCode.Accepted => "Accepted",
                HttpStatusCode.TooManyRequests => "Too Many Requests",
                _ => statusCode.ToString(),
            };
    }

    [Fact]
    public async Task ListItemsAsync_ShouldReturnOnlyFiles_WhenFolderAndFilePresent()
    {
        // 検証対象: ListItemsAsync  目的: フォルダは result に含まれずファイルのみ返すこと
        var response = new DriveItemCollectionResponse
        {
            Value = [
                new() { Id = "folder1", Name = "Documents", Folder = new Folder() },
                new() { Id = "file1",   Name = "report.xlsx", Size = 1024 }
            ]
        };
        var (_, client) = CreateMockClient(response);
        var options = new GraphStorageOptions { OneDriveUserId = "user123" };
        var provider = new GraphStorageProvider(client, _mockLogger.Object, options);

        var result = await provider.ListItemsAsync("onedrive");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("report.xlsx");
        result[0].IsFolder.Should().BeFalse();
    }

    [Fact]
    public async Task ListItemsAsync_ShouldSkipItem_WhenNameIsNull()
    {
        // 検証対象: ListItemsAsync  目的: Name が null のアイテムはスキップされること
        var response = new DriveItemCollectionResponse
        {
            Value = [
                new() { Id = "bad",   Name = (string?)null, Size = 512 },
                new() { Id = "file2", Name = "valid.txt",   Size = 256 }
            ]
        };
        var (_, client) = CreateMockClient(response);
        var options = new GraphStorageOptions { OneDriveUserId = "user123" };
        var provider = new GraphStorageProvider(client, _mockLogger.Object, options);

        var result = await provider.ListItemsAsync("onedrive");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("valid.txt");
    }

    [Fact]
    public void DriveItemToStorageItem_ShouldSetSizeBytesToNull_ForFolderItems()
    {
        // DriveItemToStorageItem を直接呼び出し、フォルダアイテムの SizeBytes が null になることを検証
        var folderItem = new DriveItem
        {
            Id = "folder1",
            Name = "Archive",
            Folder = new Folder(),
            Size = 999, // フォルダでも Size が設定されている場合でも null になること
        };

        var result = GraphStorageProvider.DriveItemToStorageItem(folderItem, "/root");

        result.Should().NotBeNull();
        result!.SizeBytes.Should().BeNull("フォルダアイテムは意味のあるサイズを持たないため null であること");
        result.IsFolder.Should().BeTrue();
    }

    [Fact]
    public void DriveItemToStorageItem_ShouldSetSizeBytes_ForFileItems()
    {
        // ファイルアイテムでは Size がそのまま SizeBytes に設定されることを確認
        var fileItem = new DriveItem
        {
            Id = "file1",
            Name = "report.pdf",
            Size = 12345,
        };

        var result = GraphStorageProvider.DriveItemToStorageItem(fileItem, "/docs");

        result.Should().NotBeNull();
        result!.SizeBytes.Should().Be(12345);
        result.IsFolder.Should().BeFalse();
    }

    // ── OneDriveSourceFolder クロール挙動テスト ────────────────────────

    private static (Mock<IRequestAdapter> adapter, GraphServiceClient client) CreateMockClientWithFolderItem(
        DriveItemCollectionResponse itemsResponse,
        DriveItem? folderItem = null,
        ApiException? folderException = null)
    {
        var mockAdapter = new Mock<IRequestAdapter>();
        mockAdapter.Setup(a => a.SerializationWriterFactory)
            .Returns(new Mock<ISerializationWriterFactory>().Object);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");

        mockAdapter.Setup(a => a.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<Drive>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Drive { Id = "drive123" });

        var folderSetup = mockAdapter.Setup(a => a.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<DriveItem>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()));
        if (folderException is not null)
            folderSetup.ThrowsAsync(folderException);
        else
            folderSetup.ReturnsAsync(folderItem);

        mockAdapter.SetupSequence(a => a.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<DriveItemCollectionResponse>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(itemsResponse)
            .ReturnsAsync((DriveItemCollectionResponse?)null);

        return (mockAdapter, new GraphServiceClient(mockAdapter.Object));
    }

    [Fact]
    public async Task ListItemsAsync_ShouldUseDriveRoot_WhenSourceFolderIsSlashOnly()
    {
        // 検証対象: ListItemsAsync  目的: SourceFolder が "/" のみの場合はドライブルートからクロールすること
        var response = new DriveItemCollectionResponse
        {
            Value = [new() { Id = "file1", Name = "readme.txt", Size = 100 }]
        };
        var (_, client) = CreateMockClient(response);
        // "/" は Trim('/') で "" になるため DriveItem 取得をスキップしドライブ全体を対象とする
        var options = new GraphStorageOptions { OneDriveUserId = "user123", OneDriveSourceFolder = "/" };
        var provider = new GraphStorageProvider(client, _mockLogger.Object, options);

        var result = await provider.ListItemsAsync("onedrive");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("readme.txt");
    }

    [Fact]
    public async Task ListItemsAsync_ShouldThrowInvalidOperation_WhenSourceFolderNotFound()
    {
        // 検証対象: ListItemsAsync  目的: 存在しないフォルダ指定時に404 ApiException が InvalidOperationException に変換されること
        var response = new DriveItemCollectionResponse { Value = [] };
        var apiException = new ApiException("Not Found") { ResponseStatusCode = 404 };
        var (_, client) = CreateMockClientWithFolderItem(response, folderException: apiException);
        var options = new GraphStorageOptions { OneDriveUserId = "user123", OneDriveSourceFolder = "NonExistent/Folder" };
        var provider = new GraphStorageProvider(client, _mockLogger.Object, options);

        var act = async () => await provider.ListItemsAsync("onedrive");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*指定フォルダが見つかりません*NonExistent/Folder*");
    }

    [Fact]
    public async Task ListItemsAsync_ShouldReturnFilesFromSubfolder_WhenSourceFolderResolvesSuccessfully()
    {
        // 検証対象: ListItemsAsync  目的: 有効なフォルダパスが解決された場合にフォルダ配下のファイルを返すこと
        var response = new DriveItemCollectionResponse
        {
            Value = [new() { Id = "file99", Name = "archive.zip", Size = 5000 }]
        };
        var folderItem = new DriveItem { Id = "folder-item-id", Name = "Documents" };
        var (_, client) = CreateMockClientWithFolderItem(response, folderItem: folderItem);
        var options = new GraphStorageOptions { OneDriveUserId = "user123", OneDriveSourceFolder = "Documents" };
        var provider = new GraphStorageProvider(client, _mockLogger.Object, options);

        var result = await provider.ListItemsAsync("onedrive");

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("archive.zip");
    }

    // ── EnsureFolder GET first / POST / 409 フォールバック テスト ────────────────────

    private static (Mock<IRequestAdapter> adapter, GraphServiceClient client) CreateEnsureFolderMockAdapter()
    {
        // PostAsync が DriveItem ボディをシリアライズするため、ISerializationWriter を
        // 空ストリームを返す最小限のモックに仕立てる。
        var mockWriter = new Mock<ISerializationWriter>();
        mockWriter.Setup(w => w.GetSerializedContent()).Returns(new System.IO.MemoryStream());

        var mockWriterFactory = new Mock<ISerializationWriterFactory>();
        mockWriterFactory
            .Setup(f => f.GetSerializationWriter(It.IsAny<string>()))
            .Returns(mockWriter.Object);

        var mockAdapter = new Mock<IRequestAdapter>();
        mockAdapter.Setup(a => a.SerializationWriterFactory).Returns(mockWriterFactory.Object);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");
        return (mockAdapter, new GraphServiceClient(mockAdapter.Object));
    }

    [Fact]
    public async Task EnsureFolderAsync_ExistingFolder_UsesGetOnlyNeverPost()
    {
        // 検証対象: EnsureFolderSegmentAsync (GET first)  目的: 既存フォルダは GET 1 回で完了し POST を呼ばないこと
        var (mockAdapter, client) = CreateEnsureFolderMockAdapter();

        mockAdapter.Setup(a => a.SendAsync(
                It.Is<RequestInformation>(r => r.HttpMethod == Method.GET),
                It.IsAny<ParsableFactory<DriveItem>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveItem { Id = "existingFolderId" });

        var options = new GraphStorageOptions { SharePointDriveId = "drive1" };
        var provider = new GraphStorageProvider(client, Mock.Of<ILogger<GraphStorageProvider>>(), options);

        await provider.EnsureFolderAsync("docs");

        // POST は一切呼ばれていないこと
        mockAdapter.Verify(a => a.SendAsync(
                It.Is<RequestInformation>(r => r.HttpMethod == Method.POST),
                It.IsAny<ParsableFactory<DriveItem>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureFolderAsync_NotFound_CreatesViaPost()
    {
        // 検証対象: EnsureFolderSegmentAsync (GET 404 → POST)  目的: GET で 404 が返った場合のみ POST でフォルダを作成すること
        var (mockAdapter, client) = CreateEnsureFolderMockAdapter();

        mockAdapter.Setup(a => a.SendAsync(
                It.Is<RequestInformation>(r => r.HttpMethod == Method.GET),
                It.IsAny<ParsableFactory<DriveItem>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ApiException("Not Found") { ResponseStatusCode = 404 });

        mockAdapter.Setup(a => a.SendAsync(
                It.Is<RequestInformation>(r => r.HttpMethod == Method.POST),
                It.IsAny<ParsableFactory<DriveItem>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DriveItem { Id = "newFolderId" });

        var options = new GraphStorageOptions { SharePointDriveId = "drive1" };
        var provider = new GraphStorageProvider(client, Mock.Of<ILogger<GraphStorageProvider>>(), options);

        await provider.EnsureFolderAsync("docs");

        // POST が 1 回呼ばれていること
        mockAdapter.Verify(a => a.SendAsync(
                It.Is<RequestInformation>(r => r.HttpMethod == Method.POST),
                It.IsAny<ParsableFactory<DriveItem>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EnsureFolderAsync_PostConflict_FallbackGetCompletesSuccessfully()
    {
        // 検証対象: EnsureFolderSegmentAsync (POST 409 → fallback GET)
        // 目的: POST が 409 を返した場合 (並行作成競合) に再 GET して正常完了すること
        var (mockAdapter, client) = CreateEnsureFolderMockAdapter();

        // GET(1回目)→404、POST→409 Conflict、GET(2回目 fallback)→成功 の順に呼ばれる。
        // SetupSequence + Setup の組み合わせによるプラットフォーム差異を避けるため、
        // 単一 Setup でコール順を数値で制御する（スレッドセーフ）。
        var sendCallIndex = 0;
        mockAdapter.Setup(a => a.SendAsync(
                It.IsAny<RequestInformation>(),
                It.IsAny<ParsableFactory<DriveItem>>(),
                It.IsAny<Dictionary<string, ParsableFactory<IParsable>>?>(),
                It.IsAny<CancellationToken>()))
            .Returns<RequestInformation, ParsableFactory<DriveItem>,
                     Dictionary<string, ParsableFactory<IParsable>>?, CancellationToken>(
                (_, __, ___, ____) =>
                {
                    var idx = System.Threading.Interlocked.Increment(ref sendCallIndex);
                    return idx switch
                    {
                        // 呼び出し 1: GET (存在確認) → 404
                        1 => Task.FromException<DriveItem?>(
                                new ApiException("Not Found") { ResponseStatusCode = 404 }),
                        // 呼び出し 2: POST (作成) → 409 Conflict
                        2 => Task.FromException<DriveItem?>(
                                new ApiException("Conflict") { ResponseStatusCode = 409 }),
                        // 呼び出し 3: GET (fallback) → 既存フォルダ返却
                        _ => Task.FromResult<DriveItem?>(new DriveItem { Id = "conflictedFolderId" }),
                    };
                });

        var options = new GraphStorageOptions { SharePointDriveId = "drive1" };
        var provider = new GraphStorageProvider(client, Mock.Of<ILogger<GraphStorageProvider>>(), options);

        // 例外なく完了すること
        var act = async () => await provider.EnsureFolderAsync("docs");
        await act.Should().NotThrowAsync();
    }

    // ── BuildDeltaPage 直接テスト ──────────────────────────────────────

    /// <summary>BuildDeltaPage テスト用に最小限の GraphStorageProvider を生成する。</summary>
    private GraphStorageProvider CreateProviderForBuildDeltaPage(string? sourceFolderOption = null)
    {
        var mockAdapter = new Mock<IRequestAdapter>();
        mockAdapter.Setup(a => a.SerializationWriterFactory).Returns(new Mock<ISerializationWriterFactory>().Object);
        mockAdapter.SetupProperty(a => a.BaseUrl, "https://graph.microsoft.com/v1.0");
        var client = new GraphServiceClient(mockAdapter.Object);
        var options = new GraphStorageOptions
        {
            OneDriveUserId = "user1",
            OneDriveSourceFolder = sourceFolderOption ?? string.Empty,
        };
        return new GraphStorageProvider(client, Mock.Of<ILogger<GraphStorageProvider>>(), options);
    }

    [Fact]
    public void BuildDeltaPage_ExcludesDeletedItems()
    {
        // 検証対象: BuildDeltaPage  目的: Deleted ファセットが付いたアイテムは結果に含まれないこと
        const string driveId = "drive-abc";
        var provider = CreateProviderForBuildDeltaPage();

        var response = new DeltaGetResponse
        {
            Value =
            [
                new() { Id = "alive1", Name = "keep.txt",    Size = 100,
                    ParentReference = new() { Path = $"/drives/{driveId}/root:" } },
                new() { Id = "dead1",  Name = "deleted.txt", Size = 200,
                    Deleted = new(),
                    ParentReference = new() { Path = $"/drives/{driveId}/root:" } },
            ],
            OdataDeltaLink = "https://delta-link/",
        };

        var result = provider.BuildDeltaPage(response, driveId);

        result.Items.Should().HaveCount(1);
        result.Items[0].Name.Should().Be("keep.txt");
    }

    [Fact]
    public void BuildDeltaPage_WithNextLink_HasMoreTrueAndCursorIsNextLink()
    {
        // 検証対象: BuildDeltaPage  目的: OdataNextLink がある場合 HasMore=true、Cursor=nextLink であること
        const string driveId = "drive-abc";
        const string nextLink = "https://graph.microsoft.com/nextpage";
        var provider = CreateProviderForBuildDeltaPage();

        var response = new DeltaGetResponse
        {
            Value =
            [
                new() { Id = "f1", Name = "file.txt", Size = 10,
                    ParentReference = new() { Path = $"/drives/{driveId}/root:" } },
            ],
            OdataNextLink = nextLink,
        };

        var result = provider.BuildDeltaPage(response, driveId);

        result.HasMore.Should().BeTrue();
        result.Cursor.Should().Be(nextLink);
    }

    [Fact]
    public void BuildDeltaPage_WithDeltaLinkOnly_HasMoreFalseAndCursorIsDeltaLink()
    {
        // 検証対象: BuildDeltaPage  目的: OdataDeltaLink のみの場合 HasMore=false、Cursor=deltaLink であること
        const string driveId = "drive-abc";
        const string deltaLink = "https://graph.microsoft.com/delta";
        var provider = CreateProviderForBuildDeltaPage();

        var response = new DeltaGetResponse
        {
            Value =
            [
                new() { Id = "f1", Name = "file.txt", Size = 10,
                    ParentReference = new() { Path = $"/drives/{driveId}/root:" } },
            ],
            OdataDeltaLink = deltaLink,
        };

        var result = provider.BuildDeltaPage(response, driveId);

        result.HasMore.Should().BeFalse();
        result.Cursor.Should().Be(deltaLink);
    }

    [Theory]
    [InlineData("Documents/Projects", "/drives/drv1/root:/Documents/Projects", "")]
    [InlineData("Documents/Projects", "/drives/drv1/root:/Documents/Projects/Sub1", "Sub1")]
    [InlineData("Documents/Projects", "/drives/drv1/root:/Documents/Projects/A/B", "A/B")]
    [InlineData("", "/drives/drv1/root:/Documents/Projects/A/B", "Documents/Projects/A/B")]
    public void BuildDeltaPage_WithFolderPrefix_RelativizesParentPath(
        string sourceFolder, string rawParentPath, string expectedParentPath)
    {
        // 検証対象: BuildDeltaPage  目的: OneDriveSourceFolder 設定時に Delta API パスから
        //           フォルダプレフィックスが除去され既存 ListOneDriveItemsAsync と同じ相対パスになること
        const string driveId = "drv1";
        var provider = CreateProviderForBuildDeltaPage(sourceFolderOption: sourceFolder);
        var normalizedPrefix = string.IsNullOrEmpty(sourceFolder) ? string.Empty : sourceFolder.Trim('/');

        var response = new DeltaGetResponse
        {
            Value =
            [
                new() { Id = "f1", Name = "doc.docx", Size = 50,
                    ParentReference = new() { Path = rawParentPath } },
            ],
            OdataDeltaLink = "https://delta/",
        };

        var result = provider.BuildDeltaPage(response, driveId, normalizedPrefix);

        result.Items.Should().HaveCount(1);
        result.Items[0].Path.Should().Be(expectedParentPath);
    }

    [Fact]
    public void BuildDeltaPage_NullResponse_ReturnsEmptyPage()
    {
        // 検証対象: BuildDeltaPage  目的: response が null の場合は Items 空・HasMore=false を返すこと
        var provider = CreateProviderForBuildDeltaPage();

        var result = provider.BuildDeltaPage(null, "drv1");

        result.Items.Should().BeEmpty();
        result.HasMore.Should().BeFalse();
        result.Cursor.Should().BeNull();
    }
}
