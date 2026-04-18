using CloudMigrator.Core.Transfer;

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

    // --- レートベース転送制御設定（v0.5.0）---
    public RateControlSettings RateControl { get; set; } = new();

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

    /// <summary>並列度を回復させるまでの待機時間（秒）。0 = 即時増速可能。429 後に N 秒経過してから増速を許可する。デフォルト 60</summary>
    public int IncreaseIntervalSec { get; set; } = 60;

    /// <summary>1 回の回復で増加する並列度の幅（増速のスピード）。デフォルト 1</summary>
    public int IncreaseStep { get; set; } = 1;

    /// <summary>減速を発火するために必要な 429/503 の累積回数（減速の条件）。デフォルト 1</summary>
    public int DecreaseTriggerCount { get; set; } = 1;

    /// <summary>
    /// 429 発生時に並列度に掛ける乗数（0 より大きく 1 未満）。
    /// 例: 0.5 = 現在の並列度を半減（MinDegree 下限）。デフォルト 0.5
    /// </summary>
    public double DecreaseMultiplier { get; set; } = 0.5;
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
/// Graph API レートベース転送制御エンジンの設定（v0.5.0: <c>RateControlledTransferController</c> 用）。
/// </summary>
public sealed class RateControlSettings
{
    /// <summary>レートベース転送制御を有効にするかどうか。false の場合は旧 AdaptiveConcurrencyController を使用する。デフォルト false</summary>
    public bool UseRateControl { get; set; } = false;

    /// <summary>短期時間窓（秒）。スパイク検知・緊急制御に使用する。デフォルト 5</summary>
    public int ShortWindowSec { get; set; } = 5;

    /// <summary>中期時間窓（秒）。安定判断・レート調整のベースに使用する。デフォルト 30</summary>
    public int LongWindowSec { get; set; } = 30;

    /// <summary>緊急減速の 429 率閾値（0–1）。短期窓の 429 率がこの値を超えると緊急減速する。デフォルト 0.10（10%）</summary>
    public double EmergencyThreshold { get; set; } = 0.10;

    /// <summary>緩減速の 429 率閾値（0–1）。中期窓の 429 率がこの値を超えると緩やかに減速する。デフォルト 0.03（3%）</summary>
    public double SlowdownThreshold { get; set; } = 0.03;

    /// <summary>可変減衰の最小係数。デフォルト 0.3</summary>
    public double MinDecayFactor { get; set; } = 0.3;

    /// <summary>可変減衰の最大係数。デフォルト 0.9</summary>
    public double MaxDecayFactor { get; set; } = 0.9;

    /// <summary>
    /// 可変減衰の感度係数。<c>factor = clamp(1 - decayK * rate429, minDecay, maxDecay)</c> 計算に使用する。
    /// デフォルト 5.0（PoC 中に調整）
    /// </summary>
    public double DecayK { get; set; } = 5.0;

    /// <summary>加速時レート増加率（+accelerateRatio / サイクル）。デフォルト 0.05（+5%）</summary>
    public double AccelerateRatio { get; set; } = 0.05;

    /// <summary>並列上限（補助制御）。デフォルト 16</summary>
    public int MaxConcurrency { get; set; } = 16;

    /// <summary>dispatch 停止インフライト閾値。インフライト数がこの値を超えると dispatch を停止する。デフォルト 32（PoC 中に調整）</summary>
    public int InFlightThreshold { get; set; } = 32;

    /// <summary>スコア関数の 429 ペナルティ重み。デフォルト 1.0（PoC 中に調整）</summary>
    [System.Obsolete("PenaltyWeight は現時点では RateControlledTransferController で未使用のため、設定しても挙動に反映されません。実装反映まで互換性維持のため残置しています。")]
    public double PenaltyWeight { get; set; } = 1.0;

    /// <summary>スコア関数のレイテンシペナルティ重み。デフォルト 0.1（PoC 中に調整）</summary>
    [System.Obsolete("LatencyWeight は現時点では RateControlledTransferController で未使用のため、設定しても挙動に反映されません。実装反映まで互換性維持のため残置しています。")]
    public double LatencyWeight { get; set; } = 0.1;

    /// <summary>初期レート（req/sec）。デフォルト 7.0</summary>
    public double InitialRatePerSec { get; set; } = 7.0;

    /// <summary>レートの下限（req/sec）。デフォルト 1.0</summary>
    public double MinRatePerSec { get; set; } = 1.0;

    /// <summary>メトリクスバッファのフラッシュ間隔（秒）。デフォルト 3</summary>
    public int MetricsFlushIntervalSec { get; set; } = 3;

    // --- v0.6.0 トークンバケット設定（#160）---
    // v0.6.0 以降は tokens/sec の重み付きコスト制御を主制御とする。
    // 本設定は #160 時点ではトークンバケット単体の生成パラメーターとして使用され、
    // 既存の InitialRatePerSec / MinRatePerSec（req/sec）とは意味が異なる点に注意。

    /// <summary>トークンバケット（v0.6.0）の初期補充レート（tokens/sec）。保守的な低めの値を推奨。デフォルト 10.0</summary>
    public double InitialTokensPerSec { get; set; } = 10.0;

    /// <summary>トークンバケットの容量（最大蓄積トークン数）。補充時はこの値でクランプされる。デフォルト 100.0</summary>
    public double MaxBurstTokens { get; set; } = 100.0;

    /// <summary>トークンバケットのレート下限（tokens/sec）。実質停止を避けるため最低 1 以上を推奨。デフォルト 1.0</summary>
    public double MinTokensPerSec { get; set; } = 1.0;

    /// <summary>トークンバケットのレート上限（tokens/sec）。AIMD 増加でこの値を超えない。デフォルト 200.0</summary>
    public double MaxTokensPerSec { get; set; } = 200.0;

    /// <summary>重み付きコスト算出モード。<c>Discrete</c> or <c>Continuous</c>。デフォルト <c>Discrete</c></summary>
    public FileCostMode CostMode { get; set; } = FileCostMode.Discrete;

    /// <summary>離散モード: 小ファイル（〜1 MiB）のコスト。デフォルト 1</summary>
    public int SmallFileCost { get; set; } = 1;

    /// <summary>離散モード: 中ファイル（1〜100 MiB）のコスト。デフォルト 5</summary>
    public int MediumFileCost { get; set; } = 5;

    /// <summary>離散モード: 大ファイル（100 MiB〜）のコスト。デフォルト 20</summary>
    public int LargeFileCost { get; set; } = 20;

    /// <summary>連続モードのスケール係数（バイト）。<c>cost = ceil(size / scaleBytes)</c>。デフォルト 10,000,000（10 MB）</summary>
    public long CostScaleBytes { get; set; } = 10_000_000L;

    /// <summary>連続モードの下限コスト。デフォルト 1</summary>
    public int MinCost { get; set; } = 1;

    /// <summary>連続モードの上限コスト。デフォルト 50</summary>
    public int MaxCost { get; set; } = 50;
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
