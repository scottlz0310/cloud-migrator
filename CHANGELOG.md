# Changelog

このファイルは [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に基づき、
[セマンティックバージョニング](https://semver.org/lang/ja/) に準拠しています。

---

## [Unreleased]

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

[Unreleased]: https://github.com/scottlz0310/cloud-migrator/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/scottlz0310/cloud-migrator/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/scottlz0310/cloud-migrator/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/scottlz0310/cloud-migrator/releases/tag/v0.1.0
