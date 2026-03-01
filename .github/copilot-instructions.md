# Copilot Instructions

## 言語規約
**すべてのコード、コメント、ドキュメント、AI との対話は日本語で行う。** 技術的固有名詞（クラス名・API 名等）は英語併記可。

## プロジェクト概要
OneDrive → SharePoint Online への大容量ファイル移行を自動化する C# / .NET 10 CLI ツール。  
現行 Python 実装（[Bulk-Migrator](../Bulk-Migrator)）からのリライト。仕様は [docs/implementation-plan.md](../docs/implementation-plan.md) を参照。

## ビルド・テスト

```bash
dotnet build                          # ソリューション全体ビルド
dotnet test                           # 全テスト実行
dotnet test tests/unit                # unit のみ
dotnet test --filter "FullyQualifiedName~Auth"  # キーワード絞り込み
dotnet test tests/unit/CloudMigrator.Tests.Unit.csproj  # 単一プロジェクト
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

### タスク管理
現在の進捗は `task.md`（ルート）を参照。フェーズ完了時に更新すること。
