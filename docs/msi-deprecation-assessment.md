# MSI 配布・CI ジョブ廃止の可否調査（#286）

親 Epic: #101 / 関連: #276（Store 自動公開）、#284（Hosted WACK）

## 結論（推奨）

**段階廃止を推奨します。** 過去 release の MSI asset は削除せず保持し、新規 release から MSI の build・asset 添付と CI の WiX 検証を外します。

| 段階 | 内容 | 状態 |
| --- | --- | --- |
| 1 | README / usage に非推奨と移行手順を告知する（本変更） | 実施済み |
| 2 | MSIX の設定・認証情報の引き継ぎを実機で確認する（下記「未検証事項」） | 要確認 |
| 3 | 専用 PR で `release.yml` の `msi` job、`ci.yml` の `publish-win` / `msi-check`、`installer/wix`、`tools/Test-WixBuild.ps1` を削除する | 段階 2 の後 |
| 4 | 次の release の CHANGELOG と release note に MSI 廃止を明記する | 段階 3 と同時 |

即時廃止を採らない理由は、MSI と MSIX は別パッケージで、MSI の更新はもう届かなくなるためです。既存の MSI 利用者が自分で移行する手順と猶予を先に示します。

## 調査結果

### 1. 利用状況

- MSI は v0.3.0（2026-04-08）から v0.7.2 まで配布してきました。v0.7.2 は `cloud-migrator-setup.msi` を添付しています。
- release asset のダウンロード数は、この調査環境の GitHub API から取得できていません。**リポジトリ管理者が Releases ページで確認し、本書に追記してください。** 数値が小さければ段階 3 を前倒しできます。

### 2. 代替配布の成立性

| 経路 | 対象 | 更新 | 備考 |
| --- | --- | --- | --- |
| Microsoft Store（MSIX） | Windows | Store が自動更新 | v0.7.2 で自動公開が成功済み |
| MSIX 直接配布 | Windows | 手動 | `.msix` は release に添付済み。署名の信頼設定が必要になりうる |
| Windows ZIP | Windows | 手動 | セルフコンテインドで .NET ランタイム不要。PATH 設定は手動 |
| tar.gz | Linux / macOS | 手動 | 変更なし |

CLI を PATH で使う用途は ZIP が代替します。Dashboard を使う用途は Store 版が代替します。

### 3. 変更対象（段階 3 の範囲）

| 対象 | 変更 |
| --- | --- |
| `.github/workflows/release.yml` | `msi` job を削除。`publish` job の `publish-win-x64` artifact 保存 step は MSI 専用なので削除 |
| `.github/workflows/ci.yml` | `publish-win` と `msi-check` を削除 |
| `installer/wix/` | 削除 |
| `tools/Test-WixBuild.ps1` | 削除 |
| `README.md` / `usage.md` / `docs/architecture.md` / `docs/msix-packaging-and-store-guide.md` | MSI の記述を削除または過去 release 向けの注記へ変更 |
| `CHANGELOG.md` / release note の配布物表 | MSI 廃止を明記 |

`msix` / `wack` / `store` job は `msi` job に依存していません（`needs` は `publish`、`msix`、`wack` のみ）。MSIX 経路への影響はありません。`publish-win-x64` artifact を使うのは `msi` job だけであることは、段階 3 の前に再確認してください。

### 4. 既存 MSI 利用者の移行手順（案）

1. 設定ファイル `%APPDATA%\CloudMigrator\` をバックアップする。
2. 「アプリと機能」から MSI 版をアンインストールする。
3. Microsoft Store 版を導入する。CLI のみ使うなら ZIP を展開して PATH を通す。
4. 起動して設定と転送状態が引き継がれているか確認する。

### 5. 過去 asset の保持

過去 release の MSI asset は削除しません。復旧性と、古い版へ戻す需要を優先します。

### 6. 削減効果

直近の run（#31940437948）の実測は MSI build が約 1分40秒でした。`msi` job は `msix` job と並列なので、release の壁時計は WACK と Store 公開が決めており、短縮はほぼ見込めません。主な効果は次の 3 点です。

- PR ごとの CI から `publish-win` と `msi-check` の 2 job を除ける
- WiX 5.0.2 と拡張の version 固定という保守対象がなくなる
- release の artifact が 1 つ減る

## 未検証事項

- **MSIX 版の設定・データの扱い。** 本アプリは設定・ログ・DB を `%APPDATA%\CloudMigrator\` に置きます（`AppDataPaths`）。パッケージ化された Windows アプリは `%APPDATA%` への書き込みがパッケージ専用領域へ仮想化されることがあり、MSI 版のデータを MSIX 版が読めるとは限りません。実機で、MSI 版から Store 版へ移したときに設定と転送状態が引き継がれるかを確認し、引き継がれない場合は手順 1 の復元方法を追記してください。
- **認証情報。** Windows Credential Manager / DPAPI のデータは per-user で保持されるはずですが、MSIX 版で読み出せることを同じ実機確認で見てください。
- **ダウンロード数。** 上記「利用状況」のとおり。
