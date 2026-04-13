# Graph API 転送最適化制御 設計書 v1.1

## バージョンとスコープ

| 項目 | 内容 |
|------|------|
| ターゲットバージョン | **v0.5.0** |
| 前提バージョン | v0.4.0（Blazor Hybrid 基盤・ウィザード UI 完成） |
| 設計書バージョン | v1.1（2026-04-14 合意事項反映） |

### v0.5.0 実装スコープ

- レートベース転送制御エンジン（新規クラス `RateControlledTransferController`）
- 多時間窓フィードバック制御（短期 / 中期）
- インフライト制御
- ヒステリシス制御（緊急 / 緩 / 加速の 3 段階）
- 上級者向け設定 UI（ダッシュボード折り畳みセクション）
- 既存 `AdaptiveConcurrencyController` の `[Obsolete]` 化

### v0.5.0 スコープ外（将来拡張 → 別 ISSUE）

- パルス制御
- ファイルサイズ別レーン分離
- ユーザー単位制御
- 予測型制御（事前減速）
- 軽量強化学習（バンディット）

---

## 1. 目的

本設計は、API スロットリング（429）を回避しつつ、最大スループットで安定したデータ転送を実現するための制御アルゴリズムを定義する。

---

## 2. 設計方針

* 429 を完全にゼロにはしない（最適点は境界付近）
* 「並列数」ではなく「リクエスト流量（レート）」を主制御とする
* 短期・中期の時間窓で状態を評価する
* インフライト（未完了リクエスト）を負債として扱う
* フィードバック制御により自己最適化する
* 制御パラメーターはすべて設定で可変にする（ハードコードしない）

---

## 3. 全体アーキテクチャ

```
Queue
  ↓
Dispatcher（レート制御）
  ↓
Worker（並列上限付き）
  ↓
Graph API
  ↓
ログ収集 → リアルタイム分析 → 制御フィードバック
```

---

## 4. 実装アプローチ

### v0.5.0: 新規クラスによる並走（フェーズ 1）

既存の `AdaptiveConcurrencyController` は v0.4.0 リリース済みのため、v0.5.0 では新規クラス  
`RateControlledTransferController` を実装して並走させる。

```
v0.5.0:
  AdaptiveConcurrencyController     ← [Obsolete] 化（動作は維持）
  RateControlledTransferController  ← 新規（本設計書の実装対象）
```

> v0.5.0 で効果を確認後、`RateControlledTransferController` を正式採用として切り替える。

### 将来バージョン: 既存クラス刷新（フェーズ 2）

v0.5.0 での効果確認後、`AdaptiveConcurrencyController` を削除して一本化する（v0.6.0 以降予定）。

```
v0.6.0（予定）:
  AdaptiveConcurrencyController     ← 削除
  RateControlledTransferController  ← 本実装として定着
```

---

## 5. 制御要素

### 5.1 レート制御（主制御）

* 単位：requests/sec
* Queue からの取り出し速度を制御

```
dispatch_rate = f(429_rate_longWindow)
```

---

### 5.2 並列制御（補助）

* 上限としてのみ使用（主制御ではない）

```
max_concurrency = 設定値（デフォルト: 16、上級者設定で変更可）
```

---

### 5.3 インフライト制御

#### 定義

```
in_flight = 実行中リクエスト数 + Retry 待ちリクエスト数
```

#### 制御

```
if in_flight > inFlightThreshold:
    dispatch 停止
```

`inFlightThreshold` は上級者設定で変更可（初期値は PoC 中に決定）。

---

## 6. 時間窓設計

### 短期ウィンドウ（デフォルト 5 秒）

* スパイク検知用
* 緊急制御のトリガーに使用
* 上級者設定 `shortWindowSec` で変更可

### 中期ウィンドウ（デフォルト 30 秒）

* 安定状態の判断用
* レート調整のベースに使用
* 上級者設定 `longWindowSec` で変更可

---

## 7. 429 制御ロジック（ヒステリシス）

### 7.1 可変減衰

```
factor = clamp(1 - decayK * 429_rate, minDecayFactor, maxDecayFactor)
rate  *= factor
```

`decayK` / `minDecayFactor` / `maxDecayFactor` はすべて上級者設定で変更可。

---

### 7.2 ヒステリシス制御

```
if 429_rate_shortWindow > emergencyThreshold:   // デフォルト 0.10（10%）
    緊急減速

elif 429_rate_longWindow > slowdownThreshold:   // デフォルト 0.03（3%）
    緩やかに減速

elif 429_rate_longWindow == 0:
    加速
```

`emergencyThreshold` / `slowdownThreshold` は上級者設定で変更可。

---

### 7.3 加速制御

安定状態（429 率 = 0）が継続している場合にレートを引き上げる。

```
rate *= (1 + accelerateRatio)   // デフォルト 0.05（+5%）
```

`accelerateRatio` は上級者設定で変更可。

---

## 8. Retry-After 対応

* Retry-After は最優先で遵守
* Retry 待ちリクエストは `in_flight` に含める

```
on 429:
    wait(retry_after)
```

---

## 9. ログ設計

### 必須項目

* timestamp
* status_code
* latency
* endpoint
* file_size / chunk_size
* concurrency
* rate

### 集計指標

* 429 率（短期 / 中期）
* RPS
* 成功率
* 平均レイテンシ

---

## 10. スコア関数（最適化指標）

```
score = throughput
      - penaltyWeight * penalty(429)
      - latencyWeight * latency_penalty
```

`penaltyWeight` / `latencyWeight` は上級者設定で変更可（初期値は PoC 中に決定）。

---

## 11. 制御ループ

周期：1 秒

```
1. ログ集計更新
2. 429 率算出（短期 / 中期）
3. レート調整（ヒステリシス §7.2 → 可変減衰 §7.1）
4. インフライトチェック（§5.3）
5. dispatch 実行
```

---

## 12. 上級者向け設定パラメーター

すべての制御パラメーターは設定で変更可能とし、ダッシュボード上の折り畳みセクション（上級者向け）に配置する。

| パラメーター | デフォルト | 説明 |
|-------------|-----------|------|
| `shortWindowSec` | 5 | 短期時間窓（秒） |
| `longWindowSec` | 30 | 中期時間窓（秒） |
| `emergencyThreshold` | 0.10 | 緊急減速の 429 率閾値（10%） |
| `slowdownThreshold` | 0.03 | 緩減速の 429 率閾値（3%） |
| `minDecayFactor` | 0.3 | 可変減衰の最小係数 |
| `maxDecayFactor` | 0.9 | 可変減衰の最大係数 |
| `decayK` | TBD | 可変減衰の感度係数（PoC 中に決定） |
| `accelerateRatio` | 0.05 | 加速時レート増加率（+5%/サイクル） |
| `maxConcurrency` | 16 | 並列上限 |
| `inFlightThreshold` | TBD | dispatch 停止インフライト閾値（PoC 中に決定） |
| `penaltyWeight` | TBD | スコア関数の 429 ペナルティ重み（PoC 中に決定） |
| `latencyWeight` | TBD | スコア関数のレイテンシペナルティ重み（PoC 中に決定） |

> TBD 項目は PoC 実装（v0.5.0）中に初期値を決定し、本設計書を更新する。

---

## 13. 将来拡張（v0.5.0 スコープ外）

以下は v0.5.0 の実装対象外とし、[#136](https://github.com/scottlz0310/cloud-migrator/issues/136) で管理する。

| 機能 | 概要 |
|------|------|
| **パルス制御** | 内部カウンタリセット目的の送信 ON/OFF サイクル（cycle 5 秒、duty 0.8 ベース） |
| **ファイルサイズ別レーン分離** | 大ファイル / 小ファイルで dispatch キューを分離し、スモールファイルの詰まりを防止 |
| **ユーザー単位制御** | テナント内複数ユーザー間での流量配分制御 |
| **予測型制御** | 過去の 429 パターンに基づく事前減速（リアクティブ制御の補完） |
| **軽量強化学習（バンディット）** | 報酬ベースのパラメーター自動最適化 |

---

## 14. 期待効果

* 429 の局所化（スパイク抑制）
* スループットの安定化
* 過剰待機の削減
* 自動的な最適点収束

---

## 15. 結論

本設計は以下を実現する：

* レート主導制御（並列制御は補助）
* インフライト吸収による負債管理
* 多時間窓フィードバックによる安定判断
* 全パラメーターの設定可変化（上級者設定 UI）
* 段階的移行（案 B → 案 C）による安全なリリース戦略

これにより、API スロットリング環境下でも高効率・高安定な転送を可能とする。
