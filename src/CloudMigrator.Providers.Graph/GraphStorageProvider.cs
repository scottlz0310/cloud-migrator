using CloudMigrator.Providers.Abstractions;
using CloudMigrator.Providers.Graph.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace CloudMigrator.Providers.Graph;

/// <summary>
/// Microsoft Graph を使用した IStorageProvider 実装。
/// OneDrive（source）と SharePoint（destination）の両方をカバーする。
/// </summary>
public sealed class GraphStorageProvider : IStorageProvider
{
    private readonly GraphServiceClient _client;
    private readonly ILogger<GraphStorageProvider> _logger;
    private readonly int _largeFileThresholdBytes;

    public string ProviderId => "graph";

    /// <param name="client">GraphClientFactory で生成した GraphServiceClient</param>
    /// <param name="logger">ロガー</param>
    /// <param name="largeFileThresholdMb">大容量判定閾値（MB）。デフォルト 4</param>
    public GraphStorageProvider(
        GraphServiceClient client,
        ILogger<GraphStorageProvider> logger,
        int largeFileThresholdMb = 4)
    {
        _client = client;
        _logger = logger;
        _largeFileThresholdBytes = largeFileThresholdMb * 1024 * 1024;
    }

    // ─────────────────────────────────────────────────────────────
    // IStorageProvider: ListItemsAsync
    // Phase 3 で OneDrive/SharePoint 再帰クロールとキャッシュを実装する。
    // Phase 2 では接続確認用の最小実装のみ。
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<StorageItem>> ListItemsAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        // Phase 3 で実装。現時点では空リストを返すスタブ。
        _logger.LogWarning("ListItemsAsync は Phase 3 で実装予定です。rootPath={RootPath}", rootPath);
        return Task.FromResult<IReadOnlyList<StorageItem>>(Array.Empty<StorageItem>());
    }

    // ─────────────────────────────────────────────────────────────
    // IStorageProvider: UploadFileAsync
    // FR-04: 4MB 未満 → PUT
    // FR-05: 4MB 以上 → Upload Session（LargeFileUploadTask）
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task UploadFileAsync(TransferJob job, CancellationToken cancellationToken = default)
    {
        if (job.Source.SizeBytes is null)
            throw new InvalidOperationException($"SizeBytes が未設定のため転送できません: {job.Source.SkipKey}");

        if (job.Source.SizeBytes < _largeFileThresholdBytes)
            await SmallUploadAsync(job, cancellationToken).ConfigureAwait(false);
        else
            await LargeUploadAsync(job, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 小ファイル（4MB 未満）を単純 PUT でアップロード（FR-04）。
    /// </summary>
    private async Task SmallUploadAsync(TransferJob job, CancellationToken cancellationToken)
    {
        _logger.LogDebug("小ファイルアップロード開始: {SkipKey} ({Bytes} bytes)",
            job.Source.SkipKey, job.Source.SizeBytes);

        // TODO Phase 3: 実際のファイルストリーム取得を OneDrive クロールキャッシュと連携する
        // 現在は DriveItem の content を直接取得するパスに接続するスタブ
        _logger.LogWarning("SmallUploadAsync: Phase 3 でストリーム取得を実装します: {Path}", job.DestinationFullPath);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 大容量ファイル（4MB 以上）を Upload Session + LargeFileUploadTask でアップロード（FR-05）。
    /// Phase 3 で driveId / 親フォルダ ID の解決と組み合わせて完成する。
    /// </summary>
    private async Task LargeUploadAsync(TransferJob job, CancellationToken cancellationToken)
    {
        _logger.LogDebug("大容量ファイルアップロード開始: {SkipKey} ({Bytes} bytes)",
            job.Source.SkipKey, job.Source.SizeBytes);

        // PoC: Phase 3 で実装する Upload Session フロー（概念コード）
        //
        // var requestBody = new Microsoft.Graph.Drives.Item.Items.Item.CreateUploadSession.CreateUploadSessionPostRequestBody
        // {
        //     Item = new DriveItemUploadableProperties
        //     {
        //         AdditionalData = new Dictionary<string, object>
        //             { { "@microsoft.graph.conflictBehavior", "replace" } }
        //     }
        // };
        //
        // var session = await _client.Drives[driveId]
        //     .Items[parentId]
        //     .CreateUploadSession
        //     .PostAsync(requestBody, cancellationToken: cancellationToken);
        //
        // using var stream = await GetSourceStreamAsync(job, cancellationToken);
        // var uploadTask = new LargeFileUploadTask<DriveItem>(session, stream, chunkSizeBytes: _largeFileThresholdBytes);
        // var result = await uploadTask.UploadAsync(cancellationToken: cancellationToken);

        _logger.LogWarning("LargeUploadAsync は Phase 3 で実装予定です: {Path}", job.DestinationFullPath);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────
    // IStorageProvider: EnsureFolderAsync
    // FR-06: 転送先フォルダを階層順に自動作成
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task EnsureFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("フォルダ作成確認: {FolderPath}", folderPath);

        // TODO Phase 3: driveId / 親フォルダ ID を MigratorOptions から解決し、
        //               Graph API でフォルダを順次作成する
        // var newFolder = new DriveItem
        // {
        //     Name = folderName,
        //     Folder = new Folder(),
        //     AdditionalData = new Dictionary<string, object>
        //         { { "@microsoft.graph.conflictBehavior", "fail" } }
        // };
        // await _client.Drives[driveId].Items[parentId].Children.PostAsync(newFolder, cancellationToken: cancellationToken);

        _logger.LogWarning("EnsureFolderAsync は Phase 3 で実装予定です: {FolderPath}", folderPath);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
