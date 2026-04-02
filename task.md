# タスク管理

詳細仕様: [docs/implementation-plan.md](docs/implementation-plan.md)
前フェーズ履歴: [task-archive-20260323.md](task-archive-20260323.md)

## 現在の状態: Studio Ph-2 実装完了（PR 作成予定）

---

## CloudMigrator Studio（Issue #80）

| フェーズ | 内容 | Issue | 状態 |
|----------|------|-------|------|
| **Ph-1** | `GET/PUT /api/config` + 設定タブ UI | #81 | ✅ 完了 |
| **Ph-2** | 転送開始/停止 UI | #82 | ✅ 完了 |
| **Ph-3** | ログストリーミング SSE | #83 | 未着手 |
| **Ph-4** | 接続テスト UI | #84 | 未着手 |
| **Ph-5** | デフォルト起動 Studio | #85 | 未着手 |

---

## SharePoint 版最適化フェーズ

### 背景と目標

Dropbox 版の実装（PR #52〜#66）で SQLite 状態管理・ダッシュボード連携・AdaptiveConcurrencyController が確立された。
SharePoint 版はそれらを持たず、以下の課題がある。

| 課題 | 現状 | 目標 |
|------|------|------|
| ファイル管理 | JSON skip_list + CrawlCache（JSON）| SQLite DB（`ITransferStateDb`）に移行 |
| クロール方式 | 全件メモリロード（`ListItemsAsync`）| `ListPagedAsync` ページングで SQLite に逐次保存 |
| フォルダ先行作成 | 全件ロード後に深さソート | **フォルダ作成フェーズ**として明示化・進捗をダッシュボードに表示 |
| 中断リカバリ | skip_list 再クロールのみ | 4フェーズそれぞれにチェックポイントを持ち再開可能 |
| ダッシュボード | 未対応 | クロール・フォルダ作成・転送の各フェーズ進捗を表示 |
| エラー管理 | ログのみ | SQLite に失敗ファイル・リトライ回数を記録 |

---

### SharePoint パイプライン設計（4フェーズ構造）

SharePoint は `AutoCreateParentFolders == false` であり、フォルダを深さ順に先行作成しないとアップロードがエラーになる。
深さソートには全フォルダ一覧が必要なため、**クロール完了後にフォルダ作成フェーズを実行**する構造となる。
Dropbox 版のような完全なストリーミング（クロール中に並行転送）は取れない。

```
Phase A: リカバリ
  processing → pending リセット（クラッシュリカバリ）

Phase B: クロール（ページング）
  ListPagedAsync でページごとに DB へ UpsertPendingIfNotTerminalAsync（N+1 回避）
  カーソルをチェックポイント保存（中断再開対応）
  ┌─ ダッシュボード: sp_crawl_pages（累積ページ数）を RecordMetric
  └─ 完了時: crawl_total / crawl_complete チェックポイント保存

Phase C: フォルダ先行作成
  DB からフォルダパス一覧を取得 → 全祖先展開（HashSet） → 深さ順ソート → 同一深さを並列 EnsureFolderAsync
  ┌─ ダッシュボード: sp_folder_done（作成済み件数）を RecordMetric
  └─ 完了時: folder_creation_complete チェックポイント保存

Phase D: ファイル転送（Consumer）
  DB の pending/processing/failed レコードを並列転送
  ┌─ AdaptiveConcurrencyController で動的並列度制御
  ├─ 100 件ごと / 並列度変化時に current_parallelism を RecordMetric
  ├─ 100 件ごとに throughput_files_per_min / throughput_bytes_per_sec / rate_limit_pct を RecordMetric
  └─ pipeline_started_at チェックポイント（初回のみ）
```

**チェックポイント一覧（SQLite checkpoints テーブル）**

| キー | 意味 | 再開時の挙動 |
|------|------|-------------|
| `pipeline_started_at` | パイプライン初回開始時刻 | 存在すれば上書きしない |
| `sp_cursor` | クロールの nextLink | Phase B 再開位置 |
| `crawl_total` | クロール確定総数（ファイル＋フォルダ） | Phase B 完了後に保存 |
| `crawl_complete` | クロール完了フラグ | `"true"` なら Phase B スキップ |
| `folder_total` | 作成対象フォルダ数 | Phase C 開始時に保存（進捗バー用） |
| `folder_creation_complete` | フォルダ作成完了フラグ | `"true"` なら Phase C スキップ |

> **Phase D の失敗アイテム再試行について**
> Phase D 中に失敗したアイテムは `failed` 状態で DB に残る。次回実行時の Phase A でリカバリされる（Dropbox 版と同じ設計）。
> 同一実行内での即時再試行は行わない（複雑性コストが高く、Dropbox 版で実績のあるアプローチで十分）。

**ダッシュボード追加メトリクス（SQLite metrics テーブル）**

| メトリクス名 | タイミング | 意味 |
|---|---|---|
| `sp_crawl_pages` | Phase B 各ページ | 発見ページ数の推移 |
| `sp_folder_done` | Phase C 各フォルダ作成後 | フォルダ作成進捗 |
| `throughput_files_per_min` | Phase D 100件ごと | 転送スループット |
| `throughput_bytes_per_sec` | Phase D 100件ごと | 転送バイトレート |
| `rate_limit_pct` | Phase D 100件ごと | 429 発生率 |
| `current_parallelism` | Phase D 変化時・100件ごと | 現在の並列度 |

---

### ステップ 1: 設定モデル更新

- [x] `PathOptions` に `SharePointStateDb` パスを追加（`src/CloudMigrator.Core/Configuration/MigratorOptions.cs`）
- [x] `configs/config.json` に `sharePointStateDb` キー追加（デフォルト: `logs/sharepoint_transfer_state.db`）

---

### ステップ 2: SharePoint 専用パイプライン作成

現在の `SharePointMigrationPipeline`（`TransferEngine` ラッパー）を廃止し、全面再実装する。

- [x] `SharePointMigrationPipeline.cs` を 4フェーズ構造で再実装
  - **Phase A**: `ResetProcessingAsync` で processing → pending リセット（クラッシュリカバリ）
  - **Phase B**: `ListPagedAsync` でページごとに `UpsertPendingIfNotTerminalAsync`（N+1 回避）
    - カーソルをチェックポイント保存（中断再開対応）
    - 各ページ処理後に `sp_crawl_pages` を `RecordMetricAsync`
    - `crawl_complete == "true"` のチェックポイントがあればスキップ（再実行対応）
  - **Phase C**: DB から `DISTINCT` フォルダパス抽出 → `HashSet` で全祖先展開 → 深さ順ソート → 同一深さを並列 `EnsureFolderAsync`
    - Phase C 開始時に `folder_total` チェックポイント保存（ダッシュボードの進捗バー用）
    - 作成都度 `sp_folder_done` を `RecordMetricAsync`
    - `folder_creation_complete == "true"` があればスキップ（再実行対応）
  - **Phase D**: `GetPendingStreamAsync` → bounded Channel（容量 1000）→ 並列転送（Dropbox 版 Consumer と同構造）
    - `AdaptiveConcurrencyController` 動的並列度制御
    - 100 件ごと / 並列度変化時に metrics 記録
    - `pipeline_started_at` チェックポイント（初回のみ）
- [x] `ITransferStateDb` に `ResetProcessingAsync`・`UpsertPendingIfNotTerminalAsync`・`InsertDoneIfNotExistsAsync`・`GetDistinctFolderPathsAsync` を追加
- [x] `GraphStorageProvider.EnsureFolderAsync` が 409 Conflict を正しく無視することを確認済み
- [x] `TransferEngine` への依存を除去（`SharePointMigrationPipeline` が直接 `IStorageProvider` を使う）

---

### ステップ 3: ダッシュボード フェーズ表示対応

SharePoint 版の4フェーズ進捗をダッシュボードに表示するため、API と UI を拡張する。

- [x] `/api/phase` エンドポイント追加（`crawl_complete`・`folder_creation_complete` チェックポイントでフェーズ判定）
  - `crawl_complete != "true"` → `phase: "crawling"`
  - `crawl_complete == "true"` かつ `folder_creation_complete != "true"` → `phase: "folder_creation"`
  - `folder_creation_complete == "true"` → `phase: "transferring"`
- [x] ダッシュボード UI にフェーズバッジ（クロール中=黄 / フォルダ作成中=紫 / 転送中=緑）を追加
- [x] `sp_folder_done` を使ったフォルダ作成進捗バー（`folder_total` チェックポイントとの比較）
- [x] `DashboardCommand` のデフォルト DB を `destinationProvider` に応じて切り替え（SP: `SharePointStateDb`、Dropbox: `DropboxStateDb`）
- [ ] `sp_crawl_pages` を使ったクロール進捗表示（ページ数 or 件数）— 将来対応

---

### ステップ 4: skip_list → SQLite 移行対応

- [x] 初回起動時（DB が存在しない場合）に既存 skip_list から SQLite へのマイグレーション
  - skip_list の各エントリを `done` ステータスのレコードとして INSERT
- [x] `TransferCommand.RunCoreAsync` の SharePoint ブランチを新パイプライン呼び出しに変更
  - `SqliteTransferStateDb` を生成して `SharePointMigrationPipeline` に渡す
  - `fullRebuild` / `hashChanged` 時に DB をリセット（WAL/SHM サイドカーも個別削除）
- [ ] `CrawlCache`（JSON）・`RebuildSkipListFromSharePointAsync` を SharePoint フローから除去（ステップ 5 で対応）

---

### ステップ 5: 不要コードの整理

- [x] `SharePointMigrationPipeline.cs`（旧 TransferEngine ラッパー）が完全に置き換わったことを確認
- [x] `TransferEngine.cs` が Dropbox 以外から参照されなくなったことを確認（本番コードからの呼び出しなし・段階的廃止で可）
- [x] `CrawlCache.cs` SharePoint フロー参照除去: `RebuildSkipListFromSharePointAsync` を `TransferCommand` から削除、`RebuildSkipListCommand` を廃止メッセージに更新

---

### ステップ 6: ユニットテスト

- [x] `SharePointMigrationPipelineTests.cs` 作成（19テスト追加）
  - Phase A: `ResetProcessingAsync` が呼ばれる
  - Phase B クロール → `UpsertPendingIfNotTerminalAsync` とカーソル保存が実行される
  - Phase B スキップ（`crawl_complete == "true"` の場合）
  - Phase B: フォルダアイテムはスキップされる
  - Phase C フォルダ先行作成 → 深さ順に `EnsureFolderAsync` が呼ばれる・`folder_total` チェックポイントが保存される
  - Phase C: 重複パスの一意化
  - Phase C スキップ（`folder_creation_complete == "true"` の場合）
  - Phase D 転送成功 → `MarkDoneAsync` が呼ばれる
  - Phase D 転送失敗 → `MarkFailedAsync` が呼ばれる
  - 100 件転送で throughput メトリクスが記録される
  - `current_parallelism` 即時記録（controller あり）

---

### ステップ 7: E2E 検証

- [ ] TC-01: `dotnet build` → 0 errors, 0 warnings
- [ ] TC-02: `dotnet test` → 全テスト PASS
- [ ] TC-03: `transfer`（SharePoint モード）→ SQLite DB 作成・4フェーズ実行確認
- [ ] TC-04: `transfer --full-rebuild` → DB・WAL/SHM サイドカー削除確認
- [ ] TC-05: `transfer`（Phase B 途中でCtrl+C → 再実行）→ カーソルから再開・フォルダ重複作成なし
- [ ] TC-06: `transfer`（Phase C 途中でCtrl+C → 再実行）→ `folder_creation_complete` チェックポイントで Phase C スキップ
- [ ] TC-07: `dashboard` → SharePoint DB でクロール/フォルダ作成/転送フェーズバッジが正しく切り替わる

---

## 将来フェーズ（今回スコープ外）

- ダッシュボードからの転送計画立案（並列数・スループット目標の設定 UI）
- サイズ・更新日時によるスキップ判定強化（上書き判定）
- 本番切替計画・段階カットオーバー手順書
- 運用手順書（セットアップ / 日次 / 障害対応 / ロールバック）
