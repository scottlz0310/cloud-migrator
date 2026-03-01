using System.Text.Json;

namespace CloudMigrator.Providers.Graph;

/// <summary>
/// 大容量ファイルのアップロードセッション URL を JSON ファイルへ永続化する（FR-05 セッション再開）。
/// キー: 転送先相対パス（relPath）、値: アップロードセッション URL。
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
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dict = await LoadAsync(ct).ConfigureAwait(false);
            return dict.GetValueOrDefault(key);
        }
        finally { _lock.Release(); }
    }

    /// <summary>セッション URL を保存する（原子的書き込み）。</summary>
    public async Task SetAsync(string key, string sessionUrl, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dict = await LoadAsync(ct).ConfigureAwait(false);
            dict[key] = sessionUrl;
            await WriteAtomicAsync(dict, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    /// <summary>セッション URL を削除する（完了または期限切れ時）。</summary>
    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dict = await LoadAsync(ct).ConfigureAwait(false);
            if (dict.Remove(key))
                await WriteAtomicAsync(dict, ct).ConfigureAwait(false);
        }
        finally { _lock.Release(); }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException) { return []; }
    }

    /// <summary>テンポラリファイル経由で原子的に上書きする（プロセス中断時の破損を防ぐ）。</summary>
    private async Task WriteAtomicAsync(Dictionary<string, string> dict, CancellationToken ct)
    {
        var tmpPath = _filePath + ".tmp";
        await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(dict, JsonOpts), ct)
            .ConfigureAwait(false);
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
