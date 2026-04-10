using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudMigrator.Core.Configuration;

/// <summary>
/// GET /api/config で返す設定 DTO（シークレット除外済み）。
/// </summary>
public sealed record ConfigDto(
    int MaxParallelTransfers,
    int MaxParallelFolderCreations,
    int ChunkSizeMb,
    int LargeFileThresholdMb,
    int RetryCount,
    int TimeoutSec,
    string DestinationRoot,
    string DestinationProvider);

/// <summary>
/// PUT /api/config で受け取るマージ更新 DTO。null フィールドは上書きしない。
/// </summary>
public sealed record ConfigUpdateDto(
    int? MaxParallelTransfers = null,
    int? MaxParallelFolderCreations = null,
    int? ChunkSizeMb = null,
    int? LargeFileThresholdMb = null,
    int? RetryCount = null,
    int? TimeoutSec = null,
    string? DestinationRoot = null,
    string? DestinationProvider = null);

/// <summary>
/// config.json の読み書きを担当するサービス契約。
/// </summary>
public interface IConfigurationService
{
    /// <summary>設定を取得する（シークレット除外）。</summary>
    Task<ConfigDto> GetConfigAsync(CancellationToken ct = default);

    /// <summary>指定フィールドのみをマージ保存する（null フィールドはスキップ）。</summary>
    Task UpdateConfigAsync(ConfigUpdateDto update, CancellationToken ct = default);
}

/// <summary>
/// config.json を直接読み書きする <see cref="IConfigurationService"/> 実装。
/// 並行書き込みは SemaphoreSlim でシリアライズする。
/// </summary>
public sealed class ConfigurationService : IConfigurationService
{
    private static readonly JsonWriterOptions WriteOptions = new() { Indented = true };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _configFilePath;

    /// <param name="configFilePath">config.json の絶対パス。null の場合は自動検索する。</param>
    public ConfigurationService(string? configFilePath = null)
    {
        _configFilePath = configFilePath ?? AppConfiguration.ResolveConfigPath();
    }

    /// <inheritdoc />
    public async Task<ConfigDto> GetConfigAsync(CancellationToken ct = default)
    {
        var json = await File.ReadAllTextAsync(_configFilePath, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement.GetProperty("migrator");

        return new ConfigDto(
            MaxParallelTransfers: GetInt(m, "maxParallelTransfers", 4),
            MaxParallelFolderCreations: GetInt(m, "maxParallelFolderCreations", 4),
            ChunkSizeMb: GetInt(m, "chunkSizeMb", 5),
            LargeFileThresholdMb: GetInt(m, "largeFileThresholdMb", 4),
            RetryCount: GetInt(m, "retryCount", 3),
            TimeoutSec: GetInt(m, "timeoutSec", 300),
            DestinationRoot: GetString(m, "destinationRoot", string.Empty),
            DestinationProvider: GetString(m, "destinationProvider", "sharepoint"));
    }

    /// <inheritdoc />
    public async Task UpdateConfigAsync(ConfigUpdateDto update, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await File.ReadAllTextAsync(_configFilePath, ct).ConfigureAwait(false);
            var root = JsonNode.Parse(json)
                ?? throw new InvalidOperationException("config.json のパースに失敗しました。");
            var m = root["migrator"]?.AsObject()
                ?? throw new InvalidOperationException("config.json に migrator セクションが存在しません。");

            if (update.MaxParallelTransfers.HasValue) m["maxParallelTransfers"] = update.MaxParallelTransfers.Value;
            if (update.MaxParallelFolderCreations.HasValue) m["maxParallelFolderCreations"] = update.MaxParallelFolderCreations.Value;
            if (update.ChunkSizeMb.HasValue) m["chunkSizeMb"] = update.ChunkSizeMb.Value;
            if (update.LargeFileThresholdMb.HasValue) m["largeFileThresholdMb"] = update.LargeFileThresholdMb.Value;
            if (update.RetryCount.HasValue) m["retryCount"] = update.RetryCount.Value;
            if (update.TimeoutSec.HasValue) m["timeoutSec"] = update.TimeoutSec.Value;
            if (update.DestinationRoot is not null) m["destinationRoot"] = update.DestinationRoot;
            if (update.DestinationProvider is not null) m["destinationProvider"] = update.DestinationProvider;

            // アトミック書き込み: 一時ファイルに書き込んでからリネーム
            var tmpPath = _configFilePath + ".tmp";
            await using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await using (var writer = new Utf8JsonWriter(stream, WriteOptions))
            {
                root.WriteTo(writer);
            }
            File.Move(tmpPath, _configFilePath, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static int GetInt(JsonElement element, string name, int defaultValue)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : defaultValue;

    private static string GetString(JsonElement element, string name, string defaultValue)
        => element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? defaultValue
            : defaultValue;
}
