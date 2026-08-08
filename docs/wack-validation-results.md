# WACK 検証結果

この文書は、実機で取得した WACK XML/HTML レポートの履歴を記録するためのものです。WACK の実行結果を未取得の状態で合格扱いにしません。

## 現在の状態

- 対象パッケージ: `CloudMigrator_0.7.1.0_x64.msix`
- 対象コミット: `e1fb911e8a1d03d643988e0d2f6b22385b8b96a5` + 作業ツリー変更
- パッケージ SHA-256: `FF83D5270B638C6BB12D78E11435A22D708258852D80D935FB18B70C87EFFC82`
- 状態: 高解像度ソースからロゴを再生成した現行パッケージの UAC 付き WACK 再検証済み（PASS、Required failure 0 件、Optional failure 1 件 `[88]`）
- 実行コマンド: `.\scripts\run-wack-test.ps1`
- UAC: 非管理者セッションから昇格を許可し、昇格後プロセスは `exit=0`
- 実行日時: 2026-08-08 17:55 JST（2026-08-08 08:54 UTC）
- WACK: `10.0.26100.8249`
- OS: Windows 11 Pro `10.0.26200.0`
- PowerShell: `7.6.4`
- `appcert.exe`: `C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe`

## 実行結果履歴

| バージョン | 実行日時 | 結果 | Required failure | Optional failure | 対象 SHA | パッケージ SHA-256 | XML / HTML |
|---|---|---|---:|---:|---|---|---|
| 0.7.1.0 | 2026-08-08 17:21 JST | PASS | 0 | 3 | `e1fb911e8a1d03d643988e0d2f6b22385b8b96a5` | `54320D0B1BEE1172D40B3727022DCA5CF3841D6FB196304416D6086BE63894A7` | [XML](../scripts/wack_reports/CloudMigrator_0.7.1.0_x64-20260808-082101988.xml) / [HTML](../scripts/wack_reports/CloudMigrator_0.7.1.0_x64-20260808-082101988.htm) |
| 0.7.1.0（修正後） | 2026-08-08 17:44 JST | PASS | 0 | 1 `[88]` | `e1fb911e8a1d03d643988e0d2f6b22385b8b96a5` + 作業ツリー | `AEE113DED25F8B7E8EA4537A8566E614E226829FC4FF826E4A987AF04A019366` | [XML](../scripts/wack_reports/CloudMigrator_0.7.1.0_x64-20260808-084330695.xml) / [HTML](../scripts/wack_reports/CloudMigrator_0.7.1.0_x64-20260808-084330695.htm) |
| 0.7.1.0（高解像度ロゴ再生成後） | 2026-08-08 17:55 JST | PASS | 0 | 1 `[88]` | `e1fb911e8a1d03d643988e0d2f6b22385b8b96a5` + 作業ツリー | `FF83D5270B638C6BB12D78E11435A22D708258852D80D935FB18B70C87EFFC82` | [XML](../scripts/wack_reports/CloudMigrator_0.7.1.0_x64-20260808-085423406.xml) / [HTML](../scripts/wack_reports/CloudMigrator_0.7.1.0_x64-20260808-085423406.htm) |

## `[45]` / `[63]` 修正内容

- `[45] アプリ リソース`: `docs/assets/store-source/app_logo.jpg` をソースにして、`StoreLogo`、`Square44x44Logo`、`Square150x150Logo` を高品質に再生成した。`Wide310x150Logo` は専用のワイドソースから生成済みである。各ファイルは 204,800 bytes 未満である。
- `[63] プラットフォームに適したファイル`: `installer/msix/Package.appxmanifest` と `src/CloudMigrator.Dashboard/Package.appxmanifest` の `Identity` に `ProcessorArchitecture="x64"` を追加し、x64 パッケージを再生成した。
- 修正後パッケージ: `installer/msix/AppPackages/CloudMigrator_0.7.1.0_x64.msix`
- 再検証コマンド: `.\scripts\run-wack-test.ps1 -PackagePath .\installer\msix\AppPackages\CloudMigrator_0.7.1.0_x64.msix`

## Optional failure の記録

修正前の WACK は Required failure 0 件、Optional failure 3 件でした。修正後は Required failure 0 件、Optional failure 1 件となり、`[45]` と `[63]` が解消されました。残る `[88]` は依存ライブラリ・実行基盤・WACK のヒューリスティック検出を含むため、今回の許容方針に従って記録します。

1. `[45] アプリ リソース`: `StoreLogo.jpg`、`Square44x44Logo.jpg`、`Square150x150Logo.jpg`、`Wide310x150Logo.jpg` の寸法またはファイルサイズが検査基準を超過。
2. `[88] ブロック済みの実行可能ファイル`: `Process.Start`、`ShellExecuteW`、およびブロック対象文字列への参照を検出。
3. `[63] プラットフォームに適したファイル`: x64 バイナリの一部が manifest 上で `neutral` と宣言されていることを検出。

高解像度ソースへの差し替え後の現行パッケージでも `[45] アプリ リソース` と `[63] プラットフォームに適したファイル` は合格し、`[88] ブロック済みの実行可能ファイル` のみが Optional failure として残りました。

## 記録手順

1. [WACK 検証チェックリスト](wack-validation-checklist.md) の実行前記録を埋める。
2. PowerShell 7 で `scripts/run-wack-test.ps1` を実行し、UAC の昇格を許可する。
3. `scripts/wack_reports/` の XML/HTML と `AnalyzeWackReport.ps1` の要約を確認する。
4. Required failure が 0 件であること、Optional failure の対応方針、対象 SHA とパッケージ SHA-256 をこの表へ記録する。
