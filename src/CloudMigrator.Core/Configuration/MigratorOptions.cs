namespace CloudMigrator.Core.Configuration;

/// <summary>
/// アプリケーション設定値の型付きモデル（OPS-03）
/// </summary>
public sealed class MigratorOptions
{
    public const string SectionName = "migrator";

    // --- 転送設定 ---
    /// <summary>チャンクサイズ（MB）。デフォルト 5MB</summary>
    public int ChunkSizeMb { get; set; } = 5;

    /// <summary>チャンクアップロードに切り替えるファイルサイズ閾値（MB）。デフォルト 4MB（FR-04/FR-05）</summary>
    public int LargeFileThresholdMb { get; set; } = 4;

    /// <summary>最大並列転送数。デフォルト 4（FR-14）</summary>
    public int MaxParallelTransfers { get; set; } = 4;

    /// <summary>リトライ回数。デフォルト 3（FR-15）</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>HTTP タイムアウト（秒）。デフォルト 300</summary>
    public int TimeoutSec { get; set; } = 300;

    // --- パス設定（OPS-04）---
    public PathOptions Paths { get; set; } = new();

    // --- プロバイダー設定 ---
    public GraphProviderOptions Graph { get; set; } = new();
}

public sealed class PathOptions
{
    public string SkipList { get; set; } = "logs/skip_list.json";
    public string OneDriveCache { get; set; } = "logs/onedrive_files.json";
    public string SharePointCache { get; set; } = "logs/sharepoint_current_files.json";
    public string TransferLog { get; set; } = "logs/transfer.log";
    public string ConfigHash { get; set; } = "logs/config_hash.txt";
}

/// <summary>
/// Microsoft Graph プロバイダー固有の設定（OPS-02）。
/// 機密値（ClientSecret）は環境変数から取得し、config.json には含めない。
/// </summary>
public sealed class GraphProviderOptions
{
    /// <summary>Azure AD アプリケーション（クライアント）ID。環境変数 GRAPH_CLIENT_ID でオーバーライド可</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Azure AD テナント ID。環境変数 GRAPH_TENANT_ID でオーバーライド可</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>クライアントシークレット。必ず環境変数 GRAPH_CLIENT_SECRET から取得すること（config.json への記載禁止）</summary>
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>OneDrive ユーザー ID または UPN</summary>
    public string OneDriveUserId { get; set; } = string.Empty;

    /// <summary>SharePoint サイト ID</summary>
    public string SharePointSiteId { get; set; } = string.Empty;

    /// <summary>SharePoint ドキュメントライブラリ ID</summary>
    public string SharePointDriveId { get; set; } = string.Empty;
}
