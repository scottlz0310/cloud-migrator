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

## 次フェーズ

> Epic ISSUE: （未定）

### 概要

<!-- 次フェーズの概要を記述 -->

### 対応路線 / スコープ

<!-- 対象路線・機能スコープを記述 -->

---

## ISSUE #XXX: （タイトル）

**Issue**: [#XXX]()  
**Milestone**: （バージョン）  
**依存**: （依存 ISSUE）

### 実装タスク

- [ ] 

### 受け入れ基準

- [ ] 

---
