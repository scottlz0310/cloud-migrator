using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Storage;

/// <summary>
/// スキップリスト（skip_list.json）の読み書きと排他制御（FR-07/FR-08）。
/// 判定キー: StorageItem.SkipKey（path + name の組み合わせ）。
/// </summary>
public sealed class SkipListManager
{
    private const int WriteRetryCount = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _filePath;
    private readonly ILogger<SkipListManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SkipListManager(string filePath, ILogger<SkipListManager> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    /// <summary>スキップリストをすべて読み込む。ファイル未存在の場合は空セットを返す。</summary>
    public async Task<HashSet<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < WriteRetryCount; i++)
        {
            try
            {
                var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
                var deserialized = JsonSerializer.Deserialize<HashSet<string>>(json, JsonOptions);
                return deserialized is null
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(deserialized, StringComparer.OrdinalIgnoreCase);
            }
            catch (IOException) when (i < WriteRetryCount - 1)
            {
                // 書き込み中の一時的な共有違反 → リトライ
                await Task.Delay(50 * (i + 1), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (i >= WriteRetryCount - 1)
            {
                _logger.LogError(ex, "スキップリスト読み込みに失敗しました（リトライ上限到達）: {Path}", _filePath);
                throw;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "スキップリスト JSON が破損しています: {Path}", _filePath);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>指定キーがスキップリストに存在するか確認する。</summary>
    public async Task<bool> ContainsAsync(string skipKey, CancellationToken cancellationToken = default)
    {
        var keys = await LoadAsync(cancellationToken).ConfigureAwait(false);
        return keys.Contains(skipKey);
    }

    /// <summary>
    /// 転送成功後にスキップキーを原子的に追加する（FR-08）。
    /// 単一の FileStream (FileShare.None) でプロセス間の read-modify-write を排他期間内に収める。
    /// </summary>
    public async Task AddAsync(string skipKey, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            for (int i = 0; i < WriteRetryCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // FileShare.None で他プロセスをブロックしたまま読み込みと書き込みを行う
                    await using var stream = new FileStream(
                        _filePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 4096,
                        useAsync: true);

                    HashSet<string> keys;
                    if (stream.Length == 0)
                    {
                        keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    else
                    {
                        stream.Position = 0;
                        try
                        {
                            var existing = await JsonSerializer.DeserializeAsync<HashSet<string>>(
                                stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                            keys = existing is null
                                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                                : new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogError(ex, "スキップリスト JSON が破損しています: {Path}", _filePath);
                            keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }
                    }

                    if (!keys.Add(skipKey))
                        return; // 既に存在する場合は何もしない

                    stream.SetLength(0);
                    stream.Position = 0;
                    await JsonSerializer.SerializeAsync(stream, keys, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                    _logger.LogDebug("スキップリストに追加: {SkipKey}", skipKey);
                    return;
                }
                catch (IOException) when (i < WriteRetryCount - 1)
                {
                    await Task.Delay(50 * (i + 1), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex) when (i >= WriteRetryCount - 1)
                {
                    _logger.LogError(
                        ex,
                        "スキップリストの原子的追加に失敗しました（リトライ上限到達）: {Path}",
                        _filePath);
                    throw;
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
