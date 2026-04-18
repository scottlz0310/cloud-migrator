# タスク管理

詳細仕様: [docs/implementation-plan.md](docs/implementation-plan.md)
前フェーズ履歴: [tasks-archive-20260414.md](docs/archive/tasks-archive-20260414.md)

## 現在の状態: 次フェーズ計画中 📋

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
| 1（並行） | [#160](https://github.com/scottlz0310/cloud-migrator/issues/160) | トークンバケット + 重み付きコスト | 🔜 未着手 | v0.6.0 基盤。他全 ISSUE の前提 |
| 1（並行） | [#161](https://github.com/scottlz0310/cloud-migrator/issues/161) | スライディングウィンドウ指標収集 | 🔜 未着手 | #160 と並行着手可 |
| 2 | [#162](https://github.com/scottlz0310/cloud-migrator/issues/162) | AIMD フィードバック制御 + クールダウン | 🔜 未着手 | #160 + #161 完了後 |
| 3 | [#163](https://github.com/scottlz0310/cloud-migrator/issues/163) | 並列数補助制御ハイブリッド移行 | 🔜 未着手 | #160 + #162 完了後 |
| 4 | [#159](https://github.com/scottlz0310/cloud-migrator/issues/159) | スループット表示を制御窓ベースに変更（UI） | 🔜 未着手 | #161 完了後 |
| 5 | [#155](https://github.com/scottlz0310/cloud-migrator/issues/155) | 統計エリア統合（UI） | 🔜 未着手 | #163 完了後（表示データ確定） |
| 自由 | [#154](https://github.com/scottlz0310/cloud-migrator/issues/154) | ルート情報をダッシュボードに移動（UI） | 🔜 未着手 | logic と独立 |
| 自由 | [#156](https://github.com/scottlz0310/cloud-migrator/issues/156) | グラフ表示オン/オフ切り替え（UI） | 🔜 未着手 | logic と独立 |
| 自由 | [#157](https://github.com/scottlz0310/cloud-migrator/issues/157) | グラフ列数可変設定（UI） | 🔜 未着手 | logic と独立 |
| 自由 | [#158](https://github.com/scottlz0310/cloud-migrator/issues/158) | フォルダ/ファイル進捗 Phase 連動表示（UI） | 🔜 未着手 | logic と独立 |

### スコープ外（凍結・v0.6.0 効果確認後に判断）

| ISSUE | 内容 | 判断タイミング |
|-------|------|---------------|
| [#136](https://github.com/scottlz0310/cloud-migrator/issues/136) §1 | パルス制御 | #162 完了後 |
| [#136](https://github.com/scottlz0310/cloud-migrator/issues/136) §2 | ファイルサイズ別レーン分離 | #160 完了後 |

---
