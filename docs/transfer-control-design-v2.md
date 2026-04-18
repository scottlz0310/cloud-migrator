# 転送制御設計書 v2.0 — スループット主制御ハイブリッド方式

## バージョンとスコープ

| 項目 | 内容 |
|------|------|
| ターゲットバージョン | **v0.6.0** |
| 前提バージョン | v0.5.0（`RateControlledTransferController` 実装済み） |
| 設計書バージョン | v2.0（2026-04-18） |
| 前バージョン設計書 | [graph-api-adaptive-throttling-control-design-v1.md](graph-api-adaptive-throttling-control-design-v1.md) |

### v0.6.0 実装スコープ（ISSUE 対応）

| ISSUE | 内容 |
|-------|------|
| [#160](https://github.com/scottlz0310/cloud-migrator/issues/160) | トークンバケット方式スループット制御エンジン（重み付きコスト対応） |
| [#161](https://github.com/scottlz0310/cloud-migrator/issues/161) | スライディングウィンドウ指標収集（429率・レイテンシ・成功率） |
| [#162](https://github.com/scottlz0310/cloud-migrator/issues/162) | AIMD フィードバック制御 + クールダウン |
| [#163](https://github.com/scottlz0310/cloud-migrator/issues/163) | 並列数制御を補助制御に再設計（スループット主制御ハイブリッド移行） |

### v0.6.0 スコープ外（凍結・再評価待ち）

| ISSUE | 内容 | 状態 |
|-------|------|------|
| [#136](https://github.com/scottlz0310/cloud-migrator/issues/136) §1 | パルス制御 | #162 効果確認後に要否判断 |
| [#136](https://github.com/scottlz0310/cloud-migrator/issues/136) §2 | ファイルサイズ別レーン分離 | #160 効果確認後に要否判断 |

---

## 1. 背景と v1 との違い

### v1（v0.5.0 実装済み）の方式

v1 では「リクエスト流量（requests/sec）」を主制御とし、ヒステリシス制御（緊急 / 緩 / 加速の 3 段階）で 429 発生率に応じてレートを調整していた。

```
v1 制御ループ:
  429_rate_shortWindow > emergencyThreshold → 緊急減速
  429_rate_longWindow  > slowdownThreshold  → 緩減速
  429_rate_longWindow == 0                  → 加速
```

### v2 への移行動機

v1 の問題点：

1. **並列数制御との役割が曖昧** — レート制御と並列数制御が競合しやすく、どちらが実質的な制御主体かが実行時に変わる
2. **ファイルサイズの影響を無視** — 小ファイル 100 件と大ファイル 1 件を同コストで扱うため、帯域制御として不正確
3. **ヒステリシスの段階設定が経験則依存** — 閾値の意味が直感的でなく、チューニングが難しい

### v1 からの継続事項

以下は v1 設計の方針をそのまま引き継ぐ：

- 「並列数ではなくレートを主制御とする」という基本方針
- 全パラメーターを設定で可変にする
- `AdaptiveConcurrencyController` の廃止（v0.6.0 で削除）
- Retry-After の最優先遵守

---

## 2. 設計方針

- **並列数はその時点の動的上限まで活用する**（スロットを遊ばせない。上限は §4.3 により動的調整）
- **負荷制御はスループット（tokens/sec）で行う**（requests/sec ではなくコスト加重）
- **フィードバックは AIMD で行う**（Additive Increase / Multiplicative Decrease）
- **ファイルサイズに応じたコストで実際の帯域に近い制御を実現する**
- **全パラメーターは設定で可変にする**

---

## 3. 全体アーキテクチャ

### 同期的な dispatch 経路

```
Queue（transfer_records: pending）
  ↓
[ゲート B] 並列数セマフォ（補助制御）
  wait until inflight < max_inflight
  ↓
[ゲート A] トークンバケット（スループット制御）
  wait until tokens >= cost(file)
  tokens -= cost(file)   // ← ゲート B 通過後、dispatch 直前に消費
  ↓
Dispatch → Worker（Graph API 呼び出し）
  ↓
結果を指標バッファへ push（429 / latency / success）
```

ゲート B → ゲート A の順で直列適用する。**トークンの消費はゲート B（並列数セマフォ）の取得後、dispatch 直前**に行う。
ゲート A を先に通過させると、ゲート B の待機中にトークンが補充され続け、意図した tokens/sec のスループット制御が崩れるため、順序は B→A とする。

### 非同期フィードバック経路

制御ループは dispatch 経路とは独立して周期実行される（§7 参照）。

```
[定期ループ] controlIntervalSec 周期
  ↓
スライディングウィンドウ指標を集計（§5）
  ↓
AIMD で rate を調整（§6）
  ↓
AIMD の signals を元に max_inflight を調整（§4.3）
  ↓
トークンバケット・並列数セマフォに反映
```

---

## 4. 制御要素

### 4.1 トークンバケット（主制御）

一定レートでトークンを補充し、ファイル処理時にコストを消費する。
トークン不足時は補充されるまで待機する。

```
// 補充は monotonic clock による実時間ベースで計算（dt 固定は禁止）
now      = monotonic_now()
elapsed  = now - last_refill_time
tokens   = min(tokens + rate * elapsed, maxBurst)
last_refill_time = now

if tokens >= cost(file):
    実行
    tokens -= cost(file)
else:
    待機                       // ビジーウェイトではなく sleep+retry
```

**monotonic clock を使う理由**:

- 制御ループの実行周期（`controlIntervalSec`）は GC / スレッドスケジューリング / システム時刻変更で揺らぐため、`rate * controlIntervalSec` のような固定 `dt` 換算はレート誤差を累積する
- wall-clock（`DateTime.UtcNow`）は NTP 補正や手動変更で逆行する可能性があるため使わない
- .NET では `Stopwatch.GetTimestamp()` / `Stopwatch.Elapsed` または `Environment.TickCount64` を使用する

**バケット容量（burst）**: 瞬間的なバースト許容量。`maxBurst` として設定可。補充時は必ず `maxBurst` でクランプし、長時間アイドル後のバーストが極端に大きくなることを防ぐ。

---

### 4.2 重み付きコスト

各ファイルの処理コストをサイズに応じて設定し、実際の帯域・処理負荷に近い制御を実現する。

#### 離散コスト（デフォルト）

| ファイルサイズ | cost |
|---|---|
| 小（〜1 MB） | 1 |
| 中（1〜100 MB） | 5 |
| 大（100 MB〜） | 20 |

#### 連続コスト（オプション）

```
cost = clamp(size_bytes / costScaleBytes, minCost, maxCost)
```

`costScaleBytes` / `minCost` / `maxCost` は設定で変更可。

> 初期は離散コストで運用し、効果測定後に連続コストへの切り替えを検討する。

---

### 4.3 並列数制御（補助制御）

スループット制御と独立して同時実行数の上限を管理する。

```
max_inflight = N   // 設定値（デフォルト: 16）
```

**目的:**
- メモリ・接続数の上限保証（安全装置）
- バースト抑制（トークンバケットのバースト補完）
- **inflight 停滞の防止** — 大ファイルや遅いレスポンスが全スロットを占有し続け、後続の小ファイルが永続的に dispatch されない事態（ヘッドオブラインブロッキング）を上限で予防する。`max_inflight` を超える滞留はトークンバケット側のバックプレッシャーで吸収される

#### 動的調整

max_inflight の調整トリガーは **AIMD フィードバックループ（§6）が発行する制御信号** に連動する。独立した判定は持たない。

| AIMD 信号 | max_inflight の変化 |
|------|-------------------|
| `emergency_decrease`（急減速） | `max_inflight = max(floor(max_inflight * emergencyInflightDecay), minInflight)` |
| `stable`（§6 の stable 判定と同一条件） | `max_inflight = min(max_inflight + 1, configuredMax)` |
| `slow_decrease`（緩減速） | 変化なし |

調整は制御ループの各サイクルで最大 1 回行い、急激な上下動を避ける。

---

## 5. スライディングウィンドウ指標収集

フィードバック制御の入力となる指標をスライディングウィンドウで収集する。

### 収集指標

| 指標 | 用途 |
|------|------|
| 429 発生率 | 最重要 — レート急減速の判断 |
| レイテンシ（平均 / P95） | 処理負荷の上昇検知 |
| 成功率 | 全体健全性の把握 |

### ウィンドウ設計

- **デフォルト**: 時間ベース（直近 `windowSec` 秒、デフォルト 30 秒）
- **最低件数保証**: 件数が `minSamples`（デフォルト: 10）未満の場合は判断をスキップ
- **オプション**: 件数ベースウィンドウ（設定で切り替え可）

```
window_events = events[now - windowSec .. now]

if len(window_events) < minSamples:
    skip feedback adjustment

429_rate    = count(429) / len(window_events)
avg_latency = mean(latency)
p95_latency = percentile(latency, 95)
success_rate = count(success) / len(window_events)
```

---

## 6. AIMD フィードバック制御とクールダウン

スライディングウィンドウの指標に基づきトークンバケットの補充レート（`rate`）を動的調整する。
本節のロジックは制御ループ（§7）から毎サイクル呼び出される。

### 制御信号

判定結果を以下の 4 信号のいずれかとして出力し、§4.3 の並列数調整と連動させる。

| 信号 | 発動条件 |
|------|----------|
| `emergency_decrease` | `429_rate > emergencyThreshold` |
| `slow_decrease` | 上記に該当せず、P95 レイテンシがベースライン比で悪化（§6.1） |
| `stable` | 上記いずれにも該当せず、**かつ** 直近 `stableWindowSec` 秒間の `429_rate == 0` **かつ** レイテンシがベースライン比で悪化していない **かつ** クールダウン中でない |
| `hold` | 上記いずれにも該当しない（レート変更なし） |

### 6.1 レイテンシ判定（ベースライン比）

固定閾値（例: 「P95 > 1000 ms なら減速」）は環境差（サーバー地域・ファイルサイズ分布）への追従性が低いため採用しない。代わりに **ベースライン比での悪化検知** を行う。

#### ベースラインの定義

- **起動直後**: 最初の `baselineSamples`（デフォルト: 20）件の成功リクエストの P95 を初期ベースラインとする
- **定常運用**: ベースラインは過去の安定期（`stable` 信号が発火した直近サイクル）の P95 を指数移動平均（EMA）で更新する
  ```
  baseline_p95 = baseline_p95 * (1 - α) + current_p95 * α   // α = 0.1
  ```
- **悪化継続中は更新しない**: `slow_decrease` / `emergency_decrease` 発動中はベースラインを凍結し、悪化状態を「正常」と学習してしまうのを防ぐ

#### 悪化判定

以下のいずれかで `slow_decrease` 候補となる：

1. **ベースライン比** — `current_p95 > baseline_p95 * (1 + latencyRiseRatio)`（例: 30% 悪化で発動）
2. **直近比** — 直近 `trendWindowSec` 秒（デフォルト: 10 秒）の P95 が、その前の同時間窓の P95 と比較して `latencyRiseRatio` 以上悪化

どちらを採用するかは実装時に PoC で選定する。併用する場合は OR 条件とする（どちらかが満たされれば発動）。

### 制御ロジック

```
signal = classify_state()

switch signal:
  case emergency_decrease:
      rate *= emergencyDecay         // デフォルト 0.7
      enter_cooldown(cooldownSec)    // 増加を cooldownSec 秒間凍結
  case slow_decrease:
      rate *= slowDecay              // デフォルト 0.9
  case stable:
      rate += addStep                // 緩増加（デフォルト: +α tokens/sec）
  case hold:
      (変更なし)

rate = clamp(rate, minRate, maxRate)
```

### クールダウン制御

`emergency_decrease` 直後は `cooldownSec` 秒間 `stable` 信号を発行しない（スラッシング防止）。
クールダウン中は `slow_decrease` と `emergency_decrease` は通常どおり発動する。

### レート範囲と初期レート戦略

#### 範囲の役割

| パラメーター | 役割 |
|---|---|
| `maxRate` | 過剰加速の上限。API 側の公称上限やネットワーク帯域から逆算した値 |
| `minRate` | **実質停止を避けるための下限**。AIMD の連続減速が進んでも転送がゼロにならない最低速度を保証する。`0` や極端に小さい値を設定するとジョブが停止したように見える（大ファイル 1 件に数時間かかる等）ため、**最低でも 1 tokens/sec 以上** を推奨 |

#### 初期レート（`initialRate`）の決定フロー

```
1. logs/rate_state.json に前回実行時の最終レートが保存されているか確認
   ├─ あり: 前回値を復元（ただし [minRate, maxRate] にクランプ）
   └─ なし（初回起動 or 状態ファイル削除後）: initialRate 設定値を使用
2. 初回値は「保守的」に設定する
   - 小さく始めて AIMD の stable 判定で緩増加させる方が安全
   - maxRate からスタートすると即座に 429 を誘発し、クールダウンで無駄な停止時間を生む
3. 状態ファイルは制御ループごとに atomic write（temp→rename）で更新する
```

#### `logs/rate_state.json` のフォーマット仕様と後方互換

**v0.6.0 の保存形式**（最小項目）:

| キー | 型 | 説明 |
|------|-----|------|
| `version` | `integer` | フォーマットバージョン。v0.6.0 以降は `2` を付与 |
| `rate_tokens_per_sec` | `number` | トークンバケットの現在補充レート |
| `max_inflight` | `integer` | 補助並列数制御の上限値 |
| `updated_at` | `string` | UTC ISO 8601 形式のタイムスタンプ |

**後方互換読込（v0.5.x → v0.6.0 アップグレード時）**:

- v0.5.x の状態ファイルはキー `rate`（file/sec 単位）を持ち `version` がない
- `version` が存在しない場合でも `rate` キーがあれば読込対象とし、その値を `rate_tokens_per_sec` の前回値として復元する（復元後に `[minRate, maxRate]` でクランプ）
- 旧形式に `max_inflight` は存在しないため、読込時は設定値（未設定時は既定値 `16`）を使用する
- 旧形式を正常に読めた場合はコールドスタート（`initialRate` から開始）へフォールバックせず、前回値を引き継ぐ
- 次回の状態保存時に v0.6.0 形式（`version: 2`）で上書きし、段階的に移行する
- `version` も `rate` も存在しない、または値が壊れている場合のみ、状態ファイルなし相当として `initialRate` から開始する

---

## 7. 制御ループ

### 周期

`controlIntervalSec` 秒周期（デフォルト: 1 秒）。上級者設定で変更可。

### 手順

```
1. スライディングウィンドウの指標を再計算（§5）
2. サンプル数 < minSamples → このサイクルはスキップ
3. AIMD 判定（§6）→ 制御信号を出力
4. rate を更新し、トークンバケットに反映
5. 制御信号に応じて max_inflight を調整（§4.3）、並列数セマフォに反映
6. メトリクスを出力（§9）
```

Dispatch 経路（§3 ゲート A / B）は本ループとは独立して走行する。

---

## 8. Retry-After と再試行

- **Retry-After ヘッダは最優先で遵守する。**rate / max_inflight の計算とは独立して厳守する。
- Retry-After 待機中のリクエストは **inflight 扱い** とする（スロットを占有したまま待機）。
- Retry 完了時点のレスポンス（成功 / 429 / その他）を指標ウィンドウに記録する。
- 同一リクエストが複数回 429 を返した場合、それぞれを独立イベントとして 429 率に計上する。

> 本節は v1 §8 の方針を継続する。

---

## 9. ログ・メトリクス設計

### リクエスト単位（指標ウィンドウに push）

| 項目 | 用途 |
|---|---|
| `timestamp` | ウィンドウ集計の基準時刻 |
| `status_code` | 429 率・成功率の集計 |
| `latency_ms` | 平均・P95 算出 |
| `endpoint` | 切り分け用（集計は任意） |
| `file_size_bytes` | コスト算出・相関分析 |
| `cost` | トークン消費量の監査 |

### 制御ループ単位（`metrics` テーブルに記録）

| 項目 | 用途 |
|---|---|
| `rate_tokens_per_sec` | AIMD 調整結果 |
| `max_inflight` | 並列数上限の推移 |
| `tokens_available` | バケット残量 |
| `429_rate` | ウィンドウ集計値 |
| `p95_latency_ms` | ウィンドウ集計値 |
| `signal` | `emergency_decrease` / `slow_decrease` / `stable` / `hold` |
| `in_cooldown` | クールダウン中フラグ |

ダッシュボードは `metrics` テーブルを参照して上級者向けパネルに表示する。
書き込み頻度は制御ループと同じ 1 秒周期。`_writeLock` 競合を避けるためバッファリング書き込みを検討する（#135 リスク 2 と共通）。

---

## 10. #136 との関係

[#136](https://github.com/scottlz0310/cloud-migrator/issues/136) で管理されていた将来拡張機能と v2 設計の関係を整理する。

### §1 パルス制御 → v2 AIMD で目的が代替可能

| 項目 | パルス制御（#136 §1） | v2 AIMD（#162） |
|---|---|---|
| 目的 | API カウンタリセット誘発による長期スロットリング回避 | 429 急増時の急減速 + クールダウン後の緩増加 |
| 手段 | 周期的な送信停止（ON/OFF サイクル） | レート継続的調整（停止なし） |
| 効果の重複 | 高 | — |

**判断方針**: #162 実装後に 429 の長期抑制効果を測定し、パルス制御固有の効果が確認できない場合は `not_planned` でクローズする。

### §2 ファイルサイズ別レーン分離 → v2 重み付きコストで部分代替

| 項目 | レーン分離（#136 §2） | v2 重み付きコスト（#160） |
|---|---|---|
| 目的 | 大ファイルによる小ファイルのキュー詰まり防止 | ファイルサイズに応じた帯域制御 |
| キュー詰まり防止 | あり（Dispatcher 分離） | なし（単一キュー） |
| 帯域制御精度 | 普通 | 高（コスト加重） |
| 実装複雑度 | 高（DB クエリ・lock 競合増） | 低 |

**判断方針**: #160 実装後に大小ファイル混在時のキュー詰まり有無を測定し、問題が残る場合のみレーン分離を着手する。重み付きコストで解決できる場合は `not_planned` でクローズする。

---

## 11. 実装ロードマップ

### ISSUE 間の依存関係

```
#160 トークンバケット + 重み付きコスト ─┐
                                        ├─→ #162 AIMD + クールダウン ─→ #163 ハイブリッド移行
#161 スライディングウィンドウ指標収集 ──┘
```

- #160 と #161 は並行着手可能
- #162 は #160・#161 両方の完了が前提
- #163 は #160・#162 の完了が前提

### マイルストーン

| フェーズ | ISSUE | 成果物 |
|---------|-------|--------|
| Phase 1 | #160・#161 | トークンバケットエンジン・指標収集モジュール |
| Phase 2 | #162 | AIMD フィードバックループ |
| Phase 3 | #163 | ハイブリッド制御統合・`AdaptiveConcurrencyController` 削除 |
| Phase 4 | — | #136 §1・§2 の要否判断・クローズまたは着手 |

### 既存実装との関係

| v0.5.0 時点のクラス | v0.6.0 での扱い |
|---|---|
| `RateControlledTransferController`（v1 のレート制御） | **拡張または置換**。v1 のヒステリシス判定ロジックを v2 AIMD に差し替える。名称は維持するか、内部実装のみ入れ替える（実装時に決定） |
| `AdaptiveConcurrencyController`（旧並列数制御、[Obsolete]） | **削除**。`CloudMigrator.Dashboard.csproj` の `NoWarn>CS0618` も同時に除去 |
| `TokenBucketRateLimiter`（既存の補助レート制限） | **削除または統合**。#160 の新実装に一本化する |
| `SqliteTransferStateDb.metrics` | **項目追加**。§9 の制御ループ単位メトリクスを格納 |

---

## 12. 設定パラメーター

すべての制御パラメーターはダッシュボード上の上級者設定セクションから変更可能とする。

### 制御ループ（#162・#163 共通）

| パラメーター | デフォルト | 説明 |
|---|---|---|
| `controlIntervalSec` | 1 | 制御ループの実行周期（秒） |

### トークンバケット（#160）

| パラメーター | デフォルト | 説明 |
|---|---|---|
| `initialRate` | TBD | 初期補充レート（tokens/sec）。状態ファイルに前回値があれば優先復元、なければこの値。**保守的な低めの値を推奨**（加速は AIMD に任せる） |
| `maxBurst` | TBD | バケット容量（最大蓄積トークン数）。補充時は必ずこの値でクランプする |
| `smallFileCost` | 1 | 小ファイル（〜1 MB）のコスト |
| `mediumFileCost` | 5 | 中ファイル（1〜100 MB）のコスト |
| `largeFileCost` | 20 | 大ファイル（100 MB〜）のコスト |
| `costMode` | `discrete` | `discrete` / `continuous` |
| `costScaleBytes` | 10,000,000 | 連続コストモード時のスケール係数 |
| `minCost` / `maxCost` | 1 / 50 | 連続コストモード時のクランプ範囲 |

### スライディングウィンドウ（#161）

| パラメーター | デフォルト | 説明 |
|---|---|---|
| `windowSec` | 30 | 評価ウィンドウ幅（秒） |
| `minSamples` | 10 | フィードバック判断に必要な最低サンプル数 |
| `windowMode` | `time` | `time` / `count` |

### AIMD フィードバック（#162）

| パラメーター | デフォルト | 説明 |
|---|---|---|
| `emergencyThreshold` | 0.10 | 急減速の 429 率閾値 |
| `emergencyDecay` | 0.7 | 急減速係数 |
| `slowDecay` | 0.9 | 緩減速係数 |
| `addStep` | TBD | 緩増加ステップ（tokens/sec） |
| `latencyRiseRatio` | 0.3 | レイテンシ悪化判定比率（ベースライン比 +30% で発動、§6.1） |
| `baselineSamples` | 20 | 初期ベースライン算出に使用する成功サンプル数 |
| `trendWindowSec` | 10 | 直近比レイテンシ判定の比較窓幅（秒） |
| `stableWindowSec` | 30 | 増加判定に必要な安定継続時間（秒） |
| `cooldownSec` | 20 | 急減速後の増加停止時間（秒） |
| `minRate` | TBD | レートの下限（tokens/sec）。**実質停止を避けるため最低 1 以上を推奨**（§6 レート範囲参照） |
| `maxRate` | TBD | レートの上限（tokens/sec） |

### 並列数補助制御（#163）

| パラメーター | デフォルト | 説明 |
|---|---|---|
| `maxInflight` | 16 | 並列数の設定上限（`configuredMax`） |
| `minInflight` | 2 | 動的調整時の下限 |
| `emergencyInflightDecay` | 0.75 | 緊急時の削減係数 |

> TBD 項目は Phase 1 実装中に初期値を決定し、本設計書を更新する。

---

## 13. v1 との制御ロジック対応表

| v1（v0.5.0） | v2（v0.6.0） | 変更内容 |
|---|---|---|
| ヒステリシス 3 段階（緊急/緩/加速） | AIMD + 4 信号（emergency / slow / stable / hold） | 概念は同等。`hold` 信号を明示追加 |
| requests/sec レート制御 | tokens/sec トークンバケット | コスト加重で帯域制御を実現 |
| インフライト閾値による dispatch 停止 | max_inflight による並列数上限（動的調整） | 役割を明確化（安全装置に特化） |
| 多時間窓（短期 5 秒 / 中期 30 秒） | 単一スライディングウィンドウ（30 秒） | 短期スパイクは `emergencyThreshold` 超過で即時検知（30 秒窓内でも 429 率が 10% を超えれば発火） |
| レイテンシ固定閾値（ms） | ベースライン比 / 直近比（`latencyRiseRatio`） | 環境差に追従する相対判定に変更（§6.1） |
| スコア関数による最適化 | AIMD による自己収束 | スコア関数は廃止（AIMD が動的最適化を担う） |
| `AdaptiveConcurrencyController` [Obsolete] | 削除 | v0.6.0 で完全移行 |

### 短期ウィンドウ廃止の根拠

v1 では `shortWindowSec=5` で急激なスパイクを検知していた。v2 では単一ウィンドウに統一するが、以下の理由で検知能力は低下しない：

- スパイクの指標は「短期間での 429 率上昇」→ 30 秒窓でも 3 件/30 件 = 10% で `emergency_decrease` が発火する
- 実行周期は 1 秒なので、スパイク発生から最大 1 秒で検知可能
- 検知遅延を懸念する場合は `controlIntervalSec` を 0.5 秒等に短縮すれば対応可能

---

## 14. 期待効果

- ファイルサイズのばらつきに強い帯域制御
- AIMD による 429 の自然な吸収（スラッシングなし）
- 並列数の有効活用（スロットを遊ばせない）
- パラメーターの意味が直感的で、チューニングしやすい
- パルス制御・レーン分離を実装せずに同等の効果を得られる可能性

---

## 15. 未決事項・リスク

| 項目 | 内容 | 決定タイミング |
|---|---|---|
| TBD パラメーター初期値 | `initialRate` / `maxBurst` / `addStep` / `minRate` / `maxRate` | Phase 1 の PoC 実装中 |
| レイテンシ判定モードの選定 | ベースライン比 / 直近比 / 併用のいずれを採用するか（§6.1） | Phase 1 PoC で比較検証 |
| 離散コスト vs 連続コスト | 初期は離散。連続モードへの切替判断は効果測定後 | Phase 3 完了後 |
| `_writeLock` 競合 | メトリクス書き込みが 1 秒周期で走るため、他 DB 操作と競合する可能性（#135 リスク 2 と共通） | Phase 1 実装前に方針決定 |
| RateControlledTransferController の扱い | 拡張するか内部実装を入れ替えるかで API 互換性が変わる | Phase 2 設計時 |
