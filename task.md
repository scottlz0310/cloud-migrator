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

## Issue 18: OneDrive 転送元フォルダ指定（FR-02 完全実装）

- [x] `GraphStorageOptions` / `MigratorOptions.GraphProviderOptions` に `OneDriveSourceFolder` 追加
- [x] `GraphStorageProvider.ListOneDriveItemsAsync` でフォルダパスを itemId に解決してクロール開始
- [x] `CliServices.cs` での `storageOptions` マッピング追加
- [x] `sample.env` / `DefaultEnvTemplate` に `MIGRATOR__GRAPH__ONEDRIVESOURCEFOLDER` キー追加
- [x] `bootstrap` ウィザードにフォルダ入力ステップ追加（省略可・env変数プリフィル対応）
- [x] ユニットテスト追加（3件: フォルダ反映・コメントアウト・空維持）
- [x] ビルド・テスト確認、コミット、PR作成 → PR #23 マージ済み


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

## バグ修正: E2E 手動テスト中に発見・修正（2026-03-13）

- [x] `FindFolderIdAsync`: SharePoint の `$filter` 非サポート問題 → `PageIterator` + クライアント側フィルタリングに修正
- [x] `EnsureFolderAsync`: フォルダIDキャッシュ追加（`_folderIdCache`）で API 呼び出し O(N×depth²) → O(N) に削減
- [x] `TransferEngine.RunAsync`: フォルダ先行作成フェーズに進捗ログ追加（件数/100件ごと/完了）

## マニュアルテスト: E2E 実施（2026-03-13 手動ランブック TC-01〜TC-06e）

- [x] TC-01 ビルド PASS
- [x] TC-02 CLI ヘルプ PASS
- [x] TC-03 Setup ヘルプ PASS
- [x] TC-04 doctor PASS（error=0, warning=0）
- [x] TC-05 verify PASS（全 Graph エンドポイント OK）
- [x] TC-06a file-crawler onedrive PASS（24,481 件）
- [x] TC-06b file-crawler sharepoint PASS（0 件、未転送正常）
- [x] TC-06c rebuild-skiplist + validate PASS
- [x] TC-06d compare PASS（差分 24,481 件表示）
- [x] TC-06e transfer PASS（成功 24,481 件 / 失敗 0 件 / 所要 1h13m）

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

## バグ修正: doctor config.json 読み取り（PR #25）

- [x] `AppConfiguration.ResolveConfigPath()` をワーキングディレクトリ優先に修正
- [x] `AppContext.BaseDirectory` 遡り上限を4→6段に拡張
- [x] `DoctorCommand.Run()` で `resolvedConfigPath` を先に決定して `Build`/`BuildChecks` で共有
- [x] `SetupDoctorCommandTests` にテスト3件追加（121件全通過確認）→ PR #25 マージ済み

## パフォーマンス改善＆バグ修正: 転送チューニング（feature/phase-perf-tuning-and-bugfix）

- [x] `RateLimiterOptions` のデフォルト値を実稼働ログ（16,831 件移行）に基づき最適化
  - `InitialRequestsPerSec` 4.0 → 7.0（コールドスタート時のウォームアップ排除）
  - `BurstCapacity` 6 → 4（ThrottledRequest 誘発の抑制）
  - `IncreaseStep` 0.2 → 0.5（AIMD 収束速度改善）
- [x] `GraphStorageProvider.LargeUploadAsync`: セッション URL 復元時の `NullReferenceException` 修正
  - `UploadUrl` のみで `UploadSession` を構築すると `NextExpectedRanges` が `null` になり、
    `LargeFileUploadTask` コンストラクター内の `GetRangesRemaining()` が NPE を投げる問題を修正
  - 復元時に `NextExpectedRanges = ["0-"]` を初期値として設定することで解消
- [x] `TransferEngine.RunAsync`: 転送完了サマリーログにレート情報を追加
  - レートリミッター有効時: 「最終レート: {Rate:F1}/{Max:F1} file/sec」を付記
- [x] `CliServices`: レート状態の永続化（`logs/rate_state.json`）を実装
  - 起動時: `rate_state.json` が存在すれば前回終了レートを `initialRate` として復元（コールドスタート排除）
  - 終了時: `Dispose()` 内で現在レートを JSON 保存（UTC 日時付き）

## パフォーマンス改善: フォルダ先行作成並列化・GET優先・スキップキャッシュ（PR #36）

- [x] `TransferEngine.RunAsync`: フォルダパスを深さ別グループで並列作成
  - `folderPathSet.GroupBy(深さ).OrderBy(深さ)` でグループ化、`Parallel.ForEachAsync` で並列処理
  - `destRootNormalized` を `folderPathSet` に追加し深さ順ループで先行処理を保証（CI 安定化を含む）
- [x] `GraphStorageProvider.EnsureFolderAsync`: `_folderIdCache` によるキャッシュ済みセグメントのスキップ（再確認）
- [x] `GraphStorageProvider.EnsureFolderSegmentAsync`: GET-first パターン + 409 競合フォールバック実装
  - `catch (ApiException ex) { if (ex.ResponseStatusCode != 409) throw; ... }` パターンに変更（Ubuntu CI 安定化）
- [x] `GraphStorageProvider.ListItemsAsync`: ストリーミング取得（`PageIterator` 相当）+ スキップリスト早期除外
- [x] `TransferEngine`: `AdaptiveConcurrencyController` の `GetFirst` 最適化（`GetAsync` 優先呼び出し）
- [x] テスト: `FakeStorageProvider` を使ったプラットフォーム非依存順序テスト（TransferEngine 3件）
- [x] CI 全ジョブ（ubuntu / macOS / windows）PASS、PR #36 マージ済み

## パフォーマンス改善: フォルダ作成並列度を MaxParallelFolderCreations に分離（PR #38）

- [x] `MigratorOptions`: `MaxParallelFolderCreations` プロパティ追加（デフォルト 4）
  - フォルダ先行作成フェーズとファイル転送フェーズの並列度を独立して制御可能に
  - `configs/config.json` の `maxParallelFolderCreations` に任意の値を設定可能
- [x] `TransferEngine.RunAsync`: フォルダ作成ループを `MaxParallelFolderCreations` に切り替え
  - 修正前: `MaxParallelTransfers`（config: 32）を流用 → 3000+ フォルダで即クォータ枯渇・クラッシュ
  - 修正後: `MaxParallelFolderCreations`（config: 8）で制御 → 24,471 ファイル完走（57:07）
- [x] ユニットテスト 188 件全通過確認、PR #38 マージ済み
