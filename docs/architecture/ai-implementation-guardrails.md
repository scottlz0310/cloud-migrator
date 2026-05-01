# AI 実装ガードレール

## 目的

この文書は、Copilot CLI、Codex、Claude Code などの AI coding agent が `cloud-migrator` を変更するときに守る設計制約を明文化する。目の前の issue を閉じるための局所的な分岐追加で、Dashboard、route、provider の境界が崩れることを防ぐ。

この文書を正とし、各 agent 用の入口ファイルはここへ誘導する。

| Agent / ツール | 入口 |
|---|---|
| GitHub Copilot / Copilot CLI | `.github/copilot-instructions.md` |
| Codex | `AGENTS.md` |
| Claude Code | `CLAUDE.md` |

## 先に確認すること

実装前に、次の順で変更箇所を決める。

1. 変更が route 固有か provider 固有かを切り分ける。
2. 表示だけの差分か、実行制御、状態 DB、設定保存、転送パイプラインの差分かを切り分ける。
3. 既存の抽象、factory、route descriptor、Application Service、ViewModel に寄せられるかを確認する。
4. UI component、composition root、provider 実装へ直接分岐を増やす前に、単一の定義へ集約できない理由を説明できるか確認する。

## レイヤ責務

| レイヤ | 主責務 | 避けること |
|---|---|---|
| Dashboard component | 表示、入力、イベント転送、軽い表示整形 | 転送実行制御、provider 生成、state DB 選択、長い route 固有分岐 |
| ViewModel / Application Service | 画面状態の集約、コマンド実行、進捗更新、route に応じたユースケース呼び出し | UI framework 固有 API への依存、provider 実装詳細の露出 |
| Composition root / factory | DI 登録、provider / pipeline / state DB の組み立て | 画面ごとに重複した provider 判定を持つこと |
| Core | provider 非依存のユースケース、設定モデル、状態 DB、転送制御 | Graph / Dropbox など具体 provider への依存 |
| Provider implementation | 外部 API 固有処理、認証、再試行、アップロード実装 | 他 provider の設定、保存形式、runtime behavior への干渉 |

## Route / Provider 境界

`source provider` と `destination provider` は混同しない。現在の主な route は `OneDrive -> SharePoint` と `OneDrive -> Dropbox` であり、OneDrive は転送元、SharePoint / Dropbox は転送先として扱う。

route 固有の情報は、できるだけ単一の route 定義から参照する。

- 表示名
- 転送元 / 転送先 provider key
- 設定セクション
- state DB パス
- metrics / phase 表示
- wizard step
- pipeline / service factory
- validation rule

同じ route 判定を `DashboardPage`、`SettingsPage`、`App.xaml.cs`、CLI command、テスト用 fake に個別実装しない。既存コードに tactical な分岐がある場合も、新規拡張では集約先を作るか、後続リファクタ issue へ接続する。

## 増やしてよい分岐

次の分岐は、責務が明確でテストされていれば許容する。

- route selection / wizard で表示する選択肢の分岐
- route descriptor / factory / DI 登録で provider、pipeline、state DB を選ぶ分岐
- provider 実装内で外部 API の仕様差を吸収する分岐
- 設定 read/write / validation で provider 固有セクションだけを扱う分岐
- テストで route ごとの期待値を明示する分岐

## 増やしてはいけない分岐

次の分岐は原則として追加しない。

- Dashboard component に `if (isDropbox)` / `if (isSharePoint)` を広げ、DB、pipeline、設定保存を直接切り替える。
- `Core` 層から Graph / Dropbox の具象型、SDK 型、API 固有例外へ依存する。
- provider 固有設定の保存や validation が、他 provider の値を消す、上書きする、runtime behavior を変える。
- 転送元 provider と転送先 provider の責務を同じ変数、設定項目、UI ラベルで混同する。
- state DB、metrics、phase 名、settings section の対応を複数箇所へコピーする。
- SharePoint 向けの最適化を Dropbox に暗黙適用する、またはその逆を行う。

## MVVM への寄せ方

Dashboard は段階的に MVVM 的な構造へ寄せる。大きな一括リファクタではなく、変更箇所の近くから責務を分離する。

- `.razor` component は View とみなし、表示状態、入力、イベント発火に寄せる。
- 状態集約、非同期コマンド、転送開始 / 停止、DB リセット、設定保存などは ViewModel / Application Service へ移す。
- route / provider の解決は ViewModel 内に直書きせず、route descriptor または factory を参照する。
- 既存挙動を保ったまま薄いサービスを抽出し、テスト可能な単位を増やす。

## Tactical fix の扱い

緊急修正や小さな issue で直接分岐を追加する場合は、次の条件を満たす。

- 分岐を追加する理由が、PR 説明、コメント、または該当コードの短いコメントで説明されている。
- 影響範囲が 1 component / 1 service などに閉じている。
- 他 provider の設定保存、validation、runtime behavior に影響しないテストがある。
- 後続リファクタが必要な場合は issue 番号または TODO 方針が明記されている。

## PR レビューチェックリスト

PR 作成者と reviewer は、Dashboard / route / provider に触れる変更で次を確認する。

- [ ] UI component は表示とイベント転送に留まり、実行制御や provider 生成を抱えていない。
- [ ] route / provider 固有分岐は descriptor、factory、Application Service、ViewModel のいずれかに集約されている。
- [ ] `Core` 層が具体 provider や外部 SDK に依存していない。
- [ ] provider 固有設定は他 provider の保存値、validation、実行時挙動を変えていない。
- [ ] 転送元 provider と転送先 provider の責務が変数名、設定名、UI 表示で区別されている。
- [ ] state DB、metrics、phase、settings section の対応が単一の route 定義からたどれる。
- [ ] tactical fix の直接分岐には理由、影響範囲、後続方針がある。
- [ ] route 別の主要挙動はユニットテスト、ビルド、または手動確認手順で検証されている。

## Issue 着手時の書き方

issue / PR の実装方針には、必要に応じて次を明記する。

- 今回増やしてよい分岐
- 増やしてはいけない分岐
- 寄せるべき抽象
- tactical fix として許容する理由
- 後続リファクタ issue
