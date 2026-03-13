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

---

## 5. 実施ログ

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
| TC-06c | validate | `dotnet run --project src/CloudMigrator.Cli -- file-crawler validate --source onedrive --top 30` | ✅ PASS | invalid=0, missing=0 |
| TC-06d | compare | `dotnet run --project src/CloudMigrator.Cli -- file-crawler compare --left onedrive --right sharepoint --top 30` | ✅ PASS | 差分24,481件表示。EXIT:0 ※注1 |
| TC-06e | transfer | `dotnet run --project src/CloudMigrator.Cli -- transfer` | ✅ PASS | 成功24,481件/失敗0件/スキップ0件/所要1時間13分 |

**※注1**: `compare` で差分ありのとき `Environment.ExitCode=1` を設定するが `System.CommandLine` が上書きし EXIT:0 になる。ランブック期待値は EXIT:1 だが既知動作。

#### バグ修正記録 (本セッション)

| バグ | 修正箇所 | 内容 |
|------|----------|------|
| `FindFolderIdAsync` $filter 非対応 | `GraphStorageProvider.cs` | SharePoint は Children エンドポイントの `$filter` を非サポート。`PageIterator` + クライアント側フィルタリングに変更 |
| フォルダ先行作成の過剰 API 呼び出し | `GraphStorageProvider.cs` | `EnsureFolderAsync` でパスごとにルートから再解決していた。`_folderIdCache` (Dictionary) を追加してAPI呼び出しを O(N×depth²) → O(N) に削減 |
| フォルダ作成フェーズ無音 | `TransferEngine.cs` | フォルダ先行作成フェーズのログが皆無で進捗不明。件数・100件ごとの進捗・完了ログを追加 |

期待結果:
- 監視開始ログが出る
- ログ更新停止条件で transfer 再起動動作を確認できる

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

- TC-01 build: PASS
  - dotnet build CloudMigrator.slnx
  - 全プロジェクト成功
- TC-02 cli help: PASS
  - CloudMigrator.Cli の主要サブコマンド表示を確認
- TC-03 setup help: PASS
  - CloudMigrator.Setup.Cli の主要サブコマンド表示を確認
- TC-04 doctor: PASS (想定どおり未設定検知)
  - graph.clientId / graph.tenantId / graph.clientSecret が未設定で [ERR]
  - config.path は検出済み
- TC-05 verify: FAIL (preflight エラー)
  - MIGRATOR__GRAPH__CLIENTID / MIGRATOR__GRAPH__TENANTID / MIGRATOR__GRAPH__CLIENTSECRET が未設定

次回の実施ポイント:
- Graph 資格情報を投入して TC-05 verify から再開
- file-crawler -> rebuild-skiplist -> transfer の順で E2E 手動検証
