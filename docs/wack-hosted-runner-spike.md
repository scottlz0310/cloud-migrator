# GitHub-hosted WACK Spike（#284）

## 目的

現行の release gate は、`self-hosted / windows / wack` runner 上で Windows App Certification Kit（WACK）を実行しています。本 Spike は、同じ release MSIX artifact を `windows-latest` で WACK に渡せるかを検証し、self-hosted runner 廃止の可否を判断するためのものです。

本番の `release.yml`、`wack.yml`、`scripts/run-wack-test.ps1` の gate は変更しません。Microsoft Store への提出も行いません。

## 実行手順

1. GitHub Actions の **WACK Hosted Runner Spike** を開く。
2. **Run workflow** で `release_run_id` に、検証対象の成功済み `release.yml` run ID を指定する。
   - 例: `31944768934`
3. workflow が `msix-release-<run_id>` artifact を別 run から取得する。
4. Hosted runner の OS／image、対話セッション、管理者権限、`appcert.exe` の有無とバージョンを記録する。
5. `.msix`、`.msixupload`、SHA-256 証跡 JSON の tag／commit／ハッシュを照合する。
6. `appcert.exe` が見つかった場合だけ、Spike 専用 wrapper で WACK と `AnalyzeWackReport.ps1 -FailOnRequiredFailure` を実行する。見つからない場合は AppCertKit の導入を試さず、導入可否未検証として判定を保留する。

## 判定

| workflow 結果 | 判定 |
|---|---|
| 成功（`hosted-candidate`） | Hosted 移行候補。別 PR で本番 workflow と運用手順を変更する |
| 失敗・`appcert-setup-unverified` | Hosted runner に Kit がなく、再現可能な導入を試していないため判定保留 |
| 失敗・`capability-recording-failed` | Hosted runner の能力記録に失敗したため判定保留 |
| 失敗・`package-failure` | WACK レポート生成後の package 判定失敗。Hosted runner 能力不足とは分離して package を調査 |
| 失敗・`wack-execution-failed` | WACK またはレポート生成が未完了。OS、セッション、権限、Kit を調査 |
| 失敗・`wack-result-unavailable` | wrapper の構造化結果がないため判定保留 |
| artifact 検証失敗 | 同一 artifact の条件を満たさないため判定保留 |

成功しても、WACK の実行成功は Microsoft Store の認定・公開成功とは別の状態です。

## 証跡

`wack-hosted-spike-<run_id>` artifact に次を保存します。

- `source-run.json`: 対象 release run と tag／commit
- `artifact-integrity.json`: `.msix` / `.msixupload` の SHA-256 照合結果
- `hosted-capability.json`: Hosted runner の OS、image、対話セッション、管理者権限、`appcert.exe`、AppCertKit 導入試行状態
- `spike-result.json`: Spike の判定
- `wack-reports/`: WACK XML／HTML／コマンド出力／wrapper の構造化結果
- `wack-work/`: 隔離した TAEF／AppCertKit ログ

## スコープ境界

- 本番 WACK gate の runner 変更は別 PR で行う。
- `UserInteractive`、管理者権限、WACK script の guard は本番スクリプトから削除しない。
- Store submission、認定、公開完了の検証は対象外とする。
