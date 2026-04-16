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
    string DestinationProvider,
    bool AdaptiveConcurrencyEnabled = false,
    int AdaptiveConcurrencyInitialDegree = 0,
    int AdaptiveConcurrencyDecreasePercent = 50,
    int AdaptiveConcurrencyIncreaseIntervalSec = 60,
    // ── RateControl 設定（v0.5.0: RateControlledTransferController 用）──
    bool UseRateControl = false,
    int RcShortWindowSec = 5,
    int RcLongWindowSec = 30,
    int RcEmergencyThresholdPct = 10,
    int RcSlowdownThresholdPct = 3,
    double RcDecayK = 5.0,
    double RcMinDecayFactor = 0.3,
    double RcMaxDecayFactor = 0.9,
    int RcInFlightThreshold = 32,
    int RcMaxConcurrency = 16);

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
    string? DestinationProvider = null,
    bool? AdaptiveConcurrencyEnabled = null,
    int? AdaptiveConcurrencyInitialDegree = null,
    int? AdaptiveConcurrencyDecreasePercent = null,
    int? AdaptiveConcurrencyIncreaseIntervalSec = null,
    // ── RateControl 設定（v0.5.0: RateControlledTransferController 用）──
    bool? UseRateControl = null,
    int? RcShortWindowSec = null,
    int? RcLongWindowSec = null,
    int? RcEmergencyThresholdPct = null,
    int? RcSlowdownThresholdPct = null,
    double? RcDecayK = null,
    double? RcMinDecayFactor = null,
    double? RcMaxDecayFactor = null,
    int? RcInFlightThreshold = null,
    int? RcMaxConcurrency = null);

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

        // adaptiveConcurrency.sharepoint セクションを読み取る（なければ default プロファイルにフォールバック）
        var adaptiveEnabled = false;
        var adaptiveInitialDegree = 0;
        var adaptiveDecreasePercent = 50;
        var adaptiveIncreaseIntervalSec = 60;
        if (m.TryGetProperty("adaptiveConcurrency", out var acProp) && acProp.ValueKind == JsonValueKind.Object)
        {
            // sharepoint キーがなければ default プロファイルを試みる
            JsonElement acProfile;
            var hasProfile = (acProp.TryGetProperty("sharepoint", out acProfile) && acProfile.ValueKind == JsonValueKind.Object)
                          || (acProp.TryGetProperty("default", out acProfile) && acProfile.ValueKind == JsonValueKind.Object);
            if (hasProfile)
            {
                adaptiveEnabled = acProfile.TryGetProperty("enabled", out var enProp) && enProp.ValueKind == JsonValueKind.True;
                adaptiveInitialDegree = GetInt(acProfile, "initialDegree", 0);
                // decreaseMultiplier (double) を % (int) に変換して返す
                if (acProfile.TryGetProperty("decreaseMultiplier", out var dmProp) && dmProp.TryGetDouble(out var dm) && dm > 0 && dm < 1)
                    adaptiveDecreasePercent = Math.Clamp((int)Math.Floor(dm * 100), 1, 99);
                adaptiveIncreaseIntervalSec = GetInt(acProfile, "increaseIntervalSec", 60);
            }
        }

        // rateControl セクションを読み取る
        var useRateControl = false;
        var rcShortWindowSec = 5;
        var rcLongWindowSec = 30;
        var rcEmergencyThresholdPct = 10;
        var rcSlowdownThresholdPct = 3;
        var rcDecayK = 5.0;
        var rcMinDecayFactor = 0.3;
        var rcMaxDecayFactor = 0.9;
        var rcInFlightThreshold = 32;
        var rcMaxConcurrency = 16;
        if (m.TryGetProperty("rateControl", out var rcProp) && rcProp.ValueKind == JsonValueKind.Object)
        {
            useRateControl = rcProp.TryGetProperty("useRateControl", out var urProp) && urProp.ValueKind == JsonValueKind.True;
            rcShortWindowSec = GetInt(rcProp, "shortWindowSec", 5);
            rcLongWindowSec = GetInt(rcProp, "longWindowSec", 30);
            // emergencyThreshold / slowdownThreshold は JSON に 0–1 で保存、UI では 0–100 (%) で表示
            if (rcProp.TryGetProperty("emergencyThreshold", out var etProp) && etProp.TryGetDouble(out var et))
                rcEmergencyThresholdPct = Math.Clamp((int)Math.Round(et * 100), 0, 100);
            if (rcProp.TryGetProperty("slowdownThreshold", out var stProp) && stProp.TryGetDouble(out var st))
                rcSlowdownThresholdPct = Math.Clamp((int)Math.Round(st * 100), 0, 100);
            if (rcProp.TryGetProperty("decayK", out var dkProp) && dkProp.TryGetDouble(out var dk))
                rcDecayK = dk;
            if (rcProp.TryGetProperty("minDecayFactor", out var minDfProp) && minDfProp.TryGetDouble(out var minDf))
                rcMinDecayFactor = minDf;
            if (rcProp.TryGetProperty("maxDecayFactor", out var maxDfProp) && maxDfProp.TryGetDouble(out var maxDf))
                rcMaxDecayFactor = maxDf;
            rcInFlightThreshold = GetInt(rcProp, "inFlightThreshold", 32);
            rcMaxConcurrency = GetInt(rcProp, "maxConcurrency", 16);
        }

        return new ConfigDto(
            MaxParallelTransfers: GetInt(m, "maxParallelTransfers", 4),
            MaxParallelFolderCreations: GetInt(m, "maxParallelFolderCreations", 4),
            ChunkSizeMb: GetInt(m, "chunkSizeMb", 5),
            LargeFileThresholdMb: GetInt(m, "largeFileThresholdMb", 4),
            RetryCount: GetInt(m, "retryCount", 3),
            TimeoutSec: GetInt(m, "timeoutSec", 300),
            DestinationRoot: GetString(m, "destinationRoot", string.Empty),
            DestinationProvider: NormalizeProvider(GetString(m, "destinationProvider", "sharepoint")),
            AdaptiveConcurrencyEnabled: adaptiveEnabled,
            AdaptiveConcurrencyInitialDegree: adaptiveInitialDegree,
            AdaptiveConcurrencyDecreasePercent: adaptiveDecreasePercent,
            AdaptiveConcurrencyIncreaseIntervalSec: adaptiveIncreaseIntervalSec,
            UseRateControl: useRateControl,
            RcShortWindowSec: rcShortWindowSec,
            RcLongWindowSec: rcLongWindowSec,
            RcEmergencyThresholdPct: rcEmergencyThresholdPct,
            RcSlowdownThresholdPct: rcSlowdownThresholdPct,
            RcDecayK: rcDecayK,
            RcMinDecayFactor: rcMinDecayFactor,
            RcMaxDecayFactor: rcMaxDecayFactor,
            RcInFlightThreshold: rcInFlightThreshold,
            RcMaxConcurrency: rcMaxConcurrency);
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
            if (update.DestinationRoot is not null)
            {
                m["destinationRoot"] = update.DestinationRoot;
                // sharePointDestFolderPath（Discovery 表示用）と同期する
                if (m["graph"] is JsonObject gObj)
                    gObj["sharePointDestFolderPath"] = update.DestinationRoot;
            }
            if (update.DestinationProvider is not null) m["destinationProvider"] = update.DestinationProvider;

            // adaptiveConcurrency.sharepoint セクションを更新
            if (update.AdaptiveConcurrencyEnabled.HasValue || update.AdaptiveConcurrencyInitialDegree.HasValue
                || update.AdaptiveConcurrencyDecreasePercent.HasValue || update.AdaptiveConcurrencyIncreaseIntervalSec.HasValue)
            {
                if (m["adaptiveConcurrency"] is not JsonObject acObj)
                {
                    acObj = new JsonObject();
                    m["adaptiveConcurrency"] = acObj;
                }
                if (acObj["sharepoint"] is not JsonObject spAcObj)
                {
                    spAcObj = new JsonObject();
                    acObj["sharepoint"] = spAcObj;
                }
                if (update.AdaptiveConcurrencyEnabled.HasValue)
                    spAcObj["enabled"] = update.AdaptiveConcurrencyEnabled.Value;
                if (update.AdaptiveConcurrencyInitialDegree.HasValue)
                    spAcObj["initialDegree"] = update.AdaptiveConcurrencyInitialDegree.Value;
                if (update.AdaptiveConcurrencyDecreasePercent.HasValue)
                    spAcObj["decreaseMultiplier"] = update.AdaptiveConcurrencyDecreasePercent.Value / 100.0;
                if (update.AdaptiveConcurrencyIncreaseIntervalSec.HasValue)
                    spAcObj["increaseIntervalSec"] = update.AdaptiveConcurrencyIncreaseIntervalSec.Value;
            }

            // rateControl セクションを更新
            if (update.UseRateControl.HasValue || update.RcShortWindowSec.HasValue
                || update.RcLongWindowSec.HasValue || update.RcEmergencyThresholdPct.HasValue
                || update.RcSlowdownThresholdPct.HasValue || update.RcDecayK.HasValue
                || update.RcMinDecayFactor.HasValue || update.RcMaxDecayFactor.HasValue
                || update.RcInFlightThreshold.HasValue || update.RcMaxConcurrency.HasValue)
            {
                if (m["rateControl"] is not JsonObject rcObj)
                {
                    rcObj = new JsonObject();
                    m["rateControl"] = rcObj;
                }
                if (update.UseRateControl.HasValue) rcObj["useRateControl"] = update.UseRateControl.Value;
                if (update.RcShortWindowSec.HasValue) rcObj["shortWindowSec"] = update.RcShortWindowSec.Value;
                if (update.RcLongWindowSec.HasValue) rcObj["longWindowSec"] = update.RcLongWindowSec.Value;
                // UI 側は % (0–100)、JSON 側は 0–1 に変換して保存
                if (update.RcEmergencyThresholdPct.HasValue)
                    rcObj["emergencyThreshold"] = update.RcEmergencyThresholdPct.Value / 100.0;
                if (update.RcSlowdownThresholdPct.HasValue)
                    rcObj["slowdownThreshold"] = update.RcSlowdownThresholdPct.Value / 100.0;
                if (update.RcDecayK.HasValue) rcObj["decayK"] = update.RcDecayK.Value;
                if (update.RcMinDecayFactor.HasValue) rcObj["minDecayFactor"] = update.RcMinDecayFactor.Value;
                if (update.RcMaxDecayFactor.HasValue) rcObj["maxDecayFactor"] = update.RcMaxDecayFactor.Value;
                if (update.RcInFlightThreshold.HasValue) rcObj["inFlightThreshold"] = update.RcInFlightThreshold.Value;
                if (update.RcMaxConcurrency.HasValue) rcObj["maxConcurrency"] = update.RcMaxConcurrency.Value;
            }

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

            // oneDriveSourceFolderPath（Discovery 表示用）と oneDriveSourceFolder（転送処理用）を常に同値へ正規化する。
            // update が null の場合は既存値（どちらか一方）をフォールバックとして双方に同期し、
            // init/bootstrap 等の別経路で oneDriveSourceFolder だけが更新された場合の乖離を防ぐ。
            var existingFolderPath = g["oneDriveSourceFolderPath"]?.GetValue<string>();
            var existingSourceFolder = g["oneDriveSourceFolder"]?.GetValue<string>();
            var effectiveFolderPath = update.OneDriveSourceFolderPath
                ?? existingFolderPath
                ?? existingSourceFolder;
            if (effectiveFolderPath is not null)
            {
                g["oneDriveSourceFolderPath"] = effectiveFolderPath;
                g["oneDriveSourceFolder"] = effectiveFolderPath;
            }
            if (update.SharePointSiteId is not null) g["sharePointSiteId"] = update.SharePointSiteId;
            if (update.SharePointDriveId is not null) g["sharePointDriveId"] = update.SharePointDriveId;
            if (update.SharePointDestFolderId is not null) g["sharePointDestFolderId"] = update.SharePointDestFolderId;

            // sharePointDestFolderPath（Discovery 表示用）と destinationRoot（転送処理用）を常に同値へ正規化する。
            // update が null の場合は既存値をフォールバックとして双方に同期し、
            // 設定タブ等の別経路で destinationRoot だけが更新された場合の乖離を防ぐ。
            var existingDestFolderPath = g["sharePointDestFolderPath"]?.GetValue<string>();
            var existingDestinationRoot = m["destinationRoot"]?.GetValue<string>();
            var effectiveDestFolderPath = update.SharePointDestFolderPath
                ?? existingDestFolderPath
                ?? existingDestinationRoot;
            if (effectiveDestFolderPath is not null)
            {
                g["sharePointDestFolderPath"] = effectiveDestFolderPath;
                m["destinationRoot"] = effectiveDestFolderPath;
            }

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
