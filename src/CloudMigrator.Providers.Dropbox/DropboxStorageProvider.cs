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
    private readonly string _accessToken;
    private readonly DropboxStorageOptions _options;
    private readonly int _simpleUploadLimitBytes;
    private readonly int _uploadChunkSizeBytes;

    public string ProviderId => "dropbox";

    public DropboxStorageProvider(
        ILogger<DropboxStorageProvider> logger,
        string accessToken,
        DropboxStorageOptions? options = null,
        HttpClient? httpClient = null)
    {
        _logger = logger;
        _accessToken = accessToken;
        _options = options ?? new DropboxStorageOptions();
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _simpleUploadLimitBytes = Math.Max(1, _options.SimpleUploadLimitMb) * 1024 * 1024;
        _uploadChunkSizeBytes = Math.Max(1, _options.UploadChunkSizeMb) * 1024 * 1024;
    }

    public void Dispose()
    {
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
        var firstPage = await PostDropboxApiAsync<ListFolderRequest, ListFolderResponse>(
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

        var result = new List<StorageItem>();
        AddFileEntries(firstPage.Entries, result);

        var cursor = firstPage.Cursor;
        while (firstPage.HasMore)
        {
            firstPage = await PostDropboxApiAsync<ListFolderContinueRequest, ListFolderResponse>(
                "files/list_folder/continue",
                new ListFolderContinueRequest { Cursor = cursor },
                cancellationToken).ConfigureAwait(false);
            cursor = firstPage.Cursor;
            AddFileEntries(firstPage.Entries, result);
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
        var tempPath = Path.GetTempFileName();

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
            using var request = CreateApiRequest(
                "files/create_folder_v2",
                new CreateFolderRequest
                {
                    Path = currentPath,
                    Autorename = false,
                });

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                continue;

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                _logger.LogDebug("Dropbox フォルダは既に存在します: {Path}", currentPath);
                continue;
            }

            await ThrowDropboxErrorAsync(response, "files/create_folder_v2", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task DownloadToTempFileAsync(string sourcePath, string tempPath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/download");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { path = sourcePath }));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
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
        await using var fileStream = File.OpenRead(localPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/upload");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new
        {
            path = destinationPath,
            mode = "overwrite",
            autorename = false,
            mute = true,
            strict_conflict = false,
        }));
        request.Content = new StreamContent(fileStream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
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
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/upload_session/start");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new { close = false }));
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
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
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/upload_session/append_v2");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new
        {
            cursor = new { session_id = sessionId, offset },
            close = false,
        }));
        request.Content = new ByteArrayContent(chunk);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
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
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ContentBaseUrl}/files/upload_session/finish");
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

        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ThrowDropboxErrorAsync(response, "files/upload_session/finish", ct).ConfigureAwait(false);
    }

    private async Task<TResponse> PostDropboxApiAsync<TRequest, TResponse>(
        string endpoint,
        TRequest payload,
        CancellationToken ct)
        where TRequest : class
    {
        using var request = CreateApiRequest(endpoint, payload);
        using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
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

    private void EnsureAccessTokenConfigured()
    {
        if (string.IsNullOrWhiteSpace(_accessToken))
            throw new InvalidOperationException(
                "Dropbox アクセストークンが未設定です。MIGRATOR__DROPBOX__ACCESSTOKEN を設定してください。");
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
