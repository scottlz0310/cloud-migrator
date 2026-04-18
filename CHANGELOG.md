# Changelog

このファイルは [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に基づき、
[セマンティックバージョニング](https://semver.org/lang/ja/) に準拠しています。

---

## [Unreleased]

### Added

- **v0.6.0 AIMD フィードバック制御 + クールダウン（評価ロジック）** (#162)
  - `AimdSignal` enum（`Hold` / `EmergencyDecrease` / `SlowDecrease` / `Stable`）
  - `AimdEvaluation` record: 評価結果（信号・新旧レート・ベースライン P95・クールダウン状態・スナップショット）
  - `LatencyEvaluationMode` enum（`Baseline` / `Recent` / `Both`）: レイテンシ悪化判定モード。デフォルト `Baseline`
  - `IAimdFeedbackController` / `AimdFeedbackController`: スライディングウィンドウ指標を入力に 4 信号を判定し、`[MinRate, MaxRate]` でクランプした新レートを返す純粋クラス。副作用なし（`WeightedTokenBucket.SetRate` 反映・制御ループ駆動は #163）
  - ベースライン EMA（安定期のみ更新・悪化中は凍結）とクールダウン（急減速後 `CooldownSec` 秒 `Stable` 抑制、`EmergencyDecrease` / `SlowDecrease` は通常動作）
  - `AimdFeedbackSettings` + `FromRateControlSettings()` コピーヘルパー
  - `MigratorOptions.RateControl` に AIMD パラメーターを追加: `AimdEmergencyThreshold` / `EmergencyDecay` / `SlowDecay` / `AddStep` / `LatencyRiseRatio` / `BaselineSamples` / `BaselineEmaAlpha` / `TrendWindowSec` / `StableWindowSec` / `CooldownSec` / `LatencyEvaluationMode`
  - ユニットテスト 24 件追加（`AimdFeedbackControllerTests`）

- **v0.6.0 スライディングウィンドウ指標収集** (#161)
  - `ISlidingWindowMetrics` / `SlidingWindowMetrics`: 時間ベース / 件数ベース両対応のウィンドウ集計
  - `SlidingWindowSnapshot`: サンプル数・`HasMinSamples`・429 率・成功率・平均/P95 レイテンシを提供
  - `SlidingWindowMode` enum（`Time` / `Count`）
  - `Stopwatch.GetTimestamp()` による monotonic clock 時間判定、時間モードに安全上限（デフォルト 100,000 件）
  - P95 は線形補間（NIST Method 3 / Excel PERCENTILE.INC 相当）
  - `MigratorOptions.RateControl` に `WindowMode` / `WindowSec` / `MaxWindowCount` / `MinSamples` を追加
  - ユニットテスト 24 件追加（`SlidingWindowMetricsTests`）

- **v0.6.0 スループット主制御基盤: トークンバケット + 重み付きコスト** (#160)
  - `FileCostCalculator`: 離散モード（小/中/大 3 区分）/ 連続モード（`cost = ceil(size / scaleBytes)` + clamp）両対応の重み付きコスト算出クラス
  - `WeightedTokenBucket`: `tokens/sec` ベースのトークンバケット。`Stopwatch.GetTimestamp()` monotonic clock で補充し、`maxBurst` でクランプ。`SetRate` / `SetMaxBurst` による AIMD 動的更新（#162 先行）対応。残量不足時は `sleep + retry`（ビジーウェイトなし）で待機
  - `MigratorOptions.RateControl` に v0.6.0 トークンバケット設定を追加: `InitialTokensPerSec` / `MaxBurstTokens` / `MinTokensPerSec` / `MaxTokensPerSec` / `CostMode` / `SmallFileCost` / `MediumFileCost` / `LargeFileCost` / `CostScaleBytes` / `MinCost` / `MaxCost`
  - ユニットテスト 42 件追加（`FileCostCalculatorTests` / `WeightedTokenBucketTests`）

- **Graph API レートベース転送制御エンジン** (PR #148)
  - `ITransferRateController`: 転送レート制御の統一インターフェース（`AcquireAsync` / `NotifyCompleted` / `NotifyRateLimitHit`）
  - `IMetricsAggregator`: メトリクス集計の抽象（Aggregator 層）
  - `TransferMetricsAggregator`: 1 秒バケット固定リングバッファ（300 スロット = 5 分）で転送イベントを O(window秒) で集計
  - `MetricsBuffer`: DB 書き込み用非同期バッファ（フラッシュ間隔設定可能、`IAsyncDisposable`）
  - `RateControlledTransferController`: ヒステリシス 3 段階制御（緊急減速 / 緩減速 / 加速）+ トークンバケット方式（`IAsyncDisposable`）
  - `AdaptiveConcurrencyControllerAdapter`: 既存 `AdaptiveConcurrencyController` を `ITransferRateController` として使用するアダプター（後方互換）
  - `RateControlSettings`: 制御パラメーター（InitialRate / MinRate / MaxConcurrency / ヒステリシス閾値 等）
  - `MigratorOptions.RateControl`: CLI / JSON 設定統合（`UseRateControl` フラグ + `RateControlSettings`）
  - `TransferCommand`: `UseRateControl=true` 時に `RateControlledTransferController` を自動構築・DI 注入
  - `SharePointMigrationPipeline`: Phase C フォルダ先行作成ループの例外時インフライトカウンター回復（`catch(Exception)` → `controller?.NotifyCompleted(TimeSpan.Zero)`）

### Changed

- `AdaptiveConcurrencyController` に `[Obsolete]` を付与（v0.5.0 以降は `RateControlledTransferController` 推奨）
- `MigratorOptions.PenaltyWeight` / `LatencyWeight` に `[Obsolete]` を付与（`RateControlledTransferController` では未使用）
- `CliServices.cs`: 常に null だった `_rateController` / `_rateControllerMetricsBuffer` フィールドを削除し実態と整合

### Fixed

- `TransferMetricsAggregator.GetSnapshot()` の `Rate429` 計算式を `rateLimitCount / requestCount` に修正（旧: `rateLimitCount / (requestCount + rateLimitCount)`）
- `RateControlledTransferController.DisposeAsync()` に `_disposed` ガードを追加しべき等性を保証

- **Dashboard: DriveFolderPicker に新規フォルダ名入力欄を追加**
  - ピッカー内に「新しいフォルダ名」テキストフィールドと「このパスに設定」ボタンを追加。
  - 現在のナビゲーション階層に基づいたパスを生成し、転送パイプラインの自動作成機能を利用して転送時に作成される。
  - フォルダ一覧の各行にフォルダ名サジェストボタンを追加（既存フォルダ名をワンクリックで入力欄に反映）。

- **Dashboard: 設定ページに転送ルート情報カードを追加**
  - `SettingsPage.razor` にウィザード設定済みの転送元フォルダ・転送先フォルダ・移行ルートを読み取り専用で表示。
  - ウィザード未完了の場合は「ウィザードが未完了です」メッセージを表示。

### Changed

- **Dashboard: SettingsPage から転送先ルートパス・転送先プロバイダーを削除**
  - これらはウィザードで設定されるため、設定画面には不要。
  - UI のノイズを削減し、ウィザード設定との重複を排除。
- **Dashboard: SettingsPage の上級者設定を折りたたみパネルに移動**
  - 最大並行数・チャンクサイズ・タイムアウト等を `MudExpansionPanel`「⚙️ 上級者向け設定」に収納。
  - 通常ユーザーへの設定画面の見通しを改善。

### Fixed

- **Dashboard: 設定変更時にフルリビルドが発動しない問題を修正**
  - `App.xaml.cs` の `MigrationWork` が `ConfigHashChecker` を呼ばずに直接パイプラインを起動しており、
    Dashboard から設定を変更しても次回転送でフルリビルドにならない問題があった。
  - CLI の `TransferCommand` と同等のハッシュ確認ロジック（`ComputeHash` → `HasChangedAsync` → `ClearAll` → `ResetAllAsync`）を追加。

- **CI: MSI ビルド回帰防止ジョブを追加** (#141)
  - `publish-win` ジョブ（PR 時のみ）: win-x64 バイナリをパブリッシュしアーティファクト保存。
  - `msi-check` ジョブ（PR 時のみ）: WiX 5.0.2 で `Product.wxs` をビルド検証し `msi-ci` アーティファクトを保存（7 日保持）。
  - 両ジョブに `if: github.event_name == 'pull_request'` を付与し push 時の不要実行を防止。
  - `dotnet-version` を `build-test` に合わせ `"10.0"` に統一。

### Fixed

- **MSI ビルド: WiX 名前空間を `v4/wxs` に修正** (#139)
  - `installer/wix/Product.wxs` の `xmlns` が `http://wixtoolset.org/schemas/v5/wxs` となっており、WiX 5.0.2 ビルド時に `WIX0199` エラーが発生していた。
  - WiX ツールセットはバージョン 4/5 ともに `http://wixtoolset.org/schemas/v4/wxs` 名前空間を使用するため、`v4/wxs` に修正。

- **MSI ビルド: `Files/Exclude` を DepsDir アプローチに置換** (#140)
  - WiX 5.0.2 は `Files` 要素の `Exclude` 属性を未サポート（`WIX0004`）。
  - CI ステップで `BinDir` から `*.exe` / `*.pdb` を除いた `DepsDir` を作成し `DashboardComponents` に参照させる方式に変更。
  - ローカル検証スクリプト `tools/Test-WixBuild.ps1` を追加（CI を再現してタグプッシュ前に検証可能）。

---

## [0.4.0] - 2026-04-14

### Added

- **セキュアクレデンシャルストア（Windows Credential Manager）** (Issue #109, PR #115)
  - `ICredentialStore` / `WindowsCredentialStore`: P/Invoke で Credential Manager R/W/Delete
  - `EnvironmentCredentialStore`: 後方互換フォールバック（v0.4.x のみ・`[Obsolete]` 付与）
  - `CredentialKeys.cs`: キー名定数（`cloud-migrator/azure/*` / `cloud-migrator/dropbox/*`）
  - 非 Windows 環境で起動中断・明示的エラーメッセージ表示

- **CloudMigrator.Dashboard を Blazor Hybrid（WPF + BlazorWebView）に移行** (Issue #114, PR #116)
  - WPF + BlazorWebView + MudBlazor による完全インプロセス DI 構成
  - `DashboardServer.cs`（ASP.NET Core HTTP レイヤー）を削除
  - SSE ログストリームを `Channel` ベースのインプロセス配信に置き換え
  - アプリ起動時に WPF ネイティブウィンドウが自動表示（ブラウザ手動起動不要）
  - `IDialogService` / `LogsPage.razor` / `SettingsPage.razor` を MudBlazor コンポーネントとして実装
  - WebView2 Evergreen Bootstrapper を MSI ビルドに統合

- **Dropbox OAuth 2.0 PKCE フロー** (Issue #112, PR #120)
  - `IDropboxOAuthService` / `DropboxOAuthService`: PKCE（`code_challenge_method=S256`）+ 固定ポート `54321–54325` + ポート競合フォールバック
  - `DropboxTokenResult` / `DropboxRefreshResult`: OAuth 結果レコード
  - `DropboxOAuthException`: `ErrorCode` プロパティ付き認証例外（`IsTokenExpired` も維持）
  - `DropboxStorageProvider` Credential Store コンストラクタ: `ICredentialStore` + `IDropboxOAuthService` を受け取り、保存済みトークンを自動ロード
  - `EnsureAccessTokenAsync`: Credential Store からの遅延ロードと検証（非同期化）
  - `RefreshAccessTokenAsync` を `IDropboxOAuthService` パス対応に拡張（リフレッシュ後トークンをストアへ保存、失効時は削除して `DropboxOAuthException` を送出）
  - `DownloadStreamAsync` を async に変換
  - `DropboxOAuthServiceTests`: `RefreshTokenAsync` / Credential Store パスのユニットテスト 14 件

- **オンボーディングウィザード UI** (Issue #113, PR #121 / #124 / #125 / #127 / #128 / #131 / #132)
  - `WizardStepState` 列挙型: `NotStarted` / `InProgress` / `Verified` / `Failed` / `Skipped`
  - `WizardRoute` 列挙型: `None` / `OneDriveToDropbox` / `OneDriveToSharePoint`
  - `WizardState`: `wizard-state.json` 永続化モデル（`ToSafeForPersistence` で InProgress→NotStarted 変換）
  - `IWizardStateService` / `WizardStateService`: ファイル読み書き・初期化・バックアップ・リセット
  - `AppDataPaths.WizardStateFile()`: `%APPDATA%\CloudMigrator\wizard-state.json`
  - `IDropboxVerifyService` / `DropboxVerifyService`: Credential / Discovery / Preflight 3 層検証
  - Blazor Wizard コンポーネント群:
    - `WelcomePage.razor`: 起動時の Welcome 画面
    - `RouteSelectionPage.razor`: Step 0 移行路線選択
    - `AzureSetupPage.razor`: Step 1 Azure Entra ID 認証設定ガイド（管理者同意 URL 生成含む）
    - `DriveDiscoveryPage.razor`: Step 2a OneDrive Drive ID 取得（UPN 入力→API 取得）
    - `SharePointDiscoveryPage.razor`: Step 2b SharePoint Site 検索 → Library 選択（表示名付き・URL 直接入力フォールバック）
    - `DropboxOAuthPage.razor`: Step 3 Dropbox OAuth 連携（App Console 手順ガイド D-1〜D-6）
    - `ConnectionTestPage.razor`: Step 4 接続テスト（両路線対応・3 層 Verify・失敗層の特定表示）
    - `WizardApp.razor`: ステップルーティング・状態管理コンテナ（中断再開ロジック含む）
  - `DashboardApp.razor`: 初回起動検出・「セットアップをやり直す」メニューエントリ

- **Azure Entra ID アプリ登録 & API 権限設定ガイド UI** (Issue #110, PR #124)
  - ウィザード Step 1 のサブステップ（1-1〜1-6）ガイドパネル
  - 管理者同意 URL 生成・クリップボードコピー（ケース A/B 分岐）
  - `clientSecretExpiry` を `config.json` に保存・30 日前警告表示

- **Graph API リソース発見（OneDrive + SharePoint）** (Issue #111, PR #125 / #128)
  - `GraphDiscoveryService`: `GET /users/{userId}/drive` による OneDrive Drive ID 取得
  - SharePoint Site キーワード検索（`GET /sites?search={keyword}`）/ URL 直接入力フォールバック
  - SharePoint Document Library 一覧取得（表示名付き・`GET /sites/{siteId}/drives`）
  - Discovery Verify（`GET /drives/{driveId}`）+ Migration Preflight（読み書き権限確認）
  - Discovery 結果を `config.json` に保存（`migrator.graph.*` スキーマ）

- **AdaptiveConcurrencyController 改善** (PR #133 / #134)
  - 増速制御を成功カウントから時間ベース（`IncreaseIntervalSec` 秒）に変更
  - `decreaseMultiplier` による乗算的減速（固定値削減から変更）
  - ダッシュボードに AdaptiveConcurrency 設定パネルを追加
  - ダッシュボード Settings ページに上級者向け設定セクション追加

### Changed

- Dashboard サービス層（`ITransferStateDb` 等）を `CloudMigrator.Core.Observability` へ移動 (PR #119)

### Fixed

- ウィザードのフォルダ指定が転送処理に反映されないバグを修正 (PR #132)
- v0.4.0 起動テスト UX 改善（OneDrive→SharePoint ハッピーパス）(PR #131)
- `TransferJobService` バグ修正（ロガー注入・`Cancel()`/`CurrentJob`・例外握りつぶし解消）(PR #126)
- プロバイダー正規化・クロール状態修正・RateLimit ログ追加 (PR #128)
- 起動テストで発見したバグ修正 5 件 (PR #122)

---

## [0.3.0] - 2026-04-08

### Added
- **データパス移行: `%APPDATA%\CloudMigrator\` への統一** (Issue #98)
  - 設定ファイル: `%APPDATA%\CloudMigrator\configs\config.json`
  - ログディレクトリ: `%APPDATA%\CloudMigrator\logs\`
  - 初回起動時に旧パス (`./configs/config.json`) を AppData へ自動移行
  - `setup init` / `setup bootstrap` が AppData ディレクトリを自動作成
  - Studio「ログ」タブにデータパス（設定・ログ）を表示
- **WiX MSI インストーラー** (`installer/wix/`) (Issue #100)
  - インストール先: `%LOCALAPPDATA%\Programs\CloudMigrator\`
  - ユーザーインストール（UAC 昇格不要、`Scope="perUser"`）
  - `MajorUpgrade` によるバージョンアップグレード対応（旧バージョン自動アンインストール）
  - スタートメニューショートカット
  - PATH への追加（`ADDTOPATH` プロパティでオプション化、既定: 有効）
- **GitHub Actions Release ワークフロー** (Issue #100)
  - `v*` タグ push で GitHub Release を自動生成
  - リリース成果物: `cloud-migrator-v0.3.0-win-x64.zip` / `cloud-migrator-v0.3.0-linux-x64.tar.gz` / `cloud-migrator-v0.3.0-osx-x64.tar.gz` / `cloud-migrator-v0.3.0-osx-arm64.tar.gz` / `cloud-migrator-setup.msi`
- **`global.json` 追加**: .NET SDK `10.0.201` を `latestPatch` ロールフォワードで固定 (Issue #100)
- **`Directory.Build.props` 追加**: デフォルトバージョン `0.3.0` を設定し `cloud-migrator --version` の表示を統一

### Changed
- **`usage.md` 全面更新**: バイナリ配布前提に刷新 (Issue #99)
  - MSI インストーラー手順 / ZIP・tar.gz インストール手順を追加
  - `cloud-migrator` バイナリを使ったコマンド例に統一
  - データパス（AppData）の説明・移行手順を追加

---

## [0.2.1] - 2026-04-04

### Added
- `cloud-migrator setup` サブコマンドを追加: `bootstrap` / `init` / `doctor` / `verify` を `cloud-migrator.exe` から直接実行可能に
  - `CloudMigrator.Setup.Cli` を `CloudMigrator.Cli` の依存関係に追加
  - `SetupCommand.cs` 新設（`CloudMigrator.Cli.Commands`）
  - `AssemblyInfo.cs` に `InternalsVisibleTo("cloud-migrator")` を追加
  - `SetupCommandTests.cs` 追加: サブコマンド名・件数を検証するユニットテスト 6 件

---

## [0.2.0] - 2026-04-04

### Added
- **Studio Ph-5: 引数なしデフォルト起動 + DB なしモード** (Issue #85)
  - `NullTransferStateDb` 新設（`CloudMigrator.Core.State`）: Null Object パターンで `ITransferStateDb` を実装。DB なし起動時に全読み込みメソッドは空/ゼロ値を返し、書き込みは無視する
  - `DashboardServer.RunAsync` のシグネチャ変更: `string dbPath` → `string? dbPath`（nullable）。DB ファイルが存在しない場合は `NullTransferStateDb.Instance` で代替起動
  - `DashboardServer.BuildApp` に `hasDb` パラメータ追加（デフォルト `true`）
  - `GET /api/db-status` エンドポイント追加: `{ "connected": bool, "requiresRestart": bool }` を返す。リクエスト時点で実際に DB が使用可能か確認し、DB なしモードでファイルが後から作成された場合は `requiresRestart: true` で再起動を案内
  - 監視タブに「DB 未接続」バナー追加（Alpine.js で `/api/db-status` をポーリングし、DB なし時に黄〜橙系バナーを表示）
  - `cloud-migrator`（引数なし）で Studio を起動: `--port`・`--no-browser` トップレベルオプション対応
  - `cloud-migrator dashboard` コマンドが DB なしでも起動するよう変更（`File.Exists` エラー終了を廃止）
  - 起動メッセージ改善: `「CloudMigrator Studio 起動中: http://localhost:5050」` + DB なし時は `「DB : なし — transfer 実行後、DB が作成されたら再起動してください」`
  - `DashboardServerTests.cs` に DB-less モードテスト 4 件追加: `/api/db-status` (connected=true/false) / `/api/status` 空サマリー / `/api/metrics` 空リスト


  - `DoctorCheck` record 新設（`CloudMigrator.Dashboard`）: name / status (Pass|Warning|Fail) / detail
  - `DoctorResult` record 新設（`CloudMigrator.Dashboard`）: overallStatus (Healthy|Degraded|Unhealthy) / checks
  - `DoctorOptions` record 新設: ClientId / TenantId / ClientSecret / SiteId / DriveId / DestinationRoot
  - `ISetupDoctorService` / `SetupDoctorService` 新設: Graph 認証（client_credentials フロー）・SharePoint サイト到達確認・ドキュメントライブラリ確認を各 10 秒タイムアウトで順次実行
  - `POST /api/setup/doctor` エンドポイント追加: 3 チェック結果 + overallStatus を JSON で返す
  - Alpine.js「診断」タブ追加（5 タブ構成: 監視・実行・設定・ログ・診断）
    - 「テスト実行」ボタン・スピナー表示・全体判定バナー（Healthy/Degraded/Unhealthy）
    - チェック項目ごとに ✅ Pass / ⚠️ Warning / ❌ Fail を色付きカードで表示
    - エラーメッセージをインラインボックスで表示
  - `SetupDoctorServiceTests.cs` 新設 (9 テスト): 全チェック Pass / 認証失敗・後続スキップ / 資格情報未設定 / サイト 404 / SiteId 未設定 / DriveId 未設定 / 部分 Fail / `access_token` 空 / `DestinationRoot` 正規化
  - `DashboardServerTests.cs` に `POST /api/setup/doctor` テスト 2 件追加

- **Studio Ph-3: ログストリーミング SSE + ログビューア UI** (Issue #83)
  - `LogEntry` record 新設（`CloudMigrator.Observability`）: timestamp (UTC) / level / message
  - `LogStreamSink` 新設（`CloudMigrator.Observability`）: `ILogEventSink` 実装。リングバッファ 500 件 + 複数 SSE クライアントへのブロードキャスト
  - `LoggingSetup.CreateLoggerFactory` に `logStreamSink` オプションパラメータ追加: 既存のコンソール/ファイルシンクと併用可能
  - `ILogStreamService` / `LogStreamService` 新設（`CloudMigrator.Dashboard`）: 接続時に直近バッファを初回送信し、以降リアルタイム追記
  - `GET /api/logs/stream`: SSE エンドポイント（`Content-Type: text/event-stream`）。nginx バッファリング無効化対応
  - Alpine.js「ログ」タブ追加（4 タブ構成: 監視・実行・設定・ログ）
    - `Information` / `Warning` / `Error` のログレベルフィルタリングボタン
    - 480px のスクロール可能ログコンテナ（最大 1,000 件・超過分は古い順に削除）
    - ログレベル色分け: INFO=グレー / WARN=黄 / ERROR/FATAL=赤
    - 自動スクロール: 手動スクロール時に一時停止・「最新へ」ボタンで再有効化
    - クリアボタン（表示中ログを消去、バッファはリセットしない）
  - `CloudMigrator.Dashboard.csproj` に `CloudMigrator.Observability` の ProjectReference 追加
  - `LogStreamServiceTests.cs` 新設 (7 テスト): リングバッファ・DropOldest・複数クライアントブロードキャスト・UTC タイムスタンプ
  - `DashboardServerTests.cs` に `GET /api/logs/stream` テスト 1 件追加

- **Studio Ph-2: 転送ジョブ API + 実行タブ UI** (Issue #82)
  - `JobStatus` enum 新設: `Pending` / `Running` / `Completed` / `Failed` / `Cancelled`
  - `TransferJobInfo` record 新設: jobId・status・startedAt・completedAt・errorMessage
  - `ITransferJobService` / `TransferJobService` 新設: `SemaphoreSlim(1)` による同時実行 1 本制限・インメモリジョブ管理
  - `POST /api/transfer/start`: 202 Accepted + jobId を返す。実行中は 409 Conflict
  - `GET /api/transfer/{id}`: ジョブ状態を返す。存在しない id は 404 NotFound
  - Alpine.js 「実行」タブ追加: 開始ボタン・5秒ポーリングによるステータス表示・経過時間タイマー・停止案内パネル
  - `TransferJobServiceTests.cs` (8 テスト): 排他制御・状態遷移（Completed/Failed/Cancelled）・セマフォ解放
  - `DashboardServerTests.cs` に `POST /api/transfer/start`・`GET /api/transfer/{id}` テスト 4 件追加

- **Studio Ph-1: `GET /api/config` / `PUT /api/config` エンドポイント実装** (Issue #81)
  - `ConfigurationService` 新設: `config.json` の読み書き・シークレット除外・マージ保存をカプセル化
  - `GET /api/config`: シークレットを除外した設定 JSON を返す
  - `PUT /api/config`: 指定フィールドのみマージ保存（未指定フィールドは既存値を保持）
    - シークレットキー（`secret`/`password`/`token`/`apikey` 等）を含む body は 400 で拒否
    - バリデーション: `maxParallelTransfers` (1〜100), `chunkSizeMb` (1〜100), `retryCount` (0〜20)
  - `IConfigurationService` インターフェース: テスト・拡張向けに公開
- **Studio Ph-1: Alpine.js 設定タブ UI 実装** (Issue #81)
  - ダッシュボードに「監視」「設定」2 タブを追加（Alpine.js v3.14.9 CDN）
  - 設定タブ: 推奨設定フォーム（並列数・チャンク・リトライ・タイムアウト・転送先パス）
  - DANGER セクション（折りたたみ）: 大容量閾値・転送先プロバイダ変更
  - dirty 判定・保存ボタン・成功/失敗トースト通知
  - ブランド名を「Dashboard」→「Studio」へ更新

### Changed
- `DashboardServer.BuildApp()` の第 4 引数に `ITransferJobService? jobService = null` を追加
  - `jobService` 未指定時は `TransferJobService` を自動生成・登録
  - 既存の呼び出しとの後方互換あり
- ダッシュボードタブ: 「監視」「設定」に「実行」を追加（3 タブ構成）
- `DashboardServer.BuildApp()` の第3引数に `IConfigurationService? configService = null` を追加
  - `configService` 未指定時は `ConfigurationService` を自動生成・登録（/api/config の常に安全な稼働を保証）
  - 既存の呼び出しとの後方互換あり

- **`transfer` コマンド: 失敗時の再試行確認プロンプト**
  - 転送完了後に失敗ファイルが残っている場合、対話端末では「X件の転送に失敗しています。再試行しますか？ [y/N]」を表示
  - `y` を入力すると同一パイプラインを再実行（Phase A で `permanent_failed` リセットが走るため確実に再試行）
  - 標準入力がリダイレクトされている場合（cron 等）はプロンプトを表示せずログ警告のみ出力
- **`transfer --auto-retry <N>` オプション追加**
  - 失敗ファイルを最大 N 回まで自動再試行する（対話プロンプトなし）
  - 非対話環境・自動化スクリプトでの再試行に使用。例: `transfer --auto-retry 3`
- **`ITransferStateDb.ResetPermanentFailedAsync` 追加**
  - `permanent_failed` 状態のレコードを `failed` にリセットして再試行可能にする
  - SharePoint / Dropbox 両パイプラインの Phase A で呼び出し、リセット件数をログ出力

### Fixed
- **`permanent_failed` ファイルの永久放置を修正**（`SqliteTransferStateDb` / 両パイプライン）
  - 3 回失敗した（`permanent_failed`）ファイルが以後の実行で完全に無視される問題を修正
  - 次回実行の Phase A で `failed` に戻すことで、すべての失敗ファイルが次回必ず再試行されるようになった
- **`ControllerProxy.Active` のメモリ可視性保証**（`CliServices.cs`）
  - 複数スレッドから読み書きされる `Active` フィールドに `volatile` を追加
- **`GetController` の XML コメント不一致を修正**（`CliServices.cs`）
  - 「プロファイルが存在しない場合は null」→「"default" プロファイルへフォールバック、それも存在しない場合のみ null」に正確化
- **`NotifyRateLimit` の 429 ストーム時タスク増殖を修正**（`AdaptiveConcurrencyController.cs`）
  - `for` ループで `step` 個の fire-and-forget タスクを起動していた処理を、1 つのバックグラウンドタスク内でループする `AbsorbSlotsAsync(step)` に統合
- **`MinDegree` 到達時の `_pendingDecreases` 蓄積を修正**（`AdaptiveConcurrencyController.cs`）
  - `_current == MinDegree` 時にカウンターが増え続け、回復直後の最初の通知で即減速が発火する問題を修正
  - `_current > _min` のときのみ `_pendingDecreases` をインクリメントするよう変更

---

## [0.1.0] - 2026-03-22

### Added
- **GraphStorageProvider: Graph Delta API によるネイティブページングクロール**
  - `GraphStorageProvider.ListPagedAsync` を実装（デフォルト実装から Graph Delta API へオーバーライド）
  - `GET /drives/{id}/items/{itemId}/delta?$top=200` でページ単位（200 件）のストリーミングクロールを実現
  - `cursor = null` → Delta クロール先頭から開始（`OneDriveSourceFolder` 指定時はそのフォルダ起点）
  - `cursor = @odata.nextLink` → 同一クロールの続きページを取得（Kiota `WithUrl` パターン使用）
  - `cursor = @odata.deltaLink` → クロール完了後の増分取得起点として SQLite checkpoints に保存される
  - `parentReference.Path` から `driveRootPrefix` を除去して `StorageItem.Path` を計算（URL デコード対応）
  - 削除済みアイテム（`Deleted` ファセット付き）は自動スキップ
  - プライベートヘルパー `BuildDeltaPage` で `DeltaGetResponse` → `StoragePage` への変換を集約
  - `DropboxMigrationPipeline.cs` のコメントを新実装に合わせて更新

- **ダッシュボード: クロール確定総数の表示**
  - `DropboxMigrationPipeline` に `CrawlTotalKey = "crawl_total"` 定数追加
  - Phase B 完了直後に `GetSummaryAsync` で確定総数を取得し `crawl_total` チェックポイントに保存（`crawl_complete` フラグより先に保存）
  - `TransferDbSummary` に `CrawlTotal` プロパティ（`int?`）追加
  - `SqliteTransferStateDb.GetSummaryAsync` で `crawl_complete` / `crawl_total` を単一 IN クエリで一括取得し `CrawlTotal` を返す
  - `DashboardServer` JS: クロール完了後は `crawlTotal` を分母に使用（変動しない確定値）、クロール中は「クロール中」バッジを表示し進捗を「※クロール中のため推定」と明示
  - `DropboxMigrationPipelineTests.SetupDbBase()` に `GetSummaryAsync` モック設定を追加（ユニットテスト 246 件 PASS）

### Fixed
- **DashboardServer.cs `c-total` 二重書き込み修正**: `refreshStatus()` 内で `c-total` を旧コード `fmt(s.total)` と新コード `fmt(denominator)` の両方でセットしており、後者（正しい値）が有効だが不要な旧行を削除

- **Phase B: throughput メトリクス（スループット計測・グラフ表示）**
  - `DropboxMigrationPipeline` に `throughput_files_per_min` / `throughput_bytes_per_sec` の定期記録追加（100 回ごと、`rate_limit_pct` と同タイミング）
  - `_totalBytesTransferred`（成功転送バイト積算）・`_pipelineStartTime`（パイプライン起動時刻）フィールド追加
  - `filesPerMin` 算出の分子を `totalNow`（スナップショット）に統一し他スレッドとの不整合を解消
  - `fmtBytesPerSec` に `Number` 正規化と `NaN` フォールバック追加（Chart.js `ticks.callback` 防御）
  - `DashboardServer` HTML に「スループット（ファイル/分）」「スループット（バイト/秒）」グラフ追加（Chart.js 折れ線）
  - `refreshMetrics()` を 3 メトリクス並列取得に拡張（`rate_limit_pct` / `throughput_files_per_min` / `throughput_bytes_per_sec`）
  - メトリクス記録失敗ログのメッセージを一般化（メトリクス名に依存しない文言に変更）
  - ユニットテスト追加: `RunAsync_100Transfers_RecordsThroughputBytesPerSec`（全テスト 246 件 PASS）
- **Web ダッシュボード（`dashboard` コマンド）**
  - `CloudMigrator.Dashboard` プロジェクト追加（`src/CloudMigrator.Dashboard/`）: ASP.NET Core Minimal API + Chart.js インライン HTML
  - `ITransferStateDb` に `RecordMetricAsync` / `GetMetricsAsync` 追加（`metrics` テーブルへの時系列書き込みと読み取り）
  - `SqliteTransferStateDb` に `metrics` テーブル + インデックス作成・実装追加（`InitializeAsync` でスキーマ自動作成）
  - `MetricPoint` レコード追加（`src/CloudMigrator.Core/State/TransferSummary.cs`）: `(Timestamp, Name, Value)` の時系列データ型
  - `DropboxMigrationPipeline`: 転送試行 100 回ごとに `rate_limit_pct` を `metrics` テーブルへ記録（`RecordMetricAsync`）
  - `DashboardServer.cs` 追加: `GET /api/status`・`GET /api/metrics`・`GET /api/errors`・`GET /`（Chart.js UI）
  - `DashboardCommand.cs` 追加（`src/CloudMigrator.Cli/Commands/`）: `--db`・`--port`・`--no-browser` オプション対応
  - `Program.cs` に `dashboard` コマンド登録
  - `CloudMigrator.Cli.csproj` に `FrameworkReference Microsoft.AspNetCore.App` と `CloudMigrator.Dashboard` 参照追加
  - `CloudMigrator.slnx` に `CloudMigrator.Dashboard` 追加
  - 全テスト 240 件 PASS（既存テストへの影響なし）

### Docs
- `usage.md` に `dashboard` コマンドのセクションを追加（コマンド例・表示項目・REST API エンドポイント一覧）
  - 更新間隔を実装値（5秒）に修正、エラー件数上限を実装値（最大5件）に修正
  - `--db` 省略時は設定値を参照する旨を明記

- **`status` コマンド（Dropbox 転送ダッシュボード）**
  - `TransferDbSummary` レコード追加（`src/CloudMigrator.Core/State/TransferSummary.cs`）: ステータス別件数・完了率・完了バイト数・最近の失敗5件
  - `ITransferStateDb.GetSummaryAsync` 追加（サマリー情報を取得、DB 空時はゼロサマリー返却）
  - `SqliteTransferStateDb.GetSummaryAsync` 実装（2クエリ: 集計 + 最近の失敗）
  - `TransferStatusCommand` 追加（`src/CloudMigrator.Cli/Commands/TransferStatusCommand.cs`）: プログレスバー・完了率・ステータス別件数・最近の失敗を表示
  - `Program.cs` に `status` コマンド登録
  - `SqliteTransferStateDbTests` にユニットテスト 5 件追加（GetSummaryAsync: 空DB・混在ステータス・バイト集計・完了率・RecentFailed最大5件）
  - 全テスト 240 件 PASS
- **Dropbox最適化パイプライン（IMigrationPipeline + SQLite 状態管理）**
  - `IMigrationPipeline` インターフェース追加（`src/CloudMigrator.Core/Migration/`）
  - `TransferRecord` / `TransferStatus` 追加（`src/CloudMigrator.Core/State/`）
  - `ITransferStateDb` インターフェース追加（クラッシュリカバリ・チェックポイント・ストリーム取得）
  - `SqliteTransferStateDb` 実装追加（`Microsoft.Data.Sqlite v9.0.5`、WAL モード、MaxRetry=3 で automatic permanent_failed 遷移）
  - `DropboxMigrationPipeline` 追加：2 フェーズ Producer（Phase A: DBリカバリ / Phase B: ストリーミングクロール）+ Bounded Channel + `Parallel.ForEachAsync` Consumer
  - `SharePointMigrationPipeline` 追加（既存 `TransferEngine` の薄いラッパー）
  - `IStorageProvider.ListPagedAsync` デフォルト実装追加（`ListItemsAsync` のラッパー）
  - `DropboxStorageProvider.ListPagedAsync` オーバーライド追加（Dropbox ネイティブページング: `files/list_folder` / `files/list_folder/continue`）
  - `MigratorOptions.PathOptions.DropboxStateDb` 追加（デフォルト: `logs/dropbox_transfer_state.db`）
  - `configs/config.json` に `dropboxStateDb` キー追加
  - `TransferCommand`: Dropbox 判定→`RunDropboxPipelineAsync` 分岐追加（SQLite DB リセット対応含む）
  - ユニットテスト追加: `SqliteTransferStateDbTests`（11件）/ `DropboxMigrationPipelineTests`（10件）
  - 全テスト 233 件 PASS
- **`DropboxApiException` 型付き例外を追加（PR #52 レビュー対応）**
  - `CloudMigrator.Providers.Dropbox.DropboxApiException : HttpRequestException` 追加（`StatusCode` / `RetryAfter` / `ResponseBody` を保持）
  - `ThrowDropboxErrorAsync`: `InvalidOperationException` → `DropboxApiException` に変更（`Retry-After` ヘッダーも解析）
  - `DropboxStorageProvider`: `catch (InvalidOperationException)` → `catch (DropboxApiException)` に変更（`path/not_found` 判定を `ResponseBody` で実施）
- **`setup verify` コマンドの Dropbox OAuth2 リフレッシュトークン対応**
  - `VerifyCommand.BuildPreflightErrors()`: `hasDropboxRefresh` パラメータ追加。アクセストークン未設定でもリフレッシュ資格情報（`REFRESHTOKEN` / `CLIENTID` / `CLIENTSECRET`）が揃っている場合はエラーを報告しない
- **EnsureFolderAsync Feature Flag・観測性メトリクス追加（品質底上げ）**
  - `DropboxProviderOptions.EnableEnsureFolder` プロパティ追加（デフォルト: `false`）。Dropbox はアップロード時に親フォルダを自動作成するため、デフォルト無効
  - `DropboxMigrationPipeline.TransferItemAsync`: `EnableEnsureFolder` フラグが `false` の場合は `EnsureFolderAsync` を呼ばずに転送性能を最適化
  - `DropboxMigrationPipeline` に観測性カウンタ追加: `_ensureFolderCallCount` / `_totalTransferAttempts` / `_rateLimitHitCount`
  - 移行完了ログにメトリクスを追加出力（フォルダAPI 呼び出し回数 / 転送試行数 / 429発生回数・発生率）
  - `DropboxMigrationPipeline` クラスコメントに「空フォルダ非転送」「EnsureFolder 非推奨」の設計仕様を明記
  - ユニットテスト +2 件（`EnableEnsureFolderFalse_DoesNotCallEnsureFolder` / `EnableEnsureFolderTrue_CallsEnsureFolder`）・全テスト 235 件 PASS
  - `VerifyCommand.ProbeDropboxAsync()`: リフレッシュ資格情報を引数で受け取り、アクセストークンが空の場合に事前取得、401 レスポンス時に自動リフレッシュ + 再試行する実装を追加
  - `usage.md`: 「4.1 Dropbox 認証情報の取得」セクション追加（`Get-DropboxToken.ps1` ウィザード手順・手動手順）
- **Dropbox OAuth2 アクセストークン自動リフレッシュ（feat/dropbox-oauth2-auto-refresh）**
  - `DropboxStorageProvider`: リフレッシュトークン資格情報（`refreshToken` / `clientId` / `clientSecret`）をオプション引数として受け取る実装を追加
  - `SemaphoreSlim(1,1)` で保護された `RefreshAccessTokenAsync()` を実装。複数並列ワーカーが同時 401 を受けても 1 回だけトークン更新を行う
  - `SendWithRetryAsync()`: アクセストークンが空の場合の事前リフレッシュ、401 検出時の自動リフレッシュ + 再試行フローを追加
  - `EnsureAccessTokenConfigured()`: リフレッシュ資格情報が揃っていればアクセストークン未設定でも起動可能に変更
  - `AppConfiguration`: `GetDropboxRefreshToken()` / `GetDropboxClientId()` / `GetDropboxClientSecret()` getter 追加
  - `CliServices`: `DropboxStorageProvider` コンストラクタにリフレッシュ資格情報を渡すよう更新
  - `sample.env`: `MIGRATOR__DROPBOX__REFRESHTOKEN` / `MIGRATOR__DROPBOX__CLIENTID` / `MIGRATOR__DROPBOX__CLIENTSECRET` を追加

### Fixed
- **Dropbox `too_many_write_operations` (429) クラッシュ修正**
  - `configs/config.json`: `maxParallelFolderCreations` を `8 → 1` に変更（フォルダ作成を完全直列化）
  - `configs/config.json`: `retryCount` を `6 → 8` に増加
  - `DropboxStorageProvider.GetRetryDelayAsync()`: `too_many_write_operations` エラーボディ検出時に 30 秒待機を追加

### Added
- **OneDrive → Dropbox 転送先対応（PR #40）**
  - `IStorageProvider` に `DownloadToTempAsync` / `UploadFromLocalAsync` を追加しクロスプロバイダー転送を実現
  - `GraphStorageProvider`: `DownloadToTempAsync`（Graph API 経由でローカル一時ファイルへ）/ `UploadFromLocalAsync` 実装
  - `DropboxStorageProvider`: `UploadFromLocalAsync`（ローカルファイルから Dropbox へアップロード）実装
  - `TransferEngine`: `_sourceProvider` フィールドを追加。`UploadItemAsync` でソース DL → デスト UL → 一時ファイル削除フローを実装
  - `MigratorOptions`: `DestinationProvider` フィールド追加（デフォルト: `"sharepoint"`）
  - `CliServices` / `TransferCommand`: Dropbox 転送先ルーティング + `RebuildSkipListFromDropboxAsync` 追加
  - `DoctorCommand`: `DestinationProvider=dropbox` 時に SP フィールドを `[WRN]` に格下げ（`SpCheck` ローカル関数）
  - `BootstrapCommand`: `--destination dropbox` オプション追加。Dropbox AccessToken / RootPath プロンプト、SP 解決スキップ、`.env` への Dropbox token 書き込み対応
  - `InitCommand`: `ApplyDropboxValuesToConfigTemplate` メソッド追加
  - `docs/manual-test-runbook.md`: セクション 10「OneDrive→Dropbox 転送テストシナリオ」を追記（TC-Dropbox-01〜07 + 実施記録テンプレート）
  - テスト: 193 件全パス（+5 件追加: DoctorCommand 2件 / InitCommand 3件）

- **`MigratorOptions`: `MaxParallelFolderCreations` プロパティ追加（デフォルト 4）（PR #38）**
  - フォルダ先行作成フェーズとファイル転送フェーズの並列度を独立して制御可能に
  - `configs/config.json` の `maxParallelFolderCreations` に任意の値を設定可能
- **フォルダ先行作成の並列化・GET優先・スキップキャッシュ（PR #36）**
  - `TransferEngine.RunAsync`: フォルダパスを深さ別グループで並列作成（FR-06 強化）
    - `folderPathSet.GroupBy(深さ).OrderBy(深さ)` でグループ化し `Parallel.ForEachAsync` で並列処理
    - `destRootNormalized` を `folderPathSet` に追加して深さ順ループで先行処理を保証
  - `GraphStorageProvider.EnsureFolderSegmentAsync`: GET-first + 409 競合フォールバック実装
    - `catch(ApiException) { if (status != 409) throw; ... }` パターンで Ubuntu/macOS CI 安定化
  - テスト: `FakeStorageProvider` による呼び出し順序のプラットフォーム非依存な検証（3件追加）

### Fixed
- **フォルダ先行作成フェーズの Graph API クォータ枯渇クラッシュ（PR #38）**
  - フォルダ作成ループが `MaxParallelTransfers`（デフォルト 4、config: 32）を流用していたため
    大量フォルダ構成で HTTP 429 TooManyRequests を即時枯渇させてクラッシュする問題を修正
  - `TransferEngine` のフォルダ作成ループを `MaxParallelFolderCreations` に切り替え

---
  - `UploadUrl` のみで `UploadSession` を構築すると `NextExpectedRanges` が `null` になり、
    `LargeFileUploadTask<DriveItem>` コンストラクター内の `GetRangesRemaining()` が NPE を投げる問題を修正（cv2.pyd 等の大容量ファイルで実測発生）
  - 復元時に `NextExpectedRanges = new List<string> { "0-" }` を初期値として設定し、セッション再開時の NPE を解消
- **`RateLimiterOptions` デフォルト値を実稼働ログに基づき最適化**（16,831 件移行 / 3 回起動分のログ解析）
  - `InitialRequestsPerSec` 4.0 → **7.0**（観測ウォームアップ下限値: 7.0 で安定稼働）
  - `BurstCapacity` 6 → **4**（BurstCapacity=6 で ThrottledRequest 3 件発生、4 まで削減で回避）
  - `IncreaseStep` 0.2 → **0.5**（AIMD 鋸歯状サイクルの収束速度を改善）

### Added
- **`TransferEngine.RunAsync`: 転送完了サマリーログにレート情報を追加**
  - レートリミッター有効時: 既存「成功 / 失敗 / スキップ」に「最終レート: {Rate:F1}/{Max:F1} file/sec」を付記
- **`CliServices`: レート状態の永続化（`logs/rate_state.json`）を実装**
  - 起動時: `rate_state.json` が存在すれば前回終了レートを `initialRate` として復元（コールドスタートによる低速ウォームアップを排除）
  - 終了時: `Dispose()` 内で現在レートを JSON 保存（UTC 日時付き）
  - 保存値が `MinRequestsPerSec`〜`MaxRequestsPerSec` 範囲外の場合はデフォルトにフォールバック

---

### Fixed
- **E2E 手動テスト中に発見・修正（2026-03-13）**
  - `FindFolderIdAsync`: SharePoint の `Children` エンドポイントは `$filter` を非サポート（"Operation not supported"）。`PageIterator<DriveItem, DriveItemCollectionResponse>` + クライアント側フィルタリングに変更し、SharePoint との互換性を確保
  - `EnsureFolderAsync`: フォルダIDキャッシュ（`_folderIdCache: Dictionary<string, string>`）を追加。パスを上から解決する際に同一プレフィックスへの Graph API 呼び出しを省略し、フォルダ作成フェーズのAPI呼び出し数を O(N×depth²) から O(N) に削減

### Added
- **TransferEngine**: フォルダ先行作成フェーズにログを追加
  - フェーズ開始時（ユニークフォルダ件数表示）
  - 100件ごとの進捗（Done/Total）
  - フェーズ完了ログ
  - `GraphStorageProvider.EnsureFolderSegmentAsync`: 新規フォルダ作成成功時に Info ログを追加

- **Issue #18: OneDrive 転送元フォルダ指定（FR-02 完全実装）**
  - `GraphStorageOptions` / `MigratorOptions.GraphProviderOptions` に `OneDriveSourceFolder` プロパティを追加
  - `GraphStorageProvider` でフォルダパスを `Root.ItemWithPath()` で itemId に解決し、指定フォルダ配下のみクロール
  - 指定フォルダが存在しない場合（404/400）は `InvalidOperationException` に変換
  - スラッシュのみ（`/`、`///`）指定時はドライブルートにフォールバック
  - `CliServices.cs` へ `OneDriveSourceFolder` マッピング追加
  - `sample.env` / `DefaultEnvTemplate` に `MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER` キー追加
  - `bootstrap` ウィザードにフォルダ入力ステップ追加（省略可・`"-"` でクリア可）
  - `GraphStorageProviderTests` にテスト3件追加（フォルダ解決・404変換・スラッシュフォールバック）
- **bootstrap config.json プリフィル機能**
  - 既存 `configs/config.json` の値を次回起動時のデフォルト値として使用（env 変数優先）
  - env 由来・config.json 由来で別々に検出通知を表示
  - SharePoint SiteId+DriveId が既存の場合は「前回設定を使用しますか？」で入力スキップ可能
  - `LoadConfigJsonOptions`（JSON のみ読み込み）ヘルパー追加（例外時フォールバック対応）
  - `SetupBootstrapCommandTests` にテスト3件追加（ファイルなし・値取得・env 非汚染）
- `bootstrap` 環境変数プリフィル機能を追加
  - 起動時に `MIGRATOR__GRAPH__CLIENTID` / `MIGRATOR__GRAPH__TENANTID` / `MIGRATOR__GRAPH__CLIENTSECRET` / `MIGRATOR__GRAPH__ONEDRIVEUSERID` を自動検出
  - 設定済みの場合は Enter で現在値をそのまま使用可能（Bitwarden+dsx 等での環境変数管理をサポート）
- `bootstrap` OneDriveユーザー情報取得の403エラーを graceful fallback に変更
  - `User.Read.All` が未付与の環境でも入力UPNをそのまま使用してセットアップを継続
- `bootstrap` 対話型セットアップウィザードを `CloudMigrator.Setup.Cli` に追加
  - 認証情報・OneDriveユーザー・SharePointサイトURLを対話入力するだけでセットアップ完了
  - Graph API から候補ドライブを自動取得してインタラクティブ選択
  - `config.json` / `.env` 生成を一連のフローで統合
  - `SetupBootstrapCommandTests` を追加（8件追加）
- `CloudMigrator.Setup.Cli` プロジェクトを追加（独立セットアップCLI）
  - `doctor`: 必須設定（Graph系）と主要パスの診断
  - `init`: `config.json` / `.env` テンプレートの冪等生成（`--force` 上書き）
  - `verify`: Graph トークン取得と OneDrive / SharePoint 識別子の疎通確認
- `SetupDoctorCommandTests` / `SetupInitCommandTests` / `SetupVerifyCommandTests` を追加
- `setup init` に Graph 識別子の反映/自動解決オプションを追加
  - 直接反映: `--onedrive-user-id` / `--sharepoint-site-id` / `--sharepoint-drive-id`
  - 自動解決: `--resolve-graph-ids` + `--sharepoint-site-url` + `--sharepoint-drive-name`
  - 生成時に `config.json` と `.env` へ解決済み識別子を反映可能

### Fixed
- `doctor` コマンド単独実行時に `configs/config.json` が読み込めない問題を修正（PR #25）
  - `AppConfiguration.ResolveConfigPath()` をワーキングディレクトリ優先に変更
  - `AppContext.BaseDirectory` 遡り上限を4→6段に拡張
  - `Directory.GetParent` パターン採用で末尾セパレータ問題を解消
  - `DoctorCommand.Run()` で `resolvedConfigPath` を先に決定し `Build` と `BuildChecks` で共有
  - `BuildChecks` の `config.path` チェックを `--config-path` なし自動検出でも表示
  - `SetupDoctorCommandTests` に `resolvedConfigPath` 存在チェックのテスト3件追加

### Changed
- `CloudMigrator.slnx` に `CloudMigrator.Setup.Cli` を追加
- `CloudMigrator.Tests.Unit.csproj` に `CloudMigrator.Setup.Cli` 参照を追加
- `README.md` / `usage.md` に Setup Tool の実行手順を追記
- `README.md` / `usage.md` の `init` 手順を自動解決オプション対応へ更新

---

## 2026-03-08 開発履歴

### Added
- `ConfigHashChecker`（`CloudMigrator.Core.Configuration`）- 設定変更を SHA-256 ハッシュで検知（FR-10）
  - `ComputeHash`: ClientId / TenantId / OneDriveUserId / SharePointSiteId / SharePointDriveId / DestinationRoot を結合してハッシュ化
  - `HasChangedAsync` / `SaveHashAsync`: ハッシュファイルへの保存・比較
  - `ClearCaches` / `ClearSkipList`: 変更検知時のキャッシュ・skip_list 削除
- `MigratorOptions.DestinationRoot`（`CloudMigrator.Core.Configuration`）- SharePoint 転送先ルートパス（例: `"Migration/2026"`）
- `LoggingSetup`（`CloudMigrator.Observability`）- Serilog コンソール + ファイル（CLEF 形式、30 日ローテーション）
- `CliServices`（`CloudMigrator.Cli`）- アプリケーション依存関係ワイヤリング（`IDisposable`）
- `TransferCommand`（`CloudMigrator.Cli.Commands`）- `transfer` サブコマンド（FR-12/13）
  - 通常実行（FR-13）: ハッシュ確認 → OneDrive クロール（キャッシュ優先）→ skip_list 欠損時に SharePoint から自動再構築 → 転送実行
  - `--full-rebuild`（FR-12）: キャッシュ・skip_list をクリア → フル再クロール・再転送
- `RebuildSkipListCommand`（`CloudMigrator.Cli.Commands`）- `rebuild-skiplist` サブコマンド（FR-11）
  - SharePoint を再クロールして skip_list のみ再構築（転送なし）
- `Program.cs` エントリーポイント実装 - `System.CommandLine` 3.0-preview を使用したサブコマンド登録
- `ConfigHashCheckerTests`（`CloudMigrator.Tests.Unit`）- ConfigHashChecker の 9 ユニットテスト追加

### Changed
- `configs/config.json` に `destinationRoot` フィールド追加（デフォルト空文字）

---

## 2026-03-02 開発履歴（Dropbox基盤）

### Added
- `FileCrawlerCommand`（`CloudMigrator.Cli.Commands`）- `file-crawler` サブコマンド（FR-18）
  - `onedrive` / `sharepoint` / `dropbox` の再帰クロール + キャッシュ保存
  - `skiplist` / `compare` / `validate` / `explore` の補助運用サブコマンドを追加
- `DropboxStorageProvider`（`CloudMigrator.Providers.Dropbox`）- Dropbox 本実装
  - `ListItemsAsync`: `files/list_folder` + `list_folder/continue` による再帰クロール
  - `UploadFileAsync`: `files/download` + `files/upload` / `upload_session` による転送
  - `EnsureFolderAsync`: `files/create_folder_v2` による階層フォルダ作成（409 Conflict は既存扱い）
- `DropboxStorageOptions`（`CloudMigrator.Providers.Dropbox`）- `RootPath` / `SimpleUploadLimitMb` / `UploadChunkSizeMb`
- `MigratorOptions.Dropbox`（`CloudMigrator.Core.Configuration`）- Dropbox 設定を追加
- `PathOptions.DropboxCache`（`CloudMigrator.Core.Configuration`）- Dropbox キャッシュパスを追加
- `AppConfiguration.GetDropboxAccessToken()` - `MIGRATOR__DROPBOX__ACCESSTOKEN` を取得
- `DropboxStorageProviderTests` / `FileCrawlerCommandTests` を追加

### Changed
- `CliServices` に Dropbox プロバイダーの初期化と破棄を追加
- `Program.cs` に `file-crawler` サブコマンドを登録
- `ConfigHashChecker.ComputeHash` が `Dropbox.RootPath` 変更を検知するよう拡張
- `configs/config.json` / `sample.env` / `README.md` / `task.md` / `docs/implementation-plan.md` を Phase 7 内容に更新

---

## 2026-03-02 開発履歴（運用コマンド）

### Added
- `WatchdogCommand`（`CloudMigrator.Cli.Commands`）- `watchdog` サブコマンド（FR-16/FR-17）
  - `transfer` プロセスを起動し、ログ無更新タイムアウト（デフォルト 10 分）でフリーズを検知・自動再起動
  - フリーズ検知時はプロセスをキルして `ExitCodes.FrozenRestart = -999` で再起動シグナルを返す
  - `transfer` 正常終了（exit 0）で watchdog も停止（FR-17）
- `QualityMetricsCommand`（`CloudMigrator.Cli.Commands`）- `quality-metrics` サブコマンド（NFR-04/05）
  - `.trx` ファイル（VisualStudio TeamTest 形式）からテスト合否を集計
  - Cobertura XML から行カバレッジ率（line-rate × 100）を取得
  - カバレッジ < 60% または失敗テスト > 0 件で exit code 1（品質アラート）
  - JSON 形式でレポートを標準出力
- `SecurityScanCommand`（`CloudMigrator.Cli.Commands`）- `security-scan` サブコマンド（NFR-07）
  - `dotnet list package --vulnerable` を実行して脆弱パッケージを検出
  - 脆弱パッケージが 1 件以上あれば exit code 1
  - JSON 形式でセキュリティレポートを標準出力
- `WatchdogOptions`（`CloudMigrator.Core.Configuration.MigratorOptions`）- watchdog 設定（TimeoutMinutes / PollIntervalSeconds / TransferArgs）
- `WatchdogCommandTests`（`CloudMigrator.Tests.Unit`）- 4 ユニットテスト追加
- `QualityMetricsCommandTests`（`CloudMigrator.Tests.Unit`）- 6 ユニットテスト追加
- `SecurityScanCommandTests`（`CloudMigrator.Tests.Unit`）- 4 ユニットテスト追加
- `AssemblyInfo.cs`（`CloudMigrator.Cli`）- `[InternalsVisibleTo("CloudMigrator.Tests.Unit")]` 追加

### Changed
- `.github/workflows/ci.yml` - カバレッジ収集（`--collect:"XPlat Code Coverage"`）追加、`dotnet format --verify-no-changes` チェック追加、`quality-gate` ジョブ（品質メトリクス + セキュリティスキャン）追加
- `Program.cs` - `watchdog` / `quality-metrics` / `security-scan` の 3 サブコマンドを追加登録
- `CloudMigrator.Cli.csproj` - `Microsoft.Extensions.Logging.Console` パッケージ追加
- `CloudMigrator.Tests.Unit.csproj` - `CloudMigrator.Cli` プロジェクト参照追加、`coverlet.collector` パッケージ追加
- `CloudMigrator.Tests.Integration.csproj` - `coverlet.collector` パッケージ追加

---

## 2026-03-01 開発履歴（転送エンジン）

### Added
- `TransferSummary`（`CloudMigrator.Core.Transfer`）- 転送結果サマリーレコード（Success / Failed / Skipped / Elapsed）
- `UploadSessionStore`（`CloudMigrator.Providers.Graph`）- チャンクアップロードのセッション URL を JSON ファイルへ永続化（FR-05 再開）
- `TransferEngine`（`CloudMigrator.Core.Transfer`）- 並列転送オーケストレーター（FR-04/05/06/07/08/14）
  - フォルダを転送先に先行作成（SkipKey の長さ昇順）
  - `SkipListManager` による転送済み判定・スキップ（FR-07）
  - `Channel.CreateBounded<TransferJob>` + `Parallel.ForEachAsync` で上限付き並列転送（FR-14）
  - 転送成功後に skip_list へ追加（FR-08）
  - 転送失敗時は例外をキャッチしてカウント、処理継続
- `GraphStorageProvider.SmallUploadAsync` 実装（FR-04）
  - OneDrive ドライブ ID をキャッシュ（`GetOneDriveDriveIdAsync` - 初回のみ API コール）
  - `Drives[oneDriveId].Items[id].Content.GetAsync` でダウンロード → SharePoint へ PUT
- `GraphStorageProvider.LargeUploadAsync` 実装（FR-05）
  - 一時ファイルへダウンロード（`LargeFileUploadTask` はシーク可能ストリームが必要）
  - `UploadSessionStore` によるセッション再開対応
  - `LargeFileUploadTask<DriveItem>` でチャンク送信
  - 成功時にセッション URL を削除、一時ファイルを `finally` で確実削除
- ユニットテスト追加（`TransferEngineTests` 6 ケース、計 40 ケース）

---

## 2026-03-01 開発履歴（クロール/スキップリスト）

### Added
- `CrawlCache`（`CloudMigrator.Core.Storage`）- クロール結果を JSON ファイルへキャッシュ（FR-09）
- `SkipListManager`（`CloudMigrator.Core.Storage`）- スキップリスト読み書き・排他制御（FR-07/FR-08）
  - `SemaphoreSlim`（プロセス内）+ `FileShare.None` + リトライ（プロセス間）
  - 判定キー: `StorageItem.SkipKey`（path + name の組み合わせ）
- `GraphStorageOptions` - OneDriveUserId / SharePointDriveId の注入用設定クラス（Graph プロジェクト）
- `GraphStorageProvider.ListItemsAsync` 実装
  - `rootPath="onedrive"` → OneDrive ユーザードライブを再帰クロール（FR-02）
  - `rootPath="sharepoint"` → SharePoint ドライブを再帰クロール（FR-03）
  - `PageIterator<DriveItem, DriveItemCollectionResponse>` によるページング
  - `HashSet<string>` による重複排除
- `GraphStorageProvider.EnsureFolderAsync` 実装（FR-06）
  - パスをセグメント分割し、階層順に `Items[parentId].Children.PostAsync` でフォルダ作成
  - 409 Conflict 時は既存フォルダの ID を検索して返す
- ユニットテスト追加（`CrawlCacheTests` 5 ケース、`SkipListManagerTests` 7 ケース、計 27 ケース）

### Changed
- `GraphStorageProvider` コンストラクタに `GraphStorageOptions?` パラメータ追加（省略時は空 options）
- `CloudMigrator.Core.csproj` に `Microsoft.Extensions.Logging.Abstractions` パッケージ追加

---

## 2026-03-01 開発履歴（Graphプロバイダー）

### Added
- `GraphAuthenticator`（MSAL client credentials、`IAccessTokenProvider` 実装）
- `GraphClientFactory`（`GraphServiceClient` ファクトリ、retry max 3 / timeout / rate-limit）
- `GraphStorageProvider`（`IStorageProvider` 実装）
  - 4MB 未満: small upload ルーティング（Phase 4 で実装）
  - 4MB 以上: large upload ルーティング（Phase 4 で実装）
  - `ListItemsAsync` / `EnsureFolderAsync` スタブ（Phase 3 で実装）
- ユニットテスト追加（`GraphStorageProviderTests` 7 ケース、`AbstractionTests` 6 ケース）
- `tests/unit` に `CloudMigrator.Providers.Graph` 参照を追加

### Changed
- `Microsoft.Kiota.Http.HttpClientLibrary` NuGet パッケージ追加（Graph プロジェクト）

---

## 2026-03-01 開発履歴（修正対応）

### Fixed
- `StorageItem.SkipKey`: 空パス時に先頭スラッシュが混入する問題を修正
- `TransferJob.DestinationPath/DestinationFullPath`: `TrimEnd/TrimStart('/')` で二重スラッシュを排除
- `GraphProviderOptions.ClientSecret` を設定モデルから除外し `AppConfiguration.GetGraphClientSecret()` 経由のみに変更
- `configs/config.json` から `clientSecret` キーを削除
- `sample.env` のキーを `MIGRATOR__GRAPH__*` 形式（.NET `__` 区切り規約）に統一
- CI の `pull-requests: write` 権限を削除（最小権限）
- CI の restore/build コマンドに `CloudMigrator.slnx` を明示指定
- CI のテスト実行をプロジェクト個別指定に変更（E2E を確実に除外）
- テストファイルにプレースホルダーアサーション追加、E2E に `[Trait("Category","E2E")]` 付与
- `task.md` / `CHANGELOG.md` のソリューションファイル名を `.slnx` に修正

### Changed
- `.github/copilot-instructions.md` にイテレーションサイクルとビルドコマンドを追記

---

## 2026-03-01 開発履歴（初期セットアップ拡張）

### Added
- NuGet パッケージ追加（Microsoft.Graph, System.CommandLine, Serilog, FluentAssertions, Moq 等）
- `IStorageProvider` 契約定義（`CloudMigrator.Providers.Abstractions`）
  - `StorageItem`（スキップキー: `path + name`、FR-07）
  - `TransferJob`
- 設定ローダー `AppConfiguration`（env > config.json > default、OPS-01）
- 型付き設定モデル `MigratorOptions` / `PathOptions` / `GraphProviderOptions`（OPS-03）
- `configs/config.json` テンプレート
- `sample.env` テンプレート（機密値は環境変数のみ）
- `.github/workflows/ci.yml`（matrix: ubuntu/windows/macos × .NET 10）

---

## 2026-03-01 初期構成

### Added
- ソリューション `CloudMigrator.slnx` 作成（.NET 10）
- プロジェクト構成初期化
  - `CloudMigrator.Cli` - System.CommandLine ベース CLI エントリーポイント
  - `CloudMigrator.Core` - ドメイン・ユースケース
  - `CloudMigrator.Providers.Abstractions` - `IStorageProvider` 等の契約層
  - `CloudMigrator.Providers.Graph` - Microsoft Graph 実装
  - `CloudMigrator.Providers.Dropbox` - 将来拡張用スケルトン
  - `CloudMigrator.Observability` - 構造化ログ・メトリクス
  - `CloudMigrator.Testing` - テスト共通ユーティリティ
  - `CloudMigrator.Tests.Unit` / `Integration` / `E2E` - xUnit テストプロジェクト
- プロジェクト間参照の設定
- `docs/implementation-plan.md` - 実装計画書（仕様 FR/NFR/OPS）
- `task.md` - フェーズ別タスク管理
- `README.md` - プロジェクト概要・構成・開発手順

[Unreleased]: https://github.com/scottlz0310/cloud-migrator/compare/v0.2.1...HEAD
[0.2.1]: https://github.com/scottlz0310/cloud-migrator/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/scottlz0310/cloud-migrator/releases/tag/v0.1.0
