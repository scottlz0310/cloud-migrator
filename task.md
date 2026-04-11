# タスク管理

詳細仕様: [docs/implementation-plan.md](docs/implementation-plan.md)
前フェーズ履歴: [task-archive-20260409.md](task-archive-20260409.md)

## 現在の状態: v0.4.0 セットアップUX改善 実装中 🚧

Epic ISSUE: [#108 v0.4.0 セットアップUX改善 — オンボーディングウィザード & セキュアクレデンシャル](https://github.com/scottlz0310/cloud-migrator/issues/108)

---

## v0.4.0 実装計画

### 概要

環境変数依存のセットアップを廃止し、初回起動時のウィザードUI と Windows Credential Manager による安全な認証情報管理を実現する。  
**v0.4.0 スコープ**: Personal OneDrive → Dropbox 路線・Personal OneDrive → SharePoint 路線の両方をエンド・ツー・エンドで対応。  
**プラットフォーム**: Windows 専用（`net10.0-windows`）。

### 対応路線（v0.4.0）

```
Personal OneDrive → Dropbox（v0.4.0 でエンド・ツー・エンド対応）
Personal OneDrive → SharePoint Document Library（v0.4.0 でエンド・ツー・エンド対応）
```

### 実装順序

```
#109（Credential Store）→ #114（Blazor Hybrid 基盤）→ #110（Azure 認証ガイド）→ #111（Graph Discovery）→ #113残ステップ（SharePoint Wizard）
                                                      ↑
                              #112（Dropbox OAuth）は #114 完了後に並行着手可能
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
- [x] 障害時（Policy 禁止・破損・権限不足・AV 干渉）に適切なエラーメッセージが表示される（Win32Exception メッセージで対応）
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

- [x] **Spike 1 完了**: Dropbox App Console でのリダイレクト URI ポート仕様確認
  - 結果 B（ポート固定必須）→ 固定ポート方式（`http://127.0.0.1:54321–54325`）+ ポート競合フォールバック
  - **結果を Issue #112 に記録済み**
- ~~Spike 2（WebView2 動作確認）~~: #114 の完了をもって解決済みとみなす

### 実装タスク (バックエンド層 Phase 112a)

- [x] PKCE `code_verifier` / `code_challenge` 生成 (`DropboxOAuthService`)
- [x] Dropbox OAuth 認可 URL 組み立て (`client_secret` パラメータなし)
- [x] 固定ポート + 一時 HTTP リスナー実装 (54321–54325 フォールバック)
- [x] コールバックキャプチャ実装 (`WaitForCallbackAsync`)
- [x] `token` エンドポイントへのリクエスト処理 (`ExchangeCodeAsync`)
- [x] `IDropboxOAuthService` / `DropboxTokenResult` / `DropboxRefreshResult` / `DropboxOAuthException` 実装
- [x] トークンライフサイクル管理:
  - [x] `DropboxStorageProvider` Credential Store コンストラクタ追加
  - [x] Access Token 有効期限切れ時の透過的リフレッシュ (oAuthService パス)
  - [x] Refresh Token 失効検知 (IsTokenExpired) + Credential 削除 + 再認証例外送出
- [x] 既存 `DropboxStorageProvider` への Credential Manager 対応 (#109 連携)
- [ ] Dropbox App Console 登録手順ガイド UI（D-1〜D-5）← #113 で対応予定
- [ ] App Key 入力フォーム → Credential Manager 保存    ← #113 で対応予定
- [ ] コールバックキャプチャ（WPF WebView2 `NavigationStarting` 利用）← #113 で対応予定
- [ ] 失効時の UI 通知 + Step 3 を `NotStarted` に戻す実装  ← #113 で対応予定

### 受け入れ基準

- [x] **Spike 1 が完了しており、リダイレクト URI 方式が確定している**
- [x] App Key のみで認証が完結できる（App Secret 入力不要）
- [ ] App Key はユーザーが自身で登録・入力する（バイナリ埋め込みなし）← UI #113
- [ ] ウィザードの「Dropbox と連携」ボタンからブラウザ認証を完了できる ← UI #113
- [x] 取得したトークンが自動的に Credential Manager に保存される
- [x] 次回起動時にトークンが自動読み込みされ、再認証不要
- [x] Access Token 有効期限切れ時に透過的リフレッシュが動作する
- [x] Refresh Token 失効時に Credential 削除 + 再認証例外送出が動作する

---

## ISSUE #113: オンボーディングウィザード UI

**Issue**: [#113 feat: オンボーディングウィザード UI（初回起動フロー統合）](https://github.com/scottlz0310/cloud-migrator/issues/113)  
**Milestone**: v0.4.0  
**依存**: #109, #114, #112

### ウィザードステップ構成（v0.4.0）

```
Welcome
  └── Step 0: 移行路線選択
        ├── [OneDrive→Dropbox 路線]
        │     └── Step 1: Azure 認証設定（#110）
        │           └── Step 2a: OneDrive Drive ID 取得（#111）
        │                 └── Step 3: Dropbox OAuth 連携（#112）
        │                       └── Step 4: 接続テスト & 完了
        └── [OneDrive→SharePoint 路線]
              └── Step 1: Azure 認証設定（#110）
                    └── Step 2a: OneDrive Drive ID 取得（#111）
                          └── Step 2b: SharePoint Discovery（#111）
                                └── Step 4: 接続テスト & 完了
```

### 実装タスク（v0.4.0）

- [x] `WizardStepState` enum 定義（`NotStarted` / `InProgress` / `Verified` / `Failed` / `Skipped`）
- [x] `wizard-state.json` 読み書きサービス実装（スキーマバージョン管理・破損時フォールバックを含む）
  - 保存先: `%APPDATA%\CloudMigrator\wizard-state.json`
  - `InProgress` はファイルに書き出さない（`NotStarted` に戻してから保存）
  - 未知 `schemaVersion` / パース失敗時: `wizard-state.backup.json` にリネーム → 初期化
- [x] アプリ終了時に `InProgress` → `NotStarted` で保存するシャットダウンフック実装
- [x] Welcome 画面 UI（MudBlazor）
- [x] Step 0: 移行路線選択 UI（MudBlazor）
  - [x] SharePoint 路線選択時の未実装プレースホルダー画面（v0.4.0 スコープで #113残ステップにて本実装に置換）
- [x] ステップ間の進行制御（前ステップが `Verified`/`Skipped` でないと進めない）
- [x] 初回起動検出ロジック（`wizard-state.json` 不在 + Credential 未登録の複合判定）
- [x] 中断再開ロジック（最初の `NotStarted`/`Failed` から再開）
- [x] Step 4: 接続テスト UI（DI 経由で doctor/verify 呼び出し・Dropbox／SharePoint 両路線対応）
  - [x] Credential Verify → Discovery Verify → Migration Preflight の 3 層を順に実行
  - [x] 失敗時の原因層・ステップ特定表示
- [x] 「セットアップをやり直す」メニューエントリ（全ステップ `NotStarted` リセット）
- [x] 完了後のダッシュボードへの遷移

### 受け入れ基準（v0.4.0）

- [x] 初回起動時にウィザードが自動表示される（`wizard-state.json` 不在が主判定条件）
- [x] Step 0 で SharePoint 路線を選択するとプレースホルダー画面が表示される（v0.4.0 スコープで #113残ステップにて本実装に置換）
- [x] Dropbox 路線でステップ 0→1→2a→3→4 がエンド・ツー・エンドで動作する（Step 1・2a は #110・#111 実装後）
- [x] Step 4 が Credential Verify → Discovery Verify → Migration Preflight の 3 層を実行する
- [ ] 期限切れ・無効なトークンの場合にステップが `Failed` として検出される（→ #113残ステップで対応）
- [x] `InProgress` 状態でアプリを終了した場合、`wizard-state.json` には `NotStarted` として保存される
- [x] 中断後の再起動で最初の未完了ステップから再開できる
- [x] Step 4 接続テスト失敗時に失敗した層（Credential / Discovery / Preflight）が表示される
- [x] ウィザード完了後にダッシュボードに遷移し、ファイル転送が実行できる状態になっている
- [x] `schemaVersion` が未知の `wizard-state.json` を読み込んだ際にバックアップ化・初期化が行われる
- [x] オンボーディング完了後、Migration Runtime が config.json と Credential Manager の値を変換なしに読み込める

---

## ISSUE #110: Azure Entra ID アプリ登録 & API権限設定ガイド

**Issue**: [#110 feat: Azure Entra ID アプリ登録 & API権限設定ガイド（オンボーディング Step 1）](https://github.com/scottlz0310/cloud-migrator/issues/110)  
**Milestone**: v0.4.0  
**依存**: #114（Blazor Hybrid 基盤）

### 実装タスク

- [ ] ウィザード Step 1 UI コンポーネント（各ステップのガイドパネル）
- [ ] Application Permission / Delegated Permission の違いを説明するガイドテキスト
- [ ] 最小権限セット（`Files.ReadWrite.All` / `Sites.Read.All` / `User.Read.All`）の説明テキスト（`User.Read.All` の用途を明示）
- [ ] Step 1-3: Application Permission（App-only 認証）であることを UI 上で明示するテキスト
- [ ] Step 1-4: 管理者同意 URL 生成・クリップボードコピー機能（ケース A/B 分岐）
- [ ] Step 1-5: Secret 有効期限選択 UI + `clientSecretExpiry` の config.json 保存
- [ ] ClientID / TenantID / ClientSecret 入力フォーム → Credential Manager 保存（#109 連携）
- [ ] 入力後の接続テスト（Graph API App-only auth 疎通確認）
- [ ] 起動時 Secret 期限チェック + 30 日前警告表示

### 受け入れ基準

- [ ] Application Permission（App-only）フローで認証できることが確認できる
- [ ] 最小権限セット（3権限）で移行が完結できる
- [ ] 管理者同意が必要であることをウィザード内で認識できる
- [ ] 一般ユーザーでも同意依頼 URL を生成して管理者に送付できる
- [ ] `User.Read.All` の用途が UI 上で明示されている
- [ ] Secret 有効期限が `clientSecretExpiry` キーとして config.json に保存される
- [ ] Secret 期限 30 日前にダッシュボード警告が表示される

---

## ISSUE #111: Graph API リソース発見（OneDrive + SharePoint）

**Issue**: [#111 feat: Graph Explorer フレーム統合（Site ID / Drive ID 発見支援）（オンボーディング Step 2）](https://github.com/scottlz0310/cloud-migrator/issues/111)  
**Milestone**: v0.4.0  
**依存**: #110（Client Credentials フロー確定後に着手）

### 実装タスク

- [ ] Personal OneDrive: userId / UPN 入力フォーム → `GET /users/{userId}/drive` → Drive ID 取得
- [ ] Personal OneDrive: UI 上で App-only 認証の制約（`GET /me/drive` 不可）を説明するテキスト表示
- [ ] SharePoint: Site 名キーワード入力 UI → `GET /sites?search={keyword}` 呼び出し
- [ ] SharePoint: Site 一覧表示（表示名付き）+ 選択
- [ ] SharePoint: Drive（Document Library）一覧取得 → 表示名付きリストで表示 + 選択
- [ ] SharePoint: 検索 0 件時のフォールバック（Site URL 直接入力フォーム）
- [ ] Discovery 結果を config.json スキーマ（`migrationRoute` / `source` / `destination`）で保存
- [ ] Discovery Verify（`GET /drives/{driveId}` 疎通確認）
- [ ] Migration Preflight（読み取り権限 + 書き込み権限の確認）
- [ ] OneDrive→Dropbox 路線時は SharePoint 取得 UI をスキップし、Dropbox 用スキーマで保存する分岐
- [ ] Graph Explorer フレームへのプリセットクエリ注入（補助用）— WebView2 埋め込み可否の事前確認必須
- [ ] Graph Explorer サインインの文脈説明テキスト表示
- [ ] Admin Consent 未付与エラー時のガイドメッセージ

### 受け入れ基準

- [ ] Personal OneDrive Drive ID（移行元）がアプリ内で取得・保存できる
- [ ] App-only 認証の制約（UPN 入力必須）が UI 上で明示されている
- [ ] SharePoint Site をキーワード検索で絞り込める（`search=*` は使用しない）
- [ ] キーワード検索で 0 件の場合に Site URL 直接入力フォールバックが表示される
- [ ] Document Library の一覧に表示名（Display Name）が表示される
- [ ] Discovery 結果が config.json スキーマに従って保存される
- [ ] Discovery Verify と Migration Preflight の両方が成功した場合のみステップが `Verified` になる
- [ ] OneDrive→Dropbox 路線選択時に SharePoint 取得 UI がスキップされ、Dropbox 用スキーマで保存される
- [ ] Graph Explorer が補助参照ツールとして利用できる（または外部ブラウザで開く）
- [ ] Admin Consent 未付与エラー時に適切なガイドが表示される

---

## ISSUE #113 残ステップ: SharePoint 路線 Wizard UI

**Issue**: [#113 feat: オンボーディングウィザード UI（初回起動フロー統合）](https://github.com/scottlz0310/cloud-migrator/issues/113)  
**Milestone**: v0.4.0（残ステップ: Step 1・2a・2b）  
**依存**: #110, #111

### 実装タスク（SharePoint 路線追加分）

- [ ] Step 1（Azure 認証設定）: Step 1-1〜1-6 のウィザード UI（#110 連携）
- [ ] Step 2a（OneDrive Discovery）: UPN 入力 → Drive ID 取得・確認画面（#111 連携）
- [ ] Step 2b（SharePoint Discovery）: Site 検索 → Library 選択 → 確認画面（#111 連携）
- [ ] Step 4 を SharePoint 路線でも動作させる（Dropbox 路線との分岐拡張）
- [ ] 期限切れ・無効なトークンの場合にステップが `Failed` として検出される実装
- [ ] 失効時の UI 通知 + 該当ステップを `NotStarted` に戻す実装（#112 連携）

### 受け入れ基準

- [ ] Personal OneDrive → SharePoint 路線のウィザードがエンド・ツー・エンドで動作する
- [ ] Step 1〜2a〜2b〜4 の順序でステップが進行できる
- [ ] 期限切れ・無効なトークンの場合にステップが `Failed` として検出される

---

## v0.4.0 全体の受け入れ基準

- [ ] 環境変数なしで初回セットアップが完結できる
- [ ] アプリ起動時にネイティブウィンドウが自動的に開く（ブラウザの手動起動不要）
- [ ] Personal OneDrive → Dropbox 路線のセットアップがウィザードで完結できる
- [ ] Personal OneDrive → SharePoint 路線のセットアップがウィザードで完結できる
- [ ] Dropbox 連携が App Secret なしの OAuth PKCE フローで完結できる
- [ ] Azure 認証が Application Permission（Client Credentials）フローで完結できる
- [ ] 認証情報が Windows Credential Manager に安全に保存される
- [ ] オンボーディング完了後、Migration Runtime が追加操作なしに転送を開始できる
- [ ] 非 Windows 環境で起動した際に明示的なエラーメッセージが表示される

---

## Verify 責務の分離（全 ISSUE 共通定義）

| 層 | 目的 | 実施タイミング | 担当 ISSUE |
|----|------|--------------|-----------:|
| **Credential Verify** | シークレット / トークンの存在と有効性確認 | 各ステップの入力完了直後 | #109 / #112 |
| **Discovery Verify** | 対象リソース（Drive ID 等）が実際に到達できるか確認 | Discovery 完了後 | #111 / #112 |
| **Migration Preflight** | 実際の読み書き権限を小ファイルで確認 | Step 4（接続テスト） | #113 |
