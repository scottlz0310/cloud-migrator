# Microsoft Partner Center 提出アセット

Issue [#266](https://github.com/scottlz0310/cloud-migrator/issues/266) の成果物です。Partner Center に直接渡すクリーンなアップロードフォルダーは `../store/`、原本と本書はこの `store-source/` に分離しています。

## ファイル構成

| ファイル | 用途 | 状態 |
| --- | --- | --- |
| `../store/listingData.csv` | `en-us` / `ja-jp` の Store Listing Data テンプレート | 提出入力用 |
| `../store/store_logo_300x300.png` | 1:1 の Store logo | CSV から参照 |
| `../store/screenshot_dashboard.png` | Dashboard のスクリーンショット | CSV から参照 |
| `../store/screenshot_settings.png` | 設定画面のスクリーンショット | CSV から参照 |
| `../store/screenshot_logs.png` | ログ画面のスクリーンショット | CSV から参照 |
| `app_logo.jpg` | 700x700 の高解像度ロゴ原本 | 派生画像の原本 |
| `store_promo_header.jpg` | プロモーション構図のドラフト | 提出用 CSV では未参照 |

`../store/store_logo_300x300.png` は `app_logo.jpg` を原寸から縮小して作成しています。低解像度画像の拡大は行っていません。スクリーンショットはすべて 1920x1080 です。設定画面などに表示されるアカウント名・パスは提出用のサンプル値です。

## CSV の扱い

`../store/listingData.csv` は Partner Center のエクスポート形式を維持した UTF-8 BOM 付き CRLF の CSV です。`Field`、`ID`、`Type (種類)` の列名や行は削除・改名せず、値だけを更新してください。画像の値は、アップロードするフォルダー名を含む相対パスです（例: `store/screenshot_dashboard.png`）。

初回提出時は、次の手順で使用します。

1. Partner Center で `CloudMigrator` のアプリを作成または予約し、Store listing の最新 CSV を一度エクスポートする。
2. エクスポートした CSV の `Field` / `ID` / `Type (種類)` がこのテンプレートと一致することを確認する。Partner Center 側の最新項目を正とし、差分があれば行を維持したまま本文と相対パスを移す。
3. Partner Center の「Import folder」では、`docs/assets/store` フォルダーをそのまま選択する。フォルダー内には `listingData.csv` と CSV が参照する 4 画像だけがあり、CSV のパスは `store/...` で始まる。`store-source` の README、原本、未参照ドラフトはアップロード対象ではない。
4. インポート後に英語・日本語の説明文、検索語、スクリーンショットの表示を画面上で確認する。
5. プライバシーポリシー URL とサポート連絡先は、下記の値を使って Partner Center の該当欄にも設定する。

初回提出では `ReleaseNotes` を空欄にしています。更新提出時は対象バージョンの変更内容を記載してください。機能一覧は Store 側で箇条書き表示されるため、`Feature1` 以降の値に手動の箇条書き記号を追加しません。

## サポートとプライバシーポリシー

- サポート URL: <https://github.com/scottlz0310/cloud-migrator/issues>
- リポジトリ: <https://github.com/scottlz0310/cloud-migrator>
- 公開予定のプライバシーポリシー URL: <https://scottlz0310.github.io/cloud-migrator/privacy-policy.html>

リポジトリ内に公開メールアドレスは定義していないため、架空のメールアドレスを CSV やドキュメントへ追加していません。Partner Center がメールアドレスを要求する場合は、発行者が管理する実在のサポート連絡先をアカウント側で設定してください。GitHub Pages の有効化が完了するまで、プライバシーポリシー URL は提出用の確定値ではありません。

## 画像仕様と未完了事項

- Store logo は 1:1 の `../store/store_logo_300x300.png` を使用します。原本は `app_logo.jpg` です。
- スクリーンショットは 3 枚を用意しています。Microsoft の掲載ガイダンスでは 1 枚以上が必要で、4 枚以上が推奨されるため、公開前に 4 枚目を追加できる場合は追加します。
- `store_promo_header.jpg` は 1376x768 で、一般的な 1920x1080 の掲載用解像度に達していません。また、現在の文言に実際の転送ルートと再確認が必要な表現があるため、`../store/listingData.csv` から参照していません。提出用プロモーション素材に採用する場合は、高解像度で再作成し、文言とロゴ使用権を確認してから追加します。
- 画像・文章は開発者が権利を保有するか、利用許諾を確認したものだけを提出します。Microsoft Store の掲載内容は実際の機能と一致させます。

参考: [MSI/EXE の Store listing のインポート・エクスポート](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/import-and-export-store-listings)、[Store listing の入力項目](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/add-and-edit-store-listing-info)、[スクリーンショットと画像](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/screenshots-and-images)、[Microsoft Store ポリシー](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
