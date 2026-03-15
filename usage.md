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
dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap                           # 初回利用者向け対話型セットアップ（SharePoint 転送先）
dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap --destination dropbox     # Dropbox を転送先にする場合
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor
dotnet run --project src/CloudMigrator.Setup.Cli -- init
dotnet run --project src/CloudMigrator.Setup.Cli -- verify
```

### bootstrap（対話型セットアップウィザード）

**初回利用者向け**の対話型ウィザードです。  
ClientId / TenantId / ClientSecret / UPN / サイトURL を順に入力するだけで、Graph API から識別子を自動解決し、`config.json` を生成します。  
認証情報の保存先（`.env`）は設定状況によって異なります（後述）。  
既存ファイルがある場合は対話的に上書き確認を行います（`--force` を指定した場合は確認なしで上書きします）。

**環境変数プリフィル**: 起動時に以下の環境変数が設定済みであれば自動検出し、Enter キーで現在値をそのまま使用できます（Bitwarden+dsx 等で環境変数管理している場合に便利）。
- `MIGRATOR__GRAPH__CLIENTID` / `MIGRATOR__GRAPH__TENANTID` / `MIGRATOR__GRAPH__CLIENTSECRET` / `MIGRATOR__GRAPH__ONEDRIVEUSERID`

**`.env` 生成の条件**: 認証情報（ClientId / TenantId / ClientSecret / UPN）がすべて環境変数から取得された場合、`.env` は生成されません。代わりに取得済みの SharePoint ID / Drive ID のみコンソールに表示されます。一部だけ環境変数から取得された場合は、該当行をコメントアウトした `.env` を生成します（`.env` ローダーによる空値上書きを防ぎます）。

```bash
# OneDrive → SharePoint（デフォルト）
dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap

# OneDrive → Dropbox
dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap --destination dropbox

# オプション指定例
dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap --config-path configs/config.json --env-path .env
```

#### 転送先: SharePoint（デフォルト）のウィザードの流れ

1. **Azure AD 認証情報** — Client ID / Tenant ID / Client Secret を入力（環境変数設定済みなら Enter でスキップ）
2. **OneDrive ユーザー** — ユーザーのUPN（例: `user@contoso.com`）を入力（環境変数設定済みなら Enter でスキップ）
3. **転送設定** — 転送元フォルダ・転送先フォルダ
4. **並列転送設定** — 最大並列転送数（デフォルト: 4）と動的並列度制御（AdaptiveConcurrency）の有効/無効
5. **SharePoint サイトURL** — 移行先サイトのURLを入力
6. **ドキュメントライブラリ選択** — Graph から候補を取得し、番号で選択
7. **設定ファイル生成** — `config.json` を生成し、`.env` は認証情報の取得元に応じて条件付きで生成  
   （全認証情報が環境変数由来の場合は `.env` を生成せず、SharePoint ID / Drive ID をコンソールに表示）
8. **完了後の案内** — `verify` コマンドの実行を促す

> **注意**: Client Secret はセキュリティ上の理由から `.env` / `config.json` には保存されません。  
> ウィザード終了後、表示される環境変数設定コマンドでシェルに手動設定してください。

#### 転送先: Dropbox のウィザードの流れ（`--destination dropbox`）

1. **Azure AD 認証情報** — Client ID / Tenant ID / Client Secret を入力（SharePoint 転送と同じ）
2. **OneDrive ユーザー** — ユーザーのUPN を入力
3. **転送設定** — 転送元フォルダ
4. **並列転送設定** — 最大並列転送数・AdaptiveConcurrency の有効/無効
5. **Dropbox 設定** — Dropbox Access Token（マスク入力）と転送先フォルダパス（例: `/OneDriveMigration`、空欄でルート）  
   → Access Token は [Dropbox App Console](https://www.dropbox.com/developers/apps) の「Generated access token」から取得
6. **OneDrive 認証確認** — OneDrive トークン取得のみ実行（SharePoint 解決はスキップ）
7. **設定ファイル生成** — `config.json` に `"destinationProvider": "dropbox"` と `dropbox.rootPath` を書き込み  
   `.env` の SharePoint 関連行はコメントアウト、`MIGRATOR__DROPBOX__ACCESSTOKEN=...` を追記
8. **doctor / verify** — doctor は SP フィールドを Warning 扱い（error=0 が期待値）、verify は SharePoint チェックをスキップ

**Dropbox セットアップに必要なもの**:
- Dropbox アカウントと App Console でのアプリ作成（権限: `files.content.write`）
- Generated access token（有効期限あり。長期運用には OAuth フローで refresh token を取得推奨）

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
- `--onedrive-source-folder`: 転送元フォルダパスを `config.json` に反映（例: `Documents/Projects`。省略時はドライブ全体）
- `--destination-root`: 転送先フォルダパスを `config.json` に反映（SharePoint ドライブ上のルート。例: `移行データ/OneDrive`。省略時はドライブルート直下）
- `--sharepoint-site-id`: 生成する設定に SharePoint サイトIDを反映
- `--sharepoint-drive-id`: 生成する設定に SharePoint ドライブIDを反映
- `--resolve-graph-ids`: Graph API から SharePoint サイト/ドライブIDを自動解決（`--sharepoint-site-url` と `--onedrive-user-id` 必須）
- `--sharepoint-site-url`: 自動解決に使う SharePoint サイトURL
- `--sharepoint-drive-name`: 自動解決時に選択するドキュメントライブラリ名（既定: `Documents`）
- `--max-parallel-transfers`: 最大並列転送数を `config.json` に反映（例: `8`。省略時は変更しない。新規テンプレートでは `4`）
- `--adaptive-concurrency`: 動的並列度制御の有効/無効を `config.json` に反映（`true` で有効。省略時はデフォルト `false`）

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

> **⚠️ 注意（テナント管理者専用・最小権限原則）**  
> 以下のコマンドには `Application.ReadWrite.All`（アプリ登録の読み書き）、`DelegatedPermissionGrant.ReadWrite.All`（委任アクセス許可の付与）など、**テナント全体に影響する強力な権限スコープ**が含まれます。  
> これらは MCP Server 向けサービスプリンシパルのプロビジョニングに必要なため要求されますが、誤用すると意図しないアプリへの権限昇格やアクセス許可の過剰付与につながります。  
> **必ず Entra テナント管理者が自己の業務端末で実行し**、不要になったら権限を見直してください。  
> 参考: [Microsoft Entra のアプリ登録セキュリティベストプラクティス](https://learn.microsoft.com/en-us/entra/identity-platform/security-best-practices-for-app-registration)

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
