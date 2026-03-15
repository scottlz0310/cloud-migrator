# Changelog

このファイルは [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に基づき、
[セマンティックバージョニング](https://semver.org/lang/ja/) に準拠しています。

---

## [Unreleased]

### Added
- **フォルダ先行作成の並列化・GET優先・スキップキャッシュ（PR #36）**
  - `TransferEngine.RunAsync`: フォルダパスを深さ別グループで並列作成（FR-06 強化）
    - `folderPathSet.GroupBy(深さ).OrderBy(深さ)` でグループ化し `Parallel.ForEachAsync` で並列処理
    - `destRootNormalized` を `folderPathSet` に追加して深さ順ループで先行処理を保証
  - `GraphStorageProvider.EnsureFolderSegmentAsync`: GET-first + 409 競合フォールバック実装
    - `catch(ApiException) { if (status != 409) throw; ... }` パターンで Ubuntu/macOS CI 安定化
  - テスト: `FakeStorageProvider` による呼び出し順序のプラットフォーム非依存な検証（3件追加）

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

## [0.8.0] - 2026-03-02

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

## [0.7.0] - 2026-03-02

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

## [0.6.0] - 2026-03-08

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

## [0.5.0] - 2026-03-01

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

## [0.4.0] - 2026-03-01

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

## [0.3.0] - 2026-03-01

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

## [0.2.1] - 2026-03-01

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

## [0.2.0] - 2026-03-01

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

## [0.1.0] - 2026-03-01

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

[Unreleased]: https://github.com/scottlz0310/cloud-migrator/compare/v0.8.0...HEAD
[0.8.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.7.0...v0.8.0
[0.7.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/scottlz0310/cloud-migrator/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/scottlz0310/cloud-migrator/releases/tag/v0.1.0
