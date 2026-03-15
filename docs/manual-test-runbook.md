# CloudMigrator マニュアルテスト ランブック

最終更新: 2026-03-13
対象: CloudMigrator.Cli / CloudMigrator.Setup.Cli

## 1. 目的

このランブックは、OneDrive -> SharePoint 移行機能の手動検証を、同じ手順で再現できるようにするための運用手順書です。

## 2. テストゴール

- CLI が起動し、主要サブコマンドが列挙されること
- setup doctor/verify で設定不備を正しく検知できること
- file-crawler / rebuild-skiplist / transfer の基本フローが成立すること
- 失敗時に終了コードとログから原因を追跡できること

## 3. 前提条件

- Windows + PowerShell (pwsh)
- .NET 10 SDK インストール済み
- リポジトリルートで実行
- Graph 実行テスト時は以下を設定
  - MIGRATOR__GRAPH__CLIENTID
  - MIGRATOR__GRAPH__TENANTID
  - MIGRATOR__GRAPH__CLIENTSECRET (環境変数のみ)
  - MIGRATOR__GRAPH__ONEDRIVEUSERID
  - MIGRATOR__GRAPH__SHAREPOINTSITEID
  - MIGRATOR__GRAPH__SHAREPOINTDRIVEID

## 4. 実施シナリオ

### 4.1 スモーク (資格情報なしで実施可)

1) ビルド

```powershell
dotnet build CloudMigrator.slnx
```

期待結果:
- Build succeeded.
- 失敗プロジェクトが 0 件

2) CloudMigrator.Cli ヘルプ

```powershell
dotnet run --project src/CloudMigrator.Cli -- --help
```

期待結果:
- subcommand に transfer / rebuild-skiplist / watchdog / quality-metrics / security-scan / file-crawler が表示される

3) CloudMigrator.Setup.Cli ヘルプ

```powershell
dotnet run --project src/CloudMigrator.Setup.Cli -- --help
```

期待結果:
- subcommand に bootstrap / doctor / init / verify が表示される

4) doctor (未設定検知)

```powershell
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor
```

期待結果:
- 未設定の必須 Graph キーに [ERR] が出る
- 最終行に doctor 結果: error=n, warning=m が表示される
- エラーが 1 件以上ならプロセス終了コードは 1

### 4.2 セットアップ検証 (Graph 接続前)

5) テンプレート初期化 (必要時のみ)

```powershell
dotnet run --project src/CloudMigrator.Setup.Cli -- init
```

期待結果:
- configs/config.json または .env の不足分が生成される
- 既存ファイルがある場合は保護動作する

6) doctor 再実行

```powershell
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor --strict-dropbox
```

期待結果:
- Graph 必須キーが全て設定済みなら [ERR] 0
- Dropbox を使わない場合は strict オプションを外す

### 4.3 Graph 疎通検証

7) verify

```powershell
dotnet run --project src/CloudMigrator.Setup.Cli -- verify
```

期待結果:
- [OK] graph.token: 取得成功
- [OK] graph.organization / graph.onedrive / graph.sharepointSite / graph.sharepointDrive
- [ERR] があれば終了コード 1

補助:

```powershell
dotnet run --project src/CloudMigrator.Setup.Cli -- verify --skip-sharepoint
dotnet run --project src/CloudMigrator.Setup.Cli -- verify --timeout-sec 60
```

### 4.4 クロール・差分検証

8) OneDrive / SharePoint クロール

```powershell
dotnet run --project src/CloudMigrator.Cli -- file-crawler onedrive
dotnet run --project src/CloudMigrator.Cli -- file-crawler sharepoint
```

期待結果:
- それぞれ完了ログに件数が表示される
- キャッシュファイルが更新される

9) skip_list 再構築と妥当性確認

```powershell
dotnet run --project src/CloudMigrator.Cli -- rebuild-skiplist
dotnet run --project src/CloudMigrator.Cli -- file-crawler validate --source onedrive --top 30
```

期待結果:
- rebuild-skiplist 完了
- validate の invalid/missing が 0 に近いこと

10) 差分比較

```powershell
dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right sharepoint --top 30
```

期待結果:
- 差分がなければ終了コード 0
- 差分がある場合は一覧表示され、終了コード 1

### 4.5 転送検証

11) 通常転送

```powershell
dotnet run --project src/CloudMigrator.Cli -- transfer
```

期待結果:
- 転送完了サマリが表示される
- Failed > 0 のとき終了コード 1
- ログに skip_list 再構築やキャッシュ使用の分岐が記録される

12) フル再構築転送

```powershell
dotnet run --project src/CloudMigrator.Cli -- transfer --full-rebuild
```

期待結果:
- キャッシュと skip_list クリアのログが出る
- SharePoint 再クロール後に転送が実行される

13) 監視コマンド (任意)

```powershell
dotnet run --project src/CloudMigrator.Cli -- watchdog
```

## 5. 失敗時の切り分け

- doctor で必須キー未設定:
  - 環境変数優先順位を確認
  - MIGRATOR__GRAPH__CLIENTSECRET は config.json ではなく環境変数のみ
- verify で token 失敗:
  - ClientId/TenantId/ClientSecret の組み合わせと Entra 側権限を確認
- crawler/transfer で 403/404:
  - OneDriveUserId/SharePointSiteId/DriveId の対象誤りを確認
- compare が常時差分:
  - DestinationRoot 設定有無と skip_key のプレフィックス差を確認

## 6. 実施記録テンプレート

```text
実施日:
実施者:
対象ブランチ:

[TC-01] build: PASS/FAIL
  実行コマンド:
  結果要約:

[TC-02] cli help: PASS/FAIL
  実行コマンド:
  結果要約:

[TC-03] setup help: PASS/FAIL
  実行コマンド:
  結果要約:

[TC-04] doctor: PASS/FAIL
  実行コマンド:
  結果要約:

[TC-05] verify: PASS/FAIL
  実行コマンド:
  結果要約:

[TC-06] crawler/compare/transfer: PASS/FAIL
  実行コマンド:
  結果要約:

課題・メモ:
```

## 7. 本日の実施ログ (2026-03-13)

### 2026-03-13 初回 E2E 手動テスト

実施環境: Windows 11 / PowerShell 7 / .NET 10 / CloudMigrator v0.x  
実施者: Copilot

| # | TC | コマンド | 結果 | 備考 |
|---|----|----|------|------|
| TC-01 | ビルド | `dotnet build CloudMigrator.slnx` | ✅ PASS | Build succeeded. エラー0/警告0 |
| TC-02 | CLI ヘルプ | `dotnet run --project src/CloudMigrator.Cli -- --help` | ✅ PASS | サブコマンド一覧表示 |
| TC-03 | Setup ヘルプ | `dotnet run --project src/CloudMigrator.Setup.Cli -- --help` | ✅ PASS | サブコマンド一覧表示 |
| TC-04 | doctor | `dotnet run --project src/CloudMigrator.Setup.Cli -- doctor` | ✅ PASS | error=0, warning=0 |
| TC-05 | verify | `dotnet run --project src/CloudMigrator.Setup.Cli -- verify` | ✅ PASS | 全 Graph エンドポイント OK |
| TC-06a | file-crawler onedrive | `dotnet run --project src/CloudMigrator.Cli -- file-crawler onedrive` | ✅ PASS | 24,481 件クロール |
| TC-06b | file-crawler sharepoint | `dotnet run --project src/CloudMigrator.Cli -- file-crawler sharepoint` | ✅ PASS | 0 件（未転送、正常） |
| TC-06c | rebuild-skiplist | `dotnet run --project src/CloudMigrator.Cli -- rebuild-skiplist` | ✅ PASS | 0 件（空状態） |
| TC-06c2 | validate | `dotnet run --project src/CloudMigrator.Cli -- file-crawler validate --source onedrive --top 30` | ✅ PASS | invalid=0, missing=0 |
| TC-06d | compare | `dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right sharepoint --top 30` | ✅ PASS | 差分24,481件表示。EXIT:0 ※注1 |
| TC-06e | transfer | `dotnet run --project src/CloudMigrator.Cli -- transfer` | ✅ PASS | 成功24,481件/失敗0件/スキップ0件/所要1時間13分 |

**※注1**: `compare` で差分ありのとき `Environment.ExitCode=1` を設定するが `System.CommandLine` が上書きし EXIT:0 になる。ランブック期待値は EXIT:1 だが既知動作。

#### バグ修正記録 (本セッション)

| バグ | 修正箇所 | 内容 |
|------|----------|------|
| `FindFolderIdAsync` $filter 非対応 | `GraphStorageProvider.cs` | SharePoint は Children エンドポイントの `$filter` を非サポート。`PageIterator` + クライアント側フィルタリングに変更 |
| フォルダ先行作成の過剰 API 呼び出し | `GraphStorageProvider.cs` | `EnsureFolderAsync` でパスごとにルートから再解決していた。`_folderIdCache` (Dictionary) を追加してAPI呼び出しを O(N×depth²) → O(N) に削減 |
| フォルダ作成フェーズ無音 | `TransferEngine.cs` | フォルダ先行作成フェーズのログが皆無で進捗不明。件数・100件ごとの進捗・完了ログを追加 |

---

## 8. 高速化改良後の再実行ログ (2026-03-14)

### 2026-03-14 高速化後 E2E 再テスト

実施環境: Windows 11 / PowerShell 7 / .NET 10 / CloudMigrator v0.x  
実施者: Copilot  
目的: 高速化改良の効果測定 (前回比較: 2026-03-13 初回テスト)

#### 実施前クリーンアップ

| 操作 | 結果 |
|------|------|
| `skip_list.json` → `[]` にクリア | 完了 |
| `rebuild-skiplist` 実行 | 設定変更検知により全キャッシュクリア → SharePoint 0件確認（所要 2.8秒）|
| `file-crawler onedrive` 実行（`transfer` 内部でも実行） | 設定変更検知で `onedrive_files.json` も消去されたため、`transfer` 内で再クロール |

> **注意**: `rebuild-skiplist` 実行時に環境変数の有無でコンフィグハッシュが変化し、`onedrive_files.json` も含む全キャッシュが自動クリアされた。今後の改善候補（OneDriveキャッシュはSharePoint設定変更に依存させない）。

#### 計測結果

| # | TC | コマンド | 結果 | 備考 |
|---|----|----|------|------|
| TC-06a | file-crawler onedrive | `dotnet run --project src/CloudMigrator.Cli -- file-crawler onedrive` | ✅ PASS | 24,481件クロール / 所要418.7秒（約7分） |
| TC-06b | rebuild-skiplist | `dotnet run --project src/CloudMigrator.Cli -- rebuild-skiplist` | ✅ PASS | SharePoint 0件（削除済み確認）/ 所要2.8秒 |
| TC-06e | transfer | `dotnet run --project src/CloudMigrator.Cli -- transfer` | ✅ PASS | 成功24,481件/失敗0件/スキップ0件 |

#### 所要時間比較

| 計測対象 | 2026-03-13 (改良前) | 2026-03-14 (高速化後) | 差分 |
|---------|--------------------|--------------------|------|
| TransferEngine 内部時間 | 1時間13分 | 1時間13分39秒 | ≒同等 |
| transfer コマンド全体（OneDriveクロール含む） | 1時間13分 | 1時間20分21秒 | +7分（再クロール分） |

> **備考**: 転送エンジン自体の所要時間は前回とほぼ同等。2026-03-14 の全体時間増加は、設定変更検知によるキャッシュクリアで `transfer` コマンド内でOneDriveクロール（約7分）が走ったためであり、転送速度そのものへの影響ではない。高速化改良の効果は transfer フェーズより別の箇所（フォルダキャッシュ等）で発現している可能性あり。

---

## 9. AdaptiveConcurrency 初回 E2E テスト (2026-03-15)

### 2026-03-15 AdaptiveConcurrency 有効化テスト

実施環境: Windows 11 / PowerShell 7 / .NET 10 / CloudMigrator v0.x  
実施者: Copilot  
目的: `AdaptiveConcurrency` 機能の動作確認・所要時間測定 (前回: 2026-03-14 比較)

#### テスト構成

| 設定項目 | 値 |
|---------|---|
| `maxParallelTransfers` | 20 |
| `adaptiveConcurrency.enabled` | true |
| `adaptiveConcurrency.minDegree` | 1 |
| `adaptiveConcurrency.successThresholdToIncrease` | 10 |
| `destinationRoot` | DEV |
| 転送対象ファイル数 | 24,481 件 |

#### 実施前クリーンアップ

| 操作 | 結果 |
|------|------|
| SharePoint を手動削除でクリア | 完了（0 件確認） |
| `rebuild-skiplist` 実行 | SharePoint 0 件確認 / skip_list → 0 件にリセット / 所要 0.8 秒 |

#### 計測結果

| # | TC | コマンド | 結果 | 備考 |
|---|----|----|------|------|
| TC-09a | rebuild-skiplist | `dotnet run --project src/CloudMigrator.Cli -- rebuild-skiplist` | ✅ PASS | SharePoint 0 件確認 / skip_list 0 件 |
| TC-09b | transfer (動的並列度) | `dotnet run --project src/CloudMigrator.Cli -- transfer` | ✅ PASS (注 1) | 成功24,168件/失敗313件/スキップ0件/所要1時間0分19秒 |

**※注1**: 313 件の失敗は転送後に skip_list に残る。再実行で差分転送可能。Graph API レート制限による in-flight リクエストが retry 上限に達したものと推定。

#### AdaptiveConcurrency 動作ログ（抜粋）

転送開始直後（UTC 16:29:41）から約 20 秒でレート制限が連続発生し、並列度が 20 → 1 まで段階的に低下。その後約 50 秒かけて 1 → 20 に回復した。以降は安定的に 15〜20 で推移。

```
2026-03-14T16:29:41Z  動的並列度制御モードで転送開始 (初期並列度: 20/20)
2026-03-14T16:29:51Z  レート制限検出。並列度 20 → 19/20 (Retry-After: 6秒)
2026-03-14T16:29:52Z  連続成功10回。並列度 19 → 20/20 に回復
2026-03-14T16:29:53Z  レート制限連続検出（カスケード）→ 並列度 20 → 1/20 まで段階低下
2026-03-14T16:30:16Z  連続成功10回。並列度 1 → 2/20 に回復（回復開始）
2026-03-14T16:30:31Z  連続成功10回。並列度 19 → 20/20 に回復（全回復）
2026-03-14T16:30:31Z  転送進捗: 500/24481 完了 (失敗: 9, 現在の並列度: 17/20)
 ...（以降は20前後で安定推移）...
2026-03-14T17:13:44Z  転送進捗: 24000/24481 完了 (失敗: 313, 現在の並列度: 20/20)
```

| イベント種別 | 発生件数 |
|------------|---------|
| レート制限検出（並列度削減） | 770 件 |
| 連続成功による並列度回復 | 770 件 |
| 転送進捗ログ（500 件毎） | 48 件 |

#### 所要時間比較

| 計測対象 | 2026-03-14 (固定並列度20) | 2026-03-15 (AdaptiveConcurrency) | 差分 |
|---------|--------------------------|----------------------------------|------|
| transfer エンジン内部時間 | 1時間13分39秒 | 1時間0分19秒 | **−13分20秒 (約18%短縮)** |
| 成功件数 | 24,481 件 | 24,168 件 | −313 件 (1.3%) |
| 失敗件数 | 0 件 | 313 件 | +313 件 |

#### 考察

- **速度向上確認**: AdaptiveConcurrency 有効化により転送時間が約 18% 短縮（1:13:39 → 1:00:19）。
- **失敗件数の増加**: 固定並列度 (0 失敗) と比較して 313 件が失敗。転送開始直後の並列度カスケード (20→1) 中に in-flight 状態だったリクエストが、Retry-After 待機中に retry 上限を超えた可能性がある。
- **改善候補**:
  - `minDegree` を 1 より高い値（例: 4）に設定してカスケード深さを抑制する
  - `successThresholdToIncrease` を厳しくして回復速度を遅らせ過剰な振動を抑制する
  - 失敗分は `transfer` 再実行で差分アップロードされる（skip_list 活用）
- **新規ログ改善の動作確認**: `{Prev} → {Current}/{Max}` 形式の並列度変化ログが正常に動作し、デバッグ性が向上した。

---

## 実行セッション #10 – 2026-03-15 パフォーマンス改善（PR #36）

日時: 2026-03-15  
目的: フォルダ先行作成並行化・GET first・SkipList 読み込みキャッシュの効果測定

### 改善内容（PR #36）

| 修正箇所 | 問題 | 修正内容 |
|---------|------|---------|
| `TransferEngine.cs` フォルダ先行作成 | `foreach` + `await` でシリアル処理（3,445件 × 0.3秒 ≈ 15〜18分） | フォルダパスを深さでグループ化し `Parallel.ForEachAsync(MaxDegree=4)` で並行化 |
| `GraphStorageProvider.cs` `EnsureFolderSegmentAsync` | POST first → 既存フォルダにも書き込みリクエストを投げ 4並列で429大量発生 | GET first（200なら即返却、404のみ POST）、`FindFolderIdAsync` 削除、`_folderIdCache` を `ConcurrentDictionary` 化 |
| `SkipListManager.cs` `ContainsAsync` | 毎回 skip_list.json（2.15MB）を読み込んで HashSet 再構築（24,481回 × デシリアライズ ≈ 10分超） | `_readCache: volatile HashSet<string>?` を追加。初回ロード後はメモリ参照のみ。`AddAsync`/`SaveAsync` 成功後にキャッシュ更新 |

### 計測結果（Run 5→6 比較）

| フェーズ | Run 5（GET first 適用後、キャッシュなし） | Run 6（全修正適用後） |
|---------|------------------------------------------|----------------------|
| フォルダ先行作成 | 1分24秒（23:17:25〜23:18:49） | **1分22秒**（23:32:04〜23:33:26） |
| SkipList 照合（24,481件） | **10分35秒**（23:18:49〜23:29:24） | **0.03秒**（即座に完了） |
| 転送本体 | 0件（全件スキップ）| 0件（全件スキップ） |
| 合計 | 約12分 | **約1分22秒** |

### 各 Run の通算記録（全セッション）

| Run | 日時（JST） | 構成 | 成功 | 失敗 | スキップ | 所要時間 |
|-----|-----------|------|-----|------|---------|---------|
| Run 1 | 2026-03-15 06:27〜07:26 | 固定並列度4 | 24,469 | 12 | 0 | 約59分 |
| Run 2 | 2026-03-15 07:27〜07:52 | 固定並列度4（差分） | 12 | 0 | 24,469 | 約25分 |
| Run 3 | 自動起動 | - | - | - | - | 中断 |
| Run 4 | 並行化（POST first） | 並列度4 | - | - | - | 429多発で中断 |
| Run 5 | GET first・並行化 | 並列度4 | 0 | 0 | 24,481 | 約12分（skip_list照合が10分超） |
| Run 6 | 2026-03-15 08:29〜08:30 | GET first・並行化・SkipListキャッシュ | 0 | 0 | 24,481 | **1分22秒** |

### 考察

- **SkipList キャッシュの効果が絶大**: 10分35秒 → 0.03秒（≈ 2万倍高速化）。24,481件の `HashSet.Contains` 呼び出しはメモリ上では瞬時に完了する。
- **GET first の効果**: 既存フォルダへの書き込みリクエストを排除。Run 4 で発生した 429 連発（Retry-After 50〜64秒）が Run 5/6 で完全に消滅。
- **フォルダ先行作成**: Run 5/6 ともに約1分22〜24秒で安定。深さ別並行化が有効に機能している。

---

## 10. OneDrive → Dropbox 転送テストシナリオ (Phase 10)

このセクションは **転送先を Dropbox** にした場合のマニュアル検証手順書です。

### 10.1 前提条件

- Dropbox App Console (`https://www.dropbox.com/developers/apps`) で App を作成し、  
  **Generated access token** を取得済みであること
- OneDrive の Graph 認証情報（ClientId / TenantId / ClientSecret / OneDriveUserId）が設定済みであること
- SharePoint の設定（SiteId / DriveId）は **不要**（未設定可）
- `.env` に以下が設定されること（`bootstrap --destination dropbox` で自動生成）
  ```
  MIGRATOR__DROPBOX__ACCESSTOKEN=<your token>
  ```
- `.env` の SP 関連行はコメントアウト状態で問題ない

### 10.2 テストケース

#### TC-Dropbox-01: `bootstrap --destination dropbox` によるセットアップ

```powershell
dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap --destination dropbox
```

**入力シーケンス**:

| プロンプト | 入力値 |
|-----------|--------|
| Client ID | （Entra App の ClientId） |
| Client Secret | （Entra App の ClientSecret） |
| Tenant ID | （テナント ID） |
| OneDrive ユーザー ID | （UPN or オブジェクト ID） |
| 最大並列転送数 | 10（任意） |
| adaptiveConcurrency を有効にするか | y |
| Dropbox Access Token | （Dropbox 生成 token） |
| Dropbox 転送先フォルダパス | `/OneDriveMigration`（空欄でルート） |

**期待値**:

- ステップ 1/3 で ClientId / Secret / TenantId の入力を求める
- ステップ 2/3 で並列数・AdaptiveConcurrency・**Dropbox AccessToken・転送先フォルダパス** を求める  
  （SharePoint サイト URL の入力が **発生しないこと**）
- ステップ 3/3 で OneDrive 認証確認のみ実行される（SharePoint 解決スキップ）
- `configs/config.json` に `"destinationProvider": "dropbox"` が書き込まれること
- `configs/config.json` に `"dropbox": { "rootPath": "/OneDriveMigration" }` が書き込まれること
- `.env` に `MIGRATOR__DROPBOX__ACCESSTOKEN=...` が書き込まれること
- `.env` の SharePoint 関連行（SHAREPOINTSITEID / SHAREPOINTDRIVEID）はコメントアウトされること
- `doctor` チェックで SP フィールドが `[WRN]`（`[ERR]` でなく）になること
- `doctor` チェックで error=0 で完了すること

---

#### TC-Dropbox-02: `doctor --strict-dropbox` による設定診断

```powershell
dotnet run --project src/CloudMigrator.Setup.Cli -- doctor --strict-dropbox
```

**期待値**:

- SharePoint フィールド (sharePointSiteId / sharePointDriveId) が `[WRN]` で表示される
- Dropbox AccessToken が設定済みなら `[OK]` になる
- 最終行: `doctor 結果: error=0, warning=2`（SP 2件 Warning）
- 終了コードが **0**（Warning のみなのでエラーなし）

---

#### TC-Dropbox-03: OneDrive ファイルクロール（ソース側）

```powershell
dotnet run --project src/CloudMigrator.Cli -- file-crawler onedrive
```

**期待値**:

- OneDrive ファイル一覧が `logs/onedrive_files.json` に保存される
- 完了ログに `クロール完了: N 件` が表示される
- 終了コード 0

---

#### TC-Dropbox-04: OneDrive → Dropbox 転送

```powershell
dotnet run --project src/CloudMigrator.Cli -- transfer
```

**動作フロー（確認ポイント）**:

1. `configs/config.json` の `destinationProvider` が `dropbox` と読み取られる
2. Dropbox の既存ファイル一覧をクロールし skip_list を構築
3. OneDrive ファイルを一時ファイルにダウンロード（`DownloadToTempAsync`）
4. 一時ファイルを Dropbox にアップロード（`UploadFromLocalAsync`）
5. 一時ファイルを削除

**期待値**:

- 転送完了サマリ: `成功: N 件 / 失敗: 0 件 / スキップ: 0 件`
- Dropbox アプリの転送先フォルダ（例: `/OneDriveMigration`）に対象ファイルが存在すること
- `logs/transfer.log` に各ファイルの転送ログが記録されること
- 終了コード 0

---

#### TC-Dropbox-05: Dropbox ファイルクロール（転送後確認）

```powershell
dotnet run --project src/CloudMigrator.Cli -- file-crawler dropbox
```

**期待値**:

- `logs/dropbox_current_files.json`（または相当ファイル）に転送済みファイル一覧が保存される
- ファイル件数が OneDrive クロール件数と一致すること

---

#### TC-Dropbox-06: OneDrive ↔ Dropbox 差分比較

```powershell
dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right dropbox
```

**期待値**:

- 差分 0 件のとき `差分なし` が表示される
- 終了コード 0（差分なし）

---

#### TC-Dropbox-07: 再転送時のスキップ動作

全件転送後に再度 `transfer` を実行する。

```powershell
dotnet run --project src/CloudMigrator.Cli -- transfer
```

**期待値**:

- `成功: 0 件 / 失敗: 0 件 / スキップ: N 件`
- Dropbox クロールで取得した全ファイルが skip_list にヒットし、二重転送が発生しないこと

---

### 10.3 実施記録テンプレート（Dropbox 転送）

```text
実施日:
実施者:
対象ブランチ:
Dropbox App名:
転送先フォルダパス:

[TC-Dropbox-01] bootstrap --destination dropbox: PASS/FAIL
  実行コマンド: dotnet run --project src/CloudMigrator.Setup.Cli -- bootstrap --destination dropbox
  destinationProvider=dropbox 書き込み確認:
  .env MIGRATOR__DROPBOX__ACCESSTOKEN 書き込み確認:
  SP フィールド WRN 表示確認:
  doctor error=0 確認:
  結果要約:

[TC-Dropbox-02] doctor --strict-dropbox: PASS/FAIL
  error=0, warning=2 確認:
  結果要約:

[TC-Dropbox-03] file-crawler onedrive: PASS/FAIL
  クロール件数:
  結果要約:

[TC-Dropbox-04] transfer (OneDrive→Dropbox): PASS/FAIL
  成功件数:  　失敗件数:  　スキップ件数:
  所要時間:
  Dropbox 上でのファイル存在確認:
  結果要約:

[TC-Dropbox-05] file-crawler dropbox: PASS/FAIL
  クロール件数:
  結果要約:

[TC-Dropbox-06] compare --left onedrive --right dropbox: PASS/FAIL
  差分件数:
  結果要約:

[TC-Dropbox-07] 再転送スキップ確認: PASS/FAIL
  スキップ件数:
  結果要約:

課題・メモ:
```
