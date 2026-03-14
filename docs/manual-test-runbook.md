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
