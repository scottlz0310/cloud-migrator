# CloudMigrator 実装計画書

## 1. 目的

現行 Python 実装（[Bulk-Migrator](../Bulk-Migrator)）の仕様を欠落なく引き継ぎつつ、**C# / .NET** で新規リポジトリへリライトする。

- 対象: OneDrive → SharePoint の大容量ファイル移行ツール群
- 方針: 仕様互換を最優先し、段階的に置換する
- 成果物: 本リポジトリ（.NET 実装、CI、運用ドキュメント、移行手順）

---

## 2. 採用技術

| 項目 | 採用内容 |
|---|---|
| 言語・実行基盤 | C# / .NET 10 LTS |
| CLI | `System.CommandLine` |
| 設定 | `Microsoft.Extensions.Configuration`（env > file > default） |
| 構造化ログ | `Microsoft.Extensions.Logging` + Serilog（JSON sink） |
| HTTP | `HttpClient` / `SocketsHttpHandler` |
| Graph | Microsoft Graph SDK（LargeFileUploadTask, RetryHandler） |
| 並列処理 | `Parallel.ForEachAsync` + `System.Threading.Channels` |
| テスト | xUnit + FluentAssertions + Moq |

### プロバイダー拡張（Dropbox 等）
`IStorageProvider` で provider を抽象化し、Graph / Dropbox の実装を並行して提供する。

---

## 3. 仕様互換要件

### 3.1 コア機能（FR）

| ID | 仕様 |
|---|---|
| FR-01 | Microsoft Graph client credentials 認証・トークン有効期限前再取得 |
| FR-02 | OneDrive 指定ルートの再帰クロール（重複排除、path/name/id/size/更新日時保持） |
| FR-03 | SharePoint ドキュメントライブラリ配下の再帰クロール |
| FR-04 | 4MB 未満: 単純 PUT アップロード |
| FR-05 | 4MB 以上: Upload Session + チャンクアップロード |
| FR-06 | 転送先フォルダ自動作成（階層を順次作成） |
| FR-07 | スキップリスト管理（判定キー: **path + name**、サイズ/時刻は判定に使わない） |
| FR-08 | 転送成功後に skip_list へ原子的追加（排他制御あり） |
| FR-09 | OneDrive/SharePoint クロール結果のキャッシュファイル化 |
| FR-10 | 設定変更検知（設定ハッシュ）とキャッシュ・ログのクリア |
| FR-11 | `--reset`: 再構築のみ実施（転送なし） |
| FR-12 | `--full-rebuild`: キャッシュ再作成 + 再構築 + 転送実行 |
| FR-13 | 通常実行: skip_list 不在時は自動再構築後に転送 |
| FR-14 | 並列転送（max_parallel_transfers 相当） |
| FR-15 | ネットワーク失敗時リトライ（指数バックオフ + ジッター） |
| FR-16 | watchdog によるフリーズ監視（ログ無更新 10 分で再起動） |
| FR-17 | 転送残ありで main 正常終了時に watchdog が再起動継続 |
| FR-18 | file_crawler CLI（onedrive/sharepoint/skiplist/compare/validate/explore） |

### 3.2 品質・監視（NFR）

| ID | 仕様 |
|---|---|
| NFR-01 | 構造化 JSON ログ（timestamp UTC ISO8601, level, event, message, logger, module, trace_id, span_id, request_id） |
| NFR-02 | 機密情報マスキング（secret/token/password/api_key 等） |
| NFR-03 | 転送ログ・watchdog ログ・監査ログの分離 |
| NFR-04 | 品質メトリクス収集（coverage/lint/type/security/tests） |
| NFR-05 | 品質アラート（coverage<60, lint>0, type>0, security>0, failed_tests>0） |
| NFR-06 | 月次/四半期/半年レポート生成 |
| NFR-07 | セキュリティスキャン統合サマリ出力 |

### 3.3 設定・運用（OPS）

| ID | 仕様 |
|---|---|
| OPS-01 | 設定優先順位: **環境変数 > config file > default** |
| OPS-02 | 現行 `.env` キー互換（Graph 資格情報、OneDrive/SharePoint ID 群） |
| OPS-03 | `config/config.json` 相当（chunk_size_mb, large_file_threshold_mb, max_parallel_transfers, retry_count, timeout_sec, paths） |
| OPS-04 | ログ/レポート/キャッシュ出力先の外部設定可能化 |
| OPS-05 | CLI サブコマンド互換（transfer/rebuild-skiplist/watchdog/quality-metrics/security-scan/file-crawler） |
| OPS-06 | CI 品質ゲート互換（lint/format/type/test+coverage/security） |
| OPS-07 | Renovate/依存更新運用の維持 |

---

## 4. アーキテクチャ

```
cloud-migrator/
├─ src/
│  ├─ CloudMigrator.Cli/                    # System.CommandLine によるエントリーポイント
│  ├─ CloudMigrator.Core/                   # ドメイン・ユースケース
│  ├─ CloudMigrator.Providers.Abstractions/ # IStorageProvider 等の契約
│  ├─ CloudMigrator.Providers.Graph/        # Graph 認証・転送実装
│  ├─ CloudMigrator.Providers.Dropbox/      # Dropbox 実装
│  ├─ CloudMigrator.Observability/          # 構造化ログ・メトリクス
│  └─ CloudMigrator.Testing/               # テスト共通ユーティリティ
├─ tests/
│  ├─ unit/
│  ├─ integration/
│  └─ e2e/
├─ configs/
│  └─ config.json                           # ランタイム設定
└─ docs/
```

### 並列実行設計
1. `Channel.CreateBounded<TransferJob>(capacity)` で有界キュー（背圧制御）
2. `Parallel.ForEachAsync` + `MaxDegreeOfParallelism` で上限付き並列転送
3. Graph SDK の `LargeFileUploadTask` でチャンク送信・進捗・再開
4. 指数バックオフ + ジッターで再試行（Graph は `Retry-After` 準拠）

---

## 5. 実装フェーズ

| Phase | 内容 | 状態 |
|---|---|---|
| Phase 0 | 仕様凍結・旧ドキュメント差分一覧化 | ✅ 完了 |
| Phase 1 | プロジェクト基盤（solution/CI/設定ローダー/provider 抽象） | 🚧 進行中 |
| Phase 2 | 認証・Graph 基盤（client credentials/retry/LargeFileUpload PoC） | ⬜ 未着手 |
| Phase 3 | クロール + スキップリスト | ⬜ 未着手 |
| Phase 4 | 転送エンジン（small/large/フォルダ作成/並列/セッション再開） | ⬜ 未着手 |
| Phase 5 | 実行モード互換（--reset/--full-rebuild/通常実行/設定ハッシュ） | ⬜ 未着手 |
| Phase 6 | 監視・品質・セキュリティ（watchdog/メトリクス/アラート） | ⬜ 未着手 |
| Phase 7 | 補助 CLI・運用機能（file_crawler/validate/Dropbox 実装） | ⬜ 未着手 |
| Phase 8 | E2E・性能検証・本番切替 | ⬜ 未着手 |

---

## 6. 完了条件（Definition of Done）

- FR/NFR/OPS の全 ID に対するテストが green
- 既存運用コマンドに対応する CLI が提供済み
- 品質ゲート（lint/type/test/security/coverage）を新リポジトリで再現
- 本番想定データでの転送成功率・再実行安全性・監視挙動を確認
- 運用手順書（セットアップ、日次運用、障害対応、ロールバック）を更新済み
