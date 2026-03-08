# Changelog

このファイルは [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に基づき、
[セマンティックバージョニング](https://semver.org/lang/ja/) に準拠しています。

---

## [Unreleased]

### Added
- `CloudMigrator.Setup.Cli` プロジェクトを追加（独立セットアップCLI）
  - `doctor`: 必須設定（Graph系）と主要パスの診断
  - `init`: `config.json` / `.env` テンプレートの冪等生成（`--force` 上書き）
  - `verify`: Graph トークン取得と OneDrive / SharePoint 識別子の疎通確認
- `SetupDoctorCommandTests` / `SetupInitCommandTests` / `SetupVerifyCommandTests` を追加
- `setup init` に Graph 識別子の反映/自動解決オプションを追加
  - 直接反映: `--onedrive-user-id` / `--sharepoint-site-id` / `--sharepoint-drive-id`
  - 自動解決: `--resolve-graph-ids` + `--sharepoint-site-url` + `--sharepoint-drive-name`
  - 生成時に `config.json` と `.env` へ解決済み識別子を反映可能

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
