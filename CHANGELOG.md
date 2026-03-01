# Changelog

このファイルは [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に基づき、
[セマンティックバージョニング](https://semver.org/lang/ja/) に準拠しています。

---

## [Unreleased]

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

[Unreleased]: https://github.com/scottlz0310/cloud-migrator/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/scottlz0310/cloud-migrator/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/scottlz0310/cloud-migrator/releases/tag/v0.1.0
