# usage

CloudMigrator CLI の実行手順と主要サブコマンドをまとめた利用ガイドです。

## 1. 前提条件

- .NET 10 SDK
- `configs/config.json`（必要に応じて編集）
- 必須環境変数（`sample.env` 参照）
  - `MIGRATOR__GRAPH__CLIENTID`
  - `MIGRATOR__GRAPH__CLIENTSECRET`
  - `MIGRATOR__GRAPH__TENANTID`
  - `MIGRATOR__GRAPH__ONEDRIVEUSERID`
  - `MIGRATOR__GRAPH__SHAREPOINTSITEID`
  - `MIGRATOR__GRAPH__SHAREPOINTDRIVEID`
  - `MIGRATOR__DROPBOX__ACCESSTOKEN`（Dropbox 利用時）

設定の優先順位は **環境変数 > `configs/config.json` > デフォルト値** です。

## 2. 実行方法

開発環境では以下の形式で実行します。

```bash
dotnet run --project src/CloudMigrator.Cli -- <subcommand> [options]
```

例:

```bash
dotnet run --project src/CloudMigrator.Cli -- transfer
dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right sharepoint
```

## 3. 主要サブコマンド

### transfer

OneDrive → SharePoint 転送を実行します。

```bash
dotnet run --project src/CloudMigrator.Cli -- transfer
dotnet run --project src/CloudMigrator.Cli -- transfer --full-rebuild
```

- `--full-rebuild`: キャッシュと `skip_list` をクリアして再構築後に転送

### rebuild-skiplist

SharePoint を再クロールし、`skip_list` を再構築します（転送なし）。

```bash
dotnet run --project src/CloudMigrator.Cli -- rebuild-skiplist
```

### watchdog

転送ログを監視し、フリーズ検知時に `transfer` を自動再起動します。

```bash
dotnet run --project src/CloudMigrator.Cli -- watchdog
```

### file-crawler

クロール結果・`skip_list` の確認/比較/検証を行います。

```bash
dotnet run --project src/CloudMigrator.Cli -- file-crawler onedrive
dotnet run --project src/CloudMigrator.Cli -- file-crawler sharepoint
dotnet run --project src/CloudMigrator.Cli -- file-crawler dropbox
dotnet run --project src/CloudMigrator.Cli -- file-crawler skiplist --top 20
dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right dropbox --top 50
dotnet run --project src/CloudMigrator.Cli -- file-crawler validate --source onedrive --top 50
dotnet run --project src/CloudMigrator.Cli -- file-crawler explore --source sharepoint --top 30
```

### quality-metrics

`.trx` と Cobertura XML を集計し、品質メトリクスを出力します。

```bash
dotnet run --project src/CloudMigrator.Cli -- quality-metrics --trx-dir . --coverage-xml coverage.cobertura.xml --output logs/quality-metrics.json
```

### security-scan

NuGet パッケージ脆弱性をスキャンし、構造化サマリを出力します。

```bash
dotnet run --project src/CloudMigrator.Cli -- security-scan --project CloudMigrator.slnx --output logs/security-scan.json
```

## 4. 代表的な運用フロー

1. `file-crawler onedrive/sharepoint/dropbox` で最新クロール
2. `file-crawler compare` / `validate` で整合性確認
3. `transfer` で本転送
4. 長時間実行時は `watchdog` を使用
