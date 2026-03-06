# 利用方法

CloudMigrator CLI の実行手順と主要サブコマンドをまとめた利用ガイドです。

## 1. 前提条件

- .NET 10 SDK
- `configs/config.json`（必要に応じて編集）
- 必須設定値
  - 以下はいずれかで設定します: **環境変数** または **`configs/config.json`**（`sample.env` 参照）
    - `MIGRATOR__GRAPH__CLIENTID`
    - `MIGRATOR__GRAPH__TENANTID`
    - `MIGRATOR__GRAPH__ONEDRIVEUSERID`
    - `MIGRATOR__GRAPH__SHAREPOINTSITEID`
    - `MIGRATOR__GRAPH__SHAREPOINTDRIVEID`
  - 以下は **環境変数のみ** で設定します（`configs/config.json` には含めない）
    - `MIGRATOR__GRAPH__CLIENTSECRET`
- 任意設定値
  - `MIGRATOR__DROPBOX__ACCESSTOKEN`（Dropbox 利用時。環境変数での設定を推奨）

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

## 4. Setup Tool（初期設定支援CLI）

`CloudMigrator.Setup.Cli` は実行前セットアップの診断とテンプレート生成を行います。

```bash
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor
dotnet run --project src/CloudMigrator.Setup.Cli -- init
dotnet run --project src/CloudMigrator.Setup.Cli -- verify
```

### doctor

必須設定（Graph系）と主要パス設定を診断します。  
不足がある場合は `ExitCode=1` になります。

```bash
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor --config-path configs/config.json --strict-dropbox
```

### init

`config.json` / `.env` テンプレートを冪等に生成します。  
既存ファイルは上書きせず、`--force` 指定時のみ上書きします。

```bash
dotnet run --project src/CloudMigrator.Setup.Cli -- init
dotnet run --project src/CloudMigrator.Setup.Cli -- init --config-path configs/config.json --env-path .env --force
```

### verify

Graph トークン取得と主要ID（OneDrive / SharePoint）の疎通を検証します。

```bash
dotnet run --project src/CloudMigrator.Setup.Cli -- verify
dotnet run --project src/CloudMigrator.Setup.Cli -- verify --skip-sharepoint
```

## 5. 代表的な運用フロー

1. `file-crawler onedrive/sharepoint/dropbox` で最新クロール
2. `file-crawler compare` / `validate` で整合性確認
3. `transfer` で本転送
4. 長時間実行時は `watchdog` を使用
