using System.Text.Json;
using CloudMigrator.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Storage;

/// <summary>
/// クロール結果を JSON ファイルへキャッシュする（FR-09）。
/// </summary>
public sealed class CrawlCache
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger<CrawlCache> _logger;

    public CrawlCache(ILogger<CrawlCache> logger) => _logger = logger;

    /// <summary>キャッシュファイルを読み込む。ファイルが存在しない場合は空リストを返す。</summary>
    public async Task<IReadOnlyList<StorageItem>> LoadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogDebug("キャッシュファイルが存在しません: {FilePath}", filePath);
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            var items = await JsonSerializer
                .DeserializeAsync<List<StorageItem>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation("キャッシュ読み込み完了: {Count} 件 {FilePath}", items?.Count ?? 0, filePath);
            return items ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "キャッシュ読み込みに失敗しました: {FilePath}", filePath);
            return [];
        }
    }

    /// <summary>クロール結果を JSON ファイルへ保存する。</summary>
    public async Task SaveAsync(
        string filePath,
        IReadOnlyList<StorageItem> items,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var stream = new FileStream(
            filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("キャッシュ保存完了: {Count} 件 → {FilePath}", items.Count, filePath);
    }
}
