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
/// Graph プロバイダー設定 DTO（シークレット除外済み）。
/// ClientId / TenantId / ClientSecretExpiry のみ。ClientSecret は Credential Manager 管理。
/// </summary>
public sealed record GraphConfigDto(string ClientId, string TenantId, string ClientSecretExpiry);

/// <summary>
/// Graph プロバイダー設定マージ更新 DTO。null フィールドは上書きしない。
/// </summary>
public sealed record GraphConfigUpdateDto(
    string? ClientId = null,
    string? TenantId = null,
    string? ClientSecretExpiry = null);

/// <summary>
/// Discovery 結果 DTO（OneDrive Drive ID / SharePoint Site ID / Drive ID）。
/// </summary>
public sealed record DiscoveryConfigDto(
    string OneDriveUserId,
    string OneDriveDriveId,
    string OneDriveSourceFolderId,
    string OneDriveSourceFolderPath,
    string SharePointSiteId,
    string SharePointDriveId,
    string SharePointDestFolderId,
    string SharePointDestFolderPath,
    string MigrationRoute,
    string DestinationProvider);

/// <summary>
/// Discovery 結果マージ更新 DTO。null フィールドは上書きしない。
/// </summary>
public sealed record DiscoveryConfigUpdateDto(
    string? OneDriveUserId = null,
    string? OneDriveDriveId = null,
    string? OneDriveSourceFolderId = null,
    string? OneDriveSourceFolderPath = null,
    string? SharePointSiteId = null,
    string? SharePointDriveId = null,
    string? SharePointDestFolderId = null,
    string? SharePointDestFolderPath = null,
    string? MigrationRoute = null,
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

    /// <summary>Graph プロバイダー設定を取得する（ClientId / TenantId / ClientSecretExpiry）。</summary>
    Task<GraphConfigDto> GetGraphConfigAsync(CancellationToken ct = default);

    /// <summary>Graph プロバイダー設定をマージ保存する（null フィールドはスキップ）。</summary>
    Task UpdateGraphConfigAsync(GraphConfigUpdateDto update, CancellationToken ct = default);

    /// <summary>Discovery 結果（OneDrive / SharePoint リソース識別子）を取得する。</summary>
    Task<DiscoveryConfigDto> GetDiscoveryConfigAsync(CancellationToken ct = default);

    /// <summary>Discovery 結果をマージ保存する（null フィールドはスキップ）。</summary>
    Task UpdateDiscoveryConfigAsync(DiscoveryConfigUpdateDto update, CancellationToken ct = default);
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
            DestinationProvider: NormalizeProvider(GetString(m, "destinationProvider", "sharepoint")));
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

    /// <summary>
    /// プロバイダー文字列を正規化する。大文字小文字を吸収し、旧表記エイリアスをマップする。
    /// 未知の値も小文字化したうえで返す（将来プロバイダー追加時に比較しやすくするため）。
    /// </summary>
    private static string NormalizeProvider(string value) => value.ToLowerInvariant() switch
    {
        "sharepoint" or "graph" => "sharepoint",  // "Graph" は旧表記エイリアス
        "dropbox" => "dropbox",
        var v => v  // 未知のプロバイダーはそのまま保持
    };

    /// <inheritdoc />
    public async Task<GraphConfigDto> GetGraphConfigAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_configFilePath))
            return new GraphConfigDto(string.Empty, string.Empty, string.Empty);

        var json = await File.ReadAllTextAsync(_configFilePath, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("migrator", out var m) ||
                !m.TryGetProperty("graph", out var g))
                return new GraphConfigDto(string.Empty, string.Empty, string.Empty);

            return new GraphConfigDto(
                ClientId: GetString(g, "clientId", string.Empty),
                TenantId: GetString(g, "tenantId", string.Empty),
                ClientSecretExpiry: GetString(g, "clientSecretExpiry", string.Empty));
        }
        catch (JsonException)
        {
            // config.json が不正な JSON の場合は空の DTO を返す
            return new GraphConfigDto(string.Empty, string.Empty, string.Empty);
        }
    }

    /// <inheritdoc />
    public async Task UpdateGraphConfigAsync(GraphConfigUpdateDto update, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = File.Exists(_configFilePath)
                ? await File.ReadAllTextAsync(_configFilePath, ct).ConfigureAwait(false)
                : "{}";
            var root = JsonNode.Parse(json) ?? new JsonObject();

            // migrator セクションを確保
            if (root["migrator"] is not JsonObject m)
            {
                m = new JsonObject();
                root["migrator"] = m;
            }

            // graph サブセクションを確保
            if (m["graph"] is not JsonObject g)
            {
                g = new JsonObject();
                m["graph"] = g;
            }

            if (update.ClientId is not null) g["clientId"] = update.ClientId;
            if (update.TenantId is not null) g["tenantId"] = update.TenantId;
            if (update.ClientSecretExpiry is not null) g["clientSecretExpiry"] = update.ClientSecretExpiry;

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

    /// <inheritdoc />
    public async Task<DiscoveryConfigDto> GetDiscoveryConfigAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_configFilePath))
            return new DiscoveryConfigDto(string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "sharepoint");

        var json = await File.ReadAllTextAsync(_configFilePath, ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("migrator", out var m) || m.ValueKind != JsonValueKind.Object)
                return new DiscoveryConfigDto(string.Empty, string.Empty, string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "sharepoint");

            var hasGraphObject = m.TryGetProperty("graph", out var gProp) && gProp.ValueKind == JsonValueKind.Object;
            var oneDriveUserId = hasGraphObject ? GetString(gProp, "oneDriveUserId", string.Empty) : string.Empty;
            var oneDriveDriveId = hasGraphObject ? GetString(gProp, "oneDriveDriveId", string.Empty) : string.Empty;
            var oneDriveSourceFolderId = hasGraphObject ? GetString(gProp, "oneDriveSourceFolderId", string.Empty) : string.Empty;
            var oneDriveSourceFolderPath = hasGraphObject ? GetString(gProp, "oneDriveSourceFolderPath", string.Empty) : string.Empty;
            var sharePointSiteId = hasGraphObject ? GetString(gProp, "sharePointSiteId", string.Empty) : string.Empty;
            var sharePointDriveId = hasGraphObject ? GetString(gProp, "sharePointDriveId", string.Empty) : string.Empty;
            var sharePointDestFolderId = hasGraphObject ? GetString(gProp, "sharePointDestFolderId", string.Empty) : string.Empty;
            var sharePointDestFolderPath = hasGraphObject ? GetString(gProp, "sharePointDestFolderPath", string.Empty) : string.Empty;
            var migrationRoute = GetString(m, "migrationRoute", string.Empty);
            var destinationProvider = GetString(m, "destinationProvider", "sharepoint");

            return new DiscoveryConfigDto(
                oneDriveUserId, oneDriveDriveId, oneDriveSourceFolderId, oneDriveSourceFolderPath,
                sharePointSiteId, sharePointDriveId, sharePointDestFolderId, sharePointDestFolderPath,
                migrationRoute, destinationProvider);
        }
        catch (JsonException)
        {
            return new DiscoveryConfigDto(string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, "sharepoint");
        }
    }

    /// <inheritdoc />
    public async Task UpdateDiscoveryConfigAsync(DiscoveryConfigUpdateDto update, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = File.Exists(_configFilePath)
                ? await File.ReadAllTextAsync(_configFilePath, ct).ConfigureAwait(false)
                : "{}";
            var root = JsonNode.Parse(json) ?? new JsonObject();

            // migrator セクションを確保
            if (root["migrator"] is not JsonObject m)
            {
                m = new JsonObject();
                root["migrator"] = m;
            }

            // graph サブセクションを確保
            if (m["graph"] is not JsonObject g)
            {
                g = new JsonObject();
                m["graph"] = g;
            }

            if (update.OneDriveUserId is not null) g["oneDriveUserId"] = update.OneDriveUserId;
            if (update.OneDriveDriveId is not null) g["oneDriveDriveId"] = update.OneDriveDriveId;
            if (update.OneDriveSourceFolderId is not null) g["oneDriveSourceFolderId"] = update.OneDriveSourceFolderId;
            if (update.OneDriveSourceFolderPath is not null) g["oneDriveSourceFolderPath"] = update.OneDriveSourceFolderPath;
            if (update.SharePointSiteId is not null) g["sharePointSiteId"] = update.SharePointSiteId;
            if (update.SharePointDriveId is not null) g["sharePointDriveId"] = update.SharePointDriveId;
            if (update.SharePointDestFolderId is not null) g["sharePointDestFolderId"] = update.SharePointDestFolderId;
            if (update.SharePointDestFolderPath is not null) g["sharePointDestFolderPath"] = update.SharePointDestFolderPath;
            if (update.MigrationRoute is not null) m["migrationRoute"] = update.MigrationRoute;
            if (update.DestinationProvider is not null) m["destinationProvider"] = update.DestinationProvider;

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
}
