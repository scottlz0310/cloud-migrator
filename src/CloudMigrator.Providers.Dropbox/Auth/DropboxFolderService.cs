using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Providers.Dropbox.Auth;

/// <summary>
/// Dropbox <c>files/list_folder</c> API を呼び出してフォルダ一覧を返す <see cref="IDropboxFolderService"/> 実装。
/// ページネーションを自動処理し、フォルダエントリのみ返す。
/// </summary>
public sealed class DropboxFolderService : IDropboxFolderService
{
    private const string ApiBaseUrl = "https://api.dropboxapi.com/2";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DropboxFolderService> _logger;

    public DropboxFolderService(IHttpClientFactory httpClientFactory, ILogger<DropboxFolderService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<DropboxFolderListResult> ListFoldersAsync(
        string accessToken,
        string folderPath,
        CancellationToken ct = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var folders = new List<DropboxFolderEntry>();

            var hasMore = true;
            string? cursor = null;

            while (hasMore)
            {
                HttpResponseMessage response;
                if (cursor is null)
                    response = await PostListFolderAsync(client, folderPath, ct).ConfigureAwait(false);
                else
                    response = await PostListFolderContinueAsync(client, cursor, ct).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogWarning(
                        "Dropbox files/list_folder 失敗 [{Path}]: HTTP {Status} — {Body}",
                        folderPath, (int)response.StatusCode, errorBody);
                    return new DropboxFolderListResult(false, ErrorMessage: $"HTTP {(int)response.StatusCode}");
                }

                var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("entries", out var entries))
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (!entry.TryGetProperty(".tag", out var tag) || tag.GetString() != "folder")
                            continue;

                        var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                        var pathDisplay = entry.TryGetProperty("path_display", out var pd)
                            ? pd.GetString() ?? string.Empty
                            : string.Empty;

                        folders.Add(new DropboxFolderEntry(name, pathDisplay));
                    }
                }

                hasMore = root.TryGetProperty("has_more", out var hm) && hm.GetBoolean();
                cursor = hasMore && root.TryGetProperty("cursor", out var c) ? c.GetString() : null;
            }

            folders.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new DropboxFolderListResult(true, folders);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Dropbox フォルダ一覧取得中にエラーが発生しました（path={Path}）。", folderPath);
            return new DropboxFolderListResult(false, ErrorMessage: ex.Message);
        }
    }

    private async Task<HttpResponseMessage> PostListFolderAsync(HttpClient client, string path, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new
        {
            path,
            recursive = false,
            include_deleted = false,
        });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync($"{ApiBaseUrl}/files/list_folder", content, ct).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> PostListFolderContinueAsync(HttpClient client, string cursor, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new { cursor });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.PostAsync($"{ApiBaseUrl}/files/list_folder/continue", content, ct).ConfigureAwait(false);
    }
}
