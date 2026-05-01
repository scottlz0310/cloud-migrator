# CLAUDE.md

このファイルは Claude Code など、repo root の `CLAUDE.md` を読む AI coding agent 向けの入口です。

## 最初に読む文書

- 共通の実装ガードレール: [docs/architecture/ai-implementation-guardrails.md](docs/architecture/ai-implementation-guardrails.md)
- 既存アーキテクチャ: [docs/architecture.md](docs/architecture.md)
- 実装計画: [docs/implementation-plan.md](docs/implementation-plan.md)
- 現在のタスク: [tasks.md](tasks.md)

## 基本方針

- すべてのコード、コメント、ドキュメント、AI との対話は日本語で行う。技術的固有名詞は英語併記可。
- Dashboard component は表示とイベント転送を主責務とし、実行制御、状態集約、provider 選択は ViewModel / Application Service / factory へ寄せる。
- SharePoint / Dropbox など provider 固有の分岐を UI component や `Core` 層へ直接広げない。
- route / provider 固有の設定、state DB、metrics、phase 表示は route descriptor や factory など単一の定義から参照する方向を優先する。
- 寄せ先クラスが未実装でも、Target Boundary Design の境界名を基準に薄い service / factory / descriptor を作るか、tactical fix の理由を明記する。
- tactical fix で直接分岐を追加する場合は、理由、影響範囲、後続リファクタ issue を PR 説明またはコメントに明記する。

詳細な判断基準と PR レビューチェックリストは、必ず [AI 実装ガードレール](docs/architecture/ai-implementation-guardrails.md) を参照してください。
