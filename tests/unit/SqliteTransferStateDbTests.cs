using CloudMigrator.Core.State;
using CloudMigrator.Providers.Abstractions;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// 検証対象: SqliteTransferStateDb  目的: SQLite 状態 DB の CRUD・チェックポイント・ストリーミング取得
/// </summary>
public class SqliteTransferStateDbTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SqliteTransferStateDb _db;

    public SqliteTransferStateDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.db");
        _db = new SqliteTransferStateDb(_dbPath);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private static StorageItem MakeItem(string path, string name, string id = "id1", long? size = 1024)
        => new StorageItem { Id = id, Path = path, Name = name, SizeBytes = size, IsFolder = false };

    // ── InitializeAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_CreatesTablesSuccessfully()
    {
        // 検証対象: InitializeAsync  目的: スキーマ作成が冪等に成功する
        await _db.InitializeAsync(CancellationToken.None);
        // 2 回呼んでもエラーにならない（CREATE TABLE IF NOT EXISTS）
        var act = () => _db.InitializeAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── UpsertPendingAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task UpsertPendingAsync_NewItem_InsertsAsPending()
    {
        // 検証対象: UpsertPendingAsync  目的: 未登録アイテムを pending で INSERT する
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");

        await _db.UpsertPendingAsync(item, CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.Pending);
    }

    [Fact]
    public async Task UpsertPendingAsync_ExistingFailedItem_ResetsToZeroRetryCount()
    {
        // 検証対象: UpsertPendingAsync  目的: failed レコードを retry_count=0 でリセットする
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);
        await _db.MarkFailedAsync("docs", "file.txt", "error", CancellationToken.None);

        // 再度 UpsertPending → retry_count がリセットされて pending に戻る
        await _db.UpsertPendingAsync(item, CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.Pending);
    }

    // ── GetStatusAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_NonExistentItem_ReturnsNull()
    {
        // 検証対象: GetStatusAsync  目的: 未登録アイテムは null を返す
        await _db.InitializeAsync(CancellationToken.None);

        var status = await _db.GetStatusAsync("nonexistent", "file.txt", CancellationToken.None);

        status.Should().BeNull();
    }

    // ── MarkDoneAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task MarkDoneAsync_UpdatesStatusToDone()
    {
        // 検証対象: MarkDoneAsync  目的: done への遷移が正しく行われる
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);

        await _db.MarkDoneAsync("docs", "file.txt", CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.Done);
    }

    // ── MarkProcessingAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task MarkProcessingAsync_UpdatesStatusToProcessing()
    {
        // 検証対象: MarkProcessingAsync  目的: processing への遷移が正しく行われる
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);

        await _db.MarkProcessingAsync("docs", "file.txt", CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.Processing);
    }

    // ── MarkFailedAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task MarkFailedAsync_BelowMaxRetry_SetsFailed()
    {
        // 検証対象: MarkFailedAsync  目的: MaxRetry 未満のとき failed に遷移する
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);

        // 1 回失敗（MaxRetry=3 なので failed に留まる）
        await _db.MarkFailedAsync("docs", "file.txt", "err", CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.Failed);
    }

    [Fact]
    public async Task MarkFailedAsync_ReachesMaxRetry_SetsPermanentFailed()
    {
        // 検証対象: MarkFailedAsync  目的: MaxRetry 回失敗で permanent_failed に遷移する
        await _db.InitializeAsync(CancellationToken.None);
        var item = MakeItem("docs", "file.txt");
        await _db.UpsertPendingAsync(item, CancellationToken.None);

        // MaxRetry 回失敗させる
        for (var i = 0; i < SqliteTransferStateDb.MaxRetry; i++)
            await _db.MarkFailedAsync("docs", "file.txt", "err", CancellationToken.None);

        var status = await _db.GetStatusAsync("docs", "file.txt", CancellationToken.None);
        status.Should().Be(TransferStatus.PermanentFailed);
    }

    // ── チェックポイント ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetCheckpointAsync_NonExistent_ReturnsNull()
    {
        // 検証対象: GetCheckpointAsync  目的: 未保存キーは null を返す
        await _db.InitializeAsync(CancellationToken.None);

        var value = await _db.GetCheckpointAsync("cursor", CancellationToken.None);

        value.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndGetCheckpointAsync_RoundTrip()
    {
        // 検証対象: SaveCheckpointAsync + GetCheckpointAsync  目的: 保存した値が取得できる
        await _db.InitializeAsync(CancellationToken.None);

        await _db.SaveCheckpointAsync("cursor", "abc123", CancellationToken.None);
        var value = await _db.GetCheckpointAsync("cursor", CancellationToken.None);

        value.Should().Be("abc123");
    }

    [Fact]
    public async Task SaveCheckpointAsync_Overwrite_UpdatesValue()
    {
        // 検証対象: SaveCheckpointAsync  目的: 同じキーへの上書き保存が正しく動作する
        await _db.InitializeAsync(CancellationToken.None);

        await _db.SaveCheckpointAsync("cursor", "first", CancellationToken.None);
        await _db.SaveCheckpointAsync("cursor", "second", CancellationToken.None);
        var value = await _db.GetCheckpointAsync("cursor", CancellationToken.None);

        value.Should().Be("second");
    }

    // ── GetPendingStreamAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetPendingStreamAsync_ReturnsPendingProcessingFailed()
    {
        // 検証対象: GetPendingStreamAsync  目的: pending/processing/failed のみを返す（done/permanent_failed は除外）
        await _db.InitializeAsync(CancellationToken.None);

        await _db.UpsertPendingAsync(MakeItem("p", "pending.txt", "id1"),   CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "processing.txt", "id2"), CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "failed.txt", "id3"),    CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("p", "done.txt", "id4"),      CancellationToken.None);

        await _db.MarkProcessingAsync("p", "processing.txt", CancellationToken.None);
        await _db.MarkFailedAsync("p", "failed.txt", "err",   CancellationToken.None);
        await _db.MarkDoneAsync("p", "done.txt",              CancellationToken.None);

        var records = new List<TransferRecord>();
        await foreach (var r in _db.GetPendingStreamAsync(CancellationToken.None))
            records.Add(r);

        records.Should().HaveCount(3);
        records.Select(r => r.Name).Should().BeEquivalentTo(
            ["pending.txt", "processing.txt", "failed.txt"]);
    }

    [Fact]
    public async Task GetPendingStreamAsync_IncludesSourceId()
    {
        // 検証対象: GetPendingStreamAsync  目的: source_id が正しく返される（クラッシュリカバリ向け）
        await _db.InitializeAsync(CancellationToken.None);
        await _db.UpsertPendingAsync(MakeItem("docs", "file.txt", "onedrive-id-xyz"), CancellationToken.None);

        TransferRecord? record = null;
        await foreach (var r in _db.GetPendingStreamAsync(CancellationToken.None))
            record = r;

        record.Should().NotBeNull();
        record!.SourceId.Should().Be("onedrive-id-xyz");
    }
}
