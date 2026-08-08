# WACK 検証チェックリスト

このチェックリストは、`CloudMigrator` の MSIX/AppX パッケージに対して Windows App Certification Kit (WACK) を実行し、結果を再現可能な形で判定するためのものです。

WACK の公式 CLI は、`reset` の後に `test -appxpackagepath ... -reportoutputpath ...` を実行します。WACK は管理者権限とアクティブなユーザーセッションを必要とします。実行スクリプトは `scripts/wack_work/` に TEMP、ユーザープロファイル、TAEF ログを隔離し、AppCertKit が別の場所へ生成する HTML/HTM レポートを `scripts/wack_reports/` に回収します。

- [Windows アプリ認定キット（Microsoft Learn）](https://learn.microsoft.com/ja-jp/windows/uwp/debug-test-perf/windows-app-certification-kit)

## 1. 実行前の記録

- [x] 実行日時（ローカル時刻と UTC）を記録した
- [x] 対象コミット SHA を記録した
- [x] 対象パッケージの絶対パスを記録した
- [x] パッケージの SHA-256 を記録した
- [x] パッケージの `Identity Name` / `Publisher` / `Version` を記録した（`scottlz0310.CloudMigrator` / `scottlz0310` / `0.7.1.0`）
- [x] Windows のバージョンと OS ビルドを記録した
- [x] WACK の `appcert.exe` のパスとバージョンを記録した
- [x] PowerShell 7 (`pwsh`) のアクティブなユーザーセッションで実行している
- [x] UAC の昇格確認を許可した
- [x] 昇格した WACK プロセスが同じ対象パッケージとレポート出力先を引き継いでいる

記録コマンドの例:

```powershell
    $package = Resolve-Path .\installer\msix\AppPackages\CloudMigrator_0.7.1.0_x64.msix
    Get-FileHash $package -Algorithm SHA256
    Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, OsBuildNumber
    & 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe' /?
```

## 2. パッケージの事前確認

- [x] `Package.appxmanifest` がパッケージ内に存在する
- [x] `Identity Version` がリリース対象のバージョンと一致する
- [x] `Application Executable` が実際の `CloudMigrator.Dashboard.exe` と一致する
- [x] `Assets/StoreLogo.jpg`、`Square150x150Logo.jpg`、`Square44x44Logo.jpg`、`Wide310x150Logo.jpg` が存在する
- [x] 対象アーキテクチャが `x64` である
- [ ] `runFullTrust` と `internetClient` の許可が、アプリの実際の動作に必要なものとして説明できる
- [ ] 未署名パッケージの場合、その状態を記録した（WACK 結果と Store 提出可否を混同しない）

パッケージ生成:

```powershell
.\installer\msix\Build-MsixPackage.ps1
```

## 3. WACK 実行

再生成直後の確認では、最新のパッケージを自動選択して実行できる。

```powershell
.\scripts\run-wack-test.ps1
```

リリース記録に残す検証では、対象パッケージを明示して実行する。

```powershell
.\scripts\run-wack-test.ps1 -PackagePath .\installer\msix\AppPackages\CloudMigrator_0.8.0.0_x64.msix
```

非管理者で実行した場合は UAC ダイアログが表示される。UAC をキャンセルした場合は WACK を実行せず、終了コード 1 になる。

スクリプトが出力する XML 解析だけを再実行する場合:

```powershell
.\scripts\AnalyzeWackReport.ps1 -ReportPath .\scripts\wack_reports\<report>.xml
```

受け入れる成果物:

- [x] `scripts/wack_reports/` に XML レポートが生成された
- [x] `scripts/wack_reports/` に HTML/HTM レポートが生成された
- [x] `scripts/wack_work/` に WACK の作業ログが残っている（調査時に参照できる）
- [x] XML と HTML/HTM が同一の実行に対応している
- [x] レポートのパスを実行記録に保存した

## 4. 判定基準

- [x] XML レポートの `Required failure` が 0 件である
- [x] `Optional failure` がある場合、Store 提出への影響と対応方針を記録した
- [x] `AnalyzeWackReport.ps1` の要約結果と XML/HTML の failure 件数が一致する
- [x] WACK の終了コード 0 だけで合格と判定していない
- [x] レポートが存在しない、空、または別実行のものではない
- [x] HTML レポートで XML の failure 件数と対象テストを確認した

## 5. Issue #265 で重点確認する検出項目

WACK のテスト名や分類は SDK / WACK のバージョンで変わるため、レポートに表示された実際のテスト名を記録する。

### 5.1 サスペンド / レジューム・ライフサイクル

- [ ] サスペンド後にアプリが再開できる
- [ ] 再開後に認証状態、設定、進捗表示が壊れない
- [ ] 終了・再起動後に状態 DB やログを破損させない
- [ ] 失敗した場合、WACK のテスト名・再現操作・アプリログを記録した

### 5.2 非推奨 API / 互換性

- [ ] 非推奨 API の指摘について、呼び出し元と代替 API を特定した
- [ ] SDK / ランタイムの対象バージョンと矛盾する修正をしていない
- [ ] WACK の検出を握りつぶすために対象バージョンや警告を不適切に下げていない
- [ ] 修正後に同じパッケージを再生成し、再度 WACK を実行した

### 5.3 ファイル権限・インストール先

- [ ] パッケージのインストール、起動、アンインストールが完了する
- [ ] アプリがパッケージ領域を変更しようとしていない
- [ ] ユーザー書き込みが必要なファイルを `%LOCALAPPDATA%` など適切な場所に保存している
- [ ] `runFullTrust` が必要なファイル操作について審査ノートの説明を用意した

### 5.4 manifest / リソース / UTF-8

- [ ] manifest の XML が UTF-8 として正しく読み込める
- [ ] DisplayName、Description、Publisher、Logo の参照先が有効である
- [ ] 日本語を含む表示文字列が文字化けしていない
- [x] ロゴの形式、寸法、透過・背景要件を WACK レポートで確認した
- [ ] manifest の `MinVersion` / `MaxVersionTested` が検証環境とリリース方針に合っている

## 6. 指摘の調査・修正記録

WACK の failure ごとに、レポートのテスト名と再現条件を残す。

| 項目 | 記録 |
|---|---|
| レポート XML / HTML | [高解像度ロゴ再生成後 XML](../scripts/wack_reports/CloudMigrator_0.7.1.0_x64-20260808-085423406.xml) / [高解像度ロゴ再生成後 HTML](../scripts/wack_reports/CloudMigrator_0.7.1.0_x64-20260808-085423406.htm) |
| テスト名 / ID | `[45]` アプリ リソース、`[63]` プラットフォームに適したファイル、`[88]` ブロック済みの実行可能ファイル |
| Required / Optional | `[45]` / `[63]` は Optional 合格、`[88]` は Optional failure |
| エラーメッセージ | 修正前 `[45]` のロゴ寸法・容量、修正前 `[63]` の manifest `neutral`、修正後は `[88]` のみ |
| 再現条件 | 修正後の `CloudMigrator_0.7.1.0_x64.msix` を PowerShell 7 から UAC 昇格して実行 |
| 影響範囲 | `[45]` / `[63]` は解消。`[88]` は依存ライブラリ・実行基盤を含む Optional 検出 |
| 修正内容 | ロゴを WACK 要件の寸法・容量へ再生成し、manifest に `ProcessorArchitecture="x64"` を追加 |
| 再検証結果 | 高解像度ロゴ再生成後の現行パッケージで WACK `PASS`、Required failure 0 件、Optional failure 1 件（`[88]`） |

## 7. 完了条件

- [x] スクリプト 1 行で対象パッケージの WACK を起動できる
- [x] 非管理者からの起動時に UAC 昇格後も同じ引数で WACK が継続する
- [ ] UAC をキャンセルした場合に成功扱いにならない
- [x] XML と HTML/HTM のレポートを保存できる
- [x] Required failure が 0 件である、または未解決の理由と後続 Issue が明記されている
- [x] Optional failure の扱いを記録した
- [x] 本チェックリスト、対象 SHA、パッケージ SHA-256、レポートを第三者が追跡できる

## 8. 実行結果の記録

実機 WACK を実行したら、[WACK 検証結果](wack-validation-results.md) にバージョン、対象コミット、パッケージ SHA-256、Required/Optional failure、XML/HTML レポートのパスを追記する。
