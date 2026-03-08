# タスク管理

詳細仕様: [docs/implementation-plan.md](docs/implementation-plan.md)

## 現在の状態: Phase 7 完了

---

## Phase 1: プロジェクト基盤

- [x] ソリューション・プロジェクト作成（CloudMigrator.slnx + 10プロジェクト）
- [x] プロジェクト参照の設定
- [x] NuGet パッケージ追加（Graph SDK, System.CommandLine, Serilog, FluentAssertions, Moq）
- [x] `IStorageProvider` 契約定義（Providers.Abstractions）
- [x] 設定ローダー実装（env > json > default）
- [x] `configs/config.json` テンプレート作成
- [x] `.github/workflows/` CI 雛形（build/test/lint）
- [x] `.github/copilot-instructions.md` 作成

## Phase 2: 認証・Graph 基盤

- [x] `GraphAuthenticator`（client credentials、トークン自動更新）
- [x] Graph API クライアント共通化（retry/timeout/rate-limit）
- [x] `LargeFileUploadTask` PoC（コメントで設計記録、Phase 3 で実装）
- [x] `IStorageProvider` に Graph 実装を接続
- [x] `GraphStorageProvider` ユニットテスト（13 ケース）

## Phase 3: クロール + スキップリスト

- [x] OneDrive 再帰クロール（重複排除、キャッシュ）
- [x] SharePoint 再帰クロール
- [x] skip_list 読み書き・ロック制御（判定キー: path + name）

## Phase 4: 転送エンジン

- [x] small upload（4MB 未満 PUT）
- [x] large upload（Upload Session + チャンク）
- [x] フォルダ自動作成（階層順次）
- [x] 並列転送（`Parallel.ForEachAsync` + `Channels`）
- [x] セッション再開・部分再送の永続化

## Phase 5: 実行モード互換

- [x] `transfer` コマンド（通常実行）
- [x] `rebuild-skiplist` コマンド（`--reset` 相当）
- [x] `--full-rebuild` 相当動作
- [x] 設定変更ハッシュ検知・キャッシュ再生成

## Phase 6: 監視・品質・セキュリティ

- [x] `watchdog`（ログ無更新 10 分で再起動）
- [x] 品質メトリクス収集（NFR-04）
- [x] 品質アラート（NFR-05）
- [x] セキュリティスキャン統合（NFR-07）

## Phase 7: 補助 CLI・運用機能

- [x] `file-crawler` サブコマンド（onedrive/sharepoint/dropbox/skiplist/compare/validate/explore）
- [x] Dropbox プロバイダー本実装

## Phase 8: E2E・性能検証・切替

- [ ] E2E テスト（実 Graph API or ステージング）
- [ ] 並列数・チャンクサイズ最適化
- [ ] 本番切替計画・段階カットオーバー
- [ ] 運用手順書（セットアップ/日次/障害対応/ロールバック）

## Issue 16: Setup Tool（MVP）

- [x] `CloudMigrator.Setup.Cli` プロジェクト追加（独立CLI）
- [x] `doctor` / `init` / `verify` コマンド実装
- [x] ユニットテスト追加（doctor/init/verify）
- [x] `README.md` / `usage.md` への利用手順追記
- [x] `init` の Graph 識別子反映/自動解決オプション追加（`--resolve-graph-ids` ほか）

## Issue 17: Setup Tool UX再設計（Interactive）

- [x] `bootstrap` コマンド追加（対話型セットアップ入口）
- [x] 対話入力: 必須 Graph 情報（ClientId / TenantId / ClientSecret）ガイド
- [x] 対話入力: OneDrive UPN + SharePoint サイトURLから SiteId / DriveId 自動解決
- [x] Drive 名候補提示（既定: `Documents`）と選択導線
- [x] 対話完了後に `.env` / `configs/config.json` へ反映（既存ファイル保護あり）
- [x] `doctor` / `verify` への接続（自動実行オプション含む）を整備
- [x] 既存 `init` の非対話オプション互換を維持（自動化/CI用途）
- [x] ユニットテスト追加（ParseDrives/SelectDriveのロジック、異常系）
- [x] `usage.md` / `CHANGELOG.md` へ新フローを反映
