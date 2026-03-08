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
必要に応じて Graph 識別子の直接指定・自動解決も行えます。

```bash
dotnet run --project src/CloudMigrator.Setup.Cli -- init
dotnet run --project src/CloudMigrator.Setup.Cli -- init --config-path configs/config.json --env-path .env --force
dotnet run --project src/CloudMigrator.Setup.Cli -- init --onedrive-user-id user@contoso.com --sharepoint-site-id <site-id> --sharepoint-drive-id <drive-id>
dotnet run --project src/CloudMigrator.Setup.Cli -- init --resolve-graph-ids --onedrive-user-id user@contoso.com --sharepoint-site-url https://contoso.sharepoint.com/sites/migration --sharepoint-drive-name Documents
```

- `--onedrive-user-id`: 生成する `config.json` / `.env` に OneDrive ユーザーIDまたはUPNを反映
- `--sharepoint-site-id`: 生成する設定に SharePoint サイトIDを反映
- `--sharepoint-drive-id`: 生成する設定に SharePoint ドライブIDを反映
- `--resolve-graph-ids`: Graph API から SharePoint サイト/ドライブIDを自動解決（`--sharepoint-site-url` 必須）
- `--sharepoint-site-url`: 自動解決に使う SharePoint サイトURL
- `--sharepoint-drive-name`: 自動解決時に選択するドキュメントライブラリ名（既定: `Documents`）

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

## 6. Microsoft MCP Server for Enterprise の設定（任意）

> ここは `dotnet run` による移行処理そのものとは独立した、運用補助手順です。  
> VS Code と Copilot CLI は設定先が別のため、利用するクライアントごとに設定します。

### 6.1 テナント側の事前準備（管理者、1回のみ）

管理者権限の PowerShell で以下を実行します。

```powershell
Install-Module Microsoft.Entra.Beta -Force -AllowClobber
Connect-Entra -Scopes 'Application.ReadWrite.All','Directory.Read.All','DelegatedPermissionGrant.ReadWrite.All'
Grant-EntraBetaMCPServerPermission -ApplicationName VisualStudioCode
```

### 6.2 VS Code で使う場合

1. MCP サーバーを追加します（拡張機能画面の `@mcp` から追加、または `mcp.json` を編集）。
2. `mcp.json` に以下を設定します。

```json
{
  "servers": {
    "microsoft-enterprise": {
      "type": "http",
      "url": "https://mcp.svc.cloud.microsoft/enterprise"
    }
  }
}
```

3. Copilot Chat のエージェントモードで自然言語クエリを実行して疎通確認します。

### 6.3 Copilot CLI で使う場合

1. Copilot CLI を起動します。

```bash
copilot
```

2. 対話画面で MCP サーバーを追加します。

```text
/mcp add
```

3. 入力項目を以下で設定し、`Ctrl+S` で保存します。
   - Name: `microsoft-enterprise`
   - Type: `http`
   - URL: `https://mcp.svc.cloud.microsoft/enterprise`

4. 登録を確認します。

```text
/mcp show
/mcp show microsoft-enterprise
```

5. 動作確認として、Graph 問い合わせを自然言語で実行します。

```text
テナント内の有効ユーザー数を教えて
```

補足:
- VS Code の設定は `.vscode/mcp.json`（または VS Code ユーザープロファイル）に保存されます。
- Copilot CLI の設定は `~/.copilot/mcp-config.json`（Windows では `%USERPROFILE%\.copilot\mcp-config.json`）に保存されます。
- VS Code 側の設定は Copilot CLI に自動反映されません（逆も同様）。
