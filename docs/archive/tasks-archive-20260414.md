# タスク管理

詳細仕様: [docs/implementation-plan.md](docs/implementation-plan.md)
前フェーズ履歴: [tasks-archive-20260409.md](tasks-archive-20260409.md)

## 現在の状態: v0.4.0 セットアップUX改善 完了 ✅

Epic ISSUE: [#108 v0.4.0 セットアップUX改善 — オンボーディングウィザード & セキュアクレデンシャル](https://github.com/scottlz0310/cloud-migrator/issues/108) — **Closed**

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

## ISSUE #109: セキュアクレデンシャルストア ✅

**Issue**: [#109 feat: セキュアクレデンシャルストアの実装（Windows Credential Manager / DPAPI）](https://github.com/scottlz0310/cloud-migrator/issues/109) — **Closed**  
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

## ISSUE #114: Blazor Hybrid 基盤整備 ✅

**Issue**: [#114 feat: CloudMigrator.Dashboard を Blazor Hybrid（WPF + BlazorWebView）に移行](https://github.com/scottlz0310/cloud-migrator/issues/114) — **Closed**  
**Milestone**: v0.4.0  
**依存**: #109（`ICredentialStore` が DI に必要）  
**ブロック先**: #110, #111, #112, #113（完了後に着手）

### アーキテクチャ変更概要

現行の「ASP.NET Core Minimal API + HTML インライン文字列」方式を廃止し、  
**WPF + BlazorWebView（Blazor Hybrid）+ MudBlazor + 完全インプロセス DI** へ移行する。

### 実装タスク（全完了）

- [x] `CloudMigrator.Dashboard.csproj` を `net10.0-windows` + `<UseWPF>true</UseWPF>` に変換
- [x] `Microsoft.AspNetCore.Components.WebView.Wpf` NuGet パッケージを追加
- [x] `MudBlazor` NuGet パッケージを追加
- [x] WPF `App.xaml` / `MainWindow.xaml` + `BlazorWebView` コントロールの実装
- [x] `_Imports.razor` / `wwwroot/index.html` など Blazor Hybrid 基盤ファイルの追加
- [x] MudBlazor のテーマ設定（`MudThemeProvider` / `MudDialogProvider` 等）
- [x] `IDialogService` インターフェース定義 + WPF `MessageBox` 実装の DI 登録
- [x] `DashboardServer.cs` の全 HTTP エンドポイントを DI サービス呼び出しに置き換え
- [x] SSE ログストリームを `System.Threading.Channels.Channel` ベースのインプロセス配信に置き換え
- [x] `DashboardServer.cs` の削除
- [x] `LogStreamSink`（Serilog）を `Channel` に書き込む形に変更
- [x] 転送ステータス表示コンポーネント（`ITransferStateDb` 直接呼び出し）
- [x] ログストリーム表示コンポーネント（`Channel` 購読）
- [x] 設定パネルコンポーネント（`IConfigurationService` 直接呼び出し）
- [x] 転送ジョブ開始・停止コントロール（`ITransferJobService` 直接呼び出し）
- [x] `IndexHtml` 定数の削除
- [x] WPF Application エントリポイントの実装（`App.xaml.cs`）
- [x] DI コンテナ構成（現行 `DashboardServer.BuildApp` の DI 登録を WPF App に移植）
- [x] アプリ起動時に WPF ウィンドウを自動表示（ブラウザ手動起動の廃止）
- [x] WebView2 Evergreen Bootstrapper を MSI ビルドに統合
- [x] WebView2 の動作確認（**#112 Spike 2 相当を兼ねる**）

### 受け入れ基準

- [x] アプリ起動時に WPF ウィンドウが自動的に開き、ブラウザの手動起動が不要になる
- [x] 既存のダッシュボード機能（転送進捗・ログ・設定）が Blazor Hybrid + MudBlazor 上で動作する
- [x] WebView2 が WPF ウィンドウ内で正常に動作することが確認される（#112 Spike 2 解決）
- [x] `DashboardServer.cs` が削除され、HTTP レイヤーが完全に廃止されている
- [x] Blazor コンポーネントが DI 経由でサービスを直接呼び出している（HTTP 不使用）
- [x] ログストリームが `Channel` ベースのインプロセス配信で動作する
- [x] Blazor コンポーネントが WPF 型に直接依存していない（インターフェース経由のみ）
- [x] CLI コマンド（`transfer` / `doctor` 等）の既存動作に影響がない
- [x] MSI インストーラーで WebView2 Evergreen Bootstrapper が組み込まれる

---

## ISSUE #112: Dropbox OAuth 2.0 フロー実装 ✅

**Issue**: [#112 feat: Dropbox OAuth 2.0 フロー実装（WebView2 PKCE）](https://github.com/scottlz0310/cloud-migrator/issues/112) — **Closed**  
**Milestone**: v0.4.0  
**依存**: #109（トークン保存先）、#114（WebView2 ホスト）

### 事前調査（Spike）

- [x] **Spike 1 完了**: Dropbox App Console でのリダイレクト URI ポート仕様確認
  - 結果 B（ポート固定必須）→ 固定ポート方式（`http://127.0.0.1:54321–54325`）+ ポート競合フォールバック
  - **結果を Issue #112 に記録済み**
- ~~Spike 2（WebView2 動作確認）~~: #114 の完了をもって解決済みとみなす

### 実装タスク

- [x] PKCE `code_verifier` / `code_challenge` 生成 (`DropboxOAuthService`)
- [x] Dropbox OAuth 認可 URL 組み立て (`client_secret` パラメータなし)
- [x] 固定ポート + 一時 HTTP リスナー実装 (54321–54325 フォールバック)
- [x] コールバックキャプチャ実装 (`WaitForCallbackAsync`)
- [x] `token` エンドポイントへのリクエスト処理 (`ExchangeCodeAsync`)
- [x] `IDropboxOAuthService` / `DropboxTokenResult` / `DropboxRefreshResult` / `DropboxOAuthException` 実装
- [x] トークンライフサイクル管理（透過的リフレッシュ・失効検知・Credential 削除・再認証例外送出）
- [x] 既存 `DropboxStorageProvider` への Credential Manager 対応 (#109 連携)
- [x] Dropbox App Console 登録手順ガイド UI（D-1〜D-5）← #113 で対応
- [x] App Key 入力フォーム → Credential Manager 保存 ← #113 で対応
- [x] コールバックキャプチャ（WPF WebView2 `NavigationStarting` 利用）← #113 で対応
- [x] 失効時の UI 通知 + Step 3 を `NotStarted` に戻す実装 ← #113 で対応

### 受け入れ基準

- [x] Spike 1 が完了しており、リダイレクト URI 方式が確定している
- [x] App Key のみで認証が完結できる（App Secret 入力不要）
- [x] App Key はユーザーが自身で登録・入力する（バイナリ埋め込みなし）
- [x] ウィザードの「Dropbox と連携」ボタンからブラウザ認証を完了できる
- [x] 取得したトークンが自動的に Credential Manager に保存される
- [x] 次回起動時にトークンが自動読み込みされ、再認証不要
- [x] Access Token 有効期限切れ時に透過的リフレッシュが動作する
- [x] Refresh Token 失効時に Credential 削除 + 再認証例外送出が動作する

---

## ISSUE #113: オンボーディングウィザード UI ✅

**Issue**: [#113 feat: オンボーディングウィザード UI（初回起動フロー統合）](https://github.com/scottlz0310/cloud-migrator/issues/113) — **Closed**  
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

### 実装タスク（全完了）

- [x] `WizardStepState` enum 定義
- [x] `wizard-state.json` 読み書きサービス実装（スキーマバージョン管理・破損時フォールバック）
- [x] アプリ終了時 `InProgress` → `NotStarted` シャットダウンフック
- [x] Welcome 画面 UI / Step 0: 移行路線選択 UI
- [x] ステップ間進行制御（前ステップ Verified/Skipped 必須）
- [x] 初回起動検出ロジック / 中断再開ロジック
- [x] Step 4: 接続テスト UI（3層検証・両路線対応）
- [x] 「セットアップをやり直す」メニューエントリ / 完了後ダッシュボード遷移
- [x] Step 1（AzureSetupPage.razor）/ Step 2a（DriveDiscoveryPage.razor）/ Step 2b（SharePointDiscoveryPage.razor）
- [x] Step 4 SharePoint 路線対応（ConnectionTestPage.razor + ISharePointVerifyService）
- [x] 期限切れ・無効トークン時の Failed 検出 / 失効時 UI 通知 + NotStarted 復帰

### 受け入れ基準（全完了）

- [x] 初回起動時にウィザードが自動表示される
- [x] Dropbox 路線 Step 0→1→2a→3→4 エンド・ツー・エンド動作
- [x] SharePoint 路線 Step 0→1→2a→2b→4 エンド・ツー・エンド動作
- [x] Step 4 が 3層検証（Credential / Discovery / Preflight）を実行する
- [x] wizard-state.json の正常・異常系が動作する
- [x] オンボーディング完了後 Migration Runtime が変換なしに読み込める

---

## ISSUE #110: Azure Entra ID アプリ登録 & API権限設定ガイド ✅

**Issue**: [#110 feat: Azure Entra ID アプリ登録 & API権限設定ガイド（オンボーディング Step 1）](https://github.com/scottlz0310/cloud-migrator/issues/110) — **Closed**  
**Milestone**: v0.4.0  
**依存**: #114（Blazor Hybrid 基盤）

### 実装タスク（全完了）

- [x] ウィザード Step 1 UI コンポーネント（各ステップのガイドパネル）
- [x] Application Permission / Delegated Permission の違いを説明するガイドテキスト
- [x] 最小権限セット（`Files.ReadWrite.All` / `Sites.Read.All` / `User.Read.All`）の説明テキスト
- [x] Step 1-4: 管理者同意 URL 生成・クリップボードコピー機能
- [x] Step 1-5: Secret 有効期限選択 UI + `clientSecretExpiry` の config.json 保存
- [x] ClientID / TenantID / ClientSecret 入力フォーム → Credential Manager 保存
- [x] 入力後の接続テスト（Graph API App-only auth 疎通確認）
- [x] 起動時 Secret 期限チェック + 30 日前警告表示

### 受け入れ基準（全完了）

- [x] Application Permission（App-only）フローで認証できる
- [x] 最小権限セット（3権限）で移行が完結できる
- [x] 管理者同意が必要であることをウィザード内で認識できる
- [x] 一般ユーザーでも同意依頼 URL を生成して管理者に送付できる
- [x] Secret 有効期限が `clientSecretExpiry` キーとして config.json に保存される
- [x] Secret 期限 30 日前にダッシュボード警告が表示される

---

## ISSUE #111: Graph API リソース発見（OneDrive + SharePoint） ✅

**Issue**: [#111 feat: Graph Explorer フレーム統合（Site ID / Drive ID 発見支援）](https://github.com/scottlz0310/cloud-migrator/issues/111) — **Closed**  
**Milestone**: v0.4.0  
**依存**: #110（Client Credentials フロー確定後に着手）

### 実装タスク（全完了）

- [x] Personal OneDrive: userId / UPN 入力フォーム → `GET /users/{userId}/drive` → Drive ID 取得
- [x] Personal OneDrive: App-only 認証の制約（`GET /me/drive` 不可）を UI 上で明示
- [x] SharePoint: Site 名キーワード入力 UI → `GET /sites?search={keyword}` 呼び出し
- [x] SharePoint: Site 一覧表示（表示名付き）+ 選択
- [x] SharePoint: Drive（Document Library）一覧取得 → 表示名付きリストで表示 + 選択
- [x] SharePoint: 検索 0 件時のフォールバック（Site URL 直接入力フォーム）
- [x] Discovery 結果を `migrator.*` 設定スキーマで config.json に保存
- [x] Discovery Verify（`GET /drives/{driveId}` 疎通確認）
- [x] Migration Preflight（読み取り権限 + 書き込み権限の確認）
- [x] OneDrive→Dropbox 路線時は SharePoint 取得 UI をスキップし Dropbox 用スキーマで保存
- [x] Graph Explorer サインインの文脈説明テキスト表示（Graph Explorer リンク形式）
- [x] Admin Consent 未付与エラー時のガイドメッセージ

### 受け入れ基準（全完了）

- [x] Personal OneDrive Drive ID（移行元）がアプリ内で取得・保存できる
- [x] App-only 認証の制約（UPN 入力必須）が UI 上で明示されている
- [x] SharePoint Site をキーワード検索で絞り込める
- [x] キーワード検索で 0 件の場合に Site URL 直接入力フォールバックが表示される
- [x] Document Library の一覧に表示名（Display Name）が表示される
- [x] Discovery 結果が config.json スキーマに従って保存される
- [x] Discovery Verify と Migration Preflight の両方が成功した場合のみ Verified になる
- [x] OneDrive→Dropbox 路線選択時に SharePoint 取得 UI がスキップされる
- [x] Admin Consent 未付与エラー時に適切なガイドが表示される

---

## v0.4.0 全体の受け入れ基準 ✅

- [x] 環境変数なしで初回セットアップが完結できる
- [x] アプリ起動時にネイティブウィンドウが自動的に開く（ブラウザの手動起動不要）
- [x] Personal OneDrive → Dropbox 路線のセットアップがウィザードで完結できる
- [x] Personal OneDrive → SharePoint 路線のセットアップがウィザードで完結できる
- [x] Dropbox 連携が App Secret なしの OAuth PKCE フローで完結できる
- [x] Azure 認証が Application Permission（Client Credentials）フローで完結できる
- [x] 認証情報が Windows Credential Manager に安全に保存される
- [x] オンボーディング完了後、Migration Runtime が追加操作なしに転送を開始できる
- [x] 非 Windows 環境で起動した際に明示的なエラーメッセージが表示される

---

## Verify 責務の分離（全 ISSUE 共通定義）

| 層 | 目的 | 実施タイミング | 担当 ISSUE |
|----|------|--------------|-----------:|
| **Credential Verify** | シークレット / トークンの存在と有効性確認 | 各ステップの入力完了直後 | #109 / #112 |
| **Discovery Verify** | 対象リソース（Drive ID 等）が実際に到達できるか確認 | Discovery 完了後 | #111 / #112 |
| **Migration Preflight** | 実際の読み書き権限を小ファイルで確認 | Step 4（接続テスト） | #113 |
