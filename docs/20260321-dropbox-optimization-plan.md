# 2026-03-21 Dropbox最適化フェーズ 実装計画

## 背景・動機

Phase 1〜7 および各バグ修正・チューニングを経て、SharePoint 向け転送の基盤は完成した。
現行 `TransferEngine` は SharePoint 固有の制約（フォルダ先行作成・バッチ型クロール・JSON skiplist）に最適化されており、
Dropbox へそのまま適用するとボトルネックが生じる（フォルダ作成 API 連発による 429・初動遅延・再開不完全）。

本フェーズでは **Dropbox 版のみをスコープ** として、アーキテクチャを抜本的に改善する。

---

## 合意済み設計方針

| 項目 | 決定内容 |
|---|---|
| フォルダ先行作成 | **廃止**（Dropbox はフルパス直転送で親フォルダを自動生成） |
| 処理モデル | バッチ型 → **ストリーミング型**（列挙しながら即転送） |
| 状態管理 | JSON skiplist → **SQLite 状態 DB**（pending/done/failed + nextLink） |
| スキップ判定 | FR-07 維持（**path + name のみ**、サイズ・日時は記録するが判定不使用） |
| チェックポイント | **nextLink を今回含める**（DB の `checkpoints` テーブルに保存） |
| SharePoint 版 | **今回手を入れない**（JSON skiplist・TransferEngine・フォルダ先行作成は維持） |
| 実装分割 | `IMigrationPipeline` 抽象で Dropbox / SharePoint を config 分岐 |

---

## アーキテクチャ概要

### 変更しないもの

```
src/CloudMigrator.Core/Transfer/TransferEngine.cs        ← SharePoint 版として維持
src/CloudMigrator.Core/Storage/SkipListManager.cs        ← SharePoint 版が引き続き使用
src/CloudMigrator.Providers.Abstractions/IStorageProvider.cs  ← 変更なし
```

### 新規追加コンポーネント

```
src/CloudMigrator.Core/Migration/
  IMigrationPipeline.cs              ← 抽象インターフェース
  DropboxMigrationPipeline.cs        ← Dropbox 専用実装
  SharePointMigrationPipeline.cs     ← 既存 TransferEngine のラッパー

src/CloudMigrator.Core/State/
  ITransferStateDb.cs                ← SQLite 抽象インターフェース
  SqliteTransferStateDb.cs           ← Dropbox 専用 SQLite 実装
  TransferRecord.cs                  ← 状態レコード定義
```

---

## SQLite スキーマ

```sql
-- 転送状態管理
CREATE TABLE IF NOT EXISTS transfer_records (
    path        TEXT NOT NULL,
    name        TEXT NOT NULL,
    size_bytes  INTEGER,           -- 記録用（判定不使用）
    modified    TEXT,              -- 記録用（判定不使用）
    status      TEXT NOT NULL DEFAULT 'pending',  -- pending / done / failed
    error       TEXT,              -- 失敗時エラーメッセージ
    updated_at  TEXT NOT NULL,     -- ISO 8601 UTC
    PRIMARY KEY (path, name)
);

-- クロールチェックポイント
CREATE TABLE IF NOT EXISTS checkpoints (
    key         TEXT PRIMARY KEY,  -- 例: 'nextLink'
    value       TEXT NOT NULL,
    updated_at  TEXT NOT NULL      -- ISO 8601 UTC
);
```

- `status` 判定キーは `path + name`（FR-07 維持）
- `size_bytes` / `modified` は可観測性・デバッグ用途のみ
- DB ファイルパスは `MigratorOptions.Paths` に `DropboxStateDb` として追加

---

## 処理フロー（DropboxMigrationPipeline）

```
1. DB に nextLink チェックポイントが存在すれば読み込む
      ↓
2. IStorageProvider.ListItemsAsync（IAsyncEnumerable）でストリーミング列挙
      ↓
3. 各ファイルについて：
   a. status = done  → スキップ
   b. status = failed → status を pending に更新（再試行対象）
   c. 未存在          → INSERT status = pending
      ↓
4. Channel<TransferJob> に投入（Producer）
      ↓
5. SemaphoreSlim（並列数: AdaptiveConcurrencyController）で Consumer が Dropbox フルパス転送
      ↓
6. 成功 → status = done
   失敗 → status = failed + error 記録 + リトライキューへ
      ↓
7. クロール完了ごとに nextLink を checkpoints テーブルへ保存
      ↓
8. TransferSummary（Success / Failed / Skipped / Elapsed）を返す
```

---

## インターフェース定義

### IMigrationPipeline

```csharp
public interface IMigrationPipeline
{
    Task<TransferSummary> RunAsync(CancellationToken ct);
}
```

### ITransferStateDb

```csharp
public interface ITransferStateDb : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct);
    Task<TransferStatus> GetStatusAsync(string path, string name, CancellationToken ct);
    Task UpsertPendingAsync(StorageItem item, CancellationToken ct);
    Task MarkDoneAsync(string path, string name, CancellationToken ct);
    Task MarkFailedAsync(string path, string name, string error, CancellationToken ct);
    Task<string?> GetCheckpointAsync(string key, CancellationToken ct);
    Task SaveCheckpointAsync(string key, string value, CancellationToken ct);
    Task<IReadOnlyList<TransferRecord>> GetPendingAsync(CancellationToken ct);
}
```

### TransferRecord

```csharp
public sealed record TransferRecord
{
    public required string Path   { get; init; }
    public required string Name   { get; init; }
    public long?   SizeBytes      { get; init; }
    public string? Modified       { get; init; }
    public required TransferStatus Status { get; init; }
    public string? Error          { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public enum TransferStatus { Pending, Done, Failed }
```

---

## TransferCommand 分岐

```csharp
var pipeline = options.DestinationProvider switch
{
    "dropbox"    => serviceProvider.GetRequiredService<DropboxMigrationPipeline>(),
    "sharepoint" => serviceProvider.GetRequiredService<SharePointMigrationPipeline>(),
    _            => throw new InvalidOperationException(
                        $"未知の転送先プロバイダー: {options.DestinationProvider}")
};
await pipeline.RunAsync(ct);
```

---

## 実装順序

| ステップ | 対象 | 説明 |
|---|---|---|
| 1 | `IMigrationPipeline.cs` | 抽象インターフェース |
| 2 | `TransferRecord.cs` + `TransferStatus` | 状態レコード定義 |
| 3 | `ITransferStateDb.cs` | SQLite 抽象インターフェース |
| 4 | `SqliteTransferStateDb.cs` | SQLite 実装（nextLink チェックポイント含む） |
| 5 | `DropboxMigrationPipeline.cs` | ストリーミング + フルパス転送 + DB 状態管理 |
| 6 | `SharePointMigrationPipeline.cs` | 既存 `TransferEngine` のラッパー |
| 7 | `MigratorOptions.cs` 更新 | `DropboxStateDb` パス追加 |
| 8 | `CliServices.cs` / `TransferCommand.cs` 更新 | `IMigrationPipeline` 解決・分岐 |
| 9 | ユニットテスト | `SqliteTransferStateDb`・`DropboxMigrationPipeline` |
| 10 | `CHANGELOG.md` / `task.md` 更新 | フェーズ完了記録 |

---

## NuGet 追加パッケージ

| パッケージ | 用途 |
|---|---|
| `Microsoft.Data.Sqlite` | SQLite アクセス |

---

## スコープ外（今フェーズでは手を入れない）

- SharePoint 版 TransferEngine の改修
- SkipListManager の SQLite 化
- サイズ・日時によるスキップ判定
- E2E テスト（実 Dropbox API）
- SharePoint / その他クラウドドライブ対応

---

## 関連ドキュメント

- [docs/implementation-plan.md](implementation-plan.md)：全体仕様
- [task-archive-20260321.md](../task-archive-20260321.md)：Phase 1〜7 完了タスク一覧
