using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using CloudMigrator.Core.Configuration;
using CloudMigrator.Providers.Abstractions;
using CloudMigrator.Providers.Graph.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
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
    private readonly CopyLocationCaptureHandler? _copyCapture;
    private readonly ServerSideCopyOptions _serverSideCopy;
    // コピー操作のポーリングで使用する認証不要の HttpClient（Monitor URL は SAS-style）
    // タイムアウトは CancellationToken と PollCopyOperationAsync の deadline で一元管理する。
    private static readonly HttpClient s_monitorHttpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string? _cachedOneDriveDriveId;
    // フォルダ先行作成フェーズのパス→ID キャッシュ（SharePoint への重複 API 呼び出しを防ぐ・並行安全）
    private readonly ConcurrentDictionary<string, string> _folderIdCache = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderId => "graph";

    /// <param name="client">GraphClientFactory で生成した GraphServiceClient</param>
    /// <param name="logger">ロガー</param>
    /// <param name="options">OneDrive/SharePoint 識別子設定</param>
    /// <param name="largeFileThresholdMb">大容量判定閾値（MB）。デフォルト 4</param>
    /// <param name="chunkSizeMb">チャンクサイズ（MB）。デフォルト 5</param>
    /// <param name="sessionStore">アップロードセッション永続化（null = 無効）</param>
    /// <param name="copyLocationCapture">サーバーサイドコピーの Monitor URL 捕捉ハンドラー（null = 機能無効）</param>
    /// <param name="serverSideCopy">サーバーサイドコピーのポーリング設定。null の場合はデフォルト値を使用</param>
    public GraphStorageProvider(
        GraphServiceClient client,
        ILogger<GraphStorageProvider> logger,
        GraphStorageOptions? options = null,
        int largeFileThresholdMb = 4,
        int chunkSizeMb = 5,
        UploadSessionStore? sessionStore = null,
        CopyLocationCaptureHandler? copyLocationCapture = null,
        ServerSideCopyOptions? serverSideCopy = null)
    {
        _client = client;
        _logger = logger;
        _options = options ?? new GraphStorageOptions();
        _largeFileThresholdBytes = largeFileThresholdMb * 1024 * 1024;
        _chunkSizeBytes = chunkSizeMb * 1024 * 1024;
        _sessionStore = sessionStore;
        _copyCapture = copyLocationCapture;
        _serverSideCopy = serverSideCopy ?? new ServerSideCopyOptions();
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

        // 転送元フォルダが指定されている場合はパスを itemId に解決してからクロール開始（FR-02）
        string? startItemId = null;
        string startLabel = "root";
        if (!string.IsNullOrWhiteSpace(_options.OneDriveSourceFolder))
        {
            var folderPath = _options.OneDriveSourceFolder.Trim('/');

            if (string.IsNullOrEmpty(folderPath))
            {
                // "/" や "///" のようにスラッシュのみ指定された場合はドライブ全体（root）扱い
                _logger.LogInformation(
                    "OneDriveSourceFolder がスラッシュのみのためドライブルートからクロールします (UserId={UserId})",
                    _options.OneDriveUserId);
            }
            else
            {
                // Graph SDK の Root.ItemWithPath() を使用してパス正規化をSDKに委任する
                Microsoft.Graph.Models.DriveItem? folderItem;
                try
                {
                    folderItem = await _client.Drives[driveId].Root
                        .ItemWithPath(folderPath)
                        .GetAsync(cancellationToken: ct).ConfigureAwait(false);
                }
                catch (ApiException ex) when (ex.ResponseStatusCode == 404 || ex.ResponseStatusCode == 400)
                {
                    throw new InvalidOperationException(
                        $"OneDrive の指定フォルダが見つかりません: '{folderPath}' (UserId={_options.OneDriveUserId})",
                        ex);
                }

                startItemId = folderItem?.Id
                    ?? throw new InvalidOperationException(
                        $"OneDrive の指定フォルダが見つかりません: '{folderPath}' (UserId={_options.OneDriveUserId})");
                startLabel = folderPath;
                _logger.LogInformation(
                    "OneDrive クロール開始フォルダ: {Folder} (ItemId={ItemId})", folderPath, startItemId);
            }
        }

        var result = new List<StorageItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await CrawlDriveFolderAsync(driveId, startItemId, string.Empty, result, seen, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "OneDrive クロール完了: {Count} 件 (UserId={UserId}, Folder={Folder})",
            result.Count, _options.OneDriveUserId, startLabel);
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
        DriveItemCollectionResponse? firstPage;
        try
        {
            firstPage = itemId is null
                ? await _client.Drives[driveId].Items["root"].Children
                    .GetAsync(r => r.QueryParameters.Top = 200, ct).ConfigureAwait(false)
                : await _client.Drives[driveId].Items[itemId].Children
                    .GetAsync(r => r.QueryParameters.Top = 200, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ODataError or ApiException)
        {
            _logger.LogWarning(
                "フォルダへのアクセスをスキップします ({ExType} HTTP {Status}): Path={Path} ItemId={ItemId}",
                ex.GetType().Name,
                ex is ODataError oe ? oe.ResponseStatusCode : (ex is ApiException ae ? ae.ResponseStatusCode : 0),
                currentPath, itemId);
            return;
        }

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
            {
                result.Add(storageItem);
                if (result.Count % 1000 == 0)
                    _logger.LogInformation("Graph クロール進捗: {Count} 件取得中...", result.Count);
            }
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
    // IStorageProvider: ListPagedAsync（OneDrive のみ。Graph Delta API 使用）
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// OneDrive に対して Graph Delta API（<c>/drives/{id}/root/delta</c>）を使ったページングを実装する。
    /// <list type="bullet">
    ///   <item>cursor = null: Delta クロールを先頭から開始（または OneDriveSourceFolder 起点）</item>
    ///   <item>cursor = @odata.nextLink: 同一クロールの続きページを取得</item>
    ///   <item>cursor = @odata.deltaLink: クロール完了後の増分取得起点として保存される</item>
    /// </list>
    /// SharePoint（rootPath="sharepoint"）はデフォルト実装（全件一括）にフォールバックする。
    /// </remarks>
    public async Task<StoragePage> ListPagedAsync(
        string rootPath,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        if (!rootPath.StartsWith("onedrive", StringComparison.OrdinalIgnoreCase))
        {
            // SharePoint はページング未実装 → 全件取得ラッパーにフォールバック
            var allItems = await ListItemsAsync(rootPath, cancellationToken).ConfigureAwait(false);
            return new StoragePage { Items = allItems, Cursor = null, HasMore = false };
        }

        if (string.IsNullOrEmpty(_options.OneDriveUserId))
        {
            _logger.LogWarning("OneDriveUserId が未設定のため OneDrive クロールをスキップします");
            return new StoragePage { Items = [], Cursor = null, HasMore = false };
        }

        var driveId = await GetOneDriveDriveIdAsync(cancellationToken).ConfigureAwait(false);

        // OneDriveSourceFolder が設定されている場合は、Delta API が返すパスからそのプレフィックスを除去し
        // 既存の ListOneDriveItemsAsync と同一の相対パスを生成する（FR-07 スキップリストとの整合性）
        var normalizedFolderPrefix = string.IsNullOrWhiteSpace(_options.OneDriveSourceFolder)
            ? string.Empty
            : _options.OneDriveSourceFolder.Trim('/');

        if (cursor is not null)
        {
            // 2回目以降: cursor は @odata.nextLink または @odata.deltaLink の URL
            // WithUrl で任意 URL にリクエストを向ける（Kiota の標準パターン）
            var contResponse = await _client.Drives[driveId].Items["root"].Delta
                .WithUrl(cursor)
                .GetAsDeltaGetResponseAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return BuildDeltaPage(contResponse, driveId, normalizedFolderPrefix);
        }

        // 初回: OneDriveSourceFolder が設定されている場合はそのフォルダ起点で delta を開始する。
        // フォルダ起点の delta は Items[itemId].Delta で取得でき、そのフォルダ以下のみが返る。
        string? startItemId = null;
        if (!string.IsNullOrWhiteSpace(_options.OneDriveSourceFolder))
        {
            var folderPath = _options.OneDriveSourceFolder.Trim('/');
            if (!string.IsNullOrEmpty(folderPath))
            {
                try
                {
                    var folderItem = await _client.Drives[driveId].Root
                        .ItemWithPath(folderPath)
                        .GetAsync(cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    startItemId = folderItem?.Id
                        ?? throw new InvalidOperationException(
                            $"OneDrive の指定フォルダが見つかりません: '{folderPath}'");
                }
                catch (ApiException ex) when (ex.ResponseStatusCode == 404 || ex.ResponseStatusCode == 400)
                {
                    throw new InvalidOperationException(
                        $"OneDrive の指定フォルダが見つかりません: '{folderPath}' (UserId={_options.OneDriveUserId})",
                        ex);
                }
            }
        }

        var targetItemId = startItemId ?? "root";
        var initResponse = await _client.Drives[driveId].Items[targetItemId].Delta
            .GetAsDeltaGetResponseAsync(
                requestConfiguration: cfg =>
                {
                    cfg.QueryParameters.Top = 200;
                    cfg.QueryParameters.Select =
                        ["id", "name", "parentReference", "size", "lastModifiedDateTime", "file", "folder", "deleted"];
                },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return BuildDeltaPage(initResponse, driveId, normalizedFolderPrefix);
    }

    internal StoragePage BuildDeltaPage(
        Microsoft.Graph.Drives.Item.Items.Item.Delta.DeltaGetResponse? response,
        string driveId,
        string normalizedFolderPrefix = "")
    {
        if (response?.Value is null)
            return new StoragePage { Items = [], Cursor = null, HasMore = false };

        // /drives/{driveId}/root: プレフィックスを除去して相対パスを計算する
        var driveRootPrefix = $"/drives/{driveId}/root:";
        var items = new List<StorageItem>(response.Value.Count);

        foreach (var driveItem in response.Value)
        {
            // 削除済みアイテムは移行対象外（delta API では deleted ファセットが付く）
            if (driveItem.Deleted is not null) continue;
            if (driveItem.Id is null || string.IsNullOrEmpty(driveItem.Name)) continue;

            var parentPath = string.Empty;
            if (driveItem.ParentReference?.Path is { } rawPath &&
                rawPath.StartsWith(driveRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                // "/drives/{driveId}/root:/A/B" → "A/B"、"/drives/{driveId}/root:" → ""
                parentPath = rawPath.Substring(driveRootPrefix.Length).TrimStart('/');
                // パスはパーセントエンコードされている場合があるためデコードする
                parentPath = Uri.UnescapeDataString(parentPath);
                // OneDriveSourceFolder 起点クロール時は、そのフォルダパスを相対パスの先頭から除去する
                // 例: folderPrefix="Documents/Projects", parentPath="Documents/Projects/Sub1" → "Sub1"
                if (!string.IsNullOrEmpty(normalizedFolderPrefix))
                {
                    if (parentPath.Equals(normalizedFolderPrefix, StringComparison.OrdinalIgnoreCase))
                        parentPath = string.Empty;
                    else if (parentPath.Length > normalizedFolderPrefix.Length + 1 &&
                             parentPath.StartsWith(normalizedFolderPrefix + "/", StringComparison.OrdinalIgnoreCase))
                        parentPath = parentPath.Substring(normalizedFolderPrefix.Length + 1);
                }
            }

            var storageItem = DriveItemToStorageItem(driveItem, parentPath);
            if (storageItem is not null)
                items.Add(storageItem);
        }

        // @odata.nextLink / @odata.deltaLink は OdataNextLink / OdataDeltaLink プロパティで取得する。
        // SDK バージョンによっては AdditionalData に格納されている場合もあるため両方確認する。
        var nextLink = response.OdataNextLink
            ?? (response.AdditionalData.TryGetValue("@odata.nextLink", out var nl) ? nl as string : null);
        var deltaLink = response.OdataDeltaLink
            ?? (response.AdditionalData.TryGetValue("@odata.deltaLink", out var dl) ? dl as string : null);
        var nextCursor = nextLink ?? deltaLink;
        var hasMore = nextLink is not null;

        _logger.LogDebug(
            "OneDrive Delta ページ取得: {Count} 件, HasMore={HasMore}, CursorType={CursorType}",
            items.Count, hasMore,
            nextLink is not null ? "nextLink" : deltaLink is not null ? "deltaLink" : "none");

        return new StoragePage
        {
            Items = items,
            Cursor = nextCursor,
            HasMore = hasMore,
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

    /// <inheritdoc/>
    public async Task<string> DownloadToTempAsync(StorageItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.OneDriveUserId))
            throw new InvalidOperationException("OneDriveUserId が未設定のため OneDrive からダウンロードできません。");

        var tempPath = Path.GetTempFileName();
        try
        {
            await using var stream = await DownloadStreamAsync(item, cancellationToken).ConfigureAwait(false);
            await using var tempFile = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await stream.CopyToAsync(tempFile, cancellationToken).ConfigureAwait(false);

            _logger.LogDebug("OneDrive ダウンロード完了: {SkipKey} → {TempPath}", item.SkipKey, tempPath);
            return tempPath;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* ベストエフォート */ }
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Stream> DownloadStreamAsync(StorageItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.OneDriveUserId))
            throw new InvalidOperationException("OneDriveUserId が未設定のため OneDrive からダウンロードできません。");

        var oneDriveId = await GetOneDriveDriveIdAsync(cancellationToken).ConfigureAwait(false);
        return await _client.Drives[oneDriveId].Items[item.Id].Content
            .GetAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ダウンロードストリームが null です: {item.SkipKey}");
    }

    /// <inheritdoc/>
    public async Task UploadFromLocalAsync(string localFilePath, long fileSizeBytes, string destinationFullPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_options.SharePointDriveId))
            throw new InvalidOperationException("SharePointDriveId が未設定のため SharePoint へアップロードできません。");

        var relPath = destinationFullPath.TrimStart('/');
        _logger.LogDebug("SharePoint アップロード開始: {DestPath} ({Bytes} bytes)", relPath, fileSizeBytes);

        if (fileSizeBytes < _largeFileThresholdBytes)
        {
            await using var stream = File.OpenRead(localFilePath);
            await _client.Drives[_options.SharePointDriveId].Root.ItemWithPath(relPath).Content
                .PutAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _logger.LogDebug("SharePoint 小ファイルアップロード完了: {DestPath}", relPath);
        }
        else
        {
            await UploadFromLocalLargeAsync(localFilePath, fileSizeBytes, relPath, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>大容量ファイルを Upload Session でアップロード（UploadFromLocalAsync 内部処理）。</summary>
    private async Task UploadFromLocalLargeAsync(string localFilePath, long fileSizeBytes, string relPath, CancellationToken ct)
    {
        var sessionKey = relPath;
        UploadSession? uploadSession = null;
        var savedUrl = _sessionStore is not null
            ? await _sessionStore.GetAsync(sessionKey, ct).ConfigureAwait(false)
            : null;

        if (savedUrl is not null)
        {
            _logger.LogDebug("既存セッションで再開を試みます: {DestPath}", relPath);
            uploadSession = new UploadSession
            {
                UploadUrl = savedUrl,
                NextExpectedRanges = new List<string> { "0-" },
            };
        }
        else
        {
            uploadSession = await _client.Drives[_options.SharePointDriveId].Root
                .ItemWithPath(relPath)
                .CreateUploadSession
                .PostAsync(new(), cancellationToken: ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"アップロードセッションの作成に失敗: {relPath}");

            if (_sessionStore is not null && uploadSession.UploadUrl is not null)
                await _sessionStore.SetAsync(sessionKey, uploadSession.UploadUrl, ct).ConfigureAwait(false);
        }

        await using var uploadStream = File.OpenRead(localFilePath);
        var uploadTask = new LargeFileUploadTask<DriveItem>(uploadSession, uploadStream, _chunkSizeBytes, _client.RequestAdapter);

        UploadResult<DriveItem> result;
        try
        {
            result = await uploadTask.UploadAsync().ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.ResponseStatusCode is 404 or 410)
        {
            _logger.LogWarning("アップロードセッション期限切れ。再作成します: {DestPath}", relPath);
            if (_sessionStore is not null)
                await _sessionStore.RemoveAsync(sessionKey, ct).ConfigureAwait(false);

            uploadStream.Seek(0, SeekOrigin.Begin);
            uploadSession = await _client.Drives[_options.SharePointDriveId].Root
                .ItemWithPath(relPath)
                .CreateUploadSession
                .PostAsync(new(), cancellationToken: ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"セッション再作成に失敗: {relPath}");

            if (_sessionStore is not null && uploadSession.UploadUrl is not null)
                await _sessionStore.SetAsync(sessionKey, uploadSession.UploadUrl, ct).ConfigureAwait(false);

            uploadTask = new LargeFileUploadTask<DriveItem>(uploadSession, uploadStream, _chunkSizeBytes, _client.RequestAdapter);
            result = await uploadTask.UploadAsync().ConfigureAwait(false);
        }

        if (!result.UploadSucceeded)
            throw new InvalidOperationException($"大容量アップロードが完了しませんでした: {relPath}");

        if (_sessionStore is not null)
            await _sessionStore.RemoveAsync(sessionKey, ct).ConfigureAwait(false);

        _logger.LogDebug("SharePoint 大容量ファイルアップロード完了: {DestPath}", relPath);
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
                // NextExpectedRanges を初期値で埋める。
                // UploadUrl のみで UploadSession を構築すると NextExpectedRanges が null になり、
                // LargeFileUploadTask コンストラクター内の GetRangesRemaining() が NullReferenceException を
                // 投げる（cv2.pyd 等の大容量ファイルで実測済み）。"0-" は「先頭から送信可能」を意味し、
                // SDK が最初のチャンクを送信するとサーバーから実際の再開位置が返される。
                uploadSession = new UploadSession
                {
                    UploadUrl = savedUrl,
                    NextExpectedRanges = new List<string> { "0-" },
                };
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
        var currentPath = "";

        foreach (var segment in segments)
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? segment : $"{currentPath}/{segment}";
            var cacheKey = $"{driveId}:{currentPath}";

            // キャッシュに存在する場合は API 呼び出しをスキップ
            if (_folderIdCache.TryGetValue(cacheKey, out var cachedId))
            {
                parentId = cachedId;
                continue;
            }

            parentId = await EnsureFolderSegmentAsync(driveId, parentId, segment, cancellationToken)
                .ConfigureAwait(false);
            _folderIdCache[cacheKey] = parentId;
        }

        _logger.LogDebug("フォルダ確認完了: {FolderPath}", folderPath);
    }

    // ─────────────────────────────────────────────────────────────
    // IStorageProvider: ServerSideCopyAsync
    // Graph /copy エンドポイントで OneDrive → SharePoint をサーバーサイドコピー
    // ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task ServerSideCopyAsync(
        string sourceItemId,
        string destinationFolderPath,
        string fileName,
        CancellationToken ct = default)
    {
        if (_copyCapture is null)
            throw new NotSupportedException(
                "CopyLocationCaptureHandler が設定されていないためサーバーサイドコピーを使用できません。" +
                " GraphClientFactory.Create に copyLocationCapture を渡してください。");

        return CopyToSharePointAsync(sourceItemId, destinationFolderPath, fileName, ct);
    }

    /// <summary>
    /// Graph API の /copy アクションで OneDrive → SharePoint をサーバーサイドコピーする。
    /// データはクライアントを経由せず Microsoft DC 内で完結する。
    /// コピーは非同期ジョブとして開始され、Monitor URL をポーリングして完了を待つ。
    /// </summary>
    /// <param name="sourceItemId">OneDrive の driveItem.Id</param>
    /// <param name="destinationFolderPath">転送先フォルダの相対パス（ドライブルートからの相対）</param>
    /// <param name="fileName">転送先ファイル名</param>
    /// <param name="ct">キャンセルトークン</param>
    private async Task CopyToSharePointAsync(
        string sourceItemId,
        string destinationFolderPath,
        string fileName,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.SharePointDriveId))
            throw new InvalidOperationException("SharePointDriveId が未設定のためサーバーサイドコピーを実行できません。");

        // 転送先フォルダの driveItem.Id を _folderIdCache から取得する
        string destFolderId;
        if (string.IsNullOrEmpty(destinationFolderPath))
        {
            destFolderId = "root";
        }
        else
        {
            var cacheKey = $"{_options.SharePointDriveId}:{destinationFolderPath.TrimStart('/')}";
            if (!_folderIdCache.TryGetValue(cacheKey, out destFolderId!))
                throw new InvalidOperationException(
                    $"転送先フォルダ ID がキャッシュに見つかりません: {destinationFolderPath}" +
                    " Phase C のフォルダ先行作成が完了していない可能性があります。");
        }

        var oneDriveId = await GetOneDriveDriveIdAsync(ct).ConfigureAwait(false);

        // Register a scoped capture slot before calling Copy.PostAsync.
        // The handler completes the task source when a 202 Location header is observed,
        // and disposing the scope restores the previous AsyncLocal value on every exit path.
        using var capture = CopyLocationCaptureHandler.BeginCapture();
        var locationTcs = capture.TaskSource;
        var copyResult = await _client.Drives[oneDriveId].Items[sourceItemId]
                .Copy.PostAsync(
                    new Microsoft.Graph.Drives.Item.Items.Item.Copy.CopyPostRequestBody
                    {
                        ParentReference = new ItemReference
                        {
                            DriveId = _options.SharePointDriveId,
                            Id = destFolderId,
                        },
                        Name = fileName,
                    },
                    cancellationToken: ct)
                .ConfigureAwait(false);

        // 同期完了（200 OK で DriveItem が返った）場合はそのまま終了
        if (copyResult is not null)
        {
            _logger.LogDebug("サーバーサイドコピー即時完了: SourceId={SourceId} → {FileName}", sourceItemId, fileName);
            return;
        }

        // 非同期コピー（202 Accepted）: Monitor URL を取得してポーリング
        if (!locationTcs.Task.IsCompletedSuccessfully)
            throw new InvalidOperationException(
                $"Copy API から Monitor URL が取得できませんでした: SourceId={sourceItemId}");

        var monitorUrl = await locationTcs.Task.ConfigureAwait(false);
        if (string.IsNullOrEmpty(monitorUrl))
            throw new InvalidOperationException(
                $"Copy API の 202 レスポンスに Location ヘッダーがありません: SourceId={sourceItemId}");

        _logger.LogDebug("サーバーサイドコピー開始: SourceId={SourceId} → {FileName}, MonitorUrl={Url}",
            sourceItemId, fileName, monitorUrl);

        await PollCopyOperationAsync(monitorUrl, sourceItemId, fileName, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Copy 非同期ジョブの Monitor URL を指数バックオフでポーリングし、完了または失敗を待つ。
    /// </summary>
    private async Task PollCopyOperationAsync(
        string monitorUrl, string sourceItemId, string fileName, CancellationToken ct)
    {
        // 設定値を安全な範囲に正規化（負数・0 は ArgumentOutOfRangeException を引き起こす）
        var timeoutSec = Math.Max(1, _serverSideCopy.TimeoutSec);
        var jitterMaxMs = Math.Max(0, _serverSideCopy.PollJitterMaxMs);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSec);

        // 初回のみランダムジッターを入れて、並列コピーが一斉にポーリングするサンダリングハードを防ぐ
        if (jitterMaxMs > 0)
            await Task.Delay(Random.Shared.Next(0, jitterMaxMs), ct).ConfigureAwait(false);

        var delayMs = Math.Max(0, _serverSideCopy.PollInitialDelayMs);
        var maxDelayMs = Math.Max(delayMs, _serverSideCopy.PollMaxDelayMs);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            await Task.Delay(delayMs, ct).ConfigureAwait(false);
            delayMs = Math.Min(delayMs * 2, maxDelayMs);

            HttpResponseMessage pollResponse;
            try
            {
                pollResponse = await s_monitorHttpClient.GetAsync(monitorUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Monitor URL へのポーリングで例外が発生しました: {Url}", monitorUrl);
                continue;
            }

            using (pollResponse)
            {
                if (!pollResponse.IsSuccessStatusCode)
                {
                    if (pollResponse.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        // Retry-After ヘッダーがあればその値、なければ現在のバックオフ値を使用
                        double retryAfterSec = 0;
                        if (pollResponse.Headers.RetryAfter?.Delta is { } delta)
                            retryAfterSec = delta.TotalSeconds;
                        else if (pollResponse.Headers.RetryAfter?.Date is { } retryDate)
                            retryAfterSec = (retryDate - DateTimeOffset.UtcNow).TotalSeconds;
                        var waitMs = retryAfterSec > 0
                            ? (int)Math.Min(retryAfterSec * 1000, 60_000)
                            : delayMs;
                        _logger.LogWarning(
                            "Monitor URL が 429 を返しました。{WaitSec}s 待機します: SourceId={SourceId}",
                            waitMs / 1000.0, sourceItemId);
                        await Task.Delay(waitMs, ct).ConfigureAwait(false);
                        // Retry-After 待機後はループ先頭のバックオフ待機を抑制する（二重待機防止）
                        delayMs = 0;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Monitor URL ポーリングが非成功ステータスを返しました: {StatusCode}",
                            (int)pollResponse.StatusCode);
                    }
                    continue;
                }

                string? status = null;
                string? errorMessage = null;
                try
                {
                    await using var stream = await pollResponse.Content
                        .ReadAsStreamAsync(ct).ConfigureAwait(false);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                        .ConfigureAwait(false);

                    if (doc.RootElement.TryGetProperty("status", out var statusProp))
                        status = statusProp.GetString();

                    if (doc.RootElement.TryGetProperty("error", out var errorProp)
                        && errorProp.TryGetProperty("message", out var msgProp))
                        errorMessage = msgProp.GetString();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Monitor URL のレスポンス JSON 解析に失敗しました");
                    continue;
                }

                switch (status)
                {
                    case "completed":
                        _logger.LogInformation(
                            "サーバーサイドコピー完了: SourceId={SourceId} → {FileName}", sourceItemId, fileName);
                        return;

                    case "failed":
                        throw new InvalidOperationException(
                            $"サーバーサイドコピーがサーバー側で失敗しました: SourceId={sourceItemId}, " +
                            $"FileName={fileName}, Error={errorMessage}");

                    default:
                        _logger.LogDebug(
                            "サーバーサイドコピー進行中: Status={Status}, SourceId={SourceId}",
                            status ?? "(unknown)", sourceItemId);
                        break;
                }
            } // using (pollResponse)
        }

        throw new TimeoutException(
            $"サーバーサイドコピーがタイムアウトしました ({_serverSideCopy.TimeoutSec}s): " +
            $"SourceId={sourceItemId}, FileName={fileName}");
    }

    /// <summary>フォルダが存在しなければ作成し、そのアイテム ID を返す（FR-06）。</summary>
    /// <remarks>
    /// GET first（読み込み 1 回）→ 存在すれば即返却、404 なら POST で作成。
    /// POST が 409 (競合) の場合は GET を再実行して既存 ID を返す（並行処理耐性）。
    /// </remarks>
    private async Task<string> EnsureFolderSegmentAsync(
        string driveId, string parentId, string folderName, CancellationToken ct)
    {
        // 1) GET で存在確認: ItemWithPath() で正確なパス指定（URL エンコードも正しく処理される）
        //    既存フォルダならここで ID が取れる（POST 不要）
        try
        {
            var existing = await _client.Drives[driveId].Items[parentId]
                .ItemWithPath(folderName)
                .GetAsync(cancellationToken: ct).ConfigureAwait(false);

            if (existing?.Id is not null)
            {
                _logger.LogDebug("フォルダ確認: 既存 {FolderName}", folderName);
                return existing.Id;
            }
        }
        catch (ApiException ex) when (ex.ResponseStatusCode == 404)
        {
            // フォルダが存在しない → 2) へ
        }

        // 2) POST で作成
        var newFolder = new DriveItem
        {
            Name = folderName,
            Folder = new Folder(),
            AdditionalData = new Dictionary<string, object>
                { { "@microsoft.graph.conflictBehavior", "fail" } },
        };

        try
        {
            var created = await _client.Drives[driveId].Items[parentId].Children
                .PostAsync(newFolder, cancellationToken: ct).ConfigureAwait(false);

            var newId = created?.Id
                ?? throw new InvalidOperationException($"フォルダ作成後に ID が取得できません: {folderName}");
            _logger.LogInformation("フォルダ作成: {FolderName}", folderName);
            return newId;
        }
        catch (ApiException ex)
        {
            if (ex.ResponseStatusCode != 409)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();

            // 3) 競合: 別スレッドが同名フォルダを同時作成 → GET で再取得
            _logger.LogDebug("フォルダ作成競合 (409): 再 GET {FolderName}", folderName);
            var conflicted = await _client.Drives[driveId].Items[parentId]
                .ItemWithPath(folderName)
                .GetAsync(cancellationToken: ct).ConfigureAwait(false);
            return conflicted?.Id
                ?? throw new InvalidOperationException($"フォルダ競合後の再 GET で ID が取得できません: {folderName}");
        }
    }
}
