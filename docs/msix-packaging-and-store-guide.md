# MSIX パッケージング・WACK 検査・Microsoft Store 提出ガイド

本ドキュメントは、CloudMigrator の MSIX パッケージング、Windows App Certification Kit (WACK) による評価、および Microsoft Partner Center 経由での Microsoft Store 公開に関する包括的運用マニュアルです。（[Epic #101](https://github.com/scottlz0310/cloud-migrator/issues/101) 対応）

実稼働プロジェクト（`PhotoGeoExplorer`）の Store 公開実績に基づく手順・ノウハウを取り入れています。

---

## 1. パッケージング構造とビルド手順（#264 / #101-A）

### 1.1 Store 提出用パッケージビルド

Partner Center に提出する `msixupload` パッケージ、および WACK 検証用の `msix` ファイルを生成するには、以下の PowerShell スクリプトを実行します。

```powershell
.\installer\msix\Build-MsixPackage.ps1
```

#### 生成物 (`installer/msix/AppPackages/` 配下):
- **`CloudMigrator_<version>_x64_bundle.msixupload`**: Partner Center 提出用ファイル
- **`CloudMigrator_<version>_x64.msix`**: ローカル WACK テストおよびサイドローディング確認用ファイル

---

### 1.2 バージョン整合性規則（1:1 対応）

リリース時および Partner Center 提出時には、以下の 4 箇所でバージョン表記が **100% 完全一致** している必要があります。
※ 開発中（各機能 PR）の段階では `main` の `Directory.Build.props`（現 `0.7.1`）を維持し、正式な `0.8.0` へのバンプは Epic #101 の全サブ ISSUE が揃った**「リリース直前の専用 PR」**で一括して実施します。

| 対象ファイル / 箇所 | 設定項目 | 開発中表記例 | リリース直前バンプ例 |
|----------------------|----------|--------------|----------------------|
| **Git タグ** | Release Tag | - | `v0.8.0` |
| **`Package.appxmanifest`** | `<Identity Version="..." />` | `0.7.1.0` | `0.8.0.0` |
| **`.csproj` / `Directory.Build.props`** | `<Version>` | `0.7.1` | `0.8.0` |
| **`CHANGELOG.md`** | バージョン見出し | `[Unreleased]` | `## [0.8.0]` |

---

### 1.3 `Package.appxmanifest` 設定仕様

```xml
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">

  <Identity
    Name="scottlz0310.CloudMigrator"
    Publisher="CN=..." 
    Version="0.7.1.0" />

  <Properties>
    <DisplayName>CloudMigrator</DisplayName>
    <PublisherDisplayName>scottlz0310</PublisherDisplayName>
    <Logo>Assets\StoreLogo.jpg</Logo>
  </Properties>

  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.22621.0" />
  </Dependencies>

  <Capabilities>
    <Capability Name="internetClient" />
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>

  <Applications>
    <Application Id="App" Executable="CloudMigrator.Dashboard.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="CloudMigrator"
        Description="SharePoint Online、Dropbox、Local 間の大容量クラウドデータ移行ツール"
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.jpg"
        Square44x44Logo="Assets\Square44x44Logo.jpg">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.jpg" />
      </uap:VisualElements>
    </Application>
  </Applications>
</Package>
```

---

## 2. WACK (Windows App Certification Kit) 検査・検証手順（#265 / #101-B）

WACK は、Microsoft Store 提出に必要な互換性・セキュリティ・パフォーマンス基準を満たしているかを検証するツールです。

### 2.1 ローカルでの WACK 検査手順

1. 節 1.1 のコマンドで `CloudMigrator_<version>_x64.msixbundle` を生成。
2. Windows 検索で **「Windows アプリ認定キット (WACK)」** (`appcert.exe`) を起動。
3. 「ストア アプリ」プロファイルを選択し、`.msixbundle` を指定してテストを実行。
4. **合格判定基準**:
   - `Required failure == 0` であること。
   - Optional 失敗（例: [88] ブロック済みの実行可能ファイル等）は Partner Center 提出に影響しません。

### 2.2 審査ノート (Notes for Certification) の記載
CloudMigrator はファイルシステム操作およびネットワーク通信のために `runFullTrust` 権限を使用します。Partner Center 提出時には、審査ノートに以下の理由を記載します。

> **審査ノート記載例**:
> "CloudMigrator requires the runFullTrust capability to access local file systems and execute data migration between SharePoint Online, Dropbox Business, and local storage on behalf of the user."

---

## 3. Microsoft Partner Center Store Listing Data（#266 / #101-C）

### 3.1 アセット画像・スクリーンショット仕様

リポジトリ内の確定アセット一覧です。 Partner Center 登録時に使用します。

| アセット種別 | リポジトリ内格納パス | 仕様 | 用途 |
|--------------|----------------------|------|------|
| **アプリロゴ** | `docs/assets/store/app_logo.jpg` | 700x700 px (1:1) | メインアイコン・StoreLogo |
| **プロモバナー** | `docs/assets/store/store_promo_header.jpg` | 16:9 | Store ヘッダー掲載バナー |
| **スクリーンショット 1** | `docs/assets/store/screenshot_dashboard.png` | 1920x1080 (16:9) | 実際のダッシュボード画面 |
| **スクリーンショット 2** | `docs/assets/store/screenshot_settings.png` | 1920x1080 (16:9) | 実際の設定・ルート選択画面 |
| **スクリーンショット 3** | `docs/assets/store/screenshot_logs.png` | 1920x1080 (16:9) | 実際の移行監査ログ画面 |

---

### 3.2 `listingData.csv` 管理規則（一括登録時）

Partner Center で「フォルダー単位のインポート」を行う場合の厳格なフォーマット規則です。

- **ファイル形式**: **UTF-8 (BOMあり) + CRLF**
  - ※ LF 改行や BOM なし UTF-8 の場合、 Partner Center インポート時に行連結・文字化けエラーが発生します。
  - ※ Excel で開いてそのまま保存しないでください（フォーマットが崩れる原因になります）。
- **配置構造**:
  ```text
  propose/
  ├── listingData-ja-JP.csv
  ├── app_logo.jpg
  ├── screenshot_dashboard.png
  ├── screenshot_settings.png
  └── screenshot_logs.png
  ```

---

## 4. プライバシーポリシーの準備（GitHub Pages 運用）

Microsoft Store 提出時にはプライバシーポリシー URL の提示が必須です。

### 4.1 GitHub Pages での公開手順
1. 本リポジトリの `docs/privacy-policy.html`（または GitHub Pages 設定）をメインブランチに配置。
2. GitHub リポジトリ設定 (Settings > Pages) で `docs/` フォルダを公開設定。
3. 公開 URL 例:
   `https://<owner>.github.io/cloud-migrator/privacy-policy.html`
4. Partner Center の「プライバシーポリシー URL」欄に上記 URL を登録。

---

## 5. Partner Center 初回提出フロー（手動安全運用）（#267 / #101-D）

初回公開時は誤設定を防ぐため、**Partner Center Web 画面で目視確認しながら手動提出** します。

### 5.1 手順
1. **Partner Center ログイン**: アプリ `CloudMigrator` の「パッケージ」画面へ移動。
2. **パッケージアップロード**: `CloudMigrator_<version>_x64_bundle.msixupload` を手動ドラッグ＆ドロップ。
3. **ストアリスティング入力**: 節 3 の説明文、キーワード、アセット画像（または `listingData.csv`）を登録。
4. **プライバシーポリシー URL**: 節 4 の GitHub Pages URL を入力。
5. **審査ノート記載**: 節 2.2 の `runFullTrust` 用途文言を「Notes for Certification」に入力。
6. **手動審査提出**: 全設定を確認後、「認定用に提出」ボタンをクリック。

---

### 5.2 リリース安全運用と審査差戻し（Revision）対応手順

初回公開および今後のアップデート時には、Store 審査での指摘・差戻し（Rejection）リスクに対応するため、以下の運用ルールを徹底します。

1. **リリース順序の制御（Store 審査通過後の正式公開）**:
   - Store 提出用パッケージ（リリース時例: `0.8.0.0`）を Partner Center にアップロード後、Store 側の審査が通過して「公開済み (Published)」になるまで、GitHub 側の Release は **Draft (下書き)** に留めます。
   - 審査通過を確認した段階で、GitHub Release を正式公開（Tag 確定）とします。

2. **差戻し時の Revision (4桁目) 繰り上げ規則**:
   - Store 審査にて修正指摘・差戻しが発生した場合は、主バージョン（例: `0.8.0`）は変更せず、Manifest の Revision 桁（例: `0.8.0.1`, `0.8.0.2` ...）をバンプして再ビルド・再提出します。
   - これにより、GitHub 上のリリースタグ（例: `v0.8.0`）との整合性を崩さずに Store 側の更新要求を吸収できます。

---

## 6. CI/CD パイプライン自動化方針（#268 / #101-E）

継続アップデートフェーズ（2回目以降）では、GitHub Actions (`.github/workflows/release.yml`) にて MSIX パッケージ生成および Release アタッチを自動化します。

```yaml
- name: Build MSIX Store Package
  run: |
    dotnet publish src/CloudMigrator.Dashboard/CloudMigrator.Dashboard.csproj -c Release -p:Platform=x64 -p:WindowsPackageType=MSIX -p:UapAppxPackageBuildMode=StoreUpload -p:AppxBundle=Always -p:AppxPackageSigningEnabled=false
```
