using CloudMigrator.Core.Configuration;
using CloudMigrator.Core.Transfer;
using CloudMigrator.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace CloudMigrator.Core.Migration;

/// <summary>
/// SharePoint 移行パイプライン。既存 <see cref="TransferEngine"/> のラッパー。
/// クロール済みソースアイテムと TransferEngine をコンストラクタで受け取り、RunAsync で実行する。
/// </summary>
public sealed class SharePointMigrationPipeline : IMigrationPipeline
{
    private readonly TransferEngine _engine;
    private readonly IReadOnlyList<StorageItem> _sourceItems;
    private readonly string _destRoot;
    private readonly ILogger<SharePointMigrationPipeline> _logger;

    public SharePointMigrationPipeline(
        TransferEngine engine,
        IReadOnlyList<StorageItem> sourceItems,
        string destRoot,
        ILogger<SharePointMigrationPipeline> logger)
    {
        _engine      = engine;
        _sourceItems = sourceItems;
        _destRoot    = destRoot;
        _logger      = logger;
    }

    /// <inheritdoc/>
    public async Task<TransferSummary> RunAsync(CancellationToken ct)
    {
        var summary = await _engine.RunAsync(_sourceItems, _destRoot, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "SharePoint 移行完了: 成功 {Success} / 失敗 {Failed} / スキップ {Skipped} / 所要時間 {Elapsed:c}",
            summary.Success, summary.Failed, summary.Skipped, summary.Elapsed);

        return summary;
    }
}
