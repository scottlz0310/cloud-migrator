# アーキテクチャ

## 概要

`cloud-migrator` は OneDrive を転送元として、`sharepoint` または `dropbox` を転送先に選べる .NET 10 ベースの移行 CLI です。実行時の中心は `CloudMigrator.Cli` で、設定解決、認証、プロバイダー生成、状態 DB、並列制御、ログ出力を束ねてから、転送先ごとのパイプラインへ処理を委譲します。

## レイヤ構成

| レイヤ | プロジェクト | 主責務 |
|---|---|---|
| Entry points | `src/CloudMigrator.Cli`, `src/CloudMigrator.Setup.Cli` | CLI コマンド公開、設定ロード、ユースケース起動 |
| Domain / Use case | `src/CloudMigrator.Core` | 移行パイプライン、転送制御、設定モデル、状態 DB |
| Provider contract | `src/CloudMigrator.Providers.Abstractions` | `IStorageProvider` などの抽象契約 |
| Provider implementation | `src/CloudMigrator.Providers.Graph`, `src/CloudMigrator.Providers.Dropbox` | Graph / Dropbox API 実装、認証、再試行、アップロード |
| Observability | `src/CloudMigrator.Observability`, `src/CloudMigrator.Dashboard` | 構造化ログ、SQLite メトリクス参照、ダッシュボード API/UI |
| Testing | `src/CloudMigrator.Testing`, `tests/unit`, `tests/integration`, `tests/e2e` | テスト補助、回帰テスト、品質ゲート |

## 実行時コンポーネント

```text
CloudMigrator.Cli
  -> AppConfiguration
  -> CliServices
     -> LoggingSetup (Serilog)
     -> GraphAuthenticator / GraphClientFactory
     -> GraphStorageProvider
     -> DropboxStorageProvider
     -> SkipListManager / CrawlCache
     -> AdaptiveConcurrencyController / TokenBucketRateLimiter
  -> TransferCommand
     -> SharePointMigrationPipeline or DropboxMigrationPipeline
        -> SqliteTransferStateDb
        -> IStorageProvider (source/destination)
```

## コア設計

### 1. 設定と依存解決

- `AppConfiguration` が `configs/config.json` を解決し、最後に環境変数を上書きします。
- 機密値は `config.json` に持たず、`MIGRATOR__GRAPH__CLIENTSECRET` や `MIGRATOR__DROPBOX__CLIENTSECRET` などの環境変数から直接取得します。
- `CliServices` がロガー、認証、Graph/Dropbox プロバイダー、スキップリスト、クロールキャッシュ、並列制御をまとめて構築します。

### 2. プロバイダー境界

- `IStorageProvider` が一覧取得、ダウンロード、アップロード、フォルダ作成の共通契約です。
- Graph 実装は OneDrive からのクロールと SharePoint への転送を兼務します。
- Dropbox 実装は upload session とトークン更新を担当し、通常は親フォルダ自動作成を前提に動作します。

### 3. 状態管理

- `SqliteTransferStateDb` は `transfer_records`, `checkpoints`, `metrics` の 3 テーブルで構成されます。
- `transfer_records` は `pending`, `processing`, `done`, `failed`, `permanent_failed` を保持し、再実行やリカバリの基準になります。
- `checkpoints` は Graph クロールカーソル、フェーズ完了、確定総数、開始時刻などの長寿命状態を保持します。
- `metrics` はレート制限率、スループット、並列度の時系列を蓄積し、CLI と Web ダッシュボードが参照します。

### 4. 移行パイプライン

- SharePoint 向けは `SharePointMigrationPipeline` が 4 フェーズで動きます。
  - Phase A: `processing` を `pending` に戻してクラッシュリカバリ
  - Phase B: OneDrive をページングクロールして SQLite に集約
  - Phase C: 転送先フォルダを深さ順に事前作成
  - Phase D: Channel + 並列ワーカーでファイル転送
- Dropbox 向けは `DropboxMigrationPipeline` が 3 フェーズで動きます。
  - Phase A: `processing` と `permanent_failed` の再試行復帰
  - Phase B: OneDrive をクロールして新規ファイルだけを SQLite に登録
  - Phase D: SQLite をキュー化して Dropbox へストリーム転送

### 5. 並列制御

- 基本形は `Channel.CreateBounded<T>` と `Parallel.ForEachAsync` の組み合わせです。
- SharePoint / Dropbox ごとに `AdaptiveConcurrencyController` を切り替えられます。
- 補助的に `TokenBucketRateLimiter` も用意されていますが、両方有効な場合は AdaptiveConcurrency を優先します。

## 補助コンポーネント

- `CloudMigrator.Setup.Cli`
  - `bootstrap`, `doctor`, `init`, `verify` を提供し、初期設定と疎通確認を担当します。
- `CloudMigrator.Dashboard`
  - `SqliteTransferStateDb` を直接参照し、`/api/status`, `/api/metrics`, `/api/phase`, `/api/errors` を提供します。
- `TransferStatusCommand`
  - Dropbox 状態 DB をテキスト UI で参照します。

## 永続化アセット

| 種別 | 既定パス | 用途 |
|---|---|---|
| Runtime config | `configs/config.json` | 非機密設定 |
| Transfer log | `logs/transfer.log` | Serilog CLEF ログ |
| SharePoint state | `logs/sharepoint_transfer_state.db` | SharePoint 移行状態 |
| Dropbox state | `logs/dropbox_transfer_state.db` | Dropbox 移行状態 |
| Skip list | `logs/skip_list.json` | 旧フロー互換と再構築用 |
| Upload sessions | `logs/upload_sessions.json` | Graph 大容量アップロード再開 |
| Rate state | `logs/rate_state.json` | Token Bucket の前回レート復元 |

## 品質と CI

- GitHub Actions の `ci.yml` が format, build, test, artifact upload を担当します。
- unit テストは XPlat Code Coverage で Cobertura XML を生成します。
- pull request では `quality-gate` ジョブが `quality-metrics` と `security-scan` を実行します。
- Codecov への送信は Ubuntu ジョブで一度だけ行い、生成済み Cobertura XML をそのまま利用します。
