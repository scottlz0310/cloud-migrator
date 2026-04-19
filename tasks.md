# タスク管理

詳細仕様: [docs/implementation-plan.md](docs/implementation-plan.md)
前フェーズ履歴: [tasks-archive-20260414.md](docs/archive/tasks-archive-20260414.md)

## 現在の状態: v0.6.0 HybridRateController バグ修正・UI 改善 📋

---

## ✅ 完了: Graph API レートベース転送制御エンジン実装（v0.5.0）

**PR**: [#148](https://github.com/scottlz0310/cloud-migrator/pull/148)  
**完了日**: 2026-04-16

### 完了タスク

- [x] `ITransferRateController` / `IMetricsAggregator` インターフェース定義
- [x] `TransferMetricsAggregator` 実装（1 秒バケット固定リングバッファ）
- [x] `MetricsBuffer` 実装（非同期 DB バッファ）
- [x] `RateControlledTransferController` 実装（ヒステリシス 3 段階制御 + トークンバケット）
- [x] `AdaptiveConcurrencyControllerAdapter` 実装（後方互換アダプター）
- [x] `RateControlSettings` / `MigratorOptions.RateControl` 設定統合
- [x] `TransferCommand` への `UseRateControl` フラグ統合
- [x] `SharePointMigrationPipeline` Phase C インフライトカウンター回復修正
- [x] `TransferMetricsAggregator.GetSnapshot()` Rate429 計算式修正
- [x] `RateControlledTransferController.DisposeAsync()` べき等性保証
- [x] `CliServices.cs` 不要フィールド整理
- [x] ユニットテスト 627 件全合格

---

## 次フェーズ: v0.6.0 スループット主制御ハイブリッド方式

> 設計書: [docs/transfer-control-design-v2.md](docs/transfer-control-design-v2.md)

### 概要

v0.5.0 のヒステリシス制御（requests/sec）から **トークンバケット × AIMD フィードバック × 並列数補助制御のハイブリッド方式**（tokens/sec）へ移行する。

### 実装順序

| 順番 | ISSUE | タイトル | 状態 | 備考 |
|------|-------|---------|------|------|
| 1（並行） | [#160](https://github.com/scottlz0310/cloud-migrator/issues/160) | トークンバケット + 重み付きコスト | ✅ 完了（PR #167） | `FileCostCalculator` / `WeightedTokenBucket` 追加・設定統合・テスト 53 件 |
| 1（並行） | [#161](https://github.com/scottlz0310/cloud-migrator/issues/161) | スライディングウィンドウ指標収集 | ✅ 完了 | `SlidingWindowMetrics` 追加・P95/件数&時間切替・設定統合・テスト 24 件 |
| 2 | [#162](https://github.com/scottlz0310/cloud-migrator/issues/162) | AIMD フィードバック制御 + クールダウン | ✅ 完了 | `AimdFeedbackController` 追加・4 信号判定・ベースライン EMA・クールダウン・テスト 24 件 |
| 3 | [#163](https://github.com/scottlz0310/cloud-migrator/issues/163) | 並列数補助制御ハイブリッド移行 | 🟢 統合 PR 実装完了 | `HybridRateController` 追加・§7 制御ループ + §4.3 並列数補助制御・`RateStateStore` v2/v0.5.x 互換・CLI + Dashboard 統合・テスト 25 件。旧 `AdaptiveConcurrencyController` / `TokenBucketRateLimiter` は `[Obsolete]` 付与のみ（実削除は後続 PR） |
| 4 | [#159](https://github.com/scottlz0310/cloud-migrator/issues/159) | スループット表示を制御窓ベースに変更（UI） | ✅ 完了 | HybridRateController 経路で `throughput_window_sec` 記録・Dashboard タイトルに「（直近 N 秒）」付記 |
| 5 | [#155](https://github.com/scottlz0310/cloud-migrator/issues/155) | 統計エリア統合（UI） | ✅ 完了 | 統計7指標 + RCモニター4指標を1カードに統合 |
| 自由 | [#154](https://github.com/scottlz0310/cloud-migrator/issues/154) | ルート情報をダッシュボードに移動（UI） | ✅ 完了 | ヘッダーにルートチップ＋ツールチップ追加 |
| 自由 | [#156](https://github.com/scottlz0310/cloud-migrator/issues/156) | グラフ表示オン/オフ切り替え（UI） | ✅ 完了 | ShowGraphs 設定 + ヘッダーアイコンボタン |
| 自由 | [#157](https://github.com/scottlz0310/cloud-migrator/issues/157) | グラフ列数可変設定（UI） | ✅ 完了 | GraphColumns 設定 + MudItem 幅動的制御 |
| 自由 | [#158](https://github.com/scottlz0310/cloud-migrator/issues/158) | フォルダ/ファイル進捗 Phase 連動表示（UI） | ✅ 完了 | folder_creation/transferring フェーズ連動進捗バー統合 |

### スコープ外（凍結・v0.6.0 効果確認後に判断）

| ISSUE | 内容 | 判断タイミング |
|-------|------|---------------|
| [#136](https://github.com/scottlz0310/cloud-migrator/issues/136) §1 | パルス制御 | #162 完了後 |
| [#136](https://github.com/scottlz0310/cloud-migrator/issues/136) §2 | ファイルサイズ別レーン分離 | #160 完了後 |

---

## 🔧 進行中: HybridRateController バグ修正・UI 改善

### 完了タスク

- [x] `AimdFeedbackController`: クールダウン中の429で `Hold` を返す修正（二重クールダウン防止）
- [x] `LatencyEvaluationMode.None` 追加・デフォルト化（429専制御、SlowDecrease 無効化）
- [x] `HybridRateController`: 制御ログ `LogDebug` → `LogInformation` 昇格（信号・レート・429率・P95 出力）
- [x] `EmergencyDecay` / `EmergencyInflightDecay` デフォルト緩和: 0.7/0.75 → **0.9/0.9**（Retry-After主制御設計）
- [x] 設定メニュー: `UseHybridController` / `CooldownSec` / `EmergencyDecay` / `EmergencyInflightDecay` / `AddStep` / `LatencyMode` の6項目追加（UI・API・バリデーション）
- [x] Dashboard リアルタイムモニタ: HybridRateController のメトリクスキー不一致修正（`rate_tokens_per_sec` / `max_inflight` / `rate_429` / `signal` 対応）
- [x] ユニットテスト 902 件全合格

---
