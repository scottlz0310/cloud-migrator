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
- 任意設定値（Dropbox を転送先にする場合）
    - `MIGRATOR__DROPBOX__ACCESSTOKEN` — 短期アクセストークン（有効期限 約4時間）
    - `MIGRATOR__DROPBOX__REFRESHTOKEN` — OAuth2 リフレッシュトークン（無期限）。長期運用に推奨
    - `MIGRATOR__DROPBOX__CLIENTID` — Dropbox App Key
    - `MIGRATOR__DROPBOX__CLIENTSECRET` — Dropbox App Secret

> **Dropbox トークンの取得方法** → [セクション 4.1「Dropbox 認証情報の取得」](#41-dropbox-認証情報の取得)を参照してください。

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

OneDrive → SharePoint（または Dropbox）転送を実行します。

```bash
dotnet run --project src/CloudMigrator.Cli -- transfer
dotnet run --project src/CloudMigrator.Cli -- transfer --full-rebuild
dotnet run --project src/CloudMigrator.Cli -- transfer --auto-retry 3
dotnet run --project src/CloudMigrator.Cli -- transfer --auto-retry 5 --full-rebuild
```

| オプション | 説明 |
|---|---|
| `--full-rebuild` | キャッシュと `skip_list` をクリアして再構築後に転送 |
| `--auto-retry <N>` | 失敗ファイルを最大 N 回まで自動再試行する（デフォルト: 0 = 無効）。非対話環境（cron 等）での再試行に使用 |

#### 失敗時の挙動

転送完了後に失敗ファイルが残っている場合、実行環境によって以下の動作になります。

| 実行環境 | 挙動 |
|---|---|
| 対話端末（`--auto-retry` 未指定） | 「X件の転送に失敗しています。再試行しますか？ [y/N]」を表示。`y` で再試行、`N`/Enter で終了 |
| 非対話（cron 等）（`--auto-retry` 未指定） | ログ警告を出力して終了。失敗ファイルは**次回実行時に自動的に再試行**される |
| `--auto-retry N` 指定（N > 0） | プロンプトなしで最大 N 回まで自動再試行する。失敗が残った状態で N 回使い切った場合は終了コード 1 で終了 |

> **注意**: 失敗ファイルは次回の `transfer` 実行で必ず再試行されます（永続的に放置されません）。

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

### dashboard

転送状況をブラウザでリアルタイム監視します。転送実行中または実行後に使用します。

```bash
# --db / --port 省略時は設定値を使用（DB: 設定値のパス［既定: logs/dropbox_transfer_state.db］、ポート: 設定値のポート［既定: 5050］）
dotnet run --project src/CloudMigrator.Cli -- dashboard

# DB パスとポートを指定
dotnet run --project src/CloudMigrator.Cli -- dashboard --db logs/dropbox_transfer_state.db --port 8080

# ブラウザを自動で開かない
dotnet run --project src/CloudMigrator.Cli -- dashboard --no-browser
```

起動すると `http://localhost:5050`（または指定ポート）が自動でブラウザに開き、以下が表示されます。

| グラフ / 表示項目 | 内容 |
|---|---|
| **転送サマリ** | 成功数 / スキップ数 / 失敗数 / 総転送バイト数 |
| **レート制限ヒット率（%）** | 100 件ごとに計測された Graph API レート制限ヒット率の推移 |
| **スループット（ファイル/分）** | 100 件ごとに計測された転送速度（ファイル数/分）の推移 |
| **スループット（バイト/秒）** | 100 件ごとに計測された転送速度（バイト/秒）の推移 |
| **直近エラーログ** | 転送失敗の最大 5 件 |

**API エンドポイント（直接取得したい場合）**

```
GET /api/status                                          # 転送サマリ JSON
GET /api/metrics?name=rate_limit_pct&minutes=60         # レート制限ヒット率
GET /api/metrics?name=throughput_files_per_min&minutes=60  # スループット（ファイル/分）
GET /api/metrics?name=throughput_bytes_per_sec&minutes=60  # スループット（バイト/秒）
GET /api/errors                                          # 直近エラーログ（最大 5 件）
```

> **グラフ更新間隔**: 5 秒ごとに自動更新されます。  
> **注意**: `dashboard` コマンドはスループット計測に転送 DB（SQLite）を使用するため、`transfer` コマンドで生成された DB ファイルが必要です。

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
dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap                           # 初回利用者向け対話型セットアップ（転送先を対話選択、既定: SharePoint）
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

→ 詳細な取得手順は次のセクションを参照してください。

### 4.1 Dropbox 認証情報の取得

> **どの認証情報が必要？**
> - **短期テスト・1回限りの移行**: アクセストークンだけで OK（4時間以内に完了する見込みの場合）
> - **大規模移行・長時間実行**: リフレッシュトークン + App Key + App Secret を推奨（自動更新で無停止運用）

#### ウィザードスクリプトを使う（推奨）

`tools/Get-DropboxToken.ps1` を使うと、App Key / App Secret を入力するだけでブラウザ認証 → トークン交換 → `.env` への書き込みまで自動で完了します。

```powershell
.\tools\Get-DropboxToken.ps1
```

実行すると以下の手順が対話的にガイドされます。

1. App Key / App Secret を入力
2. ブラウザが自動的に開き、Dropbox の許可画面に遷移
3. 許可後に URL バーに表示される `code=XXXX` を貼り付け
4. トークンを自動取得し、`.env` に書き込み完了

> **前提**: Dropbox App Console でアプリを作成済みであること（下記 Step 1 を参照）。

---

#### Step 1: Dropbox App を作成する（初回のみ）

1. [Dropbox App Console](https://www.dropbox.com/developers/apps) を開き、**「Create app」** をクリックします。
2. 以下の設定を選択します。
   - **API**: `Scoped access`
   - **Access type**: `Full Dropbox`（転送先フォルダがルート外にある場合）または `App folder`
   - **App name**: 任意（例: `cloud-migrator`）
3. 作成後、「Permissions」タブを開き、以下の権限を **チェックして「Submit」** します。
   - `files.content.write`（ファイル書き込み）
   - `files.content.read`（クロール・差分確認に使用）
4. 「Settings」タブから **App key**（= `MIGRATOR__DROPBOX__CLIENTID`）と **App secret**（= `MIGRATOR__DROPBOX__CLIENTSECRET`）を控えます。

App を作成したら `Get-DropboxToken.ps1` を実行するだけで完了します。

---

#### 手動で取得する場合

スクリプトを使わずに手動でトークンを取得したい場合は以下の手順に従います。

**Step 2-A（短期テスト用）: アクセストークンを生成する**

1. App Console の「Settings」タブ → **「Generated access token」** セクションで **「Generate」** をクリックします。
2. 表示されたトークンを `.env` に設定します。

```
MIGRATOR__DROPBOX__ACCESSTOKEN=<コピーしたトークン>
```

> **注意**: 有効期限は約 4 時間です。大規模移行では途中で失効します。Step 2-B でリフレッシュトークンを取得することを推奨します。

**Step 2-B（長期運用）: リフレッシュトークンを取得する**

ブラウザで以下の URL を開きます（`APP_KEY` を実際の値に置換）。

```
https://www.dropbox.com/oauth2/authorize?client_id=APP_KEY&response_type=code&token_access_type=offline
```

- `token_access_type=offline` を **必ず指定**します（これがないとリフレッシュトークンが発行されません）

Dropbox のログイン画面が表示されたら許可し、リダイレクト先の URL に含まれる `code=XXXX` の値をコピーします。

> リダイレクト URI を設定していない場合、エラーページに遷移しますが URL バーに `code=XXXX` が残ります。

次のスクリプトで認可コードをトークンに交換します（PowerShell）。

```powershell
$appKey    = "<App Key>"
$appSecret = "<App Secret>"
$authCode  = "<取得した code>"

$body = @{
    code          = $authCode
    grant_type    = "authorization_code"
    client_id     = $appKey
    client_secret = $appSecret
}
$resp = Invoke-RestMethod -Method Post `
    -Uri "https://api.dropboxapi.com/oauth2/token" `
    -Body $body

Write-Host "access_token : $($resp.access_token)"
Write-Host "refresh_token: $($resp.refresh_token)"
```

取得した値を `.env` に設定します。

```
MIGRATOR__DROPBOX__ACCESSTOKEN=<access_token>   # 省略可：空にすると初回起動時に自動取得
MIGRATOR__DROPBOX__REFRESHTOKEN=<refresh_token>
MIGRATOR__DROPBOX__CLIENTID=<App Key>
MIGRATOR__DROPBOX__CLIENTSECRET=<App Secret>
```

リフレッシュトークンを設定しておくと、アクセストークンが失効しても自動的に再取得されます。`MIGRATOR__DROPBOX__ACCESSTOKEN` は空欄でも構いません（初回呼び出し前に自動取得します）。

> **セキュリティ**: `.env` はコミット禁止です（`.gitignore` に含まれています）。`MIGRATOR__DROPBOX__CLIENTSECRET` は環境変数のみで管理し、`config.json` には含めないでください。

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

### 5.1 OneDrive → SharePoint（初回セットアップ～転送）

**最短手順**（`transfer` がクロール・スキップリスト構築を自動実行します）

```bash
# ① 初回: ウィザードで config.json / .env を生成（転送先 SharePoint）
dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap

# ② 設定診断（必須設定が揃っているか確認）
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor

# ③ Graph 疎通確認（OneDrive / SharePoint トークン取得・ID 検証）
dotnet run --project src/CloudMigrator.Setup.Cli -- verify

# ④ 本転送（OneDrive・SharePoint の自動クロール＆スキップリスト構築込み）
dotnet run --project src/CloudMigrator.Cli -- transfer
```

**転送前に差分を確認したい場合**（任意）

```bash
# OneDrive・SharePoint を個別にクロールしてキャッシュ
dotnet run --project src/CloudMigrator.Cli -- file-crawler onedrive
dotnet run --project src/CloudMigrator.Cli -- file-crawler sharepoint

# 差分確認（未転送ファイルを把握してから本転送）
dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right sharepoint
dotnet run --project src/CloudMigrator.Cli -- transfer
```

### 5.2 OneDrive → Dropbox（初回セットアップ～転送）

**最短手順**

```bash
# ① 初回: ウィザードで config.json / .env を生成（転送先 Dropbox）
dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap --destination dropbox
#   ※ --destination を省略すると起動後に対話選択できます

# ② 設定診断（SharePoint 関連は Warning 扱い、ExitCode=0 が正常）
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor

# ③ OneDrive 疎通確認（Dropbox 転送先では SharePoint チェックをスキップ）
dotnet run --project src/CloudMigrator.Setup.Cli -- verify --skip-sharepoint

# ④ 本転送（OneDrive・Dropbox の自動クロール＆スキップリスト構築込み）
dotnet run --project src/CloudMigrator.Cli -- transfer
```

**転送前に差分を確認したい場合**（任意）

```bash
# OneDrive・Dropbox を個別にクロールしてキャッシュ
dotnet run --project src/CloudMigrator.Cli -- file-crawler onedrive
dotnet run --project src/CloudMigrator.Cli -- file-crawler dropbox

# 差分確認してから本転送
dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right dropbox
dotnet run --project src/CloudMigrator.Cli -- transfer
```

### 5.3 転送の再開・再試行

転送が途中で止まった場合や失敗ファイルが残っている場合は、再実行するだけで差分のみ転送されます。

```bash
# 増分転送（未転送ファイル・前回失敗ファイルを自動処理）
dotnet run --project src/CloudMigrator.Cli -- transfer

# 失敗ファイルを最大 5 回まで自動再試行してから終了（cron 等の非対話環境向け）
dotnet run --project src/CloudMigrator.Cli -- transfer --auto-retry 5

# 長時間実行が見込まれる場合は watchdog を使ってフリーズ時に自動再起動
dotnet run --project src/CloudMigrator.Cli -- watchdog
```

**失敗ファイルの扱い**

失敗ファイルは SQLite 状態 DB に記録されます。失敗が 3 回を超えると `permanent_failed` 状態になりますが、次回の `transfer` 実行時の Phase A で自動的にリセットされ、再試行対象に戻ります。永続的に放置されることはありません。

### 5.4 キャッシュ・スキップリストの削除

`logs/` 配下に以下のファイルが生成されます。

| ファイル | 内容 | 削除タイミング |
|---|---|---|
| `logs/onedrive_files.json` | OneDrive クロール結果キャッシュ | OneDrive 側の変更を反映したい時 |
| `logs/sharepoint_current_files.json` | SharePoint クロール結果キャッシュ | SharePoint 側の変更を反映したい時 |
| `logs/dropbox_files.json` | Dropbox クロール結果キャッシュ | Dropbox 側の変更を反映したい時 |
| `logs/skip_list.json` | 転送済みファイルの記録 | **全件再転送したい時のみ削除**（通常は消さない） |
| `logs/config_hash.txt` | 設定変更検知用ハッシュ | 通常は自動管理（手動削除不要） |

**`transfer --full-rebuild` による自動クリア（推奨）**

設定変更後や全件再転送したい場合は、手動削除ではなく `--full-rebuild` を使うと安全です。  
キャッシュ・スキップリスト・設定ハッシュをすべてクリアしてからフルスキャン＆全件転送します。

```bash
dotnet run --project src/CloudMigrator.Cli -- transfer --full-rebuild
```

また、設定ファイル（`configs/config.json`）を変更して `transfer` を実行すると、変更が自動検知されてキャッシュがクリアされます。

**手動で個別に削除したい場合**

```powershell
# キャッシュのみ削除（スキップリストは保持 → 次回クロールは再実行、転送済みはスキップ維持）
Remove-Item logs/onedrive_files.json -ErrorAction SilentlyContinue
Remove-Item logs/sharepoint_current_files.json -ErrorAction SilentlyContinue
Remove-Item logs/dropbox_files.json -ErrorAction SilentlyContinue

# スキップリストのみ削除（次回 transfer で全件再転送される）
Remove-Item logs/skip_list.json -ErrorAction SilentlyContinue

# キャッシュ・スキップリスト・設定ハッシュをすべて削除
Remove-Item logs/onedrive_files.json, logs/sharepoint_current_files.json, logs/dropbox_files.json, logs/skip_list.json, logs/config_hash.txt -ErrorAction SilentlyContinue
```

> **注意**: `skip_list.json` を削除すると、次回の `transfer` で転送先の再クロールが走り、全ファイルが再転送対象になります。意図せず重複アップロードしないよう注意してください。

### 5.5 全件再転送（設定変更後・障害復旧）

設定（転送元/転送先フォルダ等）を変更した場合は `--full-rebuild` でキャッシュとスキップリストをクリアしてから転送します。

```bash
# キャッシュ・スキップリストをクリアしてフルスキャン＆全件転送
dotnet run --project src/CloudMigrator.Cli -- transfer --full-rebuild

# キャッシュはそのままでスキップリストのみ再構築（転送はしない）
dotnet run --project src/CloudMigrator.Cli -- rebuild-skiplist
```

### 5.5 転送後の検証

```bash
# 両側をクロールして比較（OneDrive に残っている未転送ファイルを一覧表示）
dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right sharepoint --top 100

# OneDrive → Dropbox の場合
dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right dropbox --top 100

# ファイル名・パスの整合性を検証（文字化け・パス重複 等）
dotnet run --project src/CloudMigrator.Cli -- file-crawler validate --source onedrive --top 50

# スキップリスト上位件数を確認（何件転送済みか把握）
dotnet run --project src/CloudMigrator.Cli -- file-crawler skiplist --top 50

# 転送先フォルダ構造をブラウズ
dotnet run --project src/CloudMigrator.Cli -- file-crawler explore --source sharepoint --top 30
dotnet run --project src/CloudMigrator.Cli -- file-crawler explore --source dropbox --top 30
```

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
