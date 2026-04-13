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

    /// <summary>フォルダ先行作成フェーズの最大並列度。デフォルト 4（Graph API クォータ対策）</summary>
    public int MaxParallelFolderCreations { get; set; } = 4;

    /// <summary>リトライ回数。デフォルト 3（FR-15）</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>HTTP タイムアウト（秒）。デフォルト 300</summary>
    public int TimeoutSec { get; set; } = 300;

    // --- パス設定（OPS-04）---
    public PathOptions Paths { get; set; } = new();

    /// <summary>転送先 SharePoint ドライブ内のルートパス（例: "Migration/2026"）。空文字でドライブルート。</summary>
    public string DestinationRoot { get; set; } = string.Empty;

    /// <summary>
    /// 転送先プロバイダー識別子（"sharepoint" または "dropbox"）。デフォルト: "sharepoint"。
    /// "dropbox" の場合は <see cref="DropboxProviderOptions"/> の設定および
    /// MIGRATOR__DROPBOX__ACCESSTOKEN 環境変数が必要。
    /// </summary>
    public string DestinationProvider { get; set; } = "sharepoint";

    // --- Watchdog 設定 ---
    public WatchdogOptions Watchdog { get; set; } = new();

    // --- 動的並列度制御設定（プロファイル辞書）---
    // キー: プロバイダー名（"sharepoint", "dropbox"）または "default"。
    // GetAdaptiveConcurrency(providerName) でプロバイダー名解決 → "default" フォールバック。
    public Dictionary<string, AdaptiveConcurrencyOptions> AdaptiveConcurrency { get; set; } =
        new(StringComparer.OrdinalIgnoreCase) { ["default"] = new() };

    /// <summary>
    /// プロバイダー名に対応する <see cref="AdaptiveConcurrencyOptions"/> を返す。
    /// 一致するプロファイルがなければ "default" プロファイルを返す。どちらもなければデフォルト値を返す。
    /// </summary>
    public AdaptiveConcurrencyOptions GetAdaptiveConcurrency(string providerName) =>
        AdaptiveConcurrency.TryGetValue(providerName, out var opts) ? opts :
        AdaptiveConcurrency.TryGetValue("default", out var def) ? def :
        new AdaptiveConcurrencyOptions();

    // --- Token Bucket レートリミッター設定 ---
    public RateLimiterOptions RateLimiter { get; set; } = new();

    // --- プロバイダー設定 ---
    public GraphProviderOptions Graph { get; set; } = new();
    public DropboxProviderOptions Dropbox { get; set; } = new();

    // --- サーバーサイドコピー設定 ---
    public ServerSideCopyOptions ServerSideCopy { get; set; } = new();
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
    public string SkipList { get; set; } = AppDataPaths.LogFile("skip_list.json");
    public string OneDriveCache { get; set; } = AppDataPaths.LogFile("onedrive_files.json");
    public string SharePointCache { get; set; } = AppDataPaths.LogFile("sharepoint_current_files.json");
    public string DropboxCache { get; set; } = AppDataPaths.LogFile("dropbox_files.json");
    public string TransferLog { get; set; } = AppDataPaths.LogFile("transfer.log");
    public string ConfigHash { get; set; } = AppDataPaths.LogFile("config_hash.txt");

    /// <summary>Dropbox 移行の SQLite 状態 DB ファイルパス。</summary>
    public string DropboxStateDb { get; set; } = AppDataPaths.LogFile("dropbox_transfer_state.db");

    /// <summary>SharePoint 移行の SQLite 状態 DB ファイルパス。</summary>
    public string SharePointStateDb { get; set; } = AppDataPaths.LogFile("sharepoint_transfer_state.db");
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

    /// <summary>
    /// クライアントシークレットの有効期限（ISO 8601 文字列、例: "2027-04-11T00:00:00Z"）。
    /// ウィザード Step 1 で設定し、30 日前にダッシュボード警告を表示するために使用。
    /// </summary>
    public string ClientSecretExpiry { get; set; } = string.Empty;
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

    /// <summary>
    /// アップロード前に親フォルダを事前作成するかどうか（EnsureFolderAsync 有効化）。
    /// デフォルト: false。
    /// Dropbox はファイルアップロード時に親フォルダを自動作成するため、通常は不要です。
    /// 有効にすると files/create_folder_v2 API の呼び出し数が増加し、性能が低下します。
    /// デバッグ・互換性検証以外での有効化は非推奨です。
    /// 環境変数: MIGRATOR__DROPBOX__ENABLEENSUREFOLDER
    /// </summary>
    public bool EnableEnsureFolder { get; set; } = false;
}

/// <summary>
/// Graph API レート制限に応じた動的並列度制御の設定（プロファイルごとに指定）。
/// </summary>
public sealed class AdaptiveConcurrencyOptions
{
    /// <summary>動的並列度制御を有効にするかどうか。デフォルト false（既存の固定並列度方式を維持）</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 開始時の初期並列度。0 の場合は MaxParallelTransfers と同値（スロースタートなし）。
    /// 1 を設定すると 1 並列から徐々に増加するスロースタートになる。デフォルト 0
    /// </summary>
    public int InitialDegree { get; set; } = 0;

    /// <summary>並列度の下限。レート制限が続いてもこの値より下がらない。デフォルト 1</summary>
    public int MinDegree { get; set; } = 1;

    /// <summary>並列度を回復させるまでの待機時間（秒）。0 = 即時増速可能。429 後に N 秒経過してから増速を許可する。デフォルト 30</summary>
    public int IncreaseIntervalSec { get; set; } = 30;

    /// <summary>1 回の回復で増加する並列度の幅（増速のスピード）。デフォルト 1</summary>
    public int IncreaseStep { get; set; } = 1;

    /// <summary>1 回の減速イベントで減少する並列度の幅（減速のスピード）。デフォルト 1</summary>
    public int DecreaseStep { get; set; } = 1;

    /// <summary>減速を発火するために必要な 429/503 の累積回数（減速の条件）。デフォルト 1</summary>
    public int DecreaseTriggerCount { get; set; } = 1;
}

/// <summary>
/// Graph API サーバーサイドコピー（/copy エンドポイント）の Monitor URL ポーリング設定。
/// </summary>
public sealed class ServerSideCopyOptions
{
    /// <summary>
    /// Monitor URL 初回ポーリング前のランダムジッター上限（ミリ秒）。
    /// 並列コピー操作が一斉にポーリングするサンダリングハードを防ぐ。デフォルト 2000ms
    /// </summary>
    public int PollJitterMaxMs { get; set; } = 2000;

    /// <summary>Monitor URL ポーリングの初期待機時間（ミリ秒）。デフォルト 2000ms</summary>
    public int PollInitialDelayMs { get; set; } = 2000;

    /// <summary>Monitor URL ポーリングの最大待機時間（ミリ秒）。指数バックオフの上限。デフォルト 10000ms</summary>
    public int PollMaxDelayMs { get; set; } = 10000;

    /// <summary>
    /// サーバーサイドコピー全体のタイムアウト（秒）。
    /// この時間内に完了しなければ例外を投げてクライアント経由にフォールバックする。デフォルト 1800（30分）
    /// </summary>
    public int TimeoutSec { get; set; } = 1800;
}

/// <summary>
/// Token Bucket + AIMD によるレートリミッターの設定（FR-14 拡張: file/sec 制御）。
/// <c>AdaptiveConcurrency</c> が並列数制御であるのに対し、こちらはファイル転送発行速度を直接制御する。
/// </summary>
public sealed class RateLimiterOptions
{
    /// <summary>Token Bucket レートリミッターを有効にするかどうか。デフォルト false</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 初期レート（file/sec）。起動直後はこのレートで転送を開始する。デフォルト 7.0（実測ログから最適値）
    /// </summary>
    public double InitialRequestsPerSec { get; set; } = 7.0;

    /// <summary>
    /// レートの上限（file/sec）。AIMD 増加でこの値を超えない。デフォルト 16.0（Graph API 実測安全値）
    /// </summary>
    public double MaxRequestsPerSec { get; set; } = 16.0;

    /// <summary>
    /// レートの下限（file/sec）。AIMD 減少でこの値を下回らない。デフォルト 1.0
    /// </summary>
    public double MinRequestsPerSec { get; set; } = 1.0;

    /// <summary>
    /// バースト許容量（最大トークン数）。短時間に放出できる最大リクエスト数。デフォルト 4（BurstCapacity=6 は 429 ThrottledRequest を誘発するため削減）
    /// </summary>
    public int BurstCapacity { get; set; } = 4;

    /// <summary>
    /// AIMD 増加量（file/sec）。<see cref="IncreaseIntervalSec"/> 秒ごとに加算される。デフォルト 0.5（収束速度改善）
    /// </summary>
    public double IncreaseStep { get; set; } = 0.5;

    /// <summary>
    /// 429 時の乗算減少係数（0 &lt; factor &lt; 1）。デフォルト 0.7（俊敏性を持たせつつ適度に減速）
    /// </summary>
    public double DecreaseFactor { get; set; } = 0.7;

    /// <summary>
    /// AIMD 増加の最小間隔（秒）。この間隔で 429 が発生しなかった場合のみレートを増加する。デフォルト 5.0
    /// </summary>
    public double IncreaseIntervalSec { get; set; } = 5.0;
}
