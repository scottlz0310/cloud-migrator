# タスク管理

詳細仕様: [docs/implementation-plan.md](docs/implementation-plan.md)
前フェーズ履歴: [task-archive-20260409.md](task-archive-20260409.md)

## 現在の状態: v0.4.0 セットアップUX改善 実装中 🚧

Epic ISSUE: [#108 v0.4.0 セットアップUX改善 — オンボーディングウィザード & セキュアクレデンシャル](https://github.com/scottlz0310/cloud-migrator/issues/108)

---

## v0.4.0 実装計画

### 概要

環境変数依存のセットアップを廃止し、初回起動時のウィザードUI と Windows Credential Manager による安全な認証情報管理を実現する。  
**v0.4.0 スコープ**: Personal OneDrive → Dropbox 路線のエンド・ツー・エンド対応。  
**プラットフォーム**: Windows 専用（`net10.0-windows`）。

### 対応路線（v0.4.0）

```
Personal OneDrive → Dropbox（v0.4.0 でエンド・ツー・エンド対応）
Personal OneDrive → SharePoint Document Library（v0.5.0 に分離）
```

### 実装順序

```
#109（Credential Store）→ #114（Blazor Hybrid 基盤）→ #112（Dropbox OAuth）→ #113（Wizard Skeleton）
                                                      ↑
                              #112 の Spike 1（リダイレクト URI 仕様確認）は #114 と並行して先行調査可能
```

---

## ISSUE #109: セキュアクレデンシャルストア

**Issue**: [#109 feat: セキュアクレデンシャルストアの実装（Windows Credential Manager / DPAPI）](https://github.com/scottlz0310/cloud-migrator/issues/109)  
**Milestone**: v0.4.0  
**依存**: なし（最初に着手可能）

### Credential Key 定義（確定・変更不可）

```
cloud-migrator/azure/client-secret
cloud-migrator/azure/access-token
cloud-migrator/dropbox/app-key
cloud-migrator/dropbox/access-token
cloud-migrator/dropbox/refresh-token
```

### 実装タスク

- [x] `ICredentialStore` インターフェース定義（`CloudMigrator.Core`）— `GetAsync: Task<string?>（null = 未登録）`、`ExistsAsync: Task<bool>`
- [x] `WindowsCredentialStore` 実装（`CredWrite` / `CredRead` / `CredDelete` P/Invoke）
- [x] 障害時ハンドリング実装（Policy 禁止 / ストア破損 / 権限不足 / AV 干渉 / 非 Windows の各ケース）
- [x] Credential Key 定数クラス（`CredentialKeys.cs`）の定義
- [x] `EnvironmentCredentialStore` 実装（後方互換・v0.4.x のみ）+ `[Obsolete]` 警告付与
- [x] 上書き確認ロジック（`ExistsAsync` + ユーザー確認）— ウィザードおよび `init` コマンド両方で適用
- [x] DI 登録・既存プロバイダーへの注入
- [x] `init` コマンドでの対話的保存フロー対応
- [x] 非 Windows 環境での明示的エラーメッセージ表示 + 起動中断実装
- [x] 単体テスト（正常系 + 各障害ケース）

### 受け入れ基準

- [x] `sample.env` の認証情報項目を設定しなくてもツールが起動できる
- [x] Credential Manager に保存された認証情報が Graph / Dropbox プロバイダーで正常に使用される
- [x] 環境変数が設定されている場合は v0.4.x においてフォールバックとして機能する
- [x] config.json に秘密情報が書き出されない
- [x] 再セットアップ時（ウィザード・`init` コマンド両方）に既存 Credential の上書き確認が行われる
- [ ] 障害時（Policy 禁止・破損・権限不足・AV 干渉）に適切なエラーメッセージが表示される（Win32Exception メッセージで対応）
- [x] いかなる障害時も平文・環境変数へのサイレントフォールバックが発生しない
- [x] 非 Windows 環境では明示的なエラーメッセージが表示される

---

## ISSUE #114: Blazor Hybrid 基盤整備

**Issue**: [#114 feat: CloudMigrator.Dashboard を Blazor Hybrid（WPF + BlazorWebView）に移行](https://github.com/scottlz0310/cloud-migrator/issues/114)  
**Milestone**: v0.4.0  
**依存**: #109（`ICredentialStore` が DI に必要）  
**ブロック先**: #110, #111, #112, #113（完了後に着手）

### アーキテクチャ変更概要

現行の「ASP.NET Core Minimal API + HTML インライン文字列」方式を廃止し、  
**WPF + BlazorWebView（Blazor Hybrid）+ MudBlazor + 完全インプロセス DI** へ移行する。

### プロジェクト変換タスク

- [ ] `CloudMigrator.Dashboard.csproj` を `net10.0-windows` + `<UseWPF>true</UseWPF>` に変換
- [ ] `Microsoft.AspNetCore.Components.WebView.Wpf` NuGet パッケージを追加
- [ ] `MudBlazor` NuGet パッケージを追加
- [ ] WPF `App.xaml` / `MainWindow.xaml` + `BlazorWebView` コントロールの実装
- [ ] `_Imports.razor` / `wwwroot/index.html` など Blazor Hybrid 基盤ファイルの追加
- [ ] MudBlazor のテーマ設定（`MudThemeProvider` / `MudDialogProvider` 等）
- [ ] `IDialogService` インターフェース定義 + WPF `MessageBox` 実装の DI 登録

### HTTP レイヤー廃止タスク

- [ ] `DashboardServer.cs` の全 HTTP エンドポイントを DI サービス呼び出しに置き換え
- [ ] SSE ログストリームを `System.Threading.Channels.Channel` ベースのインプロセス配信に置き換え
- [ ] `DashboardServer.cs` の削除
- [ ] `LogStreamSink`（Serilog）を `Channel` に書き込む形に変更

### 既存ダッシュボードの移植タスク（MudBlazor コンポーネント）

- [ ] 転送ステータス表示コンポーネント（`ITransferStateDb` 直接呼び出し）
- [ ] ログストリーム表示コンポーネント（`Channel` 購読）
- [ ] 設定パネルコンポーネント（`IConfigurationService` 直接呼び出し）
- [ ] 転送ジョブ開始・停止コントロール（`ITransferJobService` 直接呼び出し）
- [ ] `IndexHtml` 定数の削除

### 起動経路変更タスク

- [ ] WPF Application エントリポイントの実装（`App.xaml.cs`）
- [ ] DI コンテナ構成（現行 `DashboardServer.BuildApp` の DI 登録を WPF App に移植）
- [ ] アプリ起動時に WPF ウィンドウを自動表示（ブラウザ手動起動の廃止）

### インフラタスク

- [ ] WebView2 Evergreen Bootstrapper を MSI ビルドに統合
- [ ] WebView2 の動作確認（**#112 Spike 2 相当を兼ねる**）

### 受け入れ基準

- [ ] アプリ起動時に WPF ウィンドウが自動的に開き、ブラウザの手動起動が不要になる
- [ ] 既存のダッシュボード機能（転送進捗・ログ・設定）が Blazor Hybrid + MudBlazor 上で動作する
- [ ] WebView2 が WPF ウィンドウ内で正常に動作することが確認される（#112 Spike 2 解決）
- [ ] `DashboardServer.cs` が削除され、HTTP レイヤーが完全に廃止されている
- [ ] Blazor コンポーネントが DI 経由でサービスを直接呼び出している（HTTP 不使用）
- [ ] ログストリームが `Channel` ベースのインプロセス配信で動作する
- [ ] Blazor コンポーネントが WPF 型に直接依存していない（インターフェース経由のみ）
- [ ] CLI コマンド（`transfer` / `doctor` 等）の既存動作に影響がない
- [ ] MSI インストーラーで WebView2 Evergreen Bootstrapper が組み込まれる

---

## ISSUE #112: Dropbox OAuth 2.0 フロー実装

**Issue**: [#112 feat: Dropbox OAuth 2.0 フロー実装（WebView2 PKCE）](https://github.com/scottlz0310/cloud-migrator/issues/112)  
**Milestone**: v0.4.0  
**依存**: #109（トークン保存先）、#114（WebView2 ホスト）

### 事前調査（Spike）

- [ ] **Spike 1 完了**: Dropbox App Console でのリダイレクト URI ポート仕様確認
  - 期待結果 A（ポート未指定OK）→ ランダムポート方式採用
  - 期待結果 B（ポート固定必須）→ 固定ポート方式（`http://127.0.0.1:54321` 等）にフォールバック
  - **結果を Issue #112 に記録すること**
- ~~Spike 2（WebView2 動作確認）~~: #114 の完了をもって解決済みとみなす

### 実装タスク

- [ ] Dropbox App Console 登録手順ガイド UI（D-1〜D-5、Public Client 方式として説明）
- [ ] App Key 入力フォーム → Credential Manager 保存（`cloud-migrator/dropbox/app-key`）
- [ ] PKCE `code_verifier` / `code_challenge` 生成ユーティリティ
- [ ] Dropbox OAuth 認可 URL 組み立て（`client_secret` パラメータなし）
- [ ] ポート選択 + 一時 HTTP リスナー実装（Spike 1 結果に応じてランダム or 固定）
- [ ] コールバックキャプチャ実装（WPF Host の WebView2 `NavigationStarting` を使用）
- [ ] `token` エンドポイントへのリクエスト処理（`client_secret` 省略）
- [ ] トークンの Credential Manager 保存（`dropbox/access-token` / `dropbox/refresh-token`）
- [ ] トークンライフサイクル管理:
  - [ ] Access Token 有効期限切れ時の透過的リフレッシュ実装
  - [ ] Refresh Token 失効検知（401 レスポンス）
  - [ ] 失効時の Credential 削除 + UI 通知 + Step 3 を `NotStarted` に戻す実装
- [ ] 既存 `DropboxStorageProvider` への Credential Manager 対応（#109 連携）

### 受け入れ基準

- [ ] **Spike 1 が完了しており、リダイレクト URI 方式が確定している**
- [ ] App Key のみで認証が完結できる（App Secret 入力不要）
- [ ] App Key はユーザーが自身で登録・入力する（バイナリ埋め込みなし）
- [ ] ウィザードの「Dropbox と連携」ボタンからブラウザ認証を完了できる
- [ ] 取得したトークンが自動的に Credential Manager に保存される
- [ ] 次回起動時にトークンが自動読み込みされ、再認証不要
- [ ] Access Token 有効期限切れ時に透過的リフレッシュが動作する
- [ ] Refresh Token 失効時に UI 通知 + Step 3 再認証フローへの誘導が動作する

---

## ISSUE #113: オンボーディングウィザード UI

**Issue**: [#113 feat: オンボーディングウィザード UI（初回起動フロー統合）](https://github.com/scottlz0310/cloud-migrator/issues/113)  
**Milestone**: v0.4.0  
**依存**: #109, #114, #112

### ウィザードステップ構成（v0.4.0）

```
Welcome
  └── Step 0: 移行路線選択
        ├── [OneDrive→Dropbox 路線]（v0.4.0 実装）
        │     └── Step 3: Dropbox OAuth 連携（#112）
        │           └── Step 4: 接続テスト & 完了
        └── [OneDrive→SharePoint 路線]（v0.5.0 予定）
              └── 「この路線は v0.5.0 で対応予定です」プレースホルダー画面
```

### 実装タスク（v0.4.0）

- [ ] `WizardStepState` enum 定義（`NotStarted` / `InProgress` / `Verified` / `Failed` / `Skipped`）
- [ ] `wizard-state.json` 読み書きサービス実装（スキーマバージョン管理・破損時フォールバックを含む）
  - 保存先: `%APPDATA%\CloudMigrator\wizard-state.json`
  - `InProgress` はファイルに書き出さない（`NotStarted` に戻してから保存）
  - 未知 `schemaVersion` / パース失敗時: `wizard-state.backup.json` にリネーム → 初期化
- [ ] アプリ終了時に `InProgress` → `NotStarted` で保存するシャットダウンフック実装
- [ ] Welcome 画面 UI（MudBlazor）
- [ ] Step 0: 移行路線選択 UI（MudBlazor）
  - [ ] SharePoint 路線選択時の「v0.5.0 で対応予定」プレースホルダー画面
- [ ] ステップ間の進行制御（前ステップが `Verified`/`Skipped` でないと進めない）
- [ ] 初回起動検出ロジック（`wizard-state.json` 不在 + Credential 未登録の複合判定）
- [ ] 中断再開ロジック（最初の `NotStarted`/`Failed` から再開）
- [ ] Step 4: 接続テスト UI（Dropbox 路線のみ・DI 経由で doctor/verify 呼び出し）
  - [ ] Credential Verify → Discovery Verify → Migration Preflight の 3 層を順に実行
  - [ ] 失敗時の原因層・ステップ特定表示
- [ ] 「セットアップをやり直す」メニューエントリ（全ステップ `NotStarted` リセット）
- [ ] 完了後のダッシュボードへの遷移

### 受け入れ基準（v0.4.0）

- [ ] 初回起動時にウィザードが自動表示される（`wizard-state.json` 不在が主判定条件）
- [ ] Step 0 で SharePoint 路線を選択すると「v0.5.0 で対応予定」画面が表示される
- [ ] Dropbox 路線でステップ 0→3→4 がエンド・ツー・エンドで動作する
- [ ] Step 4 が Credential Verify → Discovery Verify → Migration Preflight の 3 層を実行する
- [ ] 期限切れ・無効なトークンの場合にステップが `Failed` として検出される
- [ ] `InProgress` 状態でアプリを終了した場合、`wizard-state.json` には `NotStarted` として保存される
- [ ] 中断後の再起動で最初の未完了ステップから再開できる
- [ ] Step 4 接続テスト失敗時に失敗した層（Credential / Discovery / Preflight）が表示される
- [ ] ウィザード完了後にダッシュボードに遷移し、ファイル転送が実行できる状態になっている
- [ ] `schemaVersion` が未知の `wizard-state.json` を読み込んだ際にバックアップ化・初期化が行われる
- [ ] オンボーディング完了後、Migration Runtime が config.json と Credential Manager の値を変換なしに読み込める

---

## v0.4.0 全体の受け入れ基準

- [ ] 環境変数なしで初回セットアップが完結できる
- [ ] アプリ起動時にネイティブウィンドウが自動的に開く（ブラウザの手動起動不要）
- [ ] Personal OneDrive → Dropbox 路線のセットアップがウィザードで完結できる
- [ ] Dropbox 連携が App Secret なしの OAuth PKCE フローで完結できる
- [ ] 認証情報が Windows Credential Manager に安全に保存される
- [ ] オンボーディング完了後、Migration Runtime が追加操作なしに転送を開始できる
- [ ] 非 Windows 環境で起動した際に明示的なエラーメッセージが表示される

---

## v0.5.0 スコープ（今回対象外）

| ISSUE | 内容 |
|-------|------|
| [#110](https://github.com/scottlz0310/cloud-migrator/issues/110) | Azure Entra ID アプリ登録 & API権限設定ガイド |
| [#111](https://github.com/scottlz0310/cloud-migrator/issues/111) | Graph API リソース発見（OneDrive + SharePoint） |
| #113（残ステップ） | Step 1・2a・2b の Wizard UI（SharePoint 路線） |

---

## Verify 責務の分離（全 ISSUE 共通定義）

| 層 | 目的 | 実施タイミング | 担当 ISSUE |
|----|------|--------------|-----------:|
| **Credential Verify** | シークレット / トークンの存在と有効性確認 | 各ステップの入力完了直後 | #109 / #112 |
| **Discovery Verify** | 対象リソース（Drive ID 等）が実際に到達できるか確認 | Discovery 完了後 | #111（v0.5.0）/ #112 |
| **Migration Preflight** | 実際の読み書き権限を小ファイルで確認 | Step 4（接続テスト） | #113 |
