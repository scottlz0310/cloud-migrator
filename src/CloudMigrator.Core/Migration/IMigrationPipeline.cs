using CloudMigrator.Core.Transfer;

namespace CloudMigrator.Core.Migration;

/// <summary>
/// 移行パイプラインの共通契約。
/// Dropbox・SharePoint ともに SQLite 状態管理 + フェーズ構造で実装する。
/// </summary>
public interface IMigrationPipeline
{
    /// <summary>移行を実行し、結果サマリーを返す。</summary>
    Task<TransferSummary> RunAsync(CancellationToken ct);
}
