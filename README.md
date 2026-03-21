# cloud-migrator

OneDrive を転送元として、SharePoint Online または Dropbox へファイルを移行する .NET 10 CLI ツールです。

## 現在のステータス

- リリース方針: 初版リリースは `0.1.0`
- 実運用実績: OneDrive -> Dropbox 大容量転送フローは検証済み
- 実装状況: SharePoint 向け基本機能は実装済み、最適化フェーズは継続中

## 主な機能

- 転送先切り替え: `destinationProvider` で `sharepoint` / `dropbox` を選択
- Dropbox 最適化パイプライン:
  - SQLite 状態管理（再開・再試行・チェックポイント）
  - bounded channel + `Parallel.ForEachAsync` による並列実行
  - `status` / `dashboard` による進捗可視化
- Graph/OneDrive 連携:
  - 4MB 未満は単純アップロード
  - 4MB 以上は `LargeFileUploadTask` によるチャンク転送
  - `oneDriveSourceFolder` 指定によるサブフォルダ起点クロール
- 運用補助:
  - `watchdog` によるフリーズ検知・再起動
  - `file-crawler` によるクロール比較/検証
  - `quality-metrics` / `security-scan` による品質チェック

## アーキテクチャ

```text
src/
  CloudMigrator.Cli/                    CLI エントリーポイント
  CloudMigrator.Setup.Cli/              初期設定支援CLI（bootstrap/doctor/init/verify）
  CloudMigrator.Core/                   ドメイン・ユースケース
  CloudMigrator.Providers.Abstractions/ IStorageProvider 契約
  CloudMigrator.Providers.Graph/        Microsoft Graph 実装
  CloudMigrator.Providers.Dropbox/      Dropbox 実装
  CloudMigrator.Dashboard/              Webダッシュボード
  CloudMigrator.Observability/          構造化ログ
  CloudMigrator.Testing/                テスト共通ユーティリティ
tests/
  unit/ integration/ e2e/
```

## 前提条件

- .NET 10 SDK
- PowerShell 5.1 以上（Windows PowerShell 5.1 / PowerShell 7+。`tools/Get-DropboxToken.ps1` を使う場合）
- セットアップCLI（`bootstrap` / `init`）で生成される `configs/config.json` と環境変数

設定優先順位は以下です。

1. 環境変数
2. `configs/config.json`
3. 既定値

## クイックスタート（Dropbox 転送）

1. セットアップウィザードを実行

```bash
dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap --destination dropbox
```

2. 接続検証

```bash
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor
dotnet run --project src/CloudMigrator.Setup.Cli -- verify --skip-sharepoint
```

3. 転送開始

```bash
dotnet run --project src/CloudMigrator.Cli -- transfer
```

4. 進捗確認

```bash
dotnet run --project src/CloudMigrator.Cli -- status
dotnet run --project src/CloudMigrator.Cli -- dashboard
```

## 主要コマンド

### CloudMigrator.Cli

- `transfer`:
  - `destinationProvider=dropbox` なら Dropbox パイプラインで転送
  - それ以外は SharePoint パイプラインで転送
- `status`: Dropbox 転送 DB から進捗ダッシュボードを表示
- `dashboard`: Web UI で転送メトリクスを表示
- `rebuild-skiplist`: SharePoint の `skip_list` を再構築（転送なし）
- `watchdog`: `transfer` の自動監視・再起動
- `file-crawler`: クロール/比較/検証補助
- `quality-metrics`: テストとカバレッジの集計
- `security-scan`: NuGet 脆弱性スキャン

### CloudMigrator.Setup.Cli

- `bootstrap`: 対話型セットアップ
- `doctor`: 設定の妥当性診断
- `init`: `config.json` / `.env` テンプレート生成
- `verify`: Graph/Dropbox 疎通確認

詳細オプションは [usage.md](usage.md) を参照してください。

## 設定のポイント

- 機密情報は環境変数のみで管理
  - `MIGRATOR__GRAPH__CLIENTSECRET`
  - `MIGRATOR__DROPBOX__CLIENTSECRET`
- `destinationProvider` の既定は `sharepoint`
- Dropbox 長時間運用では以下を推奨
  - `MIGRATOR__DROPBOX__REFRESHTOKEN`
  - `MIGRATOR__DROPBOX__CLIENTID`
  - `MIGRATOR__DROPBOX__CLIENTSECRET`

サンプルは [sample.env](sample.env) を参照してください。`config.json` は `init` コマンドで生成できます。

## ビルド・テスト

```bash
dotnet build CloudMigrator.slnx
dotnet test tests/unit/CloudMigrator.Tests.Unit.csproj
dotnet test tests/integration/CloudMigrator.Tests.Integration.csproj
```

## ドキュメント

- [usage.md](usage.md): コマンド詳細と運用フロー
- [docs/manual-test-runbook.md](docs/manual-test-runbook.md): 手動テスト手順
- [docs/implementation-plan.md](docs/implementation-plan.md): 実装計画
- [task.md](task.md): 現在の進捗
- [CHANGELOG.md](CHANGELOG.md): 変更履歴

## ライセンス

[LICENSE](LICENSE) を参照してください。
