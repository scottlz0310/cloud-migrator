# タスク管理

詳細仕様: [docs/implementation-plan.md](docs/implementation-plan.md)

## 現在の状態: Phase 3 完了・Phase 4 準備中

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

- [ ] small upload（4MB 未満 PUT）
- [ ] large upload（Upload Session + チャンク）
- [ ] フォルダ自動作成（階層順次）
- [ ] 並列転送（`Parallel.ForEachAsync` + `Channels`）
- [ ] セッション再開・部分再送の永続化

## Phase 5: 実行モード互換

- [ ] `transfer` コマンド（通常実行）
- [ ] `rebuild-skiplist` コマンド（`--reset` 相当）
- [ ] `--full-rebuild` 相当動作
- [ ] 設定変更ハッシュ検知・キャッシュ再生成

## Phase 6: 監視・品質・セキュリティ

- [ ] `watchdog`（ログ無更新 10 分で再起動）
- [ ] 品質メトリクス収集（NFR-04）
- [ ] 品質アラート（NFR-05）
- [ ] セキュリティスキャン統合（NFR-07）

## Phase 7: 補助 CLI・運用機能

- [ ] `file-crawler` サブコマンド（onedrive/sharepoint/skiplist/compare/validate/explore）
- [ ] Dropbox プロバイダースケルトン + 開発ガイド

## Phase 8: E2E・性能検証・切替

- [ ] E2E テスト（実 Graph API or ステージング）
- [ ] 並列数・チャンクサイズ最適化
- [ ] 本番切替計画・段階カットオーバー
- [ ] 運用手順書（セットアップ/日次/障害対応/ロールバック）
