# タスク管理

詳細仕様: [docs/implementation-plan.md](docs/implementation-plan.md)  
設計計画: [docs/20260321-dropbox-optimization-plan.md](docs/20260321-dropbox-optimization-plan.md)  
前フェーズ履歴: [task-archive-20260321.md](task-archive-20260321.md)

## 現在の状態: Dropbox最適化フェーズ 実装中

---

## Dropbox最適化フェーズ

### ステップ 1: 抽象インターフェース

- [ ] `IMigrationPipeline.cs` 作成（`src/CloudMigrator.Core/Migration/`）

### ステップ 2: 状態レコード定義

- [ ] `TransferRecord.cs` 作成（`TransferStatus` enum 含む）（`src/CloudMigrator.Core/State/`）

### ステップ 3: SQLite 抽象インターフェース

- [ ] `ITransferStateDb.cs` 作成（`src/CloudMigrator.Core/State/`）

### ステップ 4: SQLite 実装

- [ ] `SqliteTransferStateDb.cs` 作成（nextLink チェックポイント含む）（`src/CloudMigrator.Core/State/`）
- [ ] `CloudMigrator.Core.csproj` に `Microsoft.Data.Sqlite` 追加

### ステップ 5: Dropbox パイプライン

- [ ] `DropboxMigrationPipeline.cs` 作成（ストリーミング + フルパス転送 + DB 状態管理）（`src/CloudMigrator.Core/Migration/`）

### ステップ 6: SharePoint ラッパー

- [ ] `SharePointMigrationPipeline.cs` 作成（既存 `TransferEngine` 委譲）（`src/CloudMigrator.Core/Migration/`）

### ステップ 7: 設定モデル更新

- [ ] `MigratorOptions.cs` に `DropboxStateDb` パス追加（`PathOptions`）
- [ ] `configs/config.json` / `sample.env` に `dropboxStateDb` キー追加

### ステップ 8: CLI 接続

- [ ] `CliServices.cs` に `IMigrationPipeline` DI 登録追加
- [ ] `TransferCommand.cs` を `IMigrationPipeline` 解決・分岐に更新

### ステップ 9: ユニットテスト

- [ ] `SqliteTransferStateDb` テスト（初期化・CRUD・チェックポイント）
- [ ] `DropboxMigrationPipeline` テスト（スキップ・pending→done・failed 再試行）

### ステップ 10: 完了処理

- [ ] `dotnet build CloudMigrator.slnx` 確認
- [ ] `dotnet test tests/unit/` 確認（全件 PASS）
- [ ] `CHANGELOG.md` 更新
- [ ] `task.md` 完了チェックボックス更新
- [ ] コミット・PR 作成

---

## 将来フェーズ（今回スコープ外）

- SharePoint 版 TransferEngine 最適化（ストリーミング化・SQLite 化）
- サイズ・日時によるスキップ判定強化
- E2E テスト（実 Dropbox API）
- 本番切替計画・段階カットオーバー
- 運用手順書（セットアップ/日次/障害対応/ロールバック）

