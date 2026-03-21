# タスク管理

詳細仕様: [docs/implementation-plan.md](docs/implementation-plan.md)  
設計計画: [docs/20260321-dropbox-optimization-plan.md](docs/20260321-dropbox-optimization-plan.md)  
前フェーズ履歴: [task-archive-20260321.md](task-archive-20260321.md)

## 現在の状態: Dropbox最適化フェーズ 完了（マージ済み・テスト済み）+ Web ダッシュボード実装完了

---

## Dropbox最適化フェーズ

### ステップ 1: 抽象インターフェース

- [x] `IMigrationPipeline.cs` 作成（`src/CloudMigrator.Core/Migration/`）

### ステップ 2: 状態レコード定義

- [x] `TransferRecord.cs` 作成（`TransferStatus` enum 含む）（`src/CloudMigrator.Core/State/`）

### ステップ 3: SQLite 抽象インターフェース

- [x] `ITransferStateDb.cs` 作成（`src/CloudMigrator.Core/State/`）

### ステップ 4: SQLite 実装

- [x] `SqliteTransferStateDb.cs` 作成（nextLink チェックポイント含む）（`src/CloudMigrator.Core/State/`）
- [x] `CloudMigrator.Core.csproj` に `Microsoft.Data.Sqlite` 追加

### ステップ 5: Dropbox パイプライン

- [x] `DropboxMigrationPipeline.cs` 作成（ストリーミング + フルパス転送 + DB 状態管理）（`src/CloudMigrator.Core/Migration/`）

### ステップ 6: SharePoint ラッパー

- [x] `SharePointMigrationPipeline.cs` 作成（既存 `TransferEngine` 委譲）（`src/CloudMigrator.Core/Migration/`）

### ステップ 7: 設定モデル更新

- [x] `MigratorOptions.cs` に `DropboxStateDb` パス追加（`PathOptions`）
- [x] `configs/config.json` に `dropboxStateDb` キー追加

### ステップ 8: CLI 接続

- [x] `TransferCommand.cs` を `IMigrationPipeline` 解決・分岐に更新

### ステップ 9: ユニットテスト

- [x] `SqliteTransferStateDb` テスト（初期化・CRUD・チェックポイント）
- [x] `DropboxMigrationPipeline` テスト（スキップ・pending→done・failed 再試行）

### ステップ 10: 完了処理

- [x] `dotnet build CloudMigrator.slnx` 確認（0 errors, 0 warnings）
- [x] `dotnet test tests/unit/` 確認（233 件 PASS）
- [x] `CHANGELOG.md` 更新
- [x] `task.md` 完了チェックボックス更新
- [x] コミット・PR 作成（PR #52 作成・マージ済み）

### ステップ 11: 追加タスク実装（追加品質改善）

- [x] `EnableEnsureFolder` フィーチャーフラグ化（`DropboxProviderOptions`, デフォルト: false）
- [x] 観測性メトリクス追加（フォルダAPI使用量・転送試行数・429発生率の Interlocked カウンタ）
- [x] 設計仕様ドキュメント（クラスコメント）追加
- [x] `NotifyRateLimit` への Retry-After 値伝搬修正（`DropboxApiException.Data["Retry-After"]` 経由）
- [x] `--full-rebuild` 時の WAL サイドカーファイル（`.db-wal`, `.db-shm`）削除対応
- [x] Date 形式 Retry-After の負値クランプ（`diff > TimeSpan.Zero` ガード）
- [x] ユニットテスト追加（Feature Flag ON/OFF、計 235 件 PASS）

### ステップ 12: マニュアルテスト（E2E 検証）

実施日: 2026-03-21  
対象ブランチ: main (98d5ed2)

- [x] TC-01: build → 0 errors, 0 warnings ✅
- [x] TC-02: cli --help → transfer/rebuild-skiplist/watchdog 等確認 ✅
- [x] TC-03: setup --help → bootstrap/doctor/init/verify 確認 ✅
- [x] TC-04: doctor → error=0, warning=0 ✅
- [x] TC-05: verify → Graph + Dropbox 全 API 接続 OK（トークン自動更新）✅
- [x] TC-06: file-crawler onedrive → **24,481 件** クロール・キャッシュ保存 ✅
- [x] TC-07: file-crawler dropbox → 0 件（転送前のため正常）✅
- [x] TC-08: transfer（Dropbox E2E）→ ファイル転送開始・SQLite DB 作成・429 検出＋リトライ動作 ✅
- [x] TC-09: transfer --full-rebuild → SQLite DB・WAL サイドカー削除（339,968B→4,096B）確認 ✅

### ステップ 13: 再テスト + ダッシュボード機能検証

実施日: 2026-03-21（ダッシュボード機能追加後）  
対象ブランチ: main (500497e)

- [x] TC-10: status（転送前）→「DB が見つかりません」メッセージ ✅ （logs/ 削除後）
- [x] TC-11: transfer（Dropbox フルリセット後 E2E）→ **24,481 件 成功 / 失敗 0 / 所要時間 02:39:23** ✅
- [x] TC-12: status（転送完了後）→ 100.0% / 24,481件 / 974.0 MB / 失敗 0件 ✅
  - ダッシュボード表示確認（プログレスバー・ステータス別件数・完了バイト数）
  - 転送中リアルタイム参照でも正常動作確認（70.7% → 80.3% → 83.0% → ... → 100.0%）

---

## ダッシュボード機能（status コマンド）

### 変更ファイル

- [x] `TransferDbSummary` レコード追加（`src/CloudMigrator.Core/State/TransferSummary.cs`）
- [x] `ITransferStateDb.GetSummaryAsync` 追加
- [x] `SqliteTransferStateDb.GetSummaryAsync` 実装（SQL 集計クエリ + 最近の失敗5件）
- [x] `TransferStatusCommand.cs` 追加（`src/CloudMigrator.Cli/Commands/`）
- [x] `Program.cs` に `status` コマンド登録
- [x] `SqliteTransferStateDbTests` に GetSummaryAsync テスト 5 件追加（計 240 件 PASS）

### 使い方

```bash
dotnet run --project src/CloudMigrator.Cli -- status --db logs/dropbox_transfer_state.db
# --db 省略時は configs/config.json の設定値（Paths.DropboxStateDb）を使用
```

---

## 将来フェーズ（今回スコープ外）

- SharePoint 版 TransferEngine 最適化（ストリーミング化・SQLite 化）
- サイズ・日時によるスキップ判定強化
- 本番切替計画・段階カットオーバー
- 運用手順書（セットアップ/日次/障害対応/ロールバック）

---

## Web ダッシュボード（dashboard コマンド）

### 実装済みファイル

- [x] `MetricPoint` レコード追加（`src/CloudMigrator.Core/State/TransferSummary.cs`）
- [x] `ITransferStateDb.RecordMetricAsync` / `GetMetricsAsync` 追加
- [x] `SqliteTransferStateDb`: `metrics` テーブル + 両メソッド実装
- [x] `DropboxMigrationPipeline`: 100 回ごとに `rate_limit_pct` を metrics 記録
- [x] `CloudMigrator.Dashboard` プロジェクト追加（Minimal API + Chart.js）
- [x] `DashboardCommand.cs` 追加（`--db`, `--port`, `--no-browser`）
- [x] `Program.cs` に `dashboard` コマンド登録
- [x] `CloudMigrator.slnx` 更新

### 使い方

```bash
dotnet run --project src/CloudMigrator.Cli -- dashboard
dotnet run --project src/CloudMigrator.Cli -- dashboard --db logs/dropbox_transfer_state.db --port 8080
dotnet run --project src/CloudMigrator.Cli -- dashboard --no-browser
```

### API エンドポイント

| エンドポイント | 説明 |
|---|---|
| `GET /` | Chart.js ダッシュボード UI |
| `GET /api/status` | ステータス別件数・完了率・バイト数 |
| `GET /api/metrics?name=rate_limit_pct&minutes=60` | 時系列メトリクス |
| `GET /api/errors` | 最近の失敗ファイル（最大5件） |

