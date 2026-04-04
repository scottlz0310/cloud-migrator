# Copilot Instructions

## 言語規約
**すべてのコード、コメント、ドキュメント、AI との対話は日本語で行う。** 技術的固有名詞（クラス名・API 名等）は英語併記可。

## プロジェクト概要
OneDrive → SharePoint Online への大容量ファイル移行を自動化する C# / .NET 10 CLI ツール。  
現行 Python 実装（[Bulk-Migrator](../Bulk-Migrator)）からのリライト。仕様は [docs/implementation-plan.md](../docs/implementation-plan.md) を参照。

## ビルド・テスト

```bash
dotnet build CloudMigrator.slnx                              # ソリューション全体ビルド
dotnet build CloudMigrator.slnx --configuration Release      # リリースビルド
dotnet test tests/unit/CloudMigrator.Tests.Unit.csproj       # unit のみ
dotnet test tests/integration/CloudMigrator.Tests.Integration.csproj  # integration のみ
dotnet test --filter "FullyQualifiedName~Auth"               # キーワード絞り込み
# E2E は CI 除外（[Trait("Category","E2E")] 付与済み）
```

## アーキテクチャ

```
src/
  CloudMigrator.Cli/                    ← System.CommandLine CLI エントリーポイント
  CloudMigrator.Core/                   ← ドメイン・ユースケース
  CloudMigrator.Providers.Abstractions/ ← IStorageProvider 等の契約（provider 非依存）
  CloudMigrator.Providers.Graph/        ← Microsoft Graph 実装
  CloudMigrator.Providers.Dropbox/      ← 将来拡張スケルトン
  CloudMigrator.Observability/          ← 構造化ログ・メトリクス（Serilog JSON）
  CloudMigrator.Testing/               ← テスト共通ユーティリティ
tests/unit | integration | e2e
configs/config.json                     ← ランタイム設定
```

### 設定優先順位
**環境変数 > configs/config.json > デフォルト値**（`Microsoft.Extensions.Configuration`）

### 大容量ファイル転送
- 4MB 未満: 単純 PUT
- 4MB 以上: Graph SDK `LargeFileUploadTask`（チャンク・再開対応）

### 並列実行
`Channel.CreateBounded<TransferJob>` + `Parallel.ForEachAsync` + `MaxDegreeOfParallelism`

## 重要な規約

### provider 抽象化
新しいストレージ操作は必ず `IStorageProvider`（`Providers.Abstractions`）経由で定義する。Graph 固有コードを `Core` に混入させない。

### スキップリスト判定キー
**path + name** の組み合わせのみ。ファイルサイズ・更新日時は判定に使用しない（FR-07）。

### ログ
全ログは `CloudMigrator.Observability` の logger 経由。UTC タイムゾーン + ISO 8601 必須。機密情報（token/secret/password/api_key）は出力前にマスク処理。

### リトライポリシー
Graph API: `Retry-After` ヘッダー準拠。その他ネットワーク障害: 指数バックオフ + ジッター。`Core` 層でポリシーを統一定義。

### テスト規約
- `CloudMigrator.Testing` の共通ヘルパーを活用
- 外部 API（Graph）はインターフェース経由で Moq を使用
- FluentAssertions でアサーション記述
- 各テストに目的コメント: `// 検証対象: XXX  目的: YYY`

### シークレット管理
`.env` / 環境変数のみ（コミット禁止）。`configs/config.json` に機密情報を含めない。`sample.env` をテンプレートとして管理。

### コミット規約
Conventional Commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`, `ci:`  
PR サイズ目安: ~300 行。

### 環境変数キー規約
`MIGRATOR__GRAPH__CLIENTID` のように `__` 区切りでセクションを表現する（.NET 標準）。  
`MIGRATOR__GRAPH__CLIENTSECRET` は `configs/config.json` に含めず `AppConfiguration.GetGraphClientSecret()` 経由のみで取得。

### タスク管理
現在の進捗は `task.md`（ルート）を参照。フェーズ完了時に更新すること。

---

## イテレーションサイクル

各フェーズは以下のサイクルで進める。

### 1. 実装
```bash
git checkout -b feature/phase{N}-{名前}  # ブランチ作成
# ... コーディング ...
dotnet build CloudMigrator.slnx          # ローカルビルド確認
dotnet test tests/unit/...               # テスト確認
```
### 2. CHANGELOG・task.md 更新
- `CHANGELOG.md` に当フェーズの変更内容を追記（Keep a Changelog 形式、日付入り）
- `task.md` の完了チェックボックスを更新

### 3. コミット & PR
```bash
git add -A
git commit -m "feat: Phase{N} - {概要}" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git push origin feature/phase{N}-{名前}
gh pr create --title "Phase{N}: {タイトル}" --body "{説明}"
```

### 4. CI 監視（自動）
- **CI**: ubuntu / windows / macos × .NET 10 の 3 ジョブが全 SUCCESS になるまで待機
- **Copilot code review**: PR 作成後に自動トリガー

### 5. レビュー対処（PR レビュー運用サイクル）

#### 基本ルール
- レビュー対応中は、対象 PR にのみコミットを積み、サブ PR を新規作成しない。
- `@copilot review` の再依頼は「前回レビューの指摘をすべて対応後」に 1 回だけ行う。
- 前回レビューが完了していない状態で新しいレビュー依頼を重ねて出さない。
- PR 作成後は **2 分間隔で最大 10 分**を目安に CI とレビューコメントを監視し、指摘があれば同一 PR で対応する。

#### サイクル終了条件
以下のいずれかを満たした場合、サイクルを終了する：
- `blocking` コメントがすべて解消されている
- 一定時間（2 分 × 3 回）新規コメントが発生していない（no comment）かつ CI が成功している

終了後、`non-blocking` / `suggestion` のみが残存する場合は、対応または対応見送り（理由明記）を行った上でサイクルを延長せず次工程へ進む。

#### ステップ 1: コメントを分類する

| 分類 | 基準 |
|------|------|
| `blocking`（修正必須） | 実行時エラーの可能性がある / データ整合性を破壊する / セキュリティリスク（認証・認可・入力検証）/ 型安全性が欠如 / 後方互換性のない変更 |
| `non-blocking`（任意） | テスト追加・ログ改善など、対応推奨だが必須ではないもの |
| `suggestion`（改善提案） | 設計・命名・抽象化の改善提案 |

#### ステップ 2: 採否を判断する
- `accept` / `reject` を明示する。
- `reject` の場合は理由を記述する（スコープ外 / 別 PR で対応予定 / 設計方針と相違）。
- `non-blocking / suggestion` は「今回対応する」か「対応しない（理由明示）」のいずれかを選択する。

#### ステップ 3: 修正を行う
- `accept` した項目のみ修正する。修正後にビルド・テストを再実行する。

#### ステップ 4: 再レビューの要否を判断する

**再レビュー必須条件**
- 仕様・API 変更がある
- ロジック変更がある（分岐条件・計算式・データフローの変更）

**再レビュー不要条件**
- 軽微修正のみ（typo・コメント・フォーマット等）

#### ステップ 5: 最終アクションを決定する

- `blocking` が残存 → 再レビュー依頼
- `blocking` がすべて解消済み かつ 再レビュー必須 → 再レビュー依頼前に「前回指摘がすべて解消・新たな blocking なし」を確認してから依頼
- マージ条件をすべて満たす → マージ

対処後：全スレッドに返信 → GraphQL `resolveReviewThread` で全スレッドを解決済みに。

### 出力フォーマット
- コメント分類結果（blocking / non-blocking / suggestion の一覧）
- 採否一覧（accept / reject + reject 理由）
- 修正内容サマリ
- 再レビュー要否（理由付き）
- 最終アクション（再レビュー依頼 / マージ）

### 6. マージ条件（全て満たすこと）
- [ ] CI 全ジョブ SUCCESS（実行中なし）
- [ ] `blocking` コメント 0 件
- [ ] 未解決レビュースレッド 0 件
- [ ] 全コメントに返信済み
- [ ] 必要な承認数を満たしている（1 approval 以上）

### 7. クリーンアップ
```bash
git checkout main && git pull origin main
git branch -d feature/phase{N}-{名前}
# リモートブランチは GitHub 側の "Delete branch" または PR マージ時自動削除
```

