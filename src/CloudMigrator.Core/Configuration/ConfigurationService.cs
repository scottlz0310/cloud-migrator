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
    int RcMaxConcurrency = 16,
    // ── HybridRateController 専用パラメーター（v0.6.0 #163）──
    bool UseHybridController = false,
    int RcCooldownSec = 20,
    double RcEmergencyDecay = 0.9,
    double RcEmergencyInflightDecay = 0.9,
    double RcAddStep = 1.0,
    string RcLatencyMode = "None",
    // ── UI 表示設定（#156/#157: ダッシュボードグラフ表示制御）──
    bool ShowGraphs = true,
    int GraphColumns = 2,
    // ── UI テーマ設定（#177）──
    string ThemeMode = "system");

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
    int? RcMaxConcurrency = null,
    // ── HybridRateController 専用パラメーター（v0.6.0 #163）──
    bool? UseHybridController = null,
    int? RcCooldownSec = null,
    double? RcEmergencyDecay = null,
    double? RcEmergencyInflightDecay = null,
    double? RcAddStep = null,
    string? RcLatencyMode = null,
    // ── UI 表示設定（#156/#157: ダッシュボードグラフ表示制御）──
    bool? ShowGraphs = null,
    int? GraphColumns = null,
    // ── UI テーマ設定（#177）──
    string? ThemeMode = null);

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
/// Discovery 結果 DTO（OneDrive Drive ID・表示名 / SharePoint Site ID・Drive ID・表示名・URL）。
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
    string DestinationProvider,
    // ── 表示名拡充（#178）──
    string OneDriveDisplayName = "",
    string SharePointSiteDisplayName = "",
    string SharePointSiteWebUrl = "",
    string SharePointDriveDisplayName = "");

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
    string? DestinationProvider = null,
    // ── 表示名拡充（#178）──
    string? OneDriveDisplayName = null,
    string? SharePointSiteDisplayName = null,
    string? SharePointSiteWebUrl = null,
    string? SharePointDriveDisplayName = null);

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
        try
        {
            var json = await File.ReadAllTextAsync(_configFilePath, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("migrator", out var m))
                return new ConfigDto(
                    MaxParallelTransfers: 4, MaxParallelFolderCreations: 4,
                    ChunkSizeMb: 5, LargeFileThresholdMb: 4,
                    RetryCount: 3, TimeoutSec: 300,
                    DestinationRoot: string.Empty, DestinationProvider: "sharepoint");

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
            var useHybridController = false;
            var rcCooldownSec = 20;
            var rcEmergencyDecay = 0.9;
            var rcEmergencyInflightDecay = 0.9;
            var rcAddStep = 1.0;
            var rcLatencyMode = "None";
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
                useHybridController = rcProp.TryGetProperty("useHybridController", out var uhProp) && uhProp.ValueKind == JsonValueKind.True;
                rcCooldownSec = GetInt(rcProp, "cooldownSec", 20);
                if (rcProp.TryGetProperty("emergencyDecay", out var edProp) && edProp.TryGetDouble(out var ed))
                    rcEmergencyDecay = ed;
                if (rcProp.TryGetProperty("emergencyInflightDecay", out var eidProp) && eidProp.TryGetDouble(out var eid))
                    rcEmergencyInflightDecay = eid;
                if (rcProp.TryGetProperty("addStep", out var asProp) && asProp.TryGetDouble(out var addS))
                    rcAddStep = addS;
                if (rcProp.TryGetProperty("latencyEvaluationMode", out var lmProp) && lmProp.ValueKind == JsonValueKind.String)
                    rcLatencyMode = lmProp.GetString() ?? "None";
            }

            // migrator.ui.themeMode を読み取る（大文字小文字を吸収・未知値は system にフォールバック）
            var themeMode = "system";
            if (m.TryGetProperty("ui", out var uiProp) && uiProp.ValueKind == JsonValueKind.Object)
            {
                if (uiProp.TryGetProperty("themeMode", out var tmProp) && tmProp.ValueKind == JsonValueKind.String)
                {
                    var raw = tmProp.GetString()?.Trim().ToLowerInvariant() ?? "system";
                    themeMode = raw is "light" or "dark" or "system" ? raw : "system";
                }
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
                RcMaxConcurrency: rcMaxConcurrency,
                UseHybridController: useHybridController,
                RcCooldownSec: rcCooldownSec,
                RcEmergencyDecay: rcEmergencyDecay,
                RcEmergencyInflightDecay: rcEmergencyInflightDecay,
                RcAddStep: rcAddStep,
                RcLatencyMode: rcLatencyMode,
                ShowGraphs: !(m.TryGetProperty("showGraphs", out var sgProp) && sgProp.ValueKind == JsonValueKind.False),
                GraphColumns: Math.Clamp(GetInt(m, "graphColumns", 2), 1, 4),
                ThemeMode: themeMode);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // ファイル不在・アクセス拒否・競合・不正 JSON の場合はデフォルト値を返す
            return new ConfigDto(
                MaxParallelTransfers: 4, MaxParallelFolderCreations: 4,
                ChunkSizeMb: 5, LargeFileThresholdMb: 4,
                RetryCount: 3, TimeoutSec: 300,
                DestinationRoot: string.Empty, DestinationProvider: "sharepoint");
        }
    }

    /// <inheritdoc />
    public async Task UpdateConfigAsync(ConfigUpdateDto update, CancellationToken ct = default)
    {
        // 単一フィールド範囲チェック（ロック外でフェイルファスト）
        if (update.RcShortWindowSec.HasValue && update.RcShortWindowSec.Value < 1)
            throw new ArgumentException("RcShortWindowSec は 1 以上である必要があります。", nameof(update));
        if (update.RcLongWindowSec.HasValue && update.RcLongWindowSec.Value < 5)
            throw new ArgumentException("RcLongWindowSec は 5 以上である必要があります。", nameof(update));
        if (update.RcEmergencyThresholdPct.HasValue && update.RcEmergencyThresholdPct.Value is < 0 or > 100)
            throw new ArgumentException("RcEmergencyThresholdPct は 0〜100 の範囲で入力してください。", nameof(update));
        if (update.RcSlowdownThresholdPct.HasValue && update.RcSlowdownThresholdPct.Value is < 0 or > 100)
            throw new ArgumentException("RcSlowdownThresholdPct は 0〜100 の範囲で入力してください。", nameof(update));
        if (update.RcMinDecayFactor.HasValue && update.RcMinDecayFactor.Value is < 0 or > 1)
            throw new ArgumentException("RcMinDecayFactor は 0〜1 の範囲で入力してください。", nameof(update));
        if (update.RcMaxDecayFactor.HasValue && update.RcMaxDecayFactor.Value is < 0 or > 1)
            throw new ArgumentException("RcMaxDecayFactor は 0〜1 の範囲で入力してください。", nameof(update));
        if (update.RcInFlightThreshold.HasValue && update.RcInFlightThreshold.Value < 1)
            throw new ArgumentException("RcInFlightThreshold は 1 以上である必要があります。", nameof(update));
        if (update.RcMaxConcurrency.HasValue && update.RcMaxConcurrency.Value < 1)
            throw new ArgumentException("RcMaxConcurrency は 1 以上である必要があります。", nameof(update));
        if (update.RcCooldownSec.HasValue && update.RcCooldownSec.Value < 0)
            throw new ArgumentException("RcCooldownSec は 0 以上である必要があります。", nameof(update));
        if (update.RcEmergencyDecay.HasValue && update.RcEmergencyDecay.Value is <= 0 or >= 1)
            throw new ArgumentException("RcEmergencyDecay は 0 より大きく 1 未満の値を指定してください。", nameof(update));
        if (update.RcEmergencyInflightDecay.HasValue && update.RcEmergencyInflightDecay.Value is <= 0 or >= 1)
            throw new ArgumentException("RcEmergencyInflightDecay は 0 より大きく 1 未満の値を指定してください。", nameof(update));
        if (update.RcAddStep.HasValue && update.RcAddStep.Value <= 0)
            throw new ArgumentException("RcAddStep は 0 より大きい値を指定してください。", nameof(update));
        if (update.RcLatencyMode is not null
            && !string.Equals(update.RcLatencyMode, "None", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(update.RcLatencyMode, "Baseline", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(update.RcLatencyMode, "Recent", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(update.RcLatencyMode, "Both", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("RcLatencyMode には None/Baseline/Recent/Both のいずれかを指定してください。", nameof(update));

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = File.Exists(_configFilePath)
                ? await File.ReadAllTextAsync(_configFilePath, ct).ConfigureAwait(false)
                : "{}";
            var root = JsonNode.Parse(json) ?? new JsonObject();
            if (root["migrator"] is not JsonObject m)
            {
                m = new JsonObject();
                root["migrator"] = m;
            }

            // クロスフィールドバリデーション: update と現在の config.json をマージした「実際に保存される値」で検証する
            // 片方だけ更新するケースでも既存値との組み合わせが不正にならないよう保護する
            if (update.RcShortWindowSec.HasValue || update.RcLongWindowSec.HasValue)
            {
                var rcForValidation = m["rateControl"] as JsonObject;
                var effectiveShort = update.RcShortWindowSec
                    ?? (rcForValidation?["shortWindowSec"] is JsonNode sn ? sn.GetValue<int>() : 5);
                var effectiveLong = update.RcLongWindowSec
                    ?? (rcForValidation?["longWindowSec"] is JsonNode ln ? ln.GetValue<int>() : 30);
                if (effectiveShort >= effectiveLong)
                    throw new ArgumentException(
                        "RcShortWindowSec は RcLongWindowSec より小さい値にしてください。", nameof(update));
            }
            if (update.RcMinDecayFactor.HasValue || update.RcMaxDecayFactor.HasValue)
            {
                var rcForValidation = m["rateControl"] as JsonObject;
                var effectiveMin = update.RcMinDecayFactor
                    ?? (rcForValidation?["minDecayFactor"] is JsonNode mn ? mn.GetValue<double>() : 0.3);
                var effectiveMax = update.RcMaxDecayFactor
                    ?? (rcForValidation?["maxDecayFactor"] is JsonNode mx ? mx.GetValue<double>() : 0.9);
                if (effectiveMin >= effectiveMax)
                    throw new ArgumentException(
                        "RcMinDecayFactor は RcMaxDecayFactor より小さい値にしてください。", nameof(update));
            }

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
                || update.RcInFlightThreshold.HasValue || update.RcMaxConcurrency.HasValue
                || update.UseHybridController.HasValue || update.RcCooldownSec.HasValue
                || update.RcEmergencyDecay.HasValue || update.RcEmergencyInflightDecay.HasValue
                || update.RcAddStep.HasValue
                || update.RcLatencyMode is not null)
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
                if (update.UseHybridController.HasValue) rcObj["useHybridController"] = update.UseHybridController.Value;
                if (update.RcCooldownSec.HasValue) rcObj["cooldownSec"] = update.RcCooldownSec.Value;
                if (update.RcEmergencyDecay.HasValue) rcObj["emergencyDecay"] = update.RcEmergencyDecay.Value;
                if (update.RcEmergencyInflightDecay.HasValue) rcObj["emergencyInflightDecay"] = update.RcEmergencyInflightDecay.Value;
                if (update.RcAddStep.HasValue) rcObj["addStep"] = update.RcAddStep.Value;
                if (update.RcLatencyMode is not null) rcObj["latencyEvaluationMode"] = update.RcLatencyMode;
            }

            // UI 表示設定
            if (update.ShowGraphs.HasValue) m["showGraphs"] = update.ShowGraphs.Value;
            if (update.GraphColumns.HasValue) m["graphColumns"] = Math.Clamp(update.GraphColumns.Value, 1, 4);
            if (update.ThemeMode is not null)
            {
                var normalizedThemeMode = update.ThemeMode.Trim().ToLowerInvariant();
                if (normalizedThemeMode is not ("light" or "dark" or "system"))
                    throw new ArgumentException("ThemeMode は light / dark / system のいずれかを指定してください。", nameof(update));
                if (m["ui"] is not JsonObject uiObj)
                {
                    uiObj = new JsonObject();
                    m["ui"] = uiObj;
                }
                uiObj["themeMode"] = normalizedThemeMode;
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
            var oneDriveDisplayName = hasGraphObject ? GetString(gProp, "oneDriveDisplayName", string.Empty) : string.Empty;
            var sharePointSiteDisplayName = hasGraphObject ? GetString(gProp, "sharePointSiteDisplayName", string.Empty) : string.Empty;
            var sharePointSiteWebUrl = hasGraphObject ? GetString(gProp, "sharePointSiteWebUrl", string.Empty) : string.Empty;
            var sharePointDriveDisplayName = hasGraphObject ? GetString(gProp, "sharePointDriveDisplayName", string.Empty) : string.Empty;

            return new DiscoveryConfigDto(
                oneDriveUserId, oneDriveDriveId, oneDriveSourceFolderId, oneDriveSourceFolderPath,
                sharePointSiteId, sharePointDriveId, sharePointDestFolderId, sharePointDestFolderPath,
                migrationRoute, destinationProvider,
                oneDriveDisplayName, sharePointSiteDisplayName, sharePointSiteWebUrl, sharePointDriveDisplayName);
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
            if (update.OneDriveDisplayName is not null) g["oneDriveDisplayName"] = update.OneDriveDisplayName;
            if (update.SharePointSiteDisplayName is not null) g["sharePointSiteDisplayName"] = update.SharePointSiteDisplayName;
            if (update.SharePointSiteWebUrl is not null) g["sharePointSiteWebUrl"] = update.SharePointSiteWebUrl;
            if (update.SharePointDriveDisplayName is not null) g["sharePointDriveDisplayName"] = update.SharePointDriveDisplayName;

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
