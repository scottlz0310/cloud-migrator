using CloudMigrator.Providers.Abstractions;
using CloudMigrator.Providers.Graph.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;

namespace CloudMigrator.Providers.Graph;

/// <summary>
/// Microsoft Graph を使用した IStorageProvider 実装。
/// OneDrive（source）と SharePoint（destination）の両方をカバーする。
/// </summary>
public sealed class GraphStorageProvider : IStorageProvider
{
    private readonly GraphServiceClient _client;
    private readonly ILogger<GraphStorageProvider> _logger;
    private readonly GraphStorageOptions _options;
    private readonly int _largeFileThresholdBytes;

    public string ProviderId => "graph";

    /// <param name="client">GraphClientFactory で生成した GraphServiceClient</param>
    /// <param name="logger">ロガー</param>
    /// <param name="options">OneDrive/SharePoint 識別子設定</param>
    /// <param name="largeFileThresholdMb">大容量判定閾値（MB）。デフォルト 4</param>
    public GraphStorageProvider(
        GraphServiceClient client,
        ILogger<GraphStorageProvider> logger,
        GraphStorageOptions? options = null,
        int largeFileThresholdMb = 4)
    {
        _client = client;
        _logger = logger;
        _options = options ?? new GraphStorageOptions();
        _largeFileThresholdBytes = largeFileThresholdMb * 1024 * 1024;
    }

    // ─────────────────────────────────────────────────────────────
    // IStorageProvider: ListItemsAsync
    // rootPath="onedrive"  → OneDrive 再帰クロール（FR-02）
    // rootPath="sharepoint" → SharePoint 再帰クロール（FR-03）
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<IReadOnlyList<StorageItem>> ListItemsAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (rootPath.StartsWith("onedrive", StringComparison.OrdinalIgnoreCase))
            return ListOneDriveItemsAsync(cancellationToken);

        if (rootPath.StartsWith("sharepoint", StringComparison.OrdinalIgnoreCase))
            return ListSharePointItemsAsync(cancellationToken);

        _logger.LogWarning("不明な rootPath です: {RootPath}", rootPath);
        return Task.FromResult<IReadOnlyList<StorageItem>>([]);
    }

    // ── OneDrive クロール（FR-02）──────────────────────────────

    private async Task<IReadOnlyList<StorageItem>> ListOneDriveItemsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.OneDriveUserId))
        {
            _logger.LogWarning("OneDriveUserId が未設定のため OneDrive クロールをスキップします");
            return [];
        }

        var drive = await _client.Users[_options.OneDriveUserId].Drive
            .GetAsync(cancellationToken: ct).ConfigureAwait(false);

        var driveId = drive?.Id
            ?? throw new InvalidOperationException(
                $"OneDrive が取得できません: UserId={_options.OneDriveUserId}");

        var result = new List<StorageItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await CrawlDriveFolderAsync(driveId, null, string.Empty, result, seen, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "OneDrive クロール完了: {Count} 件 (UserId={UserId})", result.Count, _options.OneDriveUserId);
        return result;
    }

    // ── SharePoint クロール（FR-03）───────────────────────────

    private async Task<IReadOnlyList<StorageItem>> ListSharePointItemsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.SharePointDriveId))
        {
            _logger.LogWarning("SharePointDriveId が未設定のため SharePoint クロールをスキップします");
            return [];
        }

        var result = new List<StorageItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await CrawlDriveFolderAsync(_options.SharePointDriveId, null, string.Empty, result, seen, ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "SharePoint クロール完了: {Count} 件 (DriveId={DriveId})",
            result.Count, _options.SharePointDriveId);
        return result;
    }

    // ── 共通再帰クロール（重複排除、ページング）──────────────────

    private async Task CrawlDriveFolderAsync(
        string driveId,
        string? itemId,
        string currentPath,
        List<StorageItem> result,
        HashSet<string> seen,
        CancellationToken ct)
    {
        DriveItemCollectionResponse? firstPage = itemId is null
            ? await _client.Drives[driveId].Items["root"].Children
                .GetAsync(r => r.QueryParameters.Top = 200, ct).ConfigureAwait(false)
            : await _client.Drives[driveId].Items[itemId].Children
                .GetAsync(r => r.QueryParameters.Top = 200, ct).ConfigureAwait(false);

        if (firstPage is null) return;

        // ページング対応で全アイテムを収集してから再帰
        var levelItems = new List<DriveItem>();
        var pageIterator = PageIterator<DriveItem, DriveItemCollectionResponse>
            .CreatePageIterator(_client, firstPage, item => { levelItems.Add(item); return true; });
        await pageIterator.IterateAsync(ct).ConfigureAwait(false);

        foreach (var driveItem in levelItems)
        {
            ct.ThrowIfCancellationRequested();
            if (driveItem.Id is null) continue;

            var storageItem = DriveItemToStorageItem(driveItem, currentPath);
            if (storageItem is null)
            {
                _logger.LogWarning("Name が null/空のアイテムをスキップします: Id={Id}", driveItem.Id);
                continue;
            }

            if (!seen.Add(storageItem.SkipKey))
            {
                _logger.LogDebug("重複をスキップ: {SkipKey}", storageItem.SkipKey);
                continue;
            }

            if (storageItem.IsFolder)
                await CrawlDriveFolderAsync(
                    driveId, driveItem.Id, storageItem.SkipKey, result, seen, ct)
                    .ConfigureAwait(false);
            else
                result.Add(storageItem);
        }
    }

    private static StorageItem? DriveItemToStorageItem(DriveItem item, string currentPath)
    {
        if (string.IsNullOrEmpty(item.Name))
            return null;

        return new()
        {
            Id = item.Id ?? string.Empty,
            Name = item.Name,
            Path = currentPath,
            SizeBytes = item.Folder is not null ? null : item.Size,
            LastModifiedUtc = item.LastModifiedDateTime,
            IsFolder = item.Folder is not null,
        };
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
    /// Phase 4 でストリーム取得とドライブ ID 解決を実装する。
    /// </summary>
    private async Task SmallUploadAsync(TransferJob job, CancellationToken cancellationToken)
    {
        _logger.LogDebug("小ファイルアップロード開始: {SkipKey} ({Bytes} bytes)",
            job.Source.SkipKey, job.Source.SizeBytes);

        // TODO Phase 4: ストリーム取得と driveId / itemId の解決を実装する
        _logger.LogWarning("SmallUploadAsync は Phase 4 で実装予定です: {Path}", job.DestinationFullPath);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// 大容量ファイル（4MB 以上）を Upload Session + LargeFileUploadTask でアップロード（FR-05）。
    /// Phase 4 で driveId / 親フォルダ ID の解決と組み合わせて完成する。
    /// </summary>
    private async Task LargeUploadAsync(TransferJob job, CancellationToken cancellationToken)
    {
        _logger.LogDebug("大容量ファイルアップロード開始: {SkipKey} ({Bytes} bytes)",
            job.Source.SkipKey, job.Source.SizeBytes);

        // TODO Phase 4: Upload Session フロー（LargeFileUploadTask）を実装する
        _logger.LogWarning("LargeUploadAsync は Phase 4 で実装予定です: {Path}", job.DestinationFullPath);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────
    // IStorageProvider: EnsureFolderAsync
    // FR-06: 転送先フォルダを階層順に自動作成
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task EnsureFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.SharePointDriveId))
        {
            _logger.LogWarning(
                "SharePointDriveId が未設定のため EnsureFolderAsync をスキップします: {FolderPath}", folderPath);
            return;
        }

        var segments = folderPath.Split(
            ['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return;

        var driveId = _options.SharePointDriveId;
        var parentId = "root";

        foreach (var segment in segments)
            parentId = await EnsureFolderSegmentAsync(driveId, parentId, segment, cancellationToken)
                .ConfigureAwait(false);

        _logger.LogDebug("フォルダ確認完了: {FolderPath}", folderPath);
    }

    /// <summary>フォルダが存在しなければ作成し、そのアイテム ID を返す（FR-06）。</summary>
    private async Task<string> EnsureFolderSegmentAsync(
        string driveId, string parentId, string folderName, CancellationToken ct)
    {
        var newFolder = new DriveItem
        {
            Name = folderName,
            Folder = new Folder(),
            AdditionalData = new Dictionary<string, object>
                { { "@microsoft.graph.conflictBehavior", "fail" } },
        };

        try
        {
            DriveItem? created = parentId == "root"
                ? await _client.Drives[driveId].Items["root"].Children
                    .PostAsync(newFolder, cancellationToken: ct).ConfigureAwait(false)
                : await _client.Drives[driveId].Items[parentId].Children
                    .PostAsync(newFolder, cancellationToken: ct).ConfigureAwait(false);

            return created?.Id
                ?? throw new InvalidOperationException($"フォルダ作成後に ID が取得できません: {folderName}");
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 409)
        {
            // フォルダが既に存在する場合は検索して既存 ID を返す
            _logger.LogDebug("フォルダが既に存在します。ID を検索します: {FolderName}", folderName);
            return await FindFolderIdAsync(driveId, parentId, folderName, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> FindFolderIdAsync(
        string driveId, string parentId, string folderName, CancellationToken ct)
    {
        var escapedName = folderName.Replace("'", "''", StringComparison.Ordinal);
        var filter = $"name eq '{escapedName}' and folder ne null";
        DriveItemCollectionResponse? children = parentId == "root"
            ? await _client.Drives[driveId].Items["root"].Children
                .GetAsync(r => r.QueryParameters.Filter = filter, ct).ConfigureAwait(false)
            : await _client.Drives[driveId].Items[parentId].Children
                .GetAsync(r => r.QueryParameters.Filter = filter, ct).ConfigureAwait(false);

        return children?.Value?.FirstOrDefault()?.Id
            ?? throw new InvalidOperationException(
                $"既存フォルダの ID が取得できません: {folderName}");
    }
}
