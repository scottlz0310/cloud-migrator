# Changelog

このファイルは [Keep a Changelog](https://keepachangelog.com/ja/1.1.0/) に基づき、
[セマンティックバージョニング](https://semver.org/lang/ja/) に準拠しています。

---

## [Unreleased]

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
- ソリューション `CloudMigrator.sln` 作成（.NET 10）
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

[Unreleased]: https://github.com/scottlz0310/cloud-migrator/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/scottlz0310/cloud-migrator/releases/tag/v0.1.0
