# cloud-migrator

クラウドストレージプロバイダー間のファイル一括移行を自動化する CLI ツール

## 概要

OneDrive → SharePoint Online への大容量ファイル移行を自動化します。  
現行 Python 実装 ([Bulk-Migrator](../Bulk-Migrator)) を C# / .NET 10 でリライト中。

- Microsoft Graph API によるチャンクアップロード（4MB 以上対応）
- `IStorageProvider` による provider 抽象化（Graph / Dropbox 実装）
- `Parallel.ForEachAsync` + `System.Threading.Channels` による背圧制御付き並列転送
- watchdog によるフリーズ検知・自動再起動
- 構造化 JSON ログ（UTC ISO 8601）

## 技術スタック

- **言語・実行基盤**: C# / .NET 10
- **CLI**: `System.CommandLine`
- **設定**: `Microsoft.Extensions.Configuration`（環境変数 > config.json > デフォルト）
- **ログ**: `Microsoft.Extensions.Logging` + Serilog（JSON sink）
- **Graph**: Microsoft Graph SDK（`LargeFileUploadTask`）
- **テスト**: xUnit + FluentAssertions + Moq

## プロジェクト構成

```
src/
  CloudMigrator.Cli/                    # CLI エントリーポイント
  CloudMigrator.Setup.Cli/              # セットアップ支援 CLI（doctor/init/verify）
  CloudMigrator.Core/                   # ドメイン・ユースケース
  CloudMigrator.Providers.Abstractions/ # IStorageProvider 契約
  CloudMigrator.Providers.Graph/        # Microsoft Graph 実装
  CloudMigrator.Providers.Dropbox/      # Dropbox 実装
  CloudMigrator.Observability/          # 構造化ログ・メトリクス
  CloudMigrator.Testing/               # テスト共通ユーティリティ
tests/unit | integration | e2e
configs/config.json                     # ランタイム設定テンプレート
```

## 開発

```bash
# ビルド
dotnet build

# テスト
dotnet test
dotnet test tests/unit   # unit のみ

# 単一テスト絞り込み
dotnet test --filter "FullyQualifiedName~Auth"

# 補助コマンド
dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right sharepoint
dotnet run --project src/CloudMigrator.Cli -- file-crawler dropbox

# セットアップ支援コマンド
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor
dotnet run --project src/CloudMigrator.Setup.Cli -- init
dotnet run --project src/CloudMigrator.Setup.Cli -- verify
```

## ドキュメント

- [利用方法](usage.md) - 実行手順と主要コマンド
- [実装計画書](docs/implementation-plan.md) - 仕様・フェーズ・完了条件
- [タスク管理](task.md) - フェーズ別進捗
- [変更履歴](CHANGELOG.md)

## ライセンス

[LICENSE](LICENSE) を参照。
