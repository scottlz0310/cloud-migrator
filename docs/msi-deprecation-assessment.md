# MSI 配布・CI ジョブ廃止の判断記録（#286）

親 Epic: #101 / 関連: #276（Store 自動公開）、#284（Hosted WACK）

## 結論

**MSI を即時廃止しました。** 新規 release から MSI の build・asset 添付と CI の WiX 検証を外し、v0.7.2 までの release asset は削除せず保持します。

## 判断根拠

### 利用状況

MSI は v0.3.0（2026-04-08）から v0.7.2 まで配布していました。リポジトリ管理者の確認では、MSI asset のダウンロード数は 1（管理者本人）で、他の利用者はいません。そのため、既存利用者向けの段階廃止、移行猶予、MSIX 版への設定引き継ぎの検証は行いません。

### 代替配布

| 経路 | 対象 | 更新 |
| --- | --- | --- |
| Microsoft Store（MSIX） | Windows | Store が自動更新（v0.7.2 で自動公開が成功済み） |
| MSIX 直接配布 | Windows | 手動。署名の信頼設定が必要になりうる |
| Windows ZIP | Windows | 手動。セルフコンテインドで .NET ランタイム不要 |
| tar.gz | Linux / macOS | 手動 |

CLI を PATH で使う用途は ZIP が、Dashboard を使う用途は Store 版が代替します。

### 変更内容

| 対象 | 変更 |
| --- | --- |
| `.github/workflows/release.yml` | `msi` job と、それ専用の `publish-win-x64` artifact 保存 step を削除 |
| `.github/workflows/ci.yml` | `publish-win` と `msi-check` を削除 |
| `installer/wix/`、`tools/Test-WixBuild.ps1` | 削除 |
| `README.md` / `usage.md` / `docs/` / `CHANGELOG.md` | MSI の記述を廃止の説明へ変更 |

`msix` / `wack` / `store` job は `msi` job に依存していませんでした（`needs` は `publish`、`msix`、`wack` のみ）。`publish-win-x64` artifact を使うのは `msi` job だけでした。MSIX 経路への影響はありません。

### 過去 asset

過去 release の MSI asset は、復旧性と古い版へ戻す需要を考えて削除しません。

### 削減効果

直近の run（#31940437948）の MSI build は約 1分40秒ですが、`msi` job は `msix` job と並列で、release の壁時計は WACK と Store 公開が決めています。release の短縮はほぼ見込めません。効果は次の 3 点です。

- PR ごとの CI から `publish-win` と `msi-check` の 2 job を除ける
- WiX 5.0.2 と拡張の version 固定という保守対象がなくなる
- release の artifact が 1 つ減る
