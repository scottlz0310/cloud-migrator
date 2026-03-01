using System.Text.Json;

namespace CloudMigrator.Core.Transfer;

/// <summary>
/// 大容量ファイルのアップロードセッション URL を JSON ファイルへ永続化する（FR-05 セッション再開）。
/// キー: <see cref="Providers.Abstractions.StorageItem.SkipKey"/>、値: アップロードセッション URL。
/// </summary>
public sealed class UploadSessionStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public UploadSessionStore(string filePath)
    {
        _filePath = filePath;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>保存済みのセッション URL を取得する。存在しない場合は null。</summary>
    public async Task<string?> GetAsync(string skipKey, CancellationToken ct = default)
    {
        var dict = await LoadAsync(ct).ConfigureAwait(false);
        return dict.GetValueOrDefault(skipKey);
    }

    /// <summary>セッション URL を保存する。</summary>
    public async Task SetAsync(string skipKey, string sessionUrl, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dict = await LoadAsync(ct).ConfigureAwait(false);
            dict[skipKey] = sessionUrl;
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(dict, JsonOpts), ct)
                .ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <summary>セッション URL を削除する（完了または期限切れ時）。</summary>
    public async Task RemoveAsync(string skipKey, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dict = await LoadAsync(ct).ConfigureAwait(false);
            if (dict.Remove(skipKey))
                await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(dict, JsonOpts), ct)
                    .ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
    }
}
