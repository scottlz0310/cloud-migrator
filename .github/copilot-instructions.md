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

### 2. コミット & PR
```bash
git add -A
git commit -m "feat: Phase{N} - {概要}" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git push origin feature/phase{N}-{名前}
gh pr create --title "Phase{N}: {タイトル}" --body "{説明}"
```

### 3. CI 監視（自動）
- **CI**: ubuntu / windows / macos × .NET 10 の 3 ジョブが全 SUCCESS になるまで待機
- **Copilot code review**: PR 作成後に自動トリガー

### 4. レビュー対処
Copilot レビューコメントは以下の基準で対処する：

| 種別 | 対応 |
|------|------|
| バグ・設計上の欠陥 | コードを修正してコミット・プッシュ |
| セキュリティ指摘 | 必ず修正（シークレット・権限過剰等） |
| ドキュメント不整合 | 修正 |
| 将来フェーズの実装要求 | 「Phase{N} で対応予定」と返信、コメント解決 |
| 意図的な設計の指摘 | 設計意図を返信で説明、解決 |

対処後：全スレッドに返信 → GraphQL `resolveReviewThread` で全スレッドを解決済みに。

### 5. マージ条件（全て満たすこと）
- [ ] CI 全ジョブ SUCCESS（実行中なし）
- [ ] 未解決レビュースレッド 0 件
- [ ] 全コメントに返信済み

### 6. クリーンアップ
```bash
git checkout main && git pull origin main
git branch -d feature/phase{N}-{名前}
# リモートブランチは GitHub 側の "Delete branch" または PR マージ時自動削除
```

### 7. CHANGELOG・task.md 更新
- `CHANGELOG.md` に当フェーズの変更内容を追記（Keep a Changelog 形式、日付入り）
- `task.md` の完了チェックボックスを更新
