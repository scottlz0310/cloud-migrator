# MSIX パッケージング・WACK 検査・Microsoft Store 提出ガイド

本書は、CloudMigrator の MSIX パッケージを生成し、Windows App Certification Kit (WACK) で検証し、Microsoft Partner Center へ提出・公開するための準備・手動確認・自動公開運用ガイドです。Epic [#101](https://github.com/scottlz0310/cloud-migrator/issues/101) の #264〜#268 と #276 を横断する再開用の正本として扱います。

初回 listing の登録、手動確認、または自動公開が利用できない場合は本書の手動手順を使用します。通常の更新では `v*` tag push 後に、[8.4 Store 自動公開と初期設定](#84-store-自動公開と初期設定-276) の GitHub Actions が同一 artifact を WACK から Store まで搬送します。手動の「認定用に提出」および「Publish now」は、自動経路を使わない場合の明示的な回復手順として扱います。

すべての相対パスはリポジトリルート（CloudMigrator）からのパスです。

---

## 0. 前提条件と現在のパッケージ仕様

### 0.1 作業者・実行環境

次を満たす Windows 10/11 の実機で実行します。

| 項目 | 必須条件 |
| --- | --- |
| Windows セッション | WACK の画面・UAC を表示できるアクティブな対話ユーザーセッション |
| PowerShell | PowerShell 7（`pwsh`）。Windows PowerShell 5.1 では実行しない |
| .NET SDK | リポジトリの `global.json` に従う .NET SDK 10.0.302（`latestPatch`） |
| Windows SDK | Windows App Certification Kit と `makeappx.exe` を含む Windows SDK |
| 権限 | WACK 実行時に UAC を許可できる管理者資格情報 |
| Partner Center | Microsoft Store 開発者アカウント、対象アプリを作成・編集できる権限 |
| Git | 対象コミットと生成物の SHA-256 を記録できること |

WACK は切断された RDP セッションや、UAC を表示できない非対話セッションで実行しません。Partner Center の資格情報、OAuth トークン、テストアカウントのパスワードはリポジトリ・ログ・審査ノートの公開部分へ記録しません。

### 0.2 現在の manifest とビルド仕様

リリース作業を始める前に、実際のファイルを読み直してください。現在の代表値は次のとおりです。

| 項目 | 現在値 |
| --- | --- |
| Identity Name | `scottlz0310.CloudMigrator` |
| Publisher | `CN=39FB3D39-1F1A-4B82-B081-47469FD12CA6` |
| PublisherDisplayName | `scottlz0310` |
| 開発用 Version | `Directory.Build.props` が `0.7.2`、manifest が `0.7.2.0` |
| アーキテクチャ | Windows Desktop / x64 |
| 対象 OS | MinVersion `10.0.19041.0`、MaxVersionTested `10.0.22621.0` |
| 言語 | `ja-JP` / `en-US`、既定言語 `ja-JP` |
| Capability | `internetClient`、制限付き `runFullTrust` |
| 実行方式 | `CloudMigrator.Dashboard.exe`、framework-dependent |
| 署名 | 現在の手動ビルドは `AppxPackageSigningEnabled=false` |

`src/CloudMigrator.Dashboard/Package.appxmanifest` と `installer/msix/Package.appxmanifest` は、手動ビルドで使用するファイルを含めて同じ Identity・Version を維持します。Store のアプリ予約後は Identity Name と Publisher を変更しません。

---

## 1. MSIX パッケージング

### 1.1 ビルド前の確認

リリース候補として記録する場合は、対象コミットと作業ツリーを先に確認します。

~~~powershell
git status --short
git rev-parse HEAD
dotnet --version
Test-Path .\installer\msix\Build-MsixPackage.ps1
Test-Path .\installer\msix\Package.appxmanifest
~~~

差分のある作業ツリーで作成したパッケージを正式なリリース証跡にしないでください。検証目的のローカル実行で差分が必要な場合は、記録にその理由を残します。

### 1.2 パッケージを生成する

リポジトリルートから次を実行します。

~~~powershell
.\installer\msix\Build-MsixPackage.ps1 -Configuration Release -Version 0.7.2.0
~~~

生成先は `installer/msix/AppPackages/` です。

- `CloudMigrator_0.7.2.0_x64.msix`: WACK・ローカル検査用の MSIX
- `CloudMigrator_0.7.2.0_x64_bundle.msixupload`: Partner Center のアップロード用ファイル

ビルドスクリプトは、`Package.appxmanifest` の `PublisherDisplayName`、`Resources/Resource@Language`、manifest が参照する 4 つの画像を検証した後、Windows SDK の `makepri.exe` で `resources.pri` を生成して MSIX に含めます。`Resources` の先頭にある `ja-JP` が既定言語です。`csproj` の `DefaultLanguage` / `Languages` だけでは、手動 `makeappx` 経路の manifest へ自動反映されません。`makepri.exe` の生成方式は Microsoft の [MakePRI による PRI ファイル生成手順](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion#generate-a-package-resource-index-pri-file-using-makepri) に準拠します。

現行スクリプトは `-Version` をファイル名に使用しますが、manifest の Identity Version は書き換えません。リリースバージョンに変更する場合は、専用のリリース準備 PR で `Directory.Build.props` と 2 つの manifest の値を先に更新し、その 4 桁の値を `-Version` に渡します。たとえば Store の `0.8.0` リリースでは manifest を `0.8.0.0` にし、ファイル名にも `0.8.0.0` を使用します。`-OutputDirectory` パラメーターは現行実装では生成先に反映されないため、本書では使用しません。

Store への提出には `.msix` ではなく `.msixupload` を使用します。`.msix` は WACK とローカル検査の入力です。現在のスクリプトはローカル署名を行わないため、`.msixupload` が Store で受け入れられるかは Partner Center の package validation で確認し、ローカル WACK の成功だけから「提出可能」と断定しません。Microsoft の現行ガイダンスは [MSIX パッケージのアップロード](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages) を参照してください。

Partner Center で「画像が見つからない」「PublisherDisplayName が空」「言語が未宣言」などが表示された場合は、古い `.msixupload` を再利用せず、manifest と `resources.pri` を含む新しい生成物を作成してから、Packages 画面で古いパッケージを削除して再アップロードします。アップロード済みファイルを同じ名前のまま差し替えた場合も、Partner Center 側で再検証を開始してください。

### 1.3 生成物の検証と記録

生成直後に、対象ファイルと SHA-256 を固定します。

~~~powershell
$version = "0.7.2.0"
$packageDirectory = ".\installer\msix\AppPackages"
$package = Get-Item (Join-Path $packageDirectory ("CloudMigrator_" + $version + "_x64.msix"))
$upload = Get-Item (Join-Path $packageDirectory ("CloudMigrator_" + $version + "_x64_bundle.msixupload"))

$package | Select-Object FullName, Length, LastWriteTime
$upload | Select-Object FullName, Length, LastWriteTime
Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256
Get-FileHash -LiteralPath $upload.FullName -Algorithm SHA256
~~~

次の内容も確認します。

1. MSIX と MSIXUPLOAD が同じビルドで生成されたこと。
2. ファイル名の Version と package 内 manifest の Identity Version が一致すること。
3. x64 以外のパッケージが混入していないこと。
4. package 内に `resources.pri` と manifest が参照する 4 つの画像が存在すること。
5. 生成物の SHA-256、対象コミット、実行日時、SDK と OS のバージョンを記録したこと。

package 内 manifest を確認する場合は、ビルドログに表示された `makeappx.exe` を使用します。

~~~powershell
$windowsKitsBin = Join-Path ([Environment]::GetEnvironmentVariable("ProgramFiles(x86)")) "Windows Kits\10\bin"
$makeAppx = Get-ChildItem -Path $windowsKitsBin -Recurse -File -Filter makeappx.exe |
    Where-Object { $_.FullName -match "\\x64\\makeappx\.exe$" } |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $makeAppx) {
    throw "Windows SDK の x64 版 makeappx.exe が見つかりません。"
}
$inspectDirectory = Join-Path $env:TEMP ("cloud-migrator-msix-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $inspectDirectory | Out-Null
& $makeAppx unpack /p $package.FullName /d $inspectDirectory /l
Get-Content -Raw (Join-Path $inspectDirectory "AppxManifest.xml")
Remove-Item -LiteralPath $inspectDirectory -Recurse -Force
~~~

`makeappx.exe` の実際のパスは環境により異なります。ビルドスクリプトが出力したパス、または Windows SDK の x64 版を指定してください。展開した manifest で Identity、Version、ProcessorArchitecture、Executable、Capabilities、リソース画像を確認します。

### 1.4 manifest の確認ポイント

提出前に、次の値が実際の機能と一致することを確認します。

- `internetClient`: Microsoft Graph と Dropbox API などのネットワーク通信に使用する。
- `runFullTrust`: Windows デスクトップ実行ファイルとして、ユーザーが選択したローカルファイル、設定、ログを扱うために使用する。これは制限付き Capability なので、審査ノートで用途を説明する。
- `ProcessorArchitecture="x64"`: 現在は Windows Desktop x64 のみを対象とする。
- `Application Executable="CloudMigrator.Dashboard.exe"`: publish された実行ファイルと一致する。
- Store listing の説明は実際の転送経路（OneDrive から SharePoint Online または Dropbox Business へのユーザー指示による転送）と一致させる。manifest の古い説明文を審査ノートや listing にそのまま転記しない。

---

## 2. WACK 検査

### 2.1 実行前の条件

Windows SDK の [Windows App Certification Kit](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit) をインストールし、`appcert.exe` が存在することを確認します。WACK はアクティブなユーザーセッションで実行し、PowerShell 7 から起動してください。

このリポジトリの `scripts/run-wack-test.ps1` は、非管理者から起動された場合に UAC で `pwsh` を昇格し、対象パッケージ、レポート出力先、`appcert.exe` の指定を引き継ぎます。UAC をキャンセルした場合は非ゼロ終了となり、合格扱いにしません。

### 2.2 固定した MSIX に対して実行する

リリース記録では自動選択を使わず、検査対象を明示します。

~~~powershell
$packagePath = ".\installer\msix\AppPackages\CloudMigrator_0.7.2.0_x64.msix"
.\scripts\run-wack-test.ps1 -PackagePath $packagePath
~~~

検査対象を指定しない次の形式は、直近に生成された MSIX を確認する開発時だけに使用します。

~~~powershell
.\scripts\run-wack-test.ps1
~~~

スクリプトは `appcert.exe reset` と WACK test を実行し、次へ保存します。

- `scripts/wack_reports/`: XML と HTML/HTM レポート
- `scripts/wack_work/`: WACK の一時領域、ユーザープロファイル、TAEF ログ

XML と HTML/HTM が同じ実行に対応していることを確認し、レポート名、対象 MSIX の絶対パス、パッケージ SHA-256 を記録します。ログに秘密情報が含まれていないことを確認してから共有します。

### 2.3 レポートを解析して判定する

`run-wack-test.ps1` は XML を自動解析します。個別に再確認する場合の引数は `-ReportPath` です。

~~~powershell
$xmlReport = Get-ChildItem .\scripts\wack_reports -File -Filter *.xml |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

.\scripts\AnalyzeWackReport.ps1 -ReportPath $xmlReport.FullName -FailOnRequiredFailure
~~~

判定条件は次のとおりです。

1. Required failure が 0 件であること。
2. Optional failure は件数、検出名、Store 提出への影響、採用した対応方針を記録すること。
3. WACK の終了コード 0 だけで合格と判定しないこと。レポートがない、別の MSIX を検査した、XML に TEST がない場合も未完了とすること。
4. HTML/HTM で対象テストと failure の詳細を確認し、XML の集計と突き合わせること。

現在の検証結果には Optional failure `[88]` が 1 件残る方針があります。これは現行結果の記録に基づく既知の Optional であり、将来の WACK 実行でも自動的に許容せず、毎回レポートと対応方針を記録します。`[45]` と `[63]` は修正済みのため、再発した場合は回帰として扱います。実行記録の形式は [WACK 検証チェックリスト](wack-validation-checklist.md) と [WACK 検証結果](wack-validation-results.md) を使用します。

### 2.4 トラブルシューティング

| 症状 | 確認・対処 |
| --- | --- |
| `appcert.exe` が見つからない | Windows SDK の Windows App Certification Kit を追加し、`-AppCertPath` または `WACK_APPCERT_PATH` で x64 環境の実体を指定する |
| UAC をキャンセルした | 成功扱いにせず、管理者資格情報で再実行する。レポートが残っていてもキャンセル前の別実行と混同しない |
| 対象 MSIX が違う | `-PackagePath` を絶対パスで指定し、ファイル名、更新日時、SHA-256 を記録する |
| WACK が途中終了しレポートがない | `scripts/wack_work/` と AppCertKit のログを確認し、パッケージ生成・インストール・環境条件を直して最初から再実行する |
| Required failure がある | HTML のテスト名と対象バイナリを特定し、コードまたは manifest を修正して新しいパッケージで再 WACK する |
| Optional failure がある | `[88]` などの検出名、影響、審査ノートへの反映、未修正理由を記録する。Optional でも隠蔽しない |
| `Add-AppxPackage` でインストールできない | 現在の手動生成物は未署名であるため、WACK の結果とローカルサイドロード可否を分離して扱う。署名済みテストパッケージまたは Store の検証結果が必要 |

### 2.5 Notes for Certification

`runFullTrust` は制限付き Capability です。審査ノートには、Capability 名だけでなく実際の利用目的、ユーザー操作との関係、ユーザーが開始したジョブの範囲でだけ処理することを記載します。次は現行機能に合わせた英語のたたき台です。審査用アカウントや操作手順は、実際に検証できる内容へ置き換えてください。

~~~text
CloudMigrator is a Windows desktop application that transfers user-selected files from OneDrive to SharePoint Online or Dropbox Business using the configured cloud services. It uses the runFullTrust capability because the desktop process must access user-selected local files and persist application settings and logs while executing the user-initiated migration workflow. The internetClient capability is used for the configured cloud API connections. The migration workflow is started by the user; any background processing is limited to that user-initiated job, and the application does not start unattended transfers.

Reviewer steps:
1. Launch CloudMigrator.
2. Sign in with the review test account and configure the source and destination described in the secure test instructions.
3. Select a small test set and start the migration.
4. Confirm the result in the destination and review the application log.
Test account and any service-specific setup are provided through the Partner Center certification instructions, not in the public repository.
~~~

審査用テストアカウントが必要なのに用意できない場合は、推測のアカウントや実在しない資格情報を入力せず、提出を停止します。`runFullTrust` の説明とテスト手順は [WACK 検証チェックリスト](wack-validation-checklist.md) の未完了項目も埋めてから提出します。

---

## 3. Store Listing と提出アセット

提出 CSV と画像は [docs/assets/store-source/README.md](assets/store-source/README.md) で管理しています。`docs/assets/store` は Partner Center にそのまま渡せるクリーンなアップロードフォルダーで、CSV と参照中の 4 画像だけを含みます。CSV 内の画像パスはフォルダー名を含む `store/...` 形式です。原本・README・未参照ドラフトは `docs/assets/store-source` に分離しています。

| アセット | パス | 現在の状態 |
| --- | --- | --- |
| Store logo | `docs/assets/store/store_logo_300x300.png` | `docs/assets/store-source/app_logo.jpg` を高解像度原本として縮小した 300x300 |
| ロゴ原本 | `docs/assets/store-source/app_logo.jpg` | 700x700 |
| Dashboard screenshot | `docs/assets/store/screenshot_dashboard.png` | 1920x1080 |
| Settings screenshot | `docs/assets/store/screenshot_settings.png` | 1920x1080 |
| Logs screenshot | `docs/assets/store/screenshot_logs.png` | 1920x1080 |
| Listing CSV | `docs/assets/store/listingData.csv` | `en-us` / `ja-jp`、UTF-8 BOM、CRLF |
| Promo header | `docs/assets/store-source/store_promo_header.jpg` | ドラフト。CSV から未参照 |

### 3.1 CSV の更新手順

1. Partner Center で対象アプリの Store listing をエクスポートし、Partner Center が生成した最新の `Field` / `ID` / `Type (種類)` を正とする。
2. リポジトリの CSV と行・列を比較し、Partner Center 側に追加された項目を削除せずに取り込む。
3. 本文、検索語、機能一覧、スクリーンショットの相対パスだけを更新する。機能一覧は Store 側が箇条書き表示するため、値へ手動の箇条書き記号を追加しない。
4. `docs/assets/store` フォルダーをそのままインポートする。`listingData.csv` と `store/...` 参照画像が解決されたことを確認した後、日本語・英語を画面上で確認する。
5. 画面上の実際の表示文がアプリの機能と一致し、サンプルのアカウント名・パスが実在の利用者情報でないことを確認する。

CSV は Partner Center の最新エクスポートと一致しない可能性があるため、リポジトリ版を無確認で上書きインポートしません。詳細は Microsoft の [Store listing の追加・編集](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info) と [MSIX Store listing のインポート・エクスポート](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/import-and-export-store-listings) を参照してください。

### 3.2 URL と公開前ゲート

- サポート URL は `https://github.com/scottlz0310/cloud-migrator/issues` を使用する。
- プライバシーポリシーの本文は [docs/privacy-policy.html](privacy-policy.html) を使用する。
- GitHub Pages の `https://scottlz0310.github.io/cloud-migrator/privacy-policy.html` は、Pages を有効化して HTTPS で HTTP 200 を確認するまで提出値として確定しない。
- Partner Center がメールアドレスを要求した場合は、発行者が管理する実在の連絡先だけを入力する。架空のメールアドレスを補わない。

ブラウザーで URL を確認したうえで、必要に応じて次のように応答を確認します。

~~~powershell
$privacyUri = "https://scottlz0310.github.io/cloud-migrator/privacy-policy.html"
$response = Invoke-WebRequest -Uri $privacyUri
if ($response.StatusCode -ne 200) {
    throw "プライバシーポリシー URL が HTTP 200 ではありません: $($response.StatusCode)"
}
~~~

---

## 4. プライバシーポリシーの公開

1. `docs/privacy-policy.html` を main ブランチへ反映する。
2. GitHub リポジトリの Settings > Pages で、公開元を main ブランチの `/docs` に設定する。
3. HTTPS の公開 URL をブラウザーと HTTP 200 の両方で確認する。
4. URL の内容が現行の OneDrive、SharePoint Online、Dropbox のデータ転送と、開発者が分析・広告・移行データ収集を行わない方針に一致することを確認する。
5. 確認済み URL だけを Partner Center の Privacy policy URL に入力する。

Pages 未設定の URL、リダイレクト先が不明な URL、ローカルファイルのパスは Partner Center に入力しません。

---

## 5. Partner Center 初回提出フロー

Microsoft Store の一般的な前提は [Get started with Microsoft Store](https://learn.microsoft.com/en-us/windows/apps/publish/get-started) を参照してください。Partner Center の画面名称や項目は変更される可能性があるため、画面上の最新項目を優先します。

### 5.1 提出前の停止条件

次がすべて揃うまで、提出画面で保存・提出を進めません。

- Partner Center 開発者アカウントとアプリ編集権限がある。
- `CloudMigrator` の名前予約または既存アプリがあり、Identity Name と Publisher が package と一致する。
- package の Version、x64、対象 OS、Capability、SHA-256、WACK XML/HTML の記録がある。
- Required failure が 0 件で、Optional failure の方針が記録されている。
- CSV と参照画像の差分を確認済みである。
- GitHub Pages のプライバシーポリシー URL が HTTPS/HTTP 200 である。
- Notes for Certification、審査用テスト手順、必要なテストアカウントを準備済みである。
- 市場、価格、可視性、年齢区分、連絡先、リリースノートを人間が確認済みである。

### 5.2 アプリと package を準備する

1. Partner Center にログインし、対象アプリの App overview を開く。未作成の場合は `CloudMigrator` の名前予約から始める。
2. New submission を作成し、Packages ページを開く。
3. `CloudMigrator_<version>_x64_bundle.msixupload` をアップロードする。アップロード後の検証が終わるまで先へ進まない。
4. Package details で、Identity Name、Publisher、Version、x64、対象 device family、パッケージ言語、Capabilities、警告・エラーを画面上で確認する。
5. 現在の manifest は Windows Desktop x64 用であり、Xbox、HoloLens、ARM64 用のパッケージがある前提で対象を選択しない。
6. エラーや未確認の警告がある場合は提出せず、package を修正・再生成して WACK からやり直す。

Microsoft の [MSIX パッケージのアップロード](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages) では、パッケージの言語、Capability、device family、警告・エラーをアップロード後に確認できます。

### 5.3 Store listing を入力する

1. Store listing で日本語（`ja-jp`）と英語（`en-us`）の各項目を開く。
2. まず Partner Center から最新 CSV をエクスポートし、3.1 の手順でリポジトリの本文と画像を移す。
3. Store logo と 3 枚のスクリーンショットを確認する。4 枚目が必要な場合は、公開前に実際の画面から追加する。
4. Description、機能一覧、検索語、リリースノートが実装と一致することを確認する。
5. 確認済みの Privacy policy URL、サポート URL、実在する連絡先を入力する。
6. Preview で言語を切り替え、画像のぼけ、切り抜き、文字化け、旧ルートの説明がないことを目視確認する。

### 5.4 Submission options と認定ノート

Submission options で、次を確認します。

- `runFullTrust` の用途とユーザー操作との関係を 2.5 のノートに沿って説明する。
- 審査用テストアカウントが必要なら、実際に使用できるアカウントと安全な手順を、Partner Center が提供する審査向け欄へ入力する。公開リポジトリや通常の issue コメントへ資格情報を書かない。
- Restricted capability の理由を要求された場合は、ローカルファイル・設定・ログを扱う Windows デスクトップ実行と、ユーザー指示の移行処理に限定して説明する。
- 手動提出を行う場合だけ、公開保留（`Don't publish this submission until I select Publish now` 相当）の要否を選択する。#276 の自動経路では `store-production` job が submission status を poll し、認定後の公開まで実行する。

### 5.5 最終確認と提出

手動提出を行う場合は、概要ページで package、listing、価格・市場、プライバシーポリシー、審査ノート、公開保留設定を確認します。次の停止点で提出操作を止め、実行者の明示的な承認を得ます。#276 の自動経路では、この画面操作を通常手順にしません。

> **手動経路の停止点:** 本書の手動確認だけでは「Submit for certification」または「Publish now」をクリックしない。画面の入力内容と証跡を共有し、提出・公開を実行する人の承認を得てから操作する。自動経路では tag push と workflow の成功条件を使用する。

承認後に認定へ提出した場合は、次を記録します。

| 記録項目 | 値 |
| --- | --- |
| 作業日時（JST/UTC） |  |
| 操作担当者 |  |
| Partner Center アプリ名 / App ID |  |
| Submission ID |  |
| package Version / アーキテクチャ |  |
| package SHA-256 / `.msixupload` SHA-256 |  |
| 対象コミット SHA |  |
| WACK XML / HTML パス |  |
| Required / Optional failure |  |
| privacy URL の確認日時・HTTP 結果 |  |
| 認定ステータス・差戻し内容 |  |
| 公開経路（自動 workflow / 手動公開保留）と承認者 |  |

手動経路で認定を通過して公開保留中の場合は、公開ボタンを自動的に押しません。自動経路では Store job が submission status を poll して公開完了を判定します。いずれの場合も Store の公開状態と GitHub Release の公開は別の操作として記録します。

---

## 6. 更新、差戻し、公開後確認

### 6.1 通常の更新

1. リリース準備用の専用 PR でコード、listing、`CHANGELOG.md`、2 つの manifest、`Directory.Build.props` の変更を確定する。
2. manifest の Version は既存の Store package より大きくし、4 桁の値を package ファイル名の `-Version` と一致させる。
3. 新しい `.msix` / `.msixupload` を生成し、ファイル名、package 内 manifest、SHA-256 を確認する。
4. 新しい package で WACK を再実行し、Required/Optional の記録を更新する。
5. `v<manifest Version>` の tag を push し、GitHub Actions の release workflow を開始する。
6. workflow が同一 artifact の静的検査、WACK、`.msixupload` の Store submission と公開状態の poll を実行する。
7. 公開後に Store listing、インストール、起動、ログイン、少量の転送、アンインストールを確認し、workflow の実行 URL と Submission ID を記録する。

同じ submission の入力を上書きして証跡を失わないでください。修正して再提出する場合は、新しい package と新しい SHA-256 を記録します。

### 6.2 差戻し・認定失敗

1. Certification report の検出名、対象 package、再現条件を保存する。
2. Required failure、Capability の説明不足、listing の不一致、テスト手順不足を分類する。
3. 必要なコード、manifest、listing、審査ノートを修正する。
4. Version を既存 package より大きくし、新しい package を生成して WACK を再実行する。
5. 修正内容と再 WACK の結果を新しい submission の証跡へ紐付ける。

単なる再ビルドで同じ Version・同じ入力を再提出しません。Revision の 4 桁目だけを増やすか、主バージョンを進めるかは、変更内容と Partner Center の要求に応じてリリース準備時に決定します。4 桁目の増加をすべての差戻しに対する自動規則とはしません。

### 6.3 GitHub Release と Store 自動公開の境界

`.github/workflows/release.yml` は `v*` タグで GitHub Release と CLI/Dashboard、MSI、MSIX (`.msix` / `.msixupload`) のアセットを公開します。続いて同じ MSIX artifact を `wack.yml` の GitHub-hosted Windows WACK job に渡し、WACK 成功後にだけ `store-production` Environment の Store 公開 job を開始します。GitHub Release の公開状態と Store の submission／公開状態は別の証跡として記録します。

---

## 7. インストールスコープ（per-user / per-machine）

MSIX の登録スコープは、既存の MSI のインストール先選択とは別の概念です。Microsoft の [MSIX トラブルシューティングガイド](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide)、[AppX PowerShell モジュール](https://learn.microsoft.com/en-us/powershell/module/appx/?view=windowsserver2025-ps)、[パッケージアプリの事前展開](https://learn.microsoft.com/en-us/windows/msix/desktop/deploy-preinstalled-apps) を参照してください。

| 観点 | per-user（ユーザー登録） | per-machine 相当（プロビジョニング） |
| --- | --- | --- |
| 主な用途 | 開発者のサイドロード、Store からの通常利用 | 組織管理者が新規ユーザー向けに事前展開 |
| 代表的な操作 | `Add-AppxPackage -Path <署名済み msix>` | `Add-AppxProvisionedPackage` / DISM によるオンラインイメージへのプロビジョニング |
| 権限 | 通常は対象ユーザー。署名・ポリシーは別途必要 | 管理者。依存パッケージ・ライセンス・端末ポリシーも確認 |
| 登録状態 | 現在のユーザーに登録される | パッケージをステージングし、各ユーザーのログオン時にユーザー登録される |
| データ | パッケージデータ・設定はユーザー単位 | プロビジョニングしてもアプリデータが全ユーザーで共有されるわけではない |
| CloudMigrator での扱い | 実機の起動・設定・少量転送の確認 | Partner Center の一般利用では選択しない。企業配布が必要な場合だけ IT 手順として別検証 |

現在の手動ビルドは未署名なので、次の per-user 例がそのまま成功するとは限りません。

~~~powershell
Add-AppxPackage -Path .\installer\msix\AppPackages\CloudMigrator_0.7.2.0_x64.msix
~~~

このコマンドが失敗しても、WACK の Required failure 0 と矛盾するとは限りません。信頼された証明書で署名したテスト package、または Store から取得した package でインストール・起動を確認します。プロビジョニングは管理者向けの企業展開であり、Microsoft Store の通常配布に「per-machine」を追加する manifest スイッチではありません。

---

## 8. CI/CD との責務分離（#268）

[#268](https://github.com/scottlz0310/cloud-migrator/issues/268) では、GitHub-hosted runner での静的検査と、WACK 導入済み self-hosted runner でのフル WACK を分離していました。#284 の Hosted Spike（run #31950498930）で `windows-latest` 上のフル WACK が成功したため、現在はフル WACK も GitHub-hosted runner で実行します。GitHub-hosted runner の UAC ダイアログには依存しません。

### 8.1 PR／main の MSIX 静的検査

`.github/workflows/ci.yml` の `MSIX パッケージ検証` job は、PR と `main` への push で `windows-latest` 上に MSIX を生成し、[scripts/ValidateMsixPackage.ps1](../scripts/ValidateMsixPackage.ps1) で次を検査します。

- package 内 manifest の Identity Name、Publisher、Version、ProcessorArchitecture (`x64`)
- DisplayName、PublisherDisplayName、Resources の言語、Capabilities、Executable
- manifest が参照する 4 画像と `resources.pri`
- package の SHA-256

生成した `.msix` と `.msixupload` は `msix-ci-<run_id>` artifact に保存されます。MSIX は Windows SDK の `makeappx.exe` で展開して検査します。WACK はこの job では実行しません。

### 8.2 GitHub-hosted WACK runner の前提

WACK は `windows-latest` の GitHub-hosted runner で実行します。runner の登録、ラベル付け、Windows SDK の手動インストールは不要です。Hosted image の変更で AppCertKit が利用できなくなった場合に暗黙の self-hosted fallback を行わず、能力検査で job を失敗させます。

| 条件 | 要件 |
| --- | --- |
| runner | `windows-latest` |
| Windows | Hosted image の Windows。Spike では `win25-vs2026` / Windows Server 2025 |
| 権限 | workflow の開始時に Administrators 権限であることを検査 |
| セッション | `Environment.UserInteractive` が true であることを検査。非対話 Session 0 では実行しない |
| SDK | Hosted image に Windows App Certification Kit (`appcert.exe`) が存在することを検査 |
| PowerShell | PowerShell 7 (`pwsh`) |

Hosted runner の image、管理者権限、対話セッション、`appcert.exe` の存在は WACK job の開始時に再検査します。runner に資格情報を保存せず、OAuth token も workflow のログへ出力しません。既存 self-hosted runner の停止・登録解除は、移行後のリリース確認後に行う別の環境作業です。

### 8.3 Full WACK の実行

`.github/workflows/wack.yml` は次の入口を持ちます。

- `workflow_dispatch`: Actions 画面から対象 branch と manifest Version を選択して実行する
- `workflow_call`: `release.yml` が生成した MSIX artifact を受け取り、tag push 時の再ビルドを行わずに実行する

手動実行では `windows-latest` で MSIX を生成・静的検査してから artifact に保存します。リリース実行では `release.yml` が生成した artifact（`.msix`、`.msixupload`、SHA-256 証跡 JSON）をそのまま `windows-latest` の WACK job へ渡します。WACK job は artifact 内の 1 個の `.msix` を `-PackagePath` で明示し、次の既存スクリプトを実行します。

~~~powershell
pwsh -NoProfile -File .\scripts\run-wack-test.ps1 `
  -PackagePath .\wack-input\CloudMigrator_<version>_x64.msix `
  -ReportDirectory scripts/wack_reports
~~~

`run-wack-test.ps1` は WACK の XML/HTML を回収し、`AnalyzeWackReport.ps1 -FailOnRequiredFailure` で判定します。Required failure またはレポート異常は job failure、Optional failure のみの場合は job を成功とし、XML/HTML と WACK ログを `wack-reports-<run_id>` artifact に保存します。Optional failure の審査影響は artifact と [WACK 検証結果](wack-validation-results.md) に記録してください。

WACK の UAC は通常の開発者実機でのみ使用します。CI の GitHub-hosted runner は開始時点で管理者権限を持つことを検査するため、UAC ダイアログの表示・自動操作には依存しません。runner の管理者権限、対話セッション、AppCertKit が不足する場合は、WACK を開始せず失敗させます。

### 8.4 Store 自動公開と初期設定（#276）

タグ push 後の Store 公開は、次の順序で実行します。

1. `release.yml` が tag と manifest Version を照合し、MSIX／MSIXUPLOAD と SHA-256 証跡を作成する。
2. GitHub Release に添付したものと同じ artifact を `windows-latest` の WACK job へ渡す。
3. WACK が成功した場合だけ `store-production` Environment の job を開始する。
4. Microsoft Store Developer CLI で Entra ID の client credentials を設定し、`.msixupload` を Product ID `9NG134LB022L` へ送信する。
5. 既存 submission の status と package 名を確認し、同じ package の処理中 submission は poll して再利用する。別 Version の submission が処理中なら上書きせず停止する。
6. submission status が `PUBLISHED` になるまで poll し、失敗時は workflow を失敗させる。

初回設定はリポジトリ root で行います。`.env` はローカルだけに置き、実値を Git、Issue、ログへ出力しません。

~~~powershell
Copy-Item .env.example .env
# .env に Entra ID / Partner Center の値を記入
pwsh -NoProfile -File .\scripts\Configure-StorePublishing.ps1 -WhatIf
pwsh -NoProfile -File .\scripts\Configure-StorePublishing.ps1
~~~

設定スクリプトは `store-production` Environment を作成または更新し、`v*` tag の deployment policy、`STORE_PRODUCT_ID` variable、次の Environment secrets を設定します。

- `AZURE_AD_APPLICATION_CLIENT_ID`
- `AZURE_AD_APPLICATION_SECRET`
- `AZURE_AD_TENANT_ID`
- `SELLER_ID`

Entra ID アプリには Microsoft Store Submission API を利用できる client credentials と、対象アプリを更新できる Partner Center 権限を付与します。Microsoft Store Developer CLI の GitHub Actions 更新は現行ドキュメント上、free product が前提です。Product ID は `.env` の `STORE_PRODUCT_ID` で確認しますが、secret にはしません。現在はユーザー判断により required reviewer を設定せず、`v*` tag 制限だけを Environment 保護ルールとして使います。承認を再導入する場合は GitHub Environment 側で追加設定します。

Store 公開 job は Product 単位の concurrency で直列化します。GitHub Actions の timeout 後に再実行する場合は、新しい tag や手動再ビルドを作らず、同じ run の再実行で既存 submission の status を確認します。submission が別 Version の処理中または失敗状態で残っている場合も自動で上書きせず、Partner Center で状態を確認してから対応します。

### 8.5 Listing Data との責務分離

`docs/assets/store/listingData.csv` は listing の登録・更新用の入力であり、Store 公開 workflow の package artifact には含めません。Partner Center から export した CSV に含まれる一時的な絶対 asset URL や submission 識別子もリポジトリへコピーしません。listing の変更は別途確認してから package の新 Version と一緒に提出します。

---

## 9. 中断・再開チェックリスト

次のチェックが揃えば、別の作業者が同じ入力から作業を再開できます。

- [ ] 対象コミット SHA、OS、SDK、PowerShell、実行日時を記録した。
- [ ] package の生成コマンド、MSIX/MSIXUPLOAD の絶対パス、SHA-256 を記録した。
- [ ] package 内 manifest の Identity、Publisher、Version、x64、Capabilities、Executable を確認した。
- [ ] 明示した同一 MSIX で WACK を実行し、UAC 昇格結果を記録した。
- [ ] XML と HTML/HTM が揃い、Required failure が 0 件である。
- [ ] Optional failure の検出名、影響、対応方針を記録した。
- [ ] `runFullTrust` の説明と審査用テスト手順を用意した。
- [ ] per-user の実機確認、未署名 package の制約、per-machine 相当の企業展開との差を記録した。
- [ ] CSV、画像、言語、説明文、support URL、privacy URL を確認した。
- [ ] privacy URL は Pages 有効化後に HTTPS/HTTP 200 を確認した。
- [ ] Partner Center の submission 入力を確認し、提出・公開の承認待ち状態を明記した。
- [ ] 更新・差戻し時の新 Version、新 SHA-256、再 WACK、再提出の手順を追跡できる。

---

## 参考資料

- [Windows App Certification Kit](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit)
- [Packaging MSIX apps](https://learn.microsoft.com/en-us/windows/msix/package/packaging-uwp-apps)
- [Upload MSIX app packages](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages)
- [Manage submission options for MSIX app](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/manage-submission-options)
- [Add and edit Store listing info for MSIX app](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/add-and-edit-store-listing-info)
- [Import and export Store listings for MSIX app](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/import-and-export-store-listings)
- [MSIX troubleshooting guide](https://learn.microsoft.com/en-us/windows/msix/msix-troubleshooting-guide)
- [Preinstalling packaged apps](https://learn.microsoft.com/en-us/windows/msix/desktop/deploy-preinstalled-apps)
