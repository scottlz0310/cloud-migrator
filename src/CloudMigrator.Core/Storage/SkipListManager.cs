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
                return JsonSerializer.Deserialize<HashSet<string>>(json, JsonOptions)
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
    /// 同一プロセス内の排他制御には SemaphoreSlim を、
    /// プロセス間の排他制御には FileShare.None を使用する。
    /// </summary>
    public async Task AddAsync(string skipKey, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var keys = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!keys.Add(skipKey))
                return; // 既に存在する場合は何もしない

            await WriteWithRetryAsync(keys, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("スキップリストに追加: {SkipKey}", skipKey);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WriteWithRetryAsync(HashSet<string> keys, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        for (int i = 0; i < WriteRetryCount; i++)
        {
            try
            {
                await using var fs = new FileStream(
                    _filePath, FileMode.Create, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(fs, keys, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
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
                    "スキップリスト書き込みに失敗しました（リトライ上限到達）: {Path}, RetryCount={RetryCount}",
                    _filePath,
                    WriteRetryCount);
                throw;
            }
        }
    }
}
