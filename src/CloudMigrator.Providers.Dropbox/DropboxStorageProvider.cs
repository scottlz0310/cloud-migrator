using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudMigrator.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Providers.Dropbox;

/// <summary>
/// Dropbox API を使用した <see cref="IStorageProvider"/> 実装。
/// </summary>
public sealed class DropboxStorageProvider : IStorageProvider, IDisposable
{
    private const string ApiBaseUrl = "https://api.dropboxapi.com/2";
    private const string ContentBaseUrl = "https://content.dropboxapi.com/2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<DropboxStorageProvider> _logger;
    private string _accessToken;             // リフレッシュ時に更新するため非 readonly
    private readonly string? _refreshToken;
    private readonly string? _clientId;
    private readonly string? _clientSecret;
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);
    private readonly DropboxStorageOptions _options;
    private readonly int _maxRetry;
    private readonly int _simpleUploadLimitBytes;
    private readonly int _uploadChunkSizeBytes;

    public string ProviderId => "dropbox";

    /// <summary>
    /// Dropbox の <c>files/upload</c> はパスを指定するだけで親フォルダを自動作成するため、
    /// フォルダ先行作成フェーズはスキップ可能。
    /// </summary>
    public bool AutoCreateParentFolders => true;

    public DropboxStorageProvider(
        ILogger<DropboxStorageProvider> logger,
        string accessToken,
        DropboxStorageOptions? options = null,
        HttpClient? httpClient = null,
        int maxRetry = 3,
        bool disposeHttpClient = false,
        string? refreshToken = null,
        string? clientId = null,
        string? clientSecret = null)
    {
        _logger = logger;
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _options = options ?? new DropboxStorageOptions();
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null || disposeHttpClient;
        _maxRetry = Math.Max(0, maxRetry);
        _simpleUploadLimitBytes = Math.Max(1, _options.SimpleUploadLimitMb) * 1024 * 1024;
        _uploadChunkSizeBytes = Math.Max(1, _options.UploadChunkSizeMb) * 1024 * 1024;
    }

    public void Dispose()
    {
        _tokenRefreshLock.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StorageItem>> ListItemsAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (!rootPath.StartsWith("dropbox", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("不明な rootPath です: {RootPath}", rootPath);
            return [];
        }

        EnsureAccessTokenConfigured();

        var crawlPath = ResolveCrawlPath(rootPath);
        ListFolderResponse firstPage;
        try
        {
            firstPage = await PostDropboxApiAsync<ListFolderRequest, ListFolderResponse>(
                "files/list_folder",
                new ListFolderRequest
                {
                    Path = crawlPath,
                    Recursive = true,
                    IncludeDeleted = false,
                    IncludeHasExplicitSharedMembers = false,
                    IncludeMountedFolders = true,
                    IncludeNonDownloadableFiles = false,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains("path/not_found", StringComparison.OrdinalIgnoreCase))
        {
            // 転送先フォルダーがまだ存在しない場合は転送済み0件として扱う
            _logger.LogInformation(
                "Dropbox パスが存在しません。転送済みファイルなし (0 件) として処理します: {Path}", crawlPath);
            return [];
        }

        var result = new List<StorageItem>();
        AddFileEntries(firstPage.Entries, result);

        var cursor = firstPage.Cursor;
        var lastLoggedMilestone = 0;
        while (firstPage.HasMore)
        {
            firstPage = await PostDropboxApiAsync<ListFolderContinueRequest, ListFolderResponse>(
                "files/list_folder/continue",
                new ListFolderContinueRequest { Cursor = cursor },
                cancellationToken).ConfigureAwait(false);
            cursor = firstPage.Cursor;
            AddFileEntries(firstPage.Entries, result);
            var milestone = result.Count / 1000;
            if (milestone > lastLoggedMilestone)
            {
                _logger.LogInformation("Dropbox クロール進捗: {Count} 件取得中...", result.Count);
                lastLoggedMilestone = milestone;
            }
        }

        _logger.LogInformation("Dropbox クロール完了: {Count} 件 (Path={Path})", result.Count, crawlPath);
        return result;
    }

    /// <inheritdoc/>
    public async Task UploadFileAsync(TransferJob job, CancellationToken cancellationToken = default)
    {
        EnsureAccessTokenConfigured();

        if (job.Source.SizeBytes is null)
            throw new InvalidOperationException($"SizeBytes が未設定のため転送できません: {job.Source.SkipKey}");

        var sourcePath = BuildSourceFilePath(job.Source);
        var destinationPath = NormalizeFilePath(job.DestinationFullPath);
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        try
        {
            await DownloadToTempFileAsync(sourcePath, tempPath, cancellationToken).ConfigureAwait(false);

            if (job.Source.SizeBytes.Value <= _simpleUploadLimitBytes)
                await UploadSimpleAsync(destinationPath, tempPath, cancellationToken).ConfigureAwait(false);
            else
                await UploadChunkedAsync(destinationPath, tempPath, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Dropbox 転送完了: {SourcePath} -> {DestinationPath}",
                sourcePath,
                destinationPath);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "テンポラリファイルの削除に失敗: {Path}", tempPath);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<string> DownloadToTempAsync(StorageItem item, CancellationToken cancellationToken = default)
    {
        EnsureAccessTokenConfigured();

        var sourcePath = BuildSourceFilePath(item);
        var tempPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await DownloadToTempFileAsync(sourcePath, tempPath, cancellationToken).ConfigureAwait(false);
            return tempPath;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ベストエフォート */ }
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UploadFromLocalAsync(string localFilePath, long fileSizeBytes, string destinationFullPath, CancellationToken cancellationToken = default)
    {
        EnsureAccessTokenConfigured();

        var destinationPath = NormalizeFilePath(destinationFullPath);
        _logger.LogDebug("Dropbox アップロード開始: {DestPath} ({Bytes} bytes)", destinationPath, fileSizeBytes);

        if (fileSizeBytes <= _simpleUploadLimitBytes)
            await UploadSimpleAsync(destinationPath, localFilePath, cancellationToken).ConfigureAwait(false);
        else
            await UploadChunkedAsync(destinationPath, localFilePath, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Dropbox アップロード完了: {DestPath}", destinationPath);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Dropbox のネイティブページング API（<c>files/list_folder</c> / <c>files/list_folder/continue</c>）を使用する。
    /// <paramref name="cursor"/> が null の場合は先頭ページを取得し、以後は続きを取得する。
    /// </remarks>
    public async Task<StoragePage> ListPagedAsync(
        string rootPath,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        EnsureAccessTokenConfigured();

        ListFolderResponse response;
        if (cursor is null)
        {
            var crawlPath = ResolveCrawlPath(rootPath);
            try
            {
                response = await PostDropboxApiAsync<ListFolderRequest, ListFolderResponse>(
                    "files/list_folder",
                    new ListFolderRequest
                    {
                        Path = crawlPath,
                        Recursive = true,
                        IncludeDeleted = false,
                        IncludeHasExplicitSharedMembers = false,
                        IncludeMountedFolders = true,
                        IncludeNonDownloadableFiles = false,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
                when (ex.Message.Contains("path/not_found", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Dropbox パスが存在しません。空ページとして返します: {RootPath}", rootPath);
                return new StoragePage { Items = [], Cursor = null, HasMore = false };
            }
        }
        else
        {
            response = await PostDropboxApiAsync<ListFolderContinueRequest, ListFolderResponse>(
                "files/list_folder/continue",
                new ListFolderContinueRequest { Cursor = cursor },
                cancellationToken).ConfigureAwait(false);
        }

        var items = new List<StorageItem>();
        AddFileEntries(response.Entries, items);

        return new StoragePage
        {
            Items   = items,
            Cursor  = response.Cursor,
            HasMore = response.HasMore,
        };
    }

    /// <inheritdoc/>
    public async Task EnsureFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        EnsureAccessTokenConfigured();

        var normalized = NormalizeFolderPath(folderPath);
        if (string.IsNullOrEmpty(normalized))
            return;

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = string.Empty;

        foreach (var segment in segments)
        {
            currentPath = $"{currentPath}/{segment}";
            using var response = await SendWithRetryAsync(
                () => CreateApiRequest(
                    "files/create_folder_v2",
                    new CreateFolderRequest
                    {
                        Path = currentPath,
                        Autorename = false,
                    }),
                "files/create_folder_v2",
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                continue;

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var conflictBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (IsFolderAlreadyExistsConflict(conflictBody))
                {
                    _logger.LogDebug("Dropbox フォルダは既に存在します: {Path}", currentPath);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Dropbox API 呼び出しに失敗しました: files/create_folder_v2 ({(int)response.StatusCode}) {conflictBody}");
            }

            await ThrowDropboxErrorAsync(response, "files/create_folder_v2", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task DownloadToTempFileAsync(string sourcePath, string tempPath, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/download");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = sourcePath }));
                return request;
            },
            "files/download",
            ct,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowDropboxErrorAsync(response, "files/download", ct).ConfigureAwait(false);

        await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var tempStream = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await responseStream.CopyToAsync(tempStream, ct).ConfigureAwait(false);
    }

    private async Task UploadSimpleAsync(string destinationPath, string localPath, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/upload");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new
                {
                    path = destinationPath,
                    mode = "overwrite",
                    autorename = false,
                    mute = true,
                    strict_conflict = false,
                }));
                request.Content = new StreamContent(File.OpenRead(localPath));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return request;
            },
            "files/upload",
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowDropboxErrorAsync(response, "files/upload", ct).ConfigureAwait(false);
    }

    private async Task UploadChunkedAsync(string destinationPath, string localPath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(localPath);
        var sessionId = await StartUploadSessionAsync(ct).ConfigureAwait(false);
        long offset = 0;

        while (true)
        {
            var chunk = await ReadChunkAsync(stream, _uploadChunkSizeBytes, ct).ConfigureAwait(false);
            var isLast = stream.Position == stream.Length;

            if (isLast)
            {
                await FinishUploadSessionAsync(sessionId, offset, destinationPath, chunk, ct)
                    .ConfigureAwait(false);
                break;
            }

            await AppendUploadSessionAsync(sessionId, offset, chunk, ct).ConfigureAwait(false);
            offset += chunk.Length;
        }
    }

    private async Task<string> StartUploadSessionAsync(CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/upload_session/start");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { close = false }));
                request.Content = new ByteArrayContent([]);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return request;
            },
            "files/upload_session/start",
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowDropboxErrorAsync(response, "files/upload_session/start", ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<UploadSessionStartResponse>(stream, JsonOptions, ct)
            .ConfigureAwait(false);
        return payload?.SessionId
            ?? throw new InvalidOperationException("Dropbox upload session の session_id が取得できませんでした。");
    }

    private async Task AppendUploadSessionAsync(string sessionId, long offset, byte[] chunk, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/upload_session/append_v2");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new
                {
                    cursor = new { session_id = sessionId, offset },
                    close = false,
                }));
                request.Content = new ByteArrayContent(chunk);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return request;
            },
            "files/upload_session/append_v2",
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowDropboxErrorAsync(response, "files/upload_session/append_v2", ct).ConfigureAwait(false);
    }

    private async Task FinishUploadSessionAsync(
        string sessionId,
        long offset,
        string destinationPath,
        byte[] chunk,
        CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/upload_session/finish");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new
                {
                    cursor = new { session_id = sessionId, offset },
                    commit = new
                    {
                        path = destinationPath,
                        mode = "overwrite",
                        autorename = false,
                        mute = true,
                        strict_conflict = false,
                    },
                }));
                request.Content = new ByteArrayContent(chunk);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                return request;
            },
            "files/upload_session/finish",
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowDropboxErrorAsync(response, "files/upload_session/finish", ct).ConfigureAwait(false);
    }

    private async Task<TResponse> PostDropboxApiAsync<TRequest, TResponse>(
        string endpoint,
        TRequest payload,
        CancellationToken ct)
        where TRequest : class
    {
        using var response = await SendWithRetryAsync(
            () => CreateApiRequest(endpoint, payload),
            endpoint,
            ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowDropboxErrorAsync(response, endpoint, ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var parsed = await JsonSerializer.DeserializeAsync<TResponse>(stream, JsonOptions, ct).ConfigureAwait(false);
        return parsed
            ?? throw new InvalidOperationException($"Dropbox API 応答のデシリアライズに失敗しました: {endpoint}");
    }

    private HttpRequestMessage CreateApiRequest<TPayload>(string endpoint, TPayload payload)
        where TPayload : class
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBaseUrl}/{endpoint}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task ThrowDropboxErrorAsync(
        HttpResponseMessage response,
        string endpoint,
        CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"Dropbox API 呼び出しに失敗しました: {endpoint} ({(int)response.StatusCode}) {body}");
    }

    private static bool IsFolderAlreadyExistsConflict(string responseBody)
        => responseBody.Contains("path/conflict/folder", StringComparison.OrdinalIgnoreCase);

    private sealed class DropboxTokenResponse
    {
        [JsonPropertyName("access_token")]
        public required string AccessToken { get; init; }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string operation,
        CancellationToken ct,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        // アクセストークンが空（空白含む）かつリフレッシュ可能な場合は事前に取得する
        if (string.IsNullOrWhiteSpace(_accessToken) && HasRefreshCapability())
        {
            _logger.LogInformation("Dropbox アクセストークンが未設定のため事前リフレッシュします: {Operation}", operation);
            await RefreshAccessTokenAsync(ct).ConfigureAwait(false);
        }

        var tokenRefreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = requestFactory();
                var response = await _httpClient.SendAsync(request, completionOption, ct).ConfigureAwait(false);

                // 401: トークン期限切れ → リフレッシュして即リトライ（attempt カウントをリセット）
                if (response.StatusCode == HttpStatusCode.Unauthorized && HasRefreshCapability() && !tokenRefreshed)
                {
                    response.Dispose();
                    _logger.LogWarning("Dropbox 401 を検出。アクセストークンを更新して再試行します: {Operation}", operation);
                    await RefreshAccessTokenAsync(ct).ConfigureAwait(false);
                    tokenRefreshed = true;
                    attempt = -1; // 次の iteration で 0 になる
                    continue;
                }

                if (!ShouldRetry(response.StatusCode) || attempt >= _maxRetry)
                    return response;

                var delay = await GetRetryDelayAsync(response, attempt, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "Dropbox API の一時エラーのため再試行します: {Operation} status={StatusCode} attempt={Attempt} delayMs={DelayMs}",
                    operation,
                    (int)response.StatusCode,
                    attempt + 1,
                    (int)delay.TotalMilliseconds);
                response.Dispose();
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex) when (attempt < _maxRetry)
            {
                _logger.LogWarning(
                    ex,
                    "Dropbox API の通信エラーのため再試行します: {Operation} attempt={Attempt}",
                    operation,
                    attempt + 1);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested && attempt < _maxRetry)
            {
                _logger.LogWarning(
                    ex,
                    "Dropbox API のタイムアウトのため再試行します: {Operation} attempt={Attempt}",
                    operation,
                    attempt + 1);
            }

            await Task.Delay(ComputeRetryDelay(attempt), ct).ConfigureAwait(false);
        }
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout
            || (int)statusCode == 429
            || statusCode == HttpStatusCode.InternalServerError
            || statusCode == HttpStatusCode.BadGateway
            || statusCode == HttpStatusCode.ServiceUnavailable
            || statusCode == HttpStatusCode.GatewayTimeout;

    private static TimeSpan ComputeRetryDelay(int attempt)
    {
        var backoffMs = Math.Min(5000, 200 * (1 << Math.Min(attempt, 5)));
        var jitterMs = Random.Shared.Next(0, 250);
        return TimeSpan.FromMilliseconds(backoffMs + jitterMs);
    }

    private static async ValueTask<TimeSpan> GetRetryDelayAsync(
        HttpResponseMessage response, int attempt, CancellationToken ct)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            // Retry-After が短い（≤10s）場合でも too_many_write_operations なら 30s 待機する
            if (delta <= TimeSpan.FromSeconds(10))
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (body.Contains("too_many_write_operations", StringComparison.OrdinalIgnoreCase))
                    return TimeSpan.FromSeconds(30);
            }
            return delta;
        }

        if (retryAfter?.Date is DateTimeOffset date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                return wait;
        }

        // Retry-After ヘッダーなし & 429 の場合もボディで too_many_write_operations を確認
        if ((int)response.StatusCode == 429)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Contains("too_many_write_operations", StringComparison.OrdinalIgnoreCase))
                return TimeSpan.FromSeconds(30);
        }

        return ComputeRetryDelay(attempt);
    }

    private void AddFileEntries(IEnumerable<DropboxEntry>? entries, List<StorageItem> result)
    {
        if (entries is null)
            return;

        foreach (var entry in entries)
        {
            if (!string.Equals(entry.Tag, "file", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            var normalizedFullPath = NormalizeFolderPath(entry.PathDisplay ?? entry.PathLower ?? string.Empty);
            var parent = string.Empty;
            if (!string.IsNullOrEmpty(normalizedFullPath))
            {
                var index = normalizedFullPath.LastIndexOf('/');
                if (index > 0)
                    parent = normalizedFullPath[..index];
            }

            result.Add(new StorageItem
            {
                Id = string.IsNullOrWhiteSpace(entry.Id)
                    ? $"{parent}/{entry.Name}".TrimStart('/')
                    : entry.Id,
                Name = entry.Name,
                Path = parent,
                SizeBytes = entry.Size,
                LastModifiedUtc = entry.ServerModified,
                IsFolder = false,
            });
        }
    }

    private string ResolveCrawlPath(string rootPath)
    {
        var normalized = rootPath.Trim().Replace('\\', '/');
        var suffix = normalized.Length > "dropbox".Length
            ? normalized["dropbox".Length..].Trim('/')
            : string.Empty;
        if (!string.IsNullOrEmpty(suffix))
            return $"/{suffix}";

        return string.IsNullOrWhiteSpace(_options.RootPath)
            ? string.Empty
            : $"/{NormalizeFolderPath(_options.RootPath)}";
    }

    private static string BuildSourceFilePath(StorageItem source)
    {
        var normalizedPath = NormalizeFolderPath(source.Path);
        var normalizedName = source.Name.Trim().Trim('/').Replace('\\', '/');
        if (string.IsNullOrEmpty(normalizedName))
            throw new InvalidOperationException("Dropbox ソースファイル名が空です。");

        return string.IsNullOrEmpty(normalizedPath)
            ? $"/{normalizedName}"
            : $"/{normalizedPath}/{normalizedName}";
    }

    private static string NormalizeFilePath(string path)
    {
        var normalized = NormalizeFolderPath(path);
        return string.IsNullOrEmpty(normalized) ? "/" : $"/{normalized}";
    }

    private static string NormalizeFolderPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Trim().Replace('\\', '/').Trim('/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        return normalized;
    }

    private static async Task<byte[]> ReadChunkAsync(Stream stream, int chunkSize, CancellationToken ct)
    {
        var buffer = new byte[chunkSize];
        var totalRead = 0;

        while (totalRead < chunkSize)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, chunkSize - totalRead),
                ct).ConfigureAwait(false);
            if (read == 0)
                break;

            totalRead += read;
        }

        return totalRead == buffer.Length ? buffer : buffer[..totalRead];
    }

    private bool HasRefreshCapability()
        => !string.IsNullOrWhiteSpace(_refreshToken)
            && !string.IsNullOrWhiteSpace(_clientId)
            && !string.IsNullOrWhiteSpace(_clientSecret);

    private void EnsureAccessTokenConfigured()
    {
        if (string.IsNullOrWhiteSpace(_accessToken) && !HasRefreshCapability())
            throw new InvalidOperationException(
                "Dropbox の認証情報が未設定です。" +
                "MIGRATOR__DROPBOX__ACCESSTOKEN、または " +
                "MIGRATOR__DROPBOX__REFRESHTOKEN + MIGRATOR__DROPBOX__CLIENTID + MIGRATOR__DROPBOX__CLIENTSECRET を設定してください。");
    }

    /// <summary>
    /// リフレッシュトークンを使って新しいアクセストークンを取得する。
    /// SemaphoreSlim で同時リフレッシュを 1 つに絞る（複数ワーカーが同時に 401 を受けても 1 回だけ更新）。
    /// </summary>
    private async Task RefreshAccessTokenAsync(CancellationToken ct)
    {
        await _tokenRefreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Dropbox アクセストークンを更新しています...");
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken!,
                ["client_id"] = _clientId!,
                ["client_secret"] = _clientSecret!,
            });
            using var response = await _httpClient
                .PostAsync("https://api.dropboxapi.com/oauth2/token", content, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Dropbox トークンリフレッシュに失敗しました: ({(int)response.StatusCode}) {errBody}");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var result = await JsonSerializer.DeserializeAsync<DropboxTokenResponse>(stream, JsonOptions, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("トークン応答のデシリアライズに失敗しました");
            _accessToken = result.AccessToken;
            _logger.LogInformation("Dropbox アクセストークンを更新しました");
        }
        finally
        {
            _tokenRefreshLock.Release();
        }
    }

    private sealed class ListFolderRequest
    {
        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [JsonPropertyName("recursive")]
        public bool Recursive { get; init; }

        [JsonPropertyName("include_deleted")]
        public bool IncludeDeleted { get; init; }

        [JsonPropertyName("include_has_explicit_shared_members")]
        public bool IncludeHasExplicitSharedMembers { get; init; }

        [JsonPropertyName("include_mounted_folders")]
        public bool IncludeMountedFolders { get; init; }

        [JsonPropertyName("include_non_downloadable_files")]
        public bool IncludeNonDownloadableFiles { get; init; }
    }

    private sealed class ListFolderContinueRequest
    {
        [JsonPropertyName("cursor")]
        public required string Cursor { get; init; }
    }

    private sealed class CreateFolderRequest
    {
        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [JsonPropertyName("autorename")]
        public bool Autorename { get; init; }
    }

    private sealed class ListFolderResponse
    {
        [JsonPropertyName("entries")]
        public List<DropboxEntry> Entries { get; init; } = [];

        [JsonPropertyName("cursor")]
        public string Cursor { get; init; } = string.Empty;

        [JsonPropertyName("has_more")]
        public bool HasMore { get; init; }
    }

    private sealed class DropboxEntry
    {
        [JsonPropertyName(".tag")]
        public string Tag { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("path_display")]
        public string PathDisplay { get; init; } = string.Empty;

        [JsonPropertyName("path_lower")]
        public string PathLower { get; init; } = string.Empty;

        [JsonPropertyName("server_modified")]
        public DateTimeOffset? ServerModified { get; init; }

        [JsonPropertyName("size")]
        public long? Size { get; init; }
    }

    private sealed class UploadSessionStartResponse
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; init; } = string.Empty;
    }
}
