# タスク管理

前フェーズ履歴: [docs/archive/tasks-archive-20260501.md](docs/archive/tasks-archive-20260501.md)

## 現在の状態: open issue 起点の v0.6.1 計画

- 確認日: 2026-05-01
- 対象リポジトリ: `scottlz0310/cloud-migrator`
- 確認方法: `gh issue list --state open --limit 200`
- open issue 数: 5 件

---

## 推奨実装順

| 順番 | Issue | 種別 | タイトル | 判断 |
|------|-------|------|----------|------|
| 1 | [#197](https://github.com/scottlz0310/cloud-migrator/issues/197) | enhancement | 転送先フォルダ確認フラグ | 実装済み（PR 作成予定）。`DriveFolderPicker` 組み込み・`DestinationConfirmed` フラグ永続化・Dropbox フォルダ選択ウィザードステップ追加。 |
| 2 | [#190](https://github.com/scottlz0310/cloud-migrator/issues/190) | bug | DashboardPage の ITransferStateDb が Dropbox 実行中も SharePoint DB を参照し続ける問題 | 実装済み（PR 対応中）。route-aware な state DB アクセサへ切り替え、Dashboard / Settings / MigrationWork が同じ DB 解決を使うようにした。 |
| 3 | [#189](https://github.com/scottlz0310/cloud-migrator/issues/189) | enhancement / dashboard | 路線に応じて設定項目を排他表示する | 次の推奨着手。#190 で整えた route/provider と DB 参照の境界を前提に、SharePoint 専用設定と Dropbox 専用設定を分離する。 |
| 4 | [#191](https://github.com/scottlz0310/cloud-migrator/issues/191) | refactor | DashboardPage タブバーを MudTabs（静的 MudTabPanel）に戻す | アクセシビリティと保守性改善。#190 と同じ DashboardPage に触れるため、bug 修正後に実施して差分衝突を避ける。 |
| 5 | [#15](https://github.com/scottlz0310/cloud-migrator/issues/15) | maintenance | Dependency Dashboard | 機能修正後に CI が安定した状態で依存関係更新を確認する。Renovate 管理のため通常実装とは別レーン。 |
| 保留 | [#101](https://github.com/scottlz0310/cloud-migrator/issues/101) | epic / installer | MSIX パッケージング・Microsoft Store 公開 | MSI 配布 #97 の運用実績、Partner Center、Store 提出素材が前提。現フェーズでは計画保持のみ。 |

---

## 1. #190: Dropbox 実行中の Dashboard state DB 参照修正

状態: 実装済み（PR 対応中）

### 目的

Dropbox 実行時に `MigrationWork` が Dropbox 専用 DB へ書き込む一方で、`DashboardPage` が DI singleton の SharePoint DB を読み続ける問題を解消する。

### 実装メモ

- `DashboardPage` の `GetLatestProcessingNameAsync` / `GetSummaryAsync` / `GetMetricsAsync` / `GetCheckpointAsync` が、実行中または選択中の provider に対応する DB を参照するようにする。
- `SettingsPage` も `ITransferStateDb` を直接注入して `ResetAllAsync` しているため、同じ route-aware な DB 解決を共有できる形を優先する。
- 候補:
  - per-run `ITransferStateDb` factory
  - route/provider に応じて DB を切り替える router 抽象
  - 起動時 provider に基づく DI 登録
- `MigrationWork` 側で使う DB と Dashboard 読み取り側の DB 選択規則を一致させる。
- DB インスタンスの lifetime と `DisposeAsync` の責務を明確にする。

### 受け入れ条件

- [x] Dropbox 路線で実行中、Dashboard の進捗バー・サマリ・スループットグラフが Dropbox DB から更新される。
- [x] SharePoint 路線の Dashboard 表示は従来どおり SharePoint DB を参照する。
- [x] route 切り替え後に古い DB ハンドルへ読み書きし続けない。
- [x] `NullTransferStateDb` フォールバック時の挙動が壊れない。
- [x] 関連ユニットテストを追加または更新する。

---

## 2. #189: 路線別 Settings 表示と Dropbox 専用設定追加

### 目的

SharePoint / Dropbox それぞれに関係する設定だけを表示し、Dropbox 専用設定を Dashboard 設定画面から編集できるようにする。

### 実装メモ

- `SettingsPage.razor` で `MigrationRoute` または `DestinationProvider` を基準に `IsSharePointRoute` / `IsDropboxRoute` を定義する。
- SharePoint 専用設定:
  - 転送制御エンジン
  - レートベース制御パラメーター
  - HybridRateController 実験的設定
  - 動的並列制御
  - 最大並行フォルダ作成数
- Dropbox 専用設定:
  - `SimpleUploadLimitMb`
  - `UploadChunkSizeMb`
  - `EnableEnsureFolder`
- 共通設定:
  - 最大並行転送数
  - タイムアウト
  - リトライ回数
  - 大ファイル閾値
- 非表示項目は保存時に値を消さず、既存設定を保持する。
- `ConfigurationService` の read/write と validation が Dropbox 設定を扱えるか確認し、不足があれば拡張する。

### 受け入れ条件

- [ ] SharePoint 路線では Dropbox 専用設定が表示されない。
- [ ] Dropbox 路線では SharePoint 専用設定が表示されない。
- [ ] Dropbox 路線で `SimpleUploadLimitMb` / `UploadChunkSizeMb` / `EnableEnsureFolder` を設定・保存できる。
- [ ] 共通設定は両路線で表示される。
- [ ] 非表示項目の既存値が保存時に失われない。
- [ ] 設定保存のユニットテストを追加または更新する。

---

## 3. #191: Dashboard タブバーを MudTabs に戻す

### 目的

PR #188 で導入したカスタム `<button>` タブバーを、MudBlazor 標準の `MudTabs` + 静的 `MudTabPanel` に戻し、保守性・キーボード操作・ARIA を回復する。

### 実装メモ

- `@foreach` による `MudTabPanel` 動的生成は避け、概要 / 詳細情報 / ログの 3 パネルを静的に配置する。
- `_dashboardTab` string は `_dashboardTabIndex` int に置き換える。
- 既存の概要・詳細情報・ログの中身は、対応する `MudTabPanel` 内へ移動する。
- 見た目調整は inline style ではなく MudBlazor の `Class` / `TabPanelClass` / `ActiveTabClass` などで対応する。
- MUD0002 が再発しないことを確認する。

### 受け入れ条件

- [ ] Dashboard タブが `MudTabs` + 静的 `MudTabPanel` 3 枚で構成される。
- [ ] キーボード操作と ARIA 属性が MudBlazor 標準挙動に戻る。
- [ ] 概要・詳細情報・ログの表示内容が既存実装から退化しない。
- [ ] MUD0002 アナライザーエラーが発生しない。
- [ ] Dashboard 関連テストまたはビルド確認を実施する。

---

## 4. #15: Dependency Dashboard 対応

### 目的

Renovate が検出した依存関係更新を、機能修正後の安定した状態で確認する。

### 実装メモ

- lock file maintenance は通常スケジュールに任せる。
- `Microsoft.Graph` / `Microsoft.NET.Test.Sdk` などの minor update PR が作成されたら CI 結果を確認する。
- `xunit` deprecation/replacement は影響範囲が大きいため、必要なら独立 issue 化して移行方針を決める。

### 受け入れ条件

- [ ] Renovate PR がある場合、CI 結果と差分を確認する。
- [ ] 破壊的変更がある dependency update は機能修正 PR と混ぜない。
- [ ] 必要に応じて追加 issue を起票する。

---

## 保留: #101 MSIX パッケージング・Microsoft Store 公開

### 保留理由

MSI 配布 #97 の安定運用、Microsoft Partner Center、Store 提出素材、プライバシーポリシー URL などが前提条件のため、現フェーズでは着手しない。

### 再開条件

- [ ] MSI 配布 #97 の運用実績が十分に積まれている。
- [ ] Partner Center アカウントと Store 提出要件が準備済み。
- [ ] MSIX / Store 配布の目的と対象ユーザーが明確になっている。

---

## 次の推奨着手

[#189](https://github.com/scottlz0310/cloud-migrator/issues/189) から開始する。
