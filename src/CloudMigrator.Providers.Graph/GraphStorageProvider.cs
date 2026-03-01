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
    private readonly int _chunkSizeBytes;
    private readonly UploadSessionStore? _sessionStore;
    private string? _cachedOneDriveDriveId;

    public string ProviderId => "graph";

    /// <param name="client">GraphClientFactory で生成した GraphServiceClient</param>
    /// <param name="logger">ロガー</param>
    /// <param name="options">OneDrive/SharePoint 識別子設定</param>
    /// <param name="largeFileThresholdMb">大容量判定閾値（MB）。デフォルト 4</param>
    /// <param name="chunkSizeMb">チャンクサイズ（MB）。デフォルト 5</param>
    /// <param name="sessionStore">アップロードセッション永続化（null = 無効）</param>
    public GraphStorageProvider(
        GraphServiceClient client,
        ILogger<GraphStorageProvider> logger,
        GraphStorageOptions? options = null,
        int largeFileThresholdMb = 4,
        int chunkSizeMb = 5,
        UploadSessionStore? sessionStore = null)
    {
        _client = client;
        _logger = logger;
        _options = options ?? new GraphStorageOptions();
        _largeFileThresholdBytes = largeFileThresholdMb * 1024 * 1024;
        _chunkSizeBytes = chunkSizeMb * 1024 * 1024;
        _sessionStore = sessionStore;
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

    internal static StorageItem? DriveItemToStorageItem(DriveItem item, string currentPath)
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

    /// <summary>OneDrive のドライブ ID を 1 回だけ取得してキャッシュする。</summary>
    private async Task<string> GetOneDriveDriveIdAsync(CancellationToken ct)
    {
        if (_cachedOneDriveDriveId is not null) return _cachedOneDriveDriveId;
        var drive = await _client.Users[_options.OneDriveUserId].Drive
            .GetAsync(cancellationToken: ct)
            .ConfigureAwait(false);
        _cachedOneDriveDriveId = drive?.Id
            ?? throw new InvalidOperationException("OneDrive ドライブ ID の取得に失敗しました");
        return _cachedOneDriveDriveId;
    }

    /// <summary>
    /// 小ファイル（4MB 未満）を OneDrive からダウンロードして SharePoint へ PUT（FR-04）。
    /// </summary>
    private async Task SmallUploadAsync(TransferJob job, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.OneDriveUserId) || string.IsNullOrEmpty(_options.SharePointDriveId))
        {
            _logger.LogError(
                "OneDriveUserId または SharePointDriveId が未設定のため転送できません: {SkipKey}",
                job.Source.SkipKey);
            throw new InvalidOperationException(
                "OneDriveUserId または SharePointDriveId が未設定のため、小ファイルアップロード処理を実行できません。");
        }

        _logger.LogDebug("小ファイルアップロード開始: {SkipKey} ({Bytes} bytes)",
            job.Source.SkipKey, job.Source.SizeBytes);

        // OneDrive からダウンロードし、SharePoint へ PUT
        var oneDriveId = await GetOneDriveDriveIdAsync(ct).ConfigureAwait(false);
        await using var stream = await _client.Drives[oneDriveId].Items[job.Source.Id].Content
            .GetAsync(cancellationToken: ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"ダウンロードストリームが null です: {job.Source.SkipKey}");

        var relPath = job.DestinationFullPath.TrimStart('/');
        await _client.Drives[_options.SharePointDriveId].Root.ItemWithPath(relPath).Content
            .PutAsync(stream, cancellationToken: ct)
            .ConfigureAwait(false);

        _logger.LogInformation("小ファイルアップロード完了: {SkipKey}", job.Source.SkipKey);
    }

    /// <summary>
    /// 大容量ファイル（4MB 以上）を Upload Session + LargeFileUploadTask でアップロード（FR-05）。
    /// <see cref="UploadSessionStore"/> が設定されている場合、中断後のセッション再開に対応する。
    /// </summary>
    private async Task LargeUploadAsync(TransferJob job, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.OneDriveUserId) || string.IsNullOrEmpty(_options.SharePointDriveId))
        {
            _logger.LogError(
                "OneDriveUserId または SharePointDriveId が未設定のため転送できません: {SkipKey}",
                job.Source.SkipKey);
            throw new InvalidOperationException(
                "OneDriveUserId または SharePointDriveId が未設定のため、大容量ファイルアップロード処理を実行できません。");
        }

        _logger.LogDebug("大容量ファイルアップロード開始: {SkipKey} ({Bytes} bytes)",
            job.Source.SkipKey, job.Source.SizeBytes);

        var relPath = job.DestinationFullPath.TrimStart('/');
        var tempPath = Path.GetTempFileName();

        try
        {
            // 1. OneDrive からテンポラリファイルにダウンロード（LargeFileUploadTask はシーク可能な Stream が必要）
            var oneDriveId = await GetOneDriveDriveIdAsync(ct).ConfigureAwait(false);
            await using var downloadStream = await _client.Drives[oneDriveId].Items[job.Source.Id].Content
                .GetAsync(cancellationToken: ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"ダウンロードストリームが null です: {job.Source.SkipKey}");

            await using (var tempWrite = File.Create(tempPath))
                await downloadStream.CopyToAsync(tempWrite, ct).ConfigureAwait(false);

            // 2. 既存セッション URL の確認（再開可能か）
            // セッションキーには転送先パスを使用（ソースキーだと destRoot 変更時に誤用される）
            var sessionKey = relPath;
            UploadSession? uploadSession = null;
            var savedUrl = _sessionStore is not null
                ? await _sessionStore.GetAsync(sessionKey, ct).ConfigureAwait(false)
                : null;

            if (savedUrl is not null)
            {
                _logger.LogDebug("既存セッションで再開を試みます: {SkipKey}", job.Source.SkipKey);
                uploadSession = new UploadSession { UploadUrl = savedUrl };
            }
            else
            {
                // 3. 新規アップロードセッション作成
                uploadSession = await _client.Drives[_options.SharePointDriveId].Root
                    .ItemWithPath(relPath)
                    .CreateUploadSession
                    .PostAsync(new(), cancellationToken: ct)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"アップロードセッションの作成に失敗: {job.Source.SkipKey}");

                if (_sessionStore is not null && uploadSession.UploadUrl is not null)
                    await _sessionStore.SetAsync(sessionKey, uploadSession.UploadUrl, ct)
                        .ConfigureAwait(false);
            }

            // 4. LargeFileUploadTask でチャンク送信（セッション期限切れ時は再作成してリトライ）
            await using var uploadStream = File.OpenRead(tempPath);
            var uploadTask = new LargeFileUploadTask<DriveItem>(
                uploadSession, uploadStream, _chunkSizeBytes, _client.RequestAdapter);

            UploadResult<DriveItem> result;
            try
            {
                result = await uploadTask.UploadAsync().ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.ResponseStatusCode is 404 or 410)
            {
                // セッション期限切れ: ストア削除 → 新規セッション作成 → リトライ
                _logger.LogWarning("アップロードセッション期限切れ。再作成します: {SkipKey}", job.Source.SkipKey);
                if (_sessionStore is not null)
                    await _sessionStore.RemoveAsync(sessionKey, ct).ConfigureAwait(false);

                uploadStream.Seek(0, SeekOrigin.Begin);
                uploadSession = await _client.Drives[_options.SharePointDriveId].Root
                    .ItemWithPath(relPath)
                    .CreateUploadSession
                    .PostAsync(new(), cancellationToken: ct)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"セッション再作成に失敗: {job.Source.SkipKey}");

                if (_sessionStore is not null && uploadSession.UploadUrl is not null)
                    await _sessionStore.SetAsync(sessionKey, uploadSession.UploadUrl, ct)
                        .ConfigureAwait(false);

                uploadTask = new LargeFileUploadTask<DriveItem>(
                    uploadSession, uploadStream, _chunkSizeBytes, _client.RequestAdapter);
                result = await uploadTask.UploadAsync().ConfigureAwait(false);
            }

            if (!result.UploadSucceeded)
                throw new InvalidOperationException(
                    $"大容量アップロードが完了しませんでした: {job.Source.SkipKey}");

            // 5. 成功時はセッション削除
            if (_sessionStore is not null)
                await _sessionStore.RemoveAsync(sessionKey, ct).ConfigureAwait(false);

            _logger.LogInformation("大容量ファイルアップロード完了: {SkipKey}", job.Source.SkipKey);
        }
        finally
        {
            // テンポラリファイルを確実に削除
            try { File.Delete(tempPath); }
            catch (Exception ex) { _logger.LogWarning(ex, "テンポラリファイルの削除に失敗: {Path}", tempPath); }
        }
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
