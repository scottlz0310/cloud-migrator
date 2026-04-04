# CloudMigrator バイナリ実機テスト ランブック

最終更新: 2026-04-04  
対象: `cloud-migrator.exe`（セルフコンテインド単一バイナリ）

---

## 1. 目的

GitHub リリースから配布されたバイナリ（`cloud-migrator.exe`）を使って、  
セットアップ診断 → Graph 疎通検証 → クロール → 転送 → Studio 監視 の  
一連のフローを実機で検証する。

`dotnet run` や SDK を前提とせず、エンドユーザーと同じ環境を再現することが目的。

---

## 2. テストゴール

| # | 検証内容 |
|---|----------|
| G-01 | バイナリが起動し、ヘルプが表示される |
| G-02 | `setup doctor` で設定不備を正しく検知できる |
| G-03 | `setup verify` で Graph/SharePoint への疎通を確認できる |
| G-04 | `file-crawler` でクロール・比較・検証ができる |
| G-05 | `transfer` で OneDrive → SharePoint への転送が完了する |
| G-06 | `dashboard` / 引数なし起動で Studio が表示される |
| G-07 | `watchdog` が転送フリーズを検知して再起動できる |
| G-08 | Studio のログタブでリアルタイムログを確認できる |

---

## 3. 前提条件

### 3.1 実行環境

- Windows 11 + PowerShell 7（pwsh）
- .NET ランタイム **不要**（セルフコンテインドバイナリのため）
- ブラウザ（Studio 確認用）

### 3.2 バイナリの取得・配置

1. GitHub リリースページから `cloud-migrator-{version}-win-x64.zip` をダウンロード  
   例: `cloud-migrator-v0.2.1-win-x64.zip`

2. 任意のフォルダに展開する

   ```powershell
   Expand-Archive cloud-migrator-v0.2.1-win-x64.zip -DestinationPath C:\tools\cloud-migrator
   ```

3. バイナリが展開できたことを確認する

   ```powershell
   Get-Item C:\tools\cloud-migrator\cloud-migrator.exe
   ```

4. 作業フォルダに移動する（以降のコマンドはここで実行）

   ```powershell
   cd C:\tools\cloud-migrator
   ```

### 3.3 設定ファイルの配置

バイナリと同じフォルダに `configs/config.json` と `.env` を用意する。  
（リポジトリの `configs/config.json` と `sample.env` を参考に作成）

```
C:\tools\cloud-migrator\
  cloud-migrator.exe
  configs\
    config.json
  .env               ← 機密情報は環境変数のみ
```

### 3.4 環境変数の設定（.env を利用する場合）

```powershell
# .env の内容を読み込む（pwsh ヘルパー）
Get-Content .env | Where-Object { $_ -match '^\s*[^#]' } | ForEach-Object {
    $key, $val = $_ -split '=', 2
    [System.Environment]::SetEnvironmentVariable($key.Trim(), $val.Trim(), 'Process')
}
```

必須環境変数（Graph 疎通テスト時）：

| 変数名 | 説明 |
|--------|------|
| `MIGRATOR__GRAPH__CLIENTID` | Entra 登録済みアプリの Client ID |
| `MIGRATOR__GRAPH__CLIENTSECRET` | Client Secret（config.json に含めない）|
| `MIGRATOR__GRAPH__TENANTID` | テナント ID |
| `MIGRATOR__GRAPH__ONEDRIVEUSERID` | 転送元 OneDrive ユーザー（UPN または GUID）|
| `MIGRATOR__GRAPH__SHAREPOINTSITEID` | 転送先 SharePoint サイト ID |
| `MIGRATOR__GRAPH__SHAREPOINTDRIVEID` | 転送先ドキュメントライブラリ Drive ID |

---

## 4. シナリオ

### TC-01: スモーク（資格情報なしで実施可）

#### TC-01-1: ヘルプ表示

```powershell
.\cloud-migrator.exe --help
```

**期待結果**:
- サブコマンド一覧が表示される
- `transfer`, `file-crawler`, `rebuild-skiplist`, `watchdog`, `dashboard`, `setup`, `status` が含まれる

#### TC-01-2: setup サブコマンドのヘルプ

```powershell
.\cloud-migrator.exe setup --help
```

**期待結果**:
- `bootstrap`, `init`, `doctor`, `verify` が表示される

#### TC-01-3: doctor（未設定検知）

```powershell
.\cloud-migrator.exe setup doctor
```

**期待結果**:
- 未設定の必須 Graph キーに `[ERR]` が出る
- 最終行に `doctor 結果: error=N, warning=M` が表示される
- 環境変数が未設定の場合、終了コード 1

```powershell
$LASTEXITCODE  # 設定済みなら 0、未設定の項目があれば 1
```

---

### TC-02: セットアップ検証（Graph 接続前）

#### TC-02-1: 設定テンプレート初期化（必要時のみ）

```powershell
.\cloud-migrator.exe setup init
```

**期待結果**:
- `configs/config.json` や `.env` の不足分が生成される
- 既存ファイルがある場合は上書きせず保護動作する

#### TC-02-2: doctor（全設定済み後）

```powershell
.\cloud-migrator.exe setup doctor
```

**期待結果**:
- `[ERR]` が 0 件
- 終了コード 0

---

### TC-03: Graph 疎通検証

#### TC-03-1: verify（全チェック）

```powershell
.\cloud-migrator.exe setup verify
```

**期待結果**:
- `[OK] graph.token: 取得成功`
- `[OK] graph.onedrive`
- `[OK] graph.sharepointSite`
- `[OK] graph.sharepointDrive`
- `[ERR]` がなければ終了コード 0

#### TC-03-2: verify（SharePoint をスキップ）

```powershell
.\cloud-migrator.exe setup verify --skip-sharepoint
```

**期待結果**:
- OneDrive まで確認し SharePoint の確認をスキップ

#### TC-03-3: verify（タイムアウト指定）

```powershell
.\cloud-migrator.exe setup verify --timeout-sec 60
```

**期待結果**:
- 60 秒でタイムアウトして結果を返す

---

### TC-04: クロール・差分検証

#### TC-04-1: OneDrive クロール

```powershell
.\cloud-migrator.exe file-crawler onedrive
```

**期待結果**:
- クロール完了ログが出力される（`クロール完了: N 件`）
- キャッシュファイル（`onedrive_files.json`）が更新される

#### TC-04-2: SharePoint クロール

```powershell
.\cloud-migrator.exe file-crawler sharepoint
```

**期待結果**:
- SharePoint に転送済みファイルがあれば件数が表示される
- 未転送の場合は 0 件（正常）

#### TC-04-3: skip_list の内容確認

```powershell
.\cloud-migrator.exe file-crawler skiplist --top 30
```

**期待結果**:
- 現在の skip_list の件数とサンプル（先頭 30 件）が表示される

#### TC-04-4: skip_list 再構築

```powershell
.\cloud-migrator.exe rebuild-skiplist
```

**期待結果**:
- SharePoint クロール結果から skip_list が再構築される

#### TC-04-5: validate（整合性確認）

```powershell
.\cloud-migrator.exe file-crawler validate --source onedrive --top 30
```

**期待結果**:
- `invalid=0, missing=0` が理想
- 差異がある場合は件数と詳細が表示される

#### TC-04-6: compare（差分確認）

```powershell
.\cloud-migrator.exe file-crawler compare --left onedrive --right sharepoint --top 30
```

**期待結果**:
- 差分がある場合はファイル一覧（先頭 30 件）が表示される
- 差分 0 なら転送完了を意味する（終了コードは既知動作として常時 0 の場合あり）

---

### TC-05: 転送

#### TC-05-1: 通常転送

```powershell
.\cloud-migrator.exe transfer
```

**期待結果**:
- 転送完了サマリが表示される（`成功=N, 失敗=0, スキップ=M`）
- Failed > 0 のとき終了コード 1
- ログに skip_list の再構築・キャッシュ使用の分岐が記録される

#### TC-05-2: フル再構築転送

```powershell
.\cloud-migrator.exe transfer --full-rebuild
```

**期待結果**:
- キャッシュと skip_list クリアのログが出る
- SharePoint を再クロール後に転送が実行される

#### TC-05-3: 自動リトライ付き転送

```powershell
.\cloud-migrator.exe transfer --auto-retry 3
```

**期待結果**:
- 失敗ファイルを最大 3 回まで自動リトライする
- 非対話環境（cron 等）での利用を想定

---

### TC-06: Studio（Web ダッシュボード）

#### TC-06-1: 引数なし起動

```powershell
.\cloud-migrator.exe
```

**期待結果**:
- `CloudMigrator Studio 起動中: http://localhost:5050` と表示される
- ブラウザが自動的に開く
- DB が存在する場合: `DB : <パス>`
- DB が存在しない場合: `DB : なし — transfer 実行後、DB が作成されたら再起動してください`

```powershell
# ポート変更・ブラウザ自動起動無効化
.\cloud-migrator.exe --studio-port 8080 --studio-no-browser
```

#### TC-06-2: dashboard サブコマンド

```powershell
.\cloud-migrator.exe dashboard
```

**期待結果**:
- Studio が起動する（TC-06-1 と同様）
- `--port`, `--no-browser`, `--db` オプションが使用可能

```powershell
.\cloud-migrator.exe dashboard --port 8080 --no-browser
.\cloud-migrator.exe dashboard --db logs\transfer_state.db
```

#### TC-06-3: Studio タブの動作確認

ブラウザで http://localhost:5050 を開き、以下を確認する：

| タブ | 確認内容 |
|------|----------|
| 監視 | 転送進捗（完了/失敗/スキップ件数、パーセンテージ）が表示される |
| 実行 | 転送開始/停止ボタンが機能する |
| 設定 | `GET /PUT /api/config` で設定値が表示・保存できる |
| ログ | SSE でリアルタイムログが流れる（INFO/WARN/ERROR フィルタが機能する）|
| 診断 | 「テスト実行」ボタンで Graph 接続テストが実行される |

#### TC-06-4: Studio 診断タブ

1. Studio ブラウザで「診断」タブを開く
2. 「テスト実行」ボタンをクリック
3. 期待結果：
   - Graph 認証 / SharePoint サイト / ドキュメントライブラリ の 3 チェックが順次実行される
   - 全チェック ✅ Pass → バナーが `Healthy` (緑) で表示
   - いずれか失敗 → `Degraded` (黄) または `Unhealthy` (赤)

#### TC-06-5: DB なしモード確認

```powershell
# transfer 実行前に Studio を起動して DB なしモードを確認
.\cloud-migrator.exe --studio-no-browser
```

**期待結果**:
- Studio の監視タブに「DB 未接続」バナーが表示される
- 転送後に Studio を再起動すると DB が接続される

---

### TC-07: watchdog（フリーズ検知・自動再起動）

```powershell
.\cloud-migrator.exe watchdog
```

**期待結果**:
- `watchdog 開始: ログパス=..., タイムアウト=N分, ポーリング=M秒` が表示される
- `transfer` プロセスが起動する
- transfer が正常終了すると watchdog も停止する
- 転送ログが `N` 分間更新されない場合、transfer を自動再起動する

---

### TC-08: ログ確認（Studio ログタブ）

transfer 実行中または実行後に Studio ブラウザで「ログ」タブを開く。

**期待結果**:
- ログがリアルタイムに流れる（SSE 接続）
- `Information` / `Warning` / `Error` フィルタボタンが機能する
- ログエントリのタイムスタンプが UTC (ISO 8601) 形式である
- 機密情報（token/secret/password/api_key）がマスクされている

---

### TC-09: ステータス確認（Dropbox 転送時のみ）

```powershell
.\cloud-migrator.exe status
```

**期待結果**:
- Dropbox 転送状態 DB のパスが表示される
- 転送の進捗（完了/待機中/処理中/失敗件数）が表示される
- DB が存在しない場合は「transfer コマンドを先に実行してください」と表示される

---

## 5. 失敗時の切り分け

| 症状 | 確認箇所 |
|------|----------|
| `doctor` で `[ERR]` が消えない | 環境変数の優先順位を確認（環境変数 > config.json）<br>`MIGRATOR__GRAPH__CLIENTSECRET` は config.json 不可 |
| `verify` で token 失敗 | ClientId/TenantId/ClientSecret の組み合わせと Entra 側アクセス許可を確認 |
| クロール/転送で 403/404 | OneDriveUserId/SharePointSiteId/DriveId の対象誤りを確認 |
| `compare` が常時差分 | `DestinationRoot` 設定の有無と skip_key プレフィックスの差を確認 |
| Studio が起動しない | ポート 5050 が使用中でないか確認（`netstat -an \| Select-String :5050`）<br>別ポートで再試行: `.\cloud-migrator.exe --studio-port 5051` |
| Studio のログタブが空 | `transfer` が起動していない、または SSE 接続が切れている（ページリロードで再接続）|
| セルフコンテインドバイナリが起動しない | Windows SmartScreen / ウイルス対策ソフトの例外設定を確認<br>「詳細情報 → 実行」で SmartScreen をバイパス |

---

## 6. 実施記録テンプレート

```
実施日: YYYY-MM-DD
実施者:
バージョン: v0.x.x (cloud-migrator-v0.x.x-win-x64.zip)
OS: Windows 11 / PowerShell 7.x

[TC-01-1] ヘルプ表示: PASS / FAIL
[TC-01-2] setup ヘルプ: PASS / FAIL
[TC-01-3] doctor (未設定): PASS / FAIL
[TC-02-1] setup init: PASS / FAIL / SKIP
[TC-02-2] doctor (設定済み): PASS / FAIL
[TC-03-1] verify (全チェック): PASS / FAIL
[TC-03-2] verify --skip-sharepoint: PASS / FAIL / SKIP
[TC-03-3] verify --timeout-sec: PASS / FAIL / SKIP
[TC-04-1] file-crawler onedrive: PASS / FAIL
[TC-04-2] file-crawler sharepoint: PASS / FAIL
[TC-04-3] file-crawler skiplist: PASS / FAIL
[TC-04-4] rebuild-skiplist: PASS / FAIL
[TC-04-5] file-crawler validate: PASS / FAIL
[TC-04-6] file-crawler compare: PASS / FAIL
[TC-05-1] transfer: PASS / FAIL
[TC-05-2] transfer --full-rebuild: PASS / FAIL / SKIP
[TC-05-3] transfer --auto-retry: PASS / FAIL / SKIP
[TC-06-1] 引数なし起動 (Studio): PASS / FAIL
[TC-06-2] dashboard サブコマンド: PASS / FAIL
[TC-06-3] Studio タブ確認: PASS / FAIL
[TC-06-4] Studio 診断タブ: PASS / FAIL
[TC-06-5] DB なしモード: PASS / FAIL
[TC-07]   watchdog: PASS / FAIL / SKIP
[TC-08]   ログタブ確認: PASS / FAIL
[TC-09]   status (Dropbox): PASS / FAIL / SKIP

課題・メモ:
```

---

## 7. 実施ログ（本日以降追記）

<!-- 実施ログはこのセクションに追記していく -->
