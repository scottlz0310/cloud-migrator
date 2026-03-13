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

    /// <summary>転送先 SharePoint ドライブ内のルートパス（例: "Migration/2026"）。空文字でドライブルート。</summary>
    public string DestinationRoot { get; set; } = string.Empty;

    // --- Watchdog 設定 ---
    public WatchdogOptions Watchdog { get; set; } = new();

    // --- 動的並列度制御設定 ---
    public AdaptiveConcurrencyOptions AdaptiveConcurrency { get; set; } = new();

    // --- プロバイダー設定 ---
    public GraphProviderOptions Graph { get; set; } = new();
    public DropboxProviderOptions Dropbox { get; set; } = new();
}

/// <summary>
/// watchdog コマンドの設定（FR-16/FR-17）。
/// ログ無更新を検知して transfer プロセスを再起動する。
/// </summary>
public sealed class WatchdogOptions
{
    /// <summary>ログ無更新のタイムアウト（分）。デフォルト 10 分。</summary>
    public int TimeoutMinutes { get; set; } = 10;

    /// <summary>watchdog がポーリングする間隔（秒）。デフォルト 30 秒。</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>再起動時に実行するサブコマンド引数。デフォルト ["transfer"]。</summary>
    public string[] TransferArgs { get; set; } = ["transfer"];
}

public sealed class PathOptions
{
    public string SkipList { get; set; } = "logs/skip_list.json";
    public string OneDriveCache { get; set; } = "logs/onedrive_files.json";
    public string SharePointCache { get; set; } = "logs/sharepoint_current_files.json";
    public string DropboxCache { get; set; } = "logs/dropbox_files.json";
    public string TransferLog { get; set; } = "logs/transfer.log";
    public string ConfigHash { get; set; } = "logs/config_hash.txt";
}

/// <summary>
/// Microsoft Graph プロバイダー固有の設定（OPS-02）。
/// 機密値（ClientSecret）は環境変数から取得し、config.json には含めない。
/// </summary>
public sealed class GraphProviderOptions
{
    /// <summary>Azure AD アプリケーション（クライアント）ID。環境変数 MIGRATOR__GRAPH__CLIENTID でオーバーライド可</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Azure AD テナント ID。環境変数 MIGRATOR__GRAPH__TENANTID でオーバーライド可</summary>
    public string TenantId { get; set; } = string.Empty;

    // クライアントシークレットはこのモデルに含めない。
    // AppConfiguration.GetGraphClientSecret() で環境変数 MIGRATOR__GRAPH__CLIENTSECRET から直接取得すること。

    /// <summary>OneDrive ユーザー ID または UPN</summary>
    public string OneDriveUserId { get; set; } = string.Empty;

    /// <summary>
    /// 転送元 OneDrive のルートフォルダパス（例: "Documents/Projects"）。
    /// 空文字の場合はドライブ全体をクロールする（FR-02）。
    /// </summary>
    public string OneDriveSourceFolder { get; set; } = string.Empty;

    /// <summary>SharePoint サイト ID</summary>
    public string SharePointSiteId { get; set; } = string.Empty;

    /// <summary>SharePoint ドキュメントライブラリ ID</summary>
    public string SharePointDriveId { get; set; } = string.Empty;
}

/// <summary>
/// Dropbox プロバイダー固有の設定。
/// 機密値（AccessToken）は環境変数から取得し、config.json には含めない。
/// </summary>
public sealed class DropboxProviderOptions
{
    /// <summary>Dropbox 側のルートパス（空文字の場合は Dropbox ルート）。</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>単純アップロードの上限サイズ（MB）。超過時は upload session を使用。</summary>
    public int SimpleUploadLimitMb { get; set; } = 100;

    /// <summary>upload session のチャンクサイズ（MB）。</summary>
    public int UploadChunkSizeMb { get; set; } = 8;
}

/// <summary>
/// Graph API レート制限に応じた動的並列度制御の設定（FR-14 拡張）。
/// </summary>
public sealed class AdaptiveConcurrencyOptions
{
    /// <summary>動的並列度制御を有効にするかどうか。デフォルト true</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 並列度の下限。レート制限が続いてもこの値より下がらない。デフォルト 1
    /// </summary>
    public int MinDegree { get; set; } = 1;

    /// <summary>
    /// 並列度を 1 回復させるために必要な連続成功回数。デフォルト 10
    /// </summary>
    public int SuccessThresholdToIncrease { get; set; } = 10;
}
