# タスク管理

前フェーズ履歴: [docs/archive/tasks-archive-20260501.md](docs/archive/tasks-archive-20260501.md)

## 現在の状態: open issue 起点の v0.6.1 計画

- 確認日: 2026-05-03
- 対象リポジトリ: `scottlz0310/cloud-migrator`
- 確認方法: `gh issue list --state open --limit 200`
- open issue 数: 6 件

### 最近の完了（main マージ済み）

| PR | Issue | タイトル | マージ日 |
|----|-------|----------|----------|
| [#200](https://github.com/scottlz0310/cloud-migrator/pull/200) | [#197](https://github.com/scottlz0310/cloud-migrator/issues/197) | 転送先フォルダ確認フラグを実装 | 2026-05-01 |
| [#194](https://github.com/scottlz0310/cloud-migrator/pull/194) | [#190](https://github.com/scottlz0310/cloud-migrator/issues/190) | Dashboard state DB を route-aware に修正 | 2026-05-01 |
| [#202](https://github.com/scottlz0310/cloud-migrator/pull/202) | [#198](https://github.com/scottlz0310/cloud-migrator/issues/198) | ルート・転送パス変更時の state DB 初期化を必須化 | 2026-05-02 |

---

## 推奨実装順

| 順番 | Issue | 種別 | タイトル | 判断 |
|------|-------|------|----------|------|
| 1 | [#189](https://github.com/scottlz0310/cloud-migrator/issues/189) | enhancement / dashboard | 路線に応じて設定項目を排他表示する | 次の推奨着手。#190 / #198 で整えた route-aware 境界を前提に、SharePoint 専用設定と Dropbox 専用設定を分離する。 |
| 2 | [#191](https://github.com/scottlz0310/cloud-migrator/issues/191) | refactor | DashboardPage タブバーを MudTabs（静的 MudTabPanel）に戻す | アクセシビリティと保守性改善。#189 と同じ DashboardPage に触れるため、設定分離後に実施して差分衝突を避ける。 |
| 3 | [#195](https://github.com/scottlz0310/cloud-migrator/issues/195) | design | SharePoint / Dropbox ルート分離の責務境界を整理する | 設計 issue。#189 / #191 の実装後に方針を確定し、#196 の足場として使う。 |
| 4 | [#196](https://github.com/scottlz0310/cloud-migrator/issues/196) | refactor / epic | Dashboard 中心の制御層を MVVM / provider 拡張しやすい構造へ整理する | 大規模リファクタ。#195 の方針合意後に段階実装。 |
| 5 | [#15](https://github.com/scottlz0310/cloud-migrator/issues/15) | maintenance | Dependency Dashboard | 機能修正後に CI が安定した状態で依存関係更新を確認する。Renovate 管理のため通常実装とは別レーン。 |
| 保留 | [#101](https://github.com/scottlz0310/cloud-migrator/issues/101) | epic / installer | MSIX パッケージング・Microsoft Store 公開 | MSI 配布 #97 の運用実績、Partner Center、Store 提出素材が前提。現フェーズでは計画保持のみ。 |

---

## 1. #189: 路線別 Settings 表示と Dropbox 専用設定追加

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

## 2. #191: Dashboard タブバーを MudTabs に戻す

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

## 3. #195: SharePoint / Dropbox ルート分離の責務境界を整理する

### 目的

`App.xaml.cs` / `DashboardPage` / `SettingsPage` / `MigratorOptions` などに散在する `DestinationProvider` / `isDropbox` 分岐を、ルート固有責務として外へ切り出す設計方針を決める。

### 議論の観点

- `IMigrationRouteRunner` / `IMigrationRouteDescriptor` などの抽象導入の要否
- state DB パス・フェーズ定義・設定セクションを route descriptor に持たせるか
- `#189` 実装時にどこまで足場を作るか（tactical fix か thin interface か）
- #196 の MVVM リファクタとの関係整理

### 受け入れ条件

- [ ] 責務境界の方針を issue または PR コメントで明文化する。
- [ ] #189 / #191 との実装順と依存関係を整理する。
- [ ] 大規模一括リファクタではなく段階的移行の計画を確認する。

---

## 4. #196: Dashboard 制御層の MVVM / provider 拡張対応（epic）

### 目的

Dashboard を単なる表示・操作入口に近づけ、状態管理と実行制御を ViewModel / Application Service 側へ段階的に移行する。

### 前提

- #195 の方針合意後に詳細設計へ入る。
- #189 / #191 完了後のクリーンな状態から着手する。

### 実装の方向性

- Dashboard ViewModel / Settings ViewModel の切り出し
- `MigrationWork` の責務を route runner / provider runner へ委譲
- provider 固有設定を provider 別 options へ分離（`MigratorOptions` 肥大化解消）
- route descriptor を単一定義として state DB / metrics / phase / settings sections を参照

### 受け入れ条件

- [ ] 段階的移行の PR 計画（最低 2 フェーズ）が issue に記録されている。
- [ ] 各 PR が既存機能を退化させない。
- [ ] provider 追加時の変更箇所が減少している。

---

## 5. #15: Dependency Dashboard 対応

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
